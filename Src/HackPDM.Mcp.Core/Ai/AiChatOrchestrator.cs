using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using HackPDM.Mcp.Protocol;
using HackPDM.Mcp.Security;

namespace HackPDM.Mcp.Ai;

/// <summary>Record of a single MCP tool execution triggered by the in-app AI assistant.</summary>
public sealed record AiToolExecutionRecord(
	string ToolName,
	string ArgumentsSummary,
	bool Succeeded,
	bool WasBlockedByPermissions,
	long DurationMs,
	string ResultPreview );

/// <summary>Single turn in the Command Palette AI conversation.</summary>
public sealed record AiChatTurn(
	string Role,
	string Text,
	DateTimeOffset TimestampUtc,
	IReadOnlyList<AiToolExecutionRecord> ToolCalls,
	string? ProviderSummary = null );

/// <summary>Result of testing an AI provider profile and discovering available models.</summary>
public sealed record AiConnectionTestResult(
	bool Success,
	string Message,
	IReadOnlyList<string> Models );

/// <summary>
/// Multi-provider agentic AI orchestrator that connects directly to the user's chosen AI provider
/// (OpenRouter, OpenAI, Anthropic Claude, Google Gemini, GitHub Models, Ollama, LM Studio, or Custom
/// OpenAI-compatible) and runs live tool-calling loops against HackPDM's in-process <see cref="McpServer"/>
/// while honoring the user's active <see cref="McpPermissionManager"/> rules.
/// </summary>
public sealed class AiChatOrchestrator
{
	private const int MaxToolRounds = 8;
	private const int MaxToolResultChars = 14000;

	private readonly HttpClient _http;
	private readonly bool _ownsHttp;
	private readonly List<AiChatTurn> _history = [];

	public AiChatOrchestrator( HttpClient? httpClient = null )
	{
		_ownsHttp = httpClient is null;
		_http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds( 90 ) };
	}

	public IReadOnlyList<AiChatTurn> History => _history;

	public void ClearHistory() => _history.Clear();

	/// <summary>Tests provider credentials/connectivity and retrieves live model IDs when available.</summary>
	public async Task<AiConnectionTestResult> TestConnectionAndFetchModelsAsync(
		AiProviderProfile profile,
		CancellationToken cancellationToken = default )
	{
		AiProviderDescriptor desc = AiProviderCatalog.Get( profile.Kind );
		string baseUrl = ( string.IsNullOrWhiteSpace( profile.BaseUrl ) ? desc.DefaultBaseUrl : profile.BaseUrl ).TrimEnd( '/' );

		if( desc.RequiresApiKey && string.IsNullOrWhiteSpace( profile.ApiKey ) )
			return new AiConnectionTestResult( false, $"API key or sign-in is required for {desc.DisplayName}.", desc.SuggestedModels );

		try
		{
			switch( profile.Kind )
			{
				case AiProviderKind.Ollama:
				{
					string root = baseUrl.EndsWith( "/v1", StringComparison.OrdinalIgnoreCase ) ? baseUrl[ ..^3 ] : baseUrl;
					using HttpResponseMessage resp = await _http.GetAsync( $"{root}/api/tags", cancellationToken );
					string body = await resp.Content.ReadAsStringAsync( cancellationToken );
					if( !resp.IsSuccessStatusCode )
						return new AiConnectionTestResult( false, $"Ollama returned HTTP {(int)resp.StatusCode}: {Truncate( body, 180 )}", desc.SuggestedModels );
					List<string> models = AiProviderStore.ParseOllamaTags( body );
					return new AiConnectionTestResult(
						true,
						models.Count > 0 ? $"Connected to Ollama ({models.Count} local model(s) ready)." : "Connected to Ollama (pull a model via 'ollama pull llama3.2').",
						models.Count > 0 ? models : desc.SuggestedModels );
				}

				case AiProviderKind.GoogleGemini:
				{
					string url = $"{baseUrl}/v1beta/models?key={Uri.EscapeDataString( profile.ApiKey! )}";
					using HttpResponseMessage resp = await _http.GetAsync( url, cancellationToken );
					string body = await resp.Content.ReadAsStringAsync( cancellationToken );
					if( !resp.IsSuccessStatusCode )
						return new AiConnectionTestResult( false, $"Gemini returned HTTP {(int)resp.StatusCode}: {ExtractErrorMessage( body )}", desc.SuggestedModels );
					List<string> models = ParseGeminiModels( body );
					return new AiConnectionTestResult( true, $"Signed in to Google Gemini ({models.Count} model(s)).", models.Count > 0 ? models : desc.SuggestedModels );
				}

				case AiProviderKind.Anthropic:
				{
					using HttpRequestMessage req = new( HttpMethod.Get, $"{baseUrl}/v1/models" );
					req.Headers.Add( "x-api-key", profile.ApiKey! );
					req.Headers.Add( "anthropic-version", "2023-06-01" );
					using HttpResponseMessage resp = await _http.SendAsync( req, cancellationToken );
					string body = await resp.Content.ReadAsStringAsync( cancellationToken );
					if( !resp.IsSuccessStatusCode )
						return new AiConnectionTestResult( false, $"Anthropic returned HTTP {(int)resp.StatusCode}: {ExtractErrorMessage( body )}", desc.SuggestedModels );
					List<string> models = AiProviderStore.ParseOpenAiModelIds( body );
					return new AiConnectionTestResult( true, $"Signed in to Anthropic Claude ({models.Count} model(s)).", models.Count > 0 ? models : desc.SuggestedModels );
				}

				default:
				{
					// OpenAI / OpenRouter / GitHub Models / LM Studio / Custom OpenAI-compatible
					string modelsUrl = baseUrl.EndsWith( "/models", StringComparison.OrdinalIgnoreCase ) ? baseUrl : $"{baseUrl}/models";
					using HttpRequestMessage req = new( HttpMethod.Get, modelsUrl );
					if( !string.IsNullOrWhiteSpace( profile.ApiKey ) )
						req.Headers.Authorization = new AuthenticationHeaderValue( "Bearer", profile.ApiKey );
					if( profile.Kind == AiProviderKind.OpenRouter )
						req.Headers.Add( "X-Title", "HackPDM" );
					using HttpResponseMessage resp = await _http.SendAsync( req, cancellationToken );
					string body = await resp.Content.ReadAsStringAsync( cancellationToken );
					if( !resp.IsSuccessStatusCode )
						return new AiConnectionTestResult( false, $"{desc.DisplayName} returned HTTP {(int)resp.StatusCode}: {ExtractErrorMessage( body )}", desc.SuggestedModels );
					List<string> models = AiProviderStore.ParseOpenAiModelIds( body );
					List<string> merged = desc.SuggestedModels
						.Concat( models.Where( m => !desc.SuggestedModels.Contains( m, StringComparer.OrdinalIgnoreCase ) ) )
						.Take( 40 )
						.ToList();
					return new AiConnectionTestResult( true, $"Connected to {desc.DisplayName} ({models.Count} model(s) available).", merged.Count > 0 ? merged : desc.SuggestedModels );
				}
			}
		}
		catch( Exception ex )
		{
			return new AiConnectionTestResult( false, $"Connection failed: {ex.Message}", desc.SuggestedModels );
		}
	}

	/// <summary>
	/// Sends a user message to the active AI provider with HackPDM's live MCP toolset attached,
	/// runs multi-step tool calls directly through <paramref name="mcpServer"/>, and appends the
	/// completed turn to <see cref="History"/>.
	/// </summary>
	public async Task<AiChatTurn> SendMessageAsync(
		AiProviderProfile profile,
		string userMessage,
		McpServer mcpServer,
		McpPermissionManager permissions,
		Action<AiToolExecutionRecord>? onToolExecuted = null,
		CancellationToken cancellationToken = default )
	{
		if( string.IsNullOrWhiteSpace( userMessage ) )
			throw new ArgumentException( "Message cannot be empty." );

		AiProviderDescriptor desc = AiProviderCatalog.Get( profile.Kind );
		string model = string.IsNullOrWhiteSpace( profile.Model ) ? desc.DefaultModel : profile.Model.Trim();
		string providerSummary = $"{desc.ShortTag} • {model}";

		AiChatTurn userTurn = new( "user", userMessage.Trim(), DateTimeOffset.UtcNow, [] );
		_history.Add( userTurn );

		IReadOnlyList<McpToolDefinition> allowedTools = SelectPermittedTools( mcpServer, permissions );
		string systemPrompt = BuildSystemPrompt( permissions, allowedTools.Count );
		List<AiToolExecutionRecord> executedTools = [];

		string finalReply;
		try
		{
			finalReply = profile.Kind switch
			{
				AiProviderKind.Anthropic => await RunAnthropicLoopAsync( profile, model, systemPrompt, allowedTools, mcpServer, executedTools, onToolExecuted, cancellationToken ),
				AiProviderKind.GoogleGemini => await RunGeminiLoopAsync( profile, model, systemPrompt, allowedTools, mcpServer, executedTools, onToolExecuted, cancellationToken ),
				_ => await RunOpenAiCompatibleLoopAsync( profile, model, systemPrompt, allowedTools, mcpServer, executedTools, onToolExecuted, cancellationToken ),
			};
		}
		catch( Exception ex )
		{
			finalReply = $"AI provider error ({providerSummary}): {ex.Message}";
		}

		AiChatTurn assistantTurn = new( "assistant", finalReply, DateTimeOffset.UtcNow, executedTools, providerSummary );
		_history.Add( assistantTurn );
		return assistantTurn;
	}

	// ------------------------------------------------------------------ OpenAI-Compatible Loop (OpenAI, OpenRouter, GitHub, Ollama, LM Studio, Custom)

	private async Task<string> RunOpenAiCompatibleLoopAsync(
		AiProviderProfile profile,
		string model,
		string systemPrompt,
		IReadOnlyList<McpToolDefinition> tools,
		McpServer mcpServer,
		List<AiToolExecutionRecord> executedTools,
		Action<AiToolExecutionRecord>? onToolExecuted,
		CancellationToken ct )
	{
		AiProviderDescriptor desc = AiProviderCatalog.Get( profile.Kind );
		string baseUrl = ( string.IsNullOrWhiteSpace( profile.BaseUrl ) ? desc.DefaultBaseUrl : profile.BaseUrl ).TrimEnd( '/' );
		string endpoint = baseUrl.EndsWith( "/chat/completions", StringComparison.OrdinalIgnoreCase )
			? baseUrl
			: $"{baseUrl}/chat/completions";

		JsonArray messages = [ new JsonObject { [ "role" ] = "system", [ "content" ] = systemPrompt } ];
		foreach( AiChatTurn turn in _history.TakeLast( 16 ) )
			messages.Add( new JsonObject { [ "role" ] = turn.Role, [ "content" ] = turn.Text } );

		JsonArray toolDefs = BuildOpenAiToolDefs( tools );

		for( int round = 0; round < MaxToolRounds; round++ )
		{
			JsonObject body = new()
			{
				[ "model" ] = model,
				[ "messages" ] = messages.DeepClone(),
			};
			if( toolDefs.Count > 0 )
			{
				body[ "tools" ] = toolDefs.DeepClone();
				body[ "tool_choice" ] = "auto";
			}

			using HttpRequestMessage req = new( HttpMethod.Post, endpoint );
			req.Content = new StringContent( body.ToJsonString(), Encoding.UTF8, "application/json" );
			if( !string.IsNullOrWhiteSpace( profile.ApiKey ) )
				req.Headers.Authorization = new AuthenticationHeaderValue( "Bearer", profile.ApiKey );
			if( profile.Kind == AiProviderKind.OpenRouter )
				req.Headers.Add( "X-Title", "HackPDM" );

			using HttpResponseMessage resp = await _http.SendAsync( req, ct );
			string respJson = await resp.Content.ReadAsStringAsync( ct );
			if( !resp.IsSuccessStatusCode )
				throw new InvalidOperationException( $"HTTP {(int)resp.StatusCode}: {ExtractErrorMessage( respJson )}" );

			using JsonDocument doc = JsonDocument.Parse( respJson );
			JsonElement message = doc.RootElement.GetProperty( "choices" )[ 0 ].GetProperty( "message" );
			string assistantText = message.TryGetProperty( "content", out JsonElement cEl ) && cEl.ValueKind == JsonValueKind.String
				? cEl.GetString() ?? string.Empty
				: string.Empty;

			if( !message.TryGetProperty( "tool_calls", out JsonElement callsEl ) || callsEl.ValueKind != JsonValueKind.Array || callsEl.GetArrayLength() == 0 )
				return string.IsNullOrWhiteSpace( assistantText ) ? "(No text response from model.)" : assistantText;

			// Record assistant message with tool_calls
			messages.Add( JsonNode.Parse( message.GetRawText() )! );

			foreach( JsonElement call in callsEl.EnumerateArray() )
			{
				string callId = call.TryGetProperty( "id", out JsonElement idEl ) ? idEl.GetString() ?? Guid.NewGuid().ToString( "N" ) : Guid.NewGuid().ToString( "N" );
				JsonElement fn = call.GetProperty( "function" );
				string toolName = fn.GetProperty( "name" ).GetString() ?? string.Empty;
				string rawArgs = fn.TryGetProperty( "arguments", out JsonElement aEl )
					? ( aEl.ValueKind == JsonValueKind.String ? aEl.GetString() ?? "{}" : aEl.GetRawText() )
					: "{}";

				string toolOutput = await ExecuteMcpToolAsync( mcpServer, toolName, rawArgs, executedTools, onToolExecuted, ct );
				messages.Add( new JsonObject
				{
					[ "role" ] = "tool",
					[ "tool_call_id" ] = callId,
					[ "content" ] = toolOutput,
				} );
			}
		}

		return "Completed maximum tool rounds. Check the tool call results above or ask a follow-up question.";
	}

	// ------------------------------------------------------------------ Anthropic Native Messages API Loop

	private async Task<string> RunAnthropicLoopAsync(
		AiProviderProfile profile,
		string model,
		string systemPrompt,
		IReadOnlyList<McpToolDefinition> tools,
		McpServer mcpServer,
		List<AiToolExecutionRecord> executedTools,
		Action<AiToolExecutionRecord>? onToolExecuted,
		CancellationToken ct )
	{
		string baseUrl = ( string.IsNullOrWhiteSpace( profile.BaseUrl ) ? "https://api.anthropic.com" : profile.BaseUrl ).TrimEnd( '/' );
		string endpoint = baseUrl.EndsWith( "/v1/messages", StringComparison.OrdinalIgnoreCase ) ? baseUrl : $"{baseUrl}/v1/messages";

		JsonArray messages = [];
		foreach( AiChatTurn turn in _history.TakeLast( 16 ) )
			messages.Add( new JsonObject { [ "role" ] = turn.Role, [ "content" ] = turn.Text } );

		JsonArray toolDefs = BuildAnthropicToolDefs( tools );

		for( int round = 0; round < MaxToolRounds; round++ )
		{
			JsonObject body = new()
			{
				[ "model" ] = model,
				[ "max_tokens" ] = 4096,
				[ "system" ] = systemPrompt,
				[ "messages" ] = messages.DeepClone(),
			};
			if( toolDefs.Count > 0 )
				body[ "tools" ] = toolDefs.DeepClone();

			using HttpRequestMessage req = new( HttpMethod.Post, endpoint );
			req.Content = new StringContent( body.ToJsonString(), Encoding.UTF8, "application/json" );
			req.Headers.Add( "x-api-key", profile.ApiKey ?? string.Empty );
			req.Headers.Add( "anthropic-version", "2023-06-01" );

			using HttpResponseMessage resp = await _http.SendAsync( req, ct );
			string respJson = await resp.Content.ReadAsStringAsync( ct );
			if( !resp.IsSuccessStatusCode )
				throw new InvalidOperationException( $"HTTP {(int)resp.StatusCode}: {ExtractErrorMessage( respJson )}" );

			using JsonDocument doc = JsonDocument.Parse( respJson );
			JsonElement contentArr = doc.RootElement.GetProperty( "content" );

			List<string> textParts = [];
			List<(string Id, string Name, string ArgsJson)> toolUses = [];

			foreach( JsonElement block in contentArr.EnumerateArray() )
			{
				string type = block.TryGetProperty( "type", out JsonElement tEl ) ? tEl.GetString() ?? "" : "";
				if( type == "text" && block.TryGetProperty( "text", out JsonElement txEl ) )
					textParts.Add( txEl.GetString() ?? "" );
				else if( type == "tool_use" )
				{
					string id = block.GetProperty( "id" ).GetString() ?? Guid.NewGuid().ToString( "N" );
					string name = block.GetProperty( "name" ).GetString() ?? "";
					string input = block.TryGetProperty( "input", out JsonElement inEl ) ? inEl.GetRawText() : "{}";
					toolUses.Add( ( id, name, input ) );
				}
			}

			if( toolUses.Count == 0 )
				return string.Join( "\n\n", textParts.Where( s => !string.IsNullOrWhiteSpace( s ) ) );

			messages.Add( new JsonObject
			{
				[ "role" ] = "assistant",
				[ "content" ] = JsonNode.Parse( contentArr.GetRawText() )!,
			} );

			JsonArray toolResults = [];
			foreach( var (id, name, argsJson) in toolUses )
			{
				string output = await ExecuteMcpToolAsync( mcpServer, name, argsJson, executedTools, onToolExecuted, ct );
				toolResults.Add( new JsonObject
				{
					[ "type" ] = "tool_result",
					[ "tool_use_id" ] = id,
					[ "content" ] = output,
				} );
			}

			messages.Add( new JsonObject
			{
				[ "role" ] = "user",
				[ "content" ] = toolResults,
			} );
		}

		return "Completed maximum tool rounds.";
	}

	// ------------------------------------------------------------------ Google Gemini Native API Loop

	private async Task<string> RunGeminiLoopAsync(
		AiProviderProfile profile,
		string model,
		string systemPrompt,
		IReadOnlyList<McpToolDefinition> tools,
		McpServer mcpServer,
		List<AiToolExecutionRecord> executedTools,
		Action<AiToolExecutionRecord>? onToolExecuted,
		CancellationToken ct )
	{
		string baseUrl = ( string.IsNullOrWhiteSpace( profile.BaseUrl ) ? "https://generativelanguage.googleapis.com" : profile.BaseUrl ).TrimEnd( '/' );
		string cleanModel = model.StartsWith( "models/", StringComparison.OrdinalIgnoreCase ) ? model[ 7.. ] : model;
		string endpoint = $"{baseUrl}/v1beta/models/{Uri.EscapeDataString( cleanModel )}:generateContent?key={Uri.EscapeDataString( profile.ApiKey ?? "" )}";

		JsonArray contents = [];
		foreach( AiChatTurn turn in _history.TakeLast( 16 ) )
		{
			contents.Add( new JsonObject
			{
				[ "role" ] = turn.Role == "assistant" ? "model" : "user",
				[ "parts" ] = new JsonArray { new JsonObject { [ "text" ] = turn.Text } },
			} );
		}

		JsonArray funcDecls = BuildGeminiFunctionDeclarations( tools );

		for( int round = 0; round < MaxToolRounds; round++ )
		{
			JsonObject body = new()
			{
				[ "systemInstruction" ] = new JsonObject
				{
					[ "parts" ] = new JsonArray { new JsonObject { [ "text" ] = systemPrompt } },
				},
				[ "contents" ] = contents.DeepClone(),
			};
			if( funcDecls.Count > 0 )
			{
				body[ "tools" ] = new JsonArray
				{
					new JsonObject { [ "functionDeclarations" ] = funcDecls.DeepClone() },
				};
			}

			using HttpRequestMessage req = new( HttpMethod.Post, endpoint );
			req.Content = new StringContent( body.ToJsonString(), Encoding.UTF8, "application/json" );

			using HttpResponseMessage resp = await _http.SendAsync( req, ct );
			string respJson = await resp.Content.ReadAsStringAsync( ct );
			if( !resp.IsSuccessStatusCode )
				throw new InvalidOperationException( $"HTTP {(int)resp.StatusCode}: {ExtractErrorMessage( respJson )}" );

			using JsonDocument doc = JsonDocument.Parse( respJson );
			JsonElement modelContent = doc.RootElement.GetProperty( "candidates" )[ 0 ].GetProperty( "content" );
			JsonElement parts = modelContent.GetProperty( "parts" );

			List<string> textParts = [];
			List<(string Name, string ArgsJson)> funcCalls = [];

			foreach( JsonElement part in parts.EnumerateArray() )
			{
				if( part.TryGetProperty( "text", out JsonElement tEl ) && tEl.ValueKind == JsonValueKind.String )
					textParts.Add( tEl.GetString() ?? "" );
				if( part.TryGetProperty( "functionCall", out JsonElement fc ) && fc.ValueKind == JsonValueKind.Object )
				{
					string name = fc.GetProperty( "name" ).GetString() ?? "";
					string args = fc.TryGetProperty( "args", out JsonElement aEl ) ? aEl.GetRawText() : "{}";
					funcCalls.Add( ( name, args ) );
				}
			}

			if( funcCalls.Count == 0 )
				return string.Join( "\n\n", textParts.Where( s => !string.IsNullOrWhiteSpace( s ) ) );

			contents.Add( JsonNode.Parse( modelContent.GetRawText() )! );

			JsonArray responseParts = [];
			foreach( var (name, argsJson) in funcCalls )
			{
				string output = await ExecuteMcpToolAsync( mcpServer, name, argsJson, executedTools, onToolExecuted, ct );
				responseParts.Add( new JsonObject
				{
					[ "functionResponse" ] = new JsonObject
					{
						[ "name" ] = name,
						[ "response" ] = new JsonObject { [ "result" ] = output },
					},
				} );
			}

			contents.Add( new JsonObject
			{
				[ "role" ] = "user",
				[ "parts" ] = responseParts,
			} );
		}

		return "Completed maximum tool rounds.";
	}

	// ------------------------------------------------------------------ Shared Tool Execution & Schema Adapters

	private static async Task<string> ExecuteMcpToolAsync(
		McpServer mcpServer,
		string toolName,
		string rawArgsJson,
		List<AiToolExecutionRecord> records,
		Action<AiToolExecutionRecord>? onToolExecuted,
		CancellationToken ct )
	{
		Stopwatch sw = Stopwatch.StartNew();
		JsonElement? parsedArgs = null;
		try
		{
			if( !string.IsNullOrWhiteSpace( rawArgsJson ) )
			{
				using JsonDocument argDoc = JsonDocument.Parse( rawArgsJson );
				parsedArgs = argDoc.RootElement.Clone();
			}
		}
		catch
		{
			using JsonDocument emptyDoc = JsonDocument.Parse( "{}" );
			parsedArgs = emptyDoc.RootElement.Clone();
		}

		McpToolResult result = await mcpServer.CallToolAsync( toolName, parsedArgs, ct );
		sw.Stop();

		string textOut = string.Join(
			"\n",
			result.Content.Where( c => c.Type == "text" && !string.IsNullOrEmpty( c.Text ) ).Select( c => c.Text ) );
		if( string.IsNullOrWhiteSpace( textOut ) )
			textOut = result.IsError == true ? "{\"error\":\"Tool failed\"}" : "{\"ok\":true}";

		bool blocked = result.IsError == true && textOut.Contains( "permission_denied", StringComparison.OrdinalIgnoreCase );
		AiToolExecutionRecord rec = new(
			ToolName: toolName,
			ArgumentsSummary: Truncate( rawArgsJson.Replace( '\n', ' ' ), 120 ),
			Succeeded: result.IsError != true,
			WasBlockedByPermissions: blocked,
			DurationMs: sw.ElapsedMilliseconds,
			ResultPreview: Truncate( textOut, 260 ) );
		records.Add( rec );
		onToolExecuted?.Invoke( rec );

		return Truncate( textOut, MaxToolResultChars );
	}

	internal static IReadOnlyList<McpToolDefinition> SelectPermittedTools( McpServer server, McpPermissionManager permissions )
	{
		// Filter out tools strictly disabled by current execution mode/categories so the AI stays focused & fast.
		List<McpToolDefinition> list = [];
		foreach( McpToolDefinition tool in server.GetToolDefinitions() )
		{
			McpPermissionEvaluation eval = permissions.Evaluate( tool.Name );
			if( eval.Allowed )
				list.Add( tool );
		}
		return list;
	}

	private static string BuildSystemPrompt( McpPermissionManager permissions, int toolCount )
		=> $"You are the HackPDM MCP AI Assistant embedded directly inside the HackPDM desktop client.\n" +
		   $"Active permission profile: '{permissions.ActiveProfile.Name}' (mode: '{permissions.ActiveProfile.Mode}', {toolCount} permitted MCP tools attached).\n" +
		   "Guidelines:\n" +
		   "- Use the attached HackPDM MCP tools directly to answer questions, inspect vault/local/SolidWorks files, check diagnostics, adjust themes, or guide the user.\n" +
		   "- When the user asks for a rich report, table, BOM rollup, diff, or supplemental view, build a clean native window using `ui_window_create` (with KPI tiles, searchable tables, trees, callouts, key-value panels, etc.).\n" +
		   "- When helping the user locate a control or learn a workflow, use `ui_guide_highlight`, `ui_guide_popup`, or `ui_guide_tour`.\n" +
		   "- Keep chat replies concise, accurate, and grounded in actual tool outputs.";

	internal static JsonArray BuildOpenAiToolDefs( IReadOnlyList<McpToolDefinition> tools )
	{
		JsonArray arr = [];
		foreach( McpToolDefinition t in tools )
		{
			arr.Add( new JsonObject
			{
				[ "type" ] = "function",
				[ "function" ] = new JsonObject
				{
					[ "name" ] = t.Name,
					[ "description" ] = Truncate( t.Description, 900 ),
					[ "parameters" ] = t.InputSchema.DeepClone(),
				},
			} );
		}
		return arr;
	}

	internal static JsonArray BuildAnthropicToolDefs( IReadOnlyList<McpToolDefinition> tools )
	{
		JsonArray arr = [];
		foreach( McpToolDefinition t in tools )
		{
			arr.Add( new JsonObject
			{
				[ "name" ] = t.Name,
				[ "description" ] = Truncate( t.Description, 900 ),
				[ "input_schema" ] = t.InputSchema.DeepClone(),
			} );
		}
		return arr;
	}

	internal static JsonArray BuildGeminiFunctionDeclarations( IReadOnlyList<McpToolDefinition> tools )
	{
		JsonArray arr = [];
		foreach( McpToolDefinition t in tools )
		{
			JsonObject parameters = SanitizeGeminiSchema( t.InputSchema );
			arr.Add( new JsonObject
			{
				[ "name" ] = t.Name,
				[ "description" ] = Truncate( t.Description, 900 ),
				[ "parameters" ] = parameters,
			} );
		}
		return arr;
	}

	private static JsonObject SanitizeGeminiSchema( JsonObject src )
	{
		JsonObject dst = [];
		foreach( var kv in src )
		{
			// Gemini rejects additionalProperties, default, minimum, maximum in function schemas
			if( kv.Key is "additionalProperties" or "default" or "minimum" or "maximum" or "$schema" )
				continue;

			if( kv.Value is JsonObject childObj )
			{
				if( kv.Key == "properties" )
				{
					JsonObject props = [];
					foreach( var p in childObj )
						if( p.Value is JsonObject po )
							props[ p.Key ] = SanitizeGeminiSchema( po );
					dst[ "properties" ] = props;
				}
				else
				{
					dst[ kv.Key ] = SanitizeGeminiSchema( childObj );
				}
			}
			else if( kv.Value is not null )
			{
				dst[ kv.Key ] = kv.Value.DeepClone();
			}
		}
		if( !dst.ContainsKey( "type" ) )
			dst[ "type" ] = "object";
		return dst;
	}

	private static List<string> ParseGeminiModels( string json )
	{
		List<string> list = [];
		try
		{
			using JsonDocument doc = JsonDocument.Parse( json );
			if( doc.RootElement.TryGetProperty( "models", out JsonElement arr ) && arr.ValueKind == JsonValueKind.Array )
			{
				foreach( JsonElement m in arr.EnumerateArray() )
				{
					if( m.TryGetProperty( "name", out JsonElement nEl ) && nEl.GetString() is { Length: > 0 } name
						&& name.Contains( "gemini", StringComparison.OrdinalIgnoreCase ) )
					{
						list.Add( name.StartsWith( "models/", StringComparison.OrdinalIgnoreCase ) ? name[ 7.. ] : name );
					}
				}
			}
		}
		catch
		{
		}
		return list;
	}

	private static string ExtractErrorMessage( string body )
	{
		try
		{
			using JsonDocument doc = JsonDocument.Parse( body );
			if( doc.RootElement.TryGetProperty( "error", out JsonElement err ) )
			{
				if( err.ValueKind == JsonValueKind.String )
					return err.GetString() ?? body;
				if( err.ValueKind == JsonValueKind.Object && err.TryGetProperty( "message", out JsonElement msg ) )
					return msg.GetString() ?? body;
			}
		}
		catch
		{
		}
		return Truncate( body, 260 );
	}

	private static string Truncate( string s, int max )
		=> s.Length <= max ? s : s[ ..max ] + "…";
}

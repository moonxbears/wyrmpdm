using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace HackPDM.Mcp.Protocol;

/// <summary>
/// Core Model Context Protocol (MCP) JSON-RPC 2.0 server engine supporting tools, resources,
/// resource templates, prompts, protocol version negotiation, and optional proxy delegation.
/// </summary>
public sealed class McpServer
{
	private static readonly string[] SupportedProtocolVersions =
	[
		"2025-06-18",
		"2025-03-26",
		"2024-11-05",
	];

	private readonly ConcurrentDictionary<string, RegisteredTool> _tools = new( StringComparer.Ordinal );
	private readonly List<string> _toolOrder = [];
	private readonly ConcurrentDictionary<string, RegisteredResource> _resources = new( StringComparer.Ordinal );
	private readonly List<string> _resourceOrder = [];
	private readonly List<RegisteredResourceTemplate> _resourceTemplates = [];
	private readonly ConcurrentDictionary<string, RegisteredPrompt> _prompts = new( StringComparer.Ordinal );
	private readonly List<string> _promptOrder = [];
	private readonly Lock _registrationGate = new();

	public McpServerInfo ServerInfo { get; }
	public string? Instructions { get; set; }
	public string LogLevel { get; private set; } = "info";

	/// <summary>
	/// Optional pre-proxy permission gate invoked BEFORE <see cref="ToolCallInterceptor"/>.
	/// Used when CLI permission overrides are explicitly active on a proxying MCP server so
	/// strictly denied tools are rejected before forwarding to a live GUI bridge.
	/// Returning null allows execution/proxying to proceed; returning a result short-circuits with that result.
	/// </summary>
	public Func<string, JsonElement?, CancellationToken, Task<McpToolResult?>>? PreProxyPermissionGate { get; set; }

	/// <summary>
	/// Optional permission &amp; review gate invoked BEFORE local tool execution (after proxy interception
	/// returns null). Returning null allows the tool handler to run; returning a result blocks or denies it.
	/// </summary>
	public Func<string, JsonElement?, CancellationToken, Task<McpToolResult?>>? PermissionGate { get; set; }

	/// <summary>
	/// Optional delegate that intercepts tool calls (e.g., to proxy to a live HackPDM GUI instance).
	/// Returning null falls back to the local in-process handler.
	/// </summary>
	public Func<string, JsonElement?, CancellationToken, Task<McpToolResult?>>? ToolCallInterceptor { get; set; }

	/// <summary>
	/// Optional delegate that intercepts resource reads (e.g., to proxy to a live HackPDM GUI instance).
	/// Returning null falls back to the local in-process handler.
	/// </summary>
	public Func<string, CancellationToken, Task<McpResourceContent?>>? ResourceReadInterceptor { get; set; }

	/// <summary>
	/// Optional observer invoked after every local tool call completes:
	/// <c>(toolName, rawArgs, result, durationMs, wasProxied, errorMessage)</c>.
	/// </summary>
	public Action<string, JsonElement?, McpToolResult?, long, bool, string?>? ToolCallObserver { get; set; }

	public McpServer( McpServerInfo serverInfo, string? instructions = null )
	{
		ServerInfo = serverInfo;
		Instructions = instructions;
	}

	public IReadOnlyList<McpToolDefinition> GetToolDefinitions()
	{
		lock( _registrationGate )
			return [ .. _toolOrder.Select( name => _tools[ name ].Definition ) ];
	}

	public IReadOnlyList<McpResourceDefinition> GetResourceDefinitions()
	{
		lock( _registrationGate )
			return [ .. _resourceOrder.Select( uri => _resources[ uri ].Definition ) ];
	}

	public IReadOnlyList<McpResourceTemplate> GetResourceTemplates()
	{
		lock( _registrationGate )
			return [ .. _resourceTemplates.Select( t => t.Template ) ];
	}

	public IReadOnlyList<McpPromptDefinition> GetPromptDefinitions()
	{
		lock( _registrationGate )
			return [ .. _promptOrder.Select( name => _prompts[ name ].Definition ) ];
	}

	public void RegisterTool(
		string name,
		string description,
		JsonObject inputSchema,
		McpToolHandler handler,
		McpToolAnnotations? annotations = null )
	{
		McpToolDefinition definition = new( name, description, inputSchema, annotations );
		lock( _registrationGate )
		{
			if( !_tools.ContainsKey( name ) )
				_toolOrder.Add( name );
			_tools[ name ] = new RegisteredTool( definition, handler );
		}
	}

	public void RegisterResource(
		string uri,
		string name,
		string description,
		McpResourceHandler handler,
		string mimeType = "application/json" )
	{
		McpResourceDefinition definition = new( uri, name, description, mimeType );
		lock( _registrationGate )
		{
			if( !_resources.ContainsKey( uri ) )
				_resourceOrder.Add( uri );
			_resources[ uri ] = new RegisteredResource( definition, handler );
		}
	}

	public void RegisterResourceTemplate(
		string uriTemplate,
		string name,
		string description,
		McpResourceTemplateHandler handler,
		string mimeType = "application/json" )
	{
		Regex regex = CompileTemplate( uriTemplate );
		McpResourceTemplate template = new( uriTemplate, name, description, mimeType );
		lock( _registrationGate )
			_resourceTemplates.Add( new RegisteredResourceTemplate( template, regex, handler ) );
	}

	public void RegisterPrompt(
		string name,
		string description,
		IReadOnlyList<McpPromptArgument>? arguments,
		McpPromptHandler handler )
	{
		McpPromptDefinition definition = new( name, description, arguments );
		lock( _registrationGate )
		{
			if( !_prompts.ContainsKey( name ) )
				_promptOrder.Add( name );
			_prompts[ name ] = new RegisteredPrompt( definition, handler );
		}
	}

	/// <summary>
	/// Processes a single JSON-RPC 2.0 payload string and returns a JSON response string,
	/// or null when the incoming message was a notification (no id).
	/// </summary>
	public async Task<string?> HandleMessageAsync( string jsonPayload, CancellationToken cancellationToken = default )
	{
		JsonDocument doc;
		try
		{
			doc = JsonDocument.Parse( jsonPayload );
		}
		catch( JsonException ex )
		{
			return SerializeError( null, JsonRpcErrorCodes.ParseError, $"Invalid JSON: {ex.Message}" );
		}

		using( doc )
		{
			JsonElement root = doc.RootElement;
			if( root.ValueKind == JsonValueKind.Array )
			{
				List<string> responses = [];
				foreach( JsonElement item in root.EnumerateArray() )
				{
					string? single = await HandleSingleAsync( item, cancellationToken );
					if( single is not null )
						responses.Add( single );
				}
				return responses.Count == 0 ? null : "[" + string.Join( ",", responses ) + "]";
			}

			if( root.ValueKind != JsonValueKind.Object )
				return SerializeError( null, JsonRpcErrorCodes.InvalidRequest, "JSON-RPC payload must be an object or array." );

			return await HandleSingleAsync( root, cancellationToken );
		}
	}

	private async Task<string?> HandleSingleAsync( JsonElement request, CancellationToken cancellationToken )
	{
		bool hasId = request.TryGetProperty( "id", out JsonElement idElement )
			&& idElement.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.Null;
		JsonElement? id = hasId ? idElement.Clone() : null;

		if( !request.TryGetProperty( "method", out JsonElement methodElement ) || methodElement.ValueKind != JsonValueKind.String )
		{
			// Ignore JSON-RPC responses sent to us (e.g. client pong/result); otherwise invalid request
			if( !hasId )
				return null;
			return SerializeError( id, JsonRpcErrorCodes.InvalidRequest, "Missing or invalid 'method' field." );
		}

		string method = methodElement.GetString()!;
		JsonElement? parameters = request.TryGetProperty( "params", out JsonElement p ) && p.ValueKind != JsonValueKind.Null
			? p.Clone()
			: null;

		// Notifications have no id and never receive a JSON-RPC response
		if( !hasId )
		{
			await HandleNotificationAsync( method, parameters, cancellationToken );
			return null;
		}

		try
		{
			object? result = method switch
			{
				"initialize" => HandleInitialize( parameters ),
				"ping" => new { },
				"tools/list" => new { tools = GetToolDefinitions() },
				"tools/call" => await HandleToolsCallAsync( parameters, cancellationToken ),
				"resources/list" => new { resources = GetResourceDefinitions() },
				"resources/templates/list" => new { resourceTemplates = GetResourceTemplates() },
				"resources/read" => await HandleResourcesReadAsync( parameters, cancellationToken ),
				"prompts/list" => new { prompts = GetPromptDefinitions() },
				"prompts/get" => await HandlePromptsGetAsync( parameters, cancellationToken ),
				"logging/setLevel" => HandleSetLogLevel( parameters ),
				_ => throw new McpProtocolException( JsonRpcErrorCodes.MethodNotFound, $"Unknown MCP method '{method}'." ),
			};

			return SerializeSuccess( id, result );
		}
		catch( McpProtocolException ex )
		{
			return SerializeError( id, ex.Code, ex.Message, ex.ErrorData );
		}
		catch( OperationCanceledException ) when( cancellationToken.IsCancellationRequested )
		{
			return SerializeError( id, JsonRpcErrorCodes.InternalError, "Request was cancelled." );
		}
		catch( Exception ex )
		{
			return SerializeError( id, JsonRpcErrorCodes.InternalError, ex.Message );
		}
	}

	private Task HandleNotificationAsync( string method, JsonElement? parameters, CancellationToken cancellationToken )
	{
		_ = parameters;
		_ = cancellationToken;
		// Standard MCP notifications: notifications/initialized, notifications/cancelled, notifications/roots/list_changed
		return Task.CompletedTask;
	}

	private object HandleInitialize( JsonElement? parameters )
	{
		string? requestedVersion = null;
		if( parameters is { ValueKind: JsonValueKind.Object } p
			&& p.TryGetProperty( "protocolVersion", out JsonElement v )
			&& v.ValueKind == JsonValueKind.String )
		{
			requestedVersion = v.GetString();
		}

		string negotiated = requestedVersion is not null && SupportedProtocolVersions.Contains( requestedVersion, StringComparer.Ordinal )
			? requestedVersion
			: SupportedProtocolVersions[ 0 ];

		return new
		{
			protocolVersion = negotiated,
			capabilities = new
			{
				tools = new { listChanged = false },
				resources = new { subscribe = false, listChanged = false },
				prompts = new { listChanged = false },
				logging = new { },
			},
			serverInfo = ServerInfo,
			instructions = Instructions,
		};
	}

	private object HandleSetLogLevel( JsonElement? parameters )
	{
		if( parameters is { ValueKind: JsonValueKind.Object } p
			&& p.TryGetProperty( "level", out JsonElement level )
			&& level.ValueKind == JsonValueKind.String
			&& level.GetString() is { Length: > 0 } value )
		{
			LogLevel = value;
		}
		return new { };
	}

	/// <summary>Invokes a tool directly (useful for tests and internal bridge calls).</summary>
	public async Task<McpToolResult> CallToolAsync( string name, JsonElement? arguments, CancellationToken cancellationToken = default )
	{
		using IDisposable callScope = State.McpRestorePointManager.BeginCallScope();
		System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();

		if( PreProxyPermissionGate is { } preGate )
		{
			McpToolResult? denied = await preGate( name, arguments, cancellationToken );
			if( denied is not null )
			{
				sw.Stop();
				ToolCallObserver?.Invoke( name, arguments, denied, sw.ElapsedMilliseconds, false, null );
				return denied;
			}
		}

		if( ToolCallInterceptor is { } interceptor )
		{
			McpToolResult? intercepted = await interceptor( name, arguments, cancellationToken );
			if( intercepted is not null )
			{
				sw.Stop();
				ToolCallObserver?.Invoke( name, arguments, intercepted, sw.ElapsedMilliseconds, true, null );
				return intercepted;
			}
		}

		if( !_tools.TryGetValue( name, out RegisteredTool? tool ) )
			throw new McpProtocolException( JsonRpcErrorCodes.InvalidParams, $"Unknown tool '{name}'." );

		if( PermissionGate is { } permGate )
		{
			try
			{
				McpToolResult? blocked = await permGate( name, arguments, cancellationToken );
				if( blocked is not null )
				{
					sw.Stop();
					ToolCallObserver?.Invoke( name, arguments, blocked, sw.ElapsedMilliseconds, false, null );
					return blocked;
				}
			}
			catch( OperationCanceledException ) when( cancellationToken.IsCancellationRequested )
			{
				sw.Stop();
				McpToolResult res = McpToolResult.Error( $"Tool '{name}' was cancelled while awaiting review." );
				ToolCallObserver?.Invoke( name, arguments, res, sw.ElapsedMilliseconds, false, "Cancelled" );
				return res;
			}
		}

		try
		{
			McpToolResult res = await tool.Handler( arguments, cancellationToken );
			sw.Stop();
			ToolCallObserver?.Invoke( name, arguments, res, sw.ElapsedMilliseconds, false, null );
			return res;
		}
		catch( McpProtocolException ex )
		{
			sw.Stop();
			ToolCallObserver?.Invoke( name, arguments, null, sw.ElapsedMilliseconds, false, ex.Message );
			throw;
		}
		catch( OperationCanceledException ) when( cancellationToken.IsCancellationRequested )
		{
			sw.Stop();
			McpToolResult res = McpToolResult.Error( $"Tool '{name}' was cancelled." );
			ToolCallObserver?.Invoke( name, arguments, res, sw.ElapsedMilliseconds, false, "Cancelled" );
			return res;
		}
		catch( Exception ex )
		{
			sw.Stop();
			McpToolResult res = McpToolResult.Error( ex.Message, new { exceptionType = ex.GetType().Name } );
			ToolCallObserver?.Invoke( name, arguments, res, sw.ElapsedMilliseconds, false, ex.Message );
			return res;
		}
	}

	private async Task<McpToolResult> HandleToolsCallAsync( JsonElement? parameters, CancellationToken cancellationToken )
	{
		if( parameters is not { ValueKind: JsonValueKind.Object } p
			|| !p.TryGetProperty( "name", out JsonElement nameEl )
			|| nameEl.ValueKind != JsonValueKind.String
			|| nameEl.GetString() is not { Length: > 0 } name )
		{
			throw new McpProtocolException( JsonRpcErrorCodes.InvalidParams, "Missing required 'name' parameter for tools/call." );
		}

		JsonElement? args = p.TryGetProperty( "arguments", out JsonElement a ) && a.ValueKind != JsonValueKind.Null
			? a.Clone()
			: null;

		return await CallToolAsync( name, args, cancellationToken );
	}

	/// <summary>Reads a resource by URI directly (useful for tests and internal bridge calls).</summary>
	public async Task<McpResourceContent> ReadResourceAsync( string uri, CancellationToken cancellationToken = default )
	{
		if( ResourceReadInterceptor is { } interceptor )
		{
			McpResourceContent? intercepted = await interceptor( uri, cancellationToken );
			if( intercepted is not null )
				return intercepted;
		}

		if( _resources.TryGetValue( uri, out RegisteredResource? exact ) )
			return await exact.Handler( uri, cancellationToken );

		List<RegisteredResourceTemplate> templates;
		lock( _registrationGate )
			templates = [ .. _resourceTemplates ];

		foreach( RegisteredResourceTemplate template in templates )
		{
			Match match = template.Pattern.Match( uri );
			if( !match.Success )
				continue;

			Dictionary<string, string> variables = new( StringComparer.OrdinalIgnoreCase );
			foreach( string groupName in template.Pattern.GetGroupNames() )
			{
				if( int.TryParse( groupName, out _ ) )
					continue;
				variables[ groupName ] = Uri.UnescapeDataString( match.Groups[ groupName ].Value );
			}
			return await template.Handler( uri, variables, cancellationToken );
		}

		throw new McpProtocolException( JsonRpcErrorCodes.ResourceNotFound, $"Resource '{uri}' not found." );
	}

	private async Task<object> HandleResourcesReadAsync( JsonElement? parameters, CancellationToken cancellationToken )
	{
		if( parameters is not { ValueKind: JsonValueKind.Object } p
			|| !p.TryGetProperty( "uri", out JsonElement uriEl )
			|| uriEl.ValueKind != JsonValueKind.String
			|| uriEl.GetString() is not { Length: > 0 } uri )
		{
			throw new McpProtocolException( JsonRpcErrorCodes.InvalidParams, "Missing required 'uri' parameter for resources/read." );
		}

		McpResourceContent content = await ReadResourceAsync( uri, cancellationToken );
		return new { contents = new[] { content } };
	}

	private async Task<McpPromptResult> HandlePromptsGetAsync( JsonElement? parameters, CancellationToken cancellationToken )
	{
		if( parameters is not { ValueKind: JsonValueKind.Object } p
			|| !p.TryGetProperty( "name", out JsonElement nameEl )
			|| nameEl.ValueKind != JsonValueKind.String
			|| nameEl.GetString() is not { Length: > 0 } name )
		{
			throw new McpProtocolException( JsonRpcErrorCodes.InvalidParams, "Missing required 'name' parameter for prompts/get." );
		}

		if( !_prompts.TryGetValue( name, out RegisteredPrompt? prompt ) )
			throw new McpProtocolException( JsonRpcErrorCodes.InvalidParams, $"Unknown prompt '{name}'." );

		Dictionary<string, string> args = new( StringComparer.OrdinalIgnoreCase );
		if( p.TryGetProperty( "arguments", out JsonElement argsEl ) && argsEl.ValueKind == JsonValueKind.Object )
		{
			foreach( JsonProperty prop in argsEl.EnumerateObject() )
				args[ prop.Name ] = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() ?? "" : prop.Value.GetRawText();
		}

		return await prompt.Handler( args, cancellationToken );
	}

	private static string SerializeSuccess( JsonElement? id, object? result )
	{
		using MemoryStream ms = new();
		using( Utf8JsonWriter writer = new( ms ) )
		{
			writer.WriteStartObject();
			writer.WriteString( "jsonrpc", "2.0" );
			writer.WritePropertyName( "id" );
			if( id is { } idEl )
				idEl.WriteTo( writer );
			else
				writer.WriteNullValue();
			writer.WritePropertyName( "result" );
			JsonSerializer.Serialize( writer, result, McpJson.Options );
			writer.WriteEndObject();
		}
		return System.Text.Encoding.UTF8.GetString( ms.ToArray() );
	}

	private static string SerializeError( JsonElement? id, int code, string message, object? data = null )
	{
		using MemoryStream ms = new();
		using( Utf8JsonWriter writer = new( ms ) )
		{
			writer.WriteStartObject();
			writer.WriteString( "jsonrpc", "2.0" );
			writer.WritePropertyName( "id" );
			if( id is { } idEl )
				idEl.WriteTo( writer );
			else
				writer.WriteNullValue();
			writer.WritePropertyName( "error" );
			JsonSerializer.Serialize( writer, new JsonRpcError( code, message, data ), McpJson.Options );
			writer.WriteEndObject();
		}
		return System.Text.Encoding.UTF8.GetString( ms.ToArray() );
	}

	private static Regex CompileTemplate( string uriTemplate )
	{
		string escaped = Regex.Escape( uriTemplate );
		string pattern = Regex.Replace( escaped, @"\\\{([A-Za-z0-9_]+)\\?\}", match => $"(?<{match.Groups[ 1 ].Value}>[^/]+)" );
		return new Regex( $"^{pattern}$", RegexOptions.Compiled | RegexOptions.CultureInvariant );
	}

	private sealed record RegisteredTool( McpToolDefinition Definition, McpToolHandler Handler );
	private sealed record RegisteredResource( McpResourceDefinition Definition, McpResourceHandler Handler );
	private sealed record RegisteredResourceTemplate( McpResourceTemplate Template, Regex Pattern, McpResourceTemplateHandler Handler );
	private sealed record RegisteredPrompt( McpPromptDefinition Definition, McpPromptHandler Handler );
}

public sealed class McpProtocolException( int code, string message, object? errorData = null ) : Exception( message )
{
	public int Code { get; } = code;
	public object? ErrorData { get; } = errorData;
}

using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using HackPDM.Mcp;
using HackPDM.Mcp.Ai;
using HackPDM.Mcp.Bridge;
using HackPDM.Mcp.Security;
using HackPDM.Tests.Characterization;

using Xunit;

namespace HackPDM.Tests.Mcp;

[Collection( SharedStateCollection.Name )]
public sealed class McpCommandPaletteAndAiConnectTests : IDisposable
{
	private readonly string _tempRoot;
	private readonly string _dataDir;

	public McpCommandPaletteAndAiConnectTests()
	{
		_tempRoot = Path.Combine( Path.GetTempPath(), "hackpdm_mcp_palette_" + Guid.NewGuid().ToString( "N" )[ ..8 ] );
		_dataDir = Path.Combine( _tempRoot, "mcpdata" );
		Directory.CreateDirectory( _dataDir );
	}

	public void Dispose()
	{
		try
		{
			if( Directory.Exists( _tempRoot ) )
				Directory.Delete( _tempRoot, recursive: true );
		}
		catch
		{
		}
	}

	[Fact]
	public void CommandPaletteCatalog_CuratedMcpCommandsExist_AndFuzzySearchWorks()
	{
		using var bundle = HackPdmMcpServerFactory.Create( McpServerMode.Headless, dataDirOverride: _dataDir );
		var registered = bundle.Server.GetToolDefinitions().Select( t => t.Name ).ToHashSet( StringComparer.Ordinal );

		// Every curated McpTool command must reference an actually registered MCP tool
		foreach( PaletteCommandItem item in CommandPaletteCatalog.CuratedCommands.Where( c => c.Kind == PaletteCommandKind.McpTool ) )
			Assert.Contains( item.Target!, registered );

		// Fuzzy search finds relevant commands
		var swMatches = CommandPaletteCatalog.SearchCommands( "solidworks" );
		Assert.Contains( swMatches, c => c.Id == "sw.inspect" );
		Assert.Contains( swMatches, c => c.Id == "sw.scan_deps" );

		var permMatches = CommandPaletteCatalog.SearchCommands( ">local-only" );
		Assert.Contains( permMatches, c => c.Id == "perm.local_only" );

		// Tool search across all 115 MCP tools
		var toolMatches = CommandPaletteCatalog.SearchMcpTools( bundle.Server.GetToolDefinitions(), "/ui_window" );
		Assert.Contains( toolMatches, x => x.Tool.Name == "ui_window_create" );
		Assert.Contains( toolMatches, x => x.Tool.Name == "ui_window_update" );
	}

	[Fact]
	public async Task AiProviderStore_PersistsMasksSignsOut_AndAutoDetectsEnvAndLocalServers()
	{
		string cfgFile = Path.Combine( _tempRoot, "mcp-ai-providers.json" );
		AiProviderStore store = new( cfgFile, persistChanges: true );
		Assert.Null( store.GetActiveProfile() );

		store.SaveAndActivate( new AiProviderProfile
		{
			Kind = AiProviderKind.OpenAI,
			ApiKey = "sk-test-secret-key-987654321",
			Model = "gpt-4.1",
		} );

		AiProviderProfile? active = store.GetActiveProfile();
		Assert.NotNull( active );
		Assert.Equal( AiProviderKind.OpenAI, active!.Kind );
		Assert.Equal( "gpt-4.1", active.Model );
		Assert.Contains( "••••", active.MaskedApiKey );
		Assert.DoesNotContain( "secret-key", active.MaskedApiKey );

		// Reload from disk verifies persistence
		AiProviderStore reloaded = new( cfgFile, persistChanges: false );
		Assert.Equal( AiProviderKind.OpenAI, reloaded.ActiveProvider );
		Assert.Equal( "sk-test-secret-key-987654321", reloaded.GetActiveProfile()?.ApiKey );

		// SignOut removes credentials cleanly
		store.SignOut( AiProviderKind.OpenAI );
		Assert.Null( store.GetActiveProfile() );

		// Auto-detect environment keys + mocked local Ollama server
		using HttpClient mockHttp = new( new StubHttpHandler( req =>
		{
			if( req.RequestUri!.AbsoluteUri.Contains( "11434/api/tags" ) )
				return JsonResponse( "{\"models\":[{\"name\":\"qwen2.5-coder:7b\"},{\"name\":\"llama3.2\"}]}" );
			return new HttpResponseMessage( HttpStatusCode.ServiceUnavailable );
		} ) );

		IReadOnlyList<DetectedAiProvider> detected = await AiProviderStore.AutoDetectAvailableAsync(
			httpClient: mockHttp,
			envReader: name => name == "ANTHROPIC_API_KEY" ? "sk-ant-demo-123" : null );

		Assert.Contains( detected, d => d.Kind == AiProviderKind.Anthropic && d.DetectedApiKey == "sk-ant-demo-123" );
		Assert.Contains( detected, d => d.Kind == AiProviderKind.Ollama && d.SuggestedModel == "qwen2.5-coder:7b" );
	}

	[Fact]
	public void AiOAuthLoginFlow_CreatesValidRfc7636PkcePairAndOpenRouterAuthUrl()
	{
		var (verifier, challenge) = AiOAuthLoginFlow.CreatePkcePair();
		Assert.True( verifier.Length >= 43 );
		byte[] expectedHash = SHA256.HashData( Encoding.ASCII.GetBytes( verifier ) );
		string expectedChallenge = Convert.ToBase64String( expectedHash ).TrimEnd( '=' ).Replace( '+', '-' ).Replace( '/', '_' );
		Assert.Equal( expectedChallenge, challenge );

		string url = AiOAuthLoginFlow.BuildOpenRouterAuthUrl( "http://127.0.0.1:45123/oauth/callback", challenge );
		Assert.StartsWith( "https://openrouter.ai/auth?", url );
		Assert.Contains( "code_challenge_method=S256", url );
		Assert.Contains( Uri.EscapeDataString( "http://127.0.0.1:45123/oauth/callback" ), url );
	}

	[Fact]
	public async Task AiChatOrchestrator_RunsLiveMcpToolCallingAcrossOpenAiAnthropicAndGemini()
	{
		using var bundle = HackPdmMcpServerFactory.Create( McpServerMode.Headless, dataDirOverride: _dataDir, defaultPermissionPresetWhenUnconfigured: "review-critical" );

		// 1. OpenAI-compatible 2-round tool calling loop
		int openAiCall = 0;
		using HttpClient openAiHttp = new( new StubHttpHandler( req =>
		{
			openAiCall++;
			if( openAiCall == 1 )
			{
				return JsonResponse( """
				{
				  "choices": [{
				    "message": {
				      "role": "assistant",
				      "content": null,
				      "tool_calls": [{
				        "id": "call_1",
				        "type": "function",
				        "function": { "name": "mcp_permissions_get", "arguments": "{}" }
				      }]
				    }
				  }]
				}
				""" );
			}
			string body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
			Assert.Contains( "review-critical", body );
			return JsonResponse( """
			{
			  "choices": [{
			    "message": { "role": "assistant", "content": "HackPDM is currently in review-critical mode." }
			  }]
			}
			""" );
		} ) );

		AiChatOrchestrator openAiOrch = new( openAiHttp );
		AiChatTurn openAiReply = await openAiOrch.SendMessageAsync(
			new AiProviderProfile { Kind = AiProviderKind.OpenAI, ApiKey = "sk-test", Model = "gpt-4.1" },
			"What permission mode am I in?",
			bundle.Server,
			bundle.Permissions );

		Assert.Contains( "review-critical", openAiReply.Text );
		Assert.Single( openAiReply.ToolCalls );
		Assert.Equal( "mcp_permissions_get", openAiReply.ToolCalls[ 0 ].ToolName );
		Assert.True( openAiReply.ToolCalls[ 0 ].Succeeded );

		// 2. Anthropic Messages API 2-round tool calling loop
		int claudeCall = 0;
		using HttpClient claudeHttp = new( new StubHttpHandler( req =>
		{
			claudeCall++;
			Assert.Equal( "sk-ant-test", req.Headers.GetValues( "x-api-key" ).First() );
			if( claudeCall == 1 )
			{
				return JsonResponse( """
				{
				  "content": [{
				    "type": "tool_use",
				    "id": "toolu_1",
				    "name": "client_status",
				    "input": {}
				  }]
				}
				""" );
			}
			return JsonResponse( """
			{
			  "content": [{ "type": "text", "text": "Client status checked via Anthropic." }]
			}
			""" );
		} ) );

		AiChatOrchestrator claudeOrch = new( claudeHttp );
		AiChatTurn claudeReply = await claudeOrch.SendMessageAsync(
			new AiProviderProfile { Kind = AiProviderKind.Anthropic, ApiKey = "sk-ant-test", Model = "claude-sonnet-4-20250514" },
			"Check status",
			bundle.Server,
			bundle.Permissions );

		Assert.Contains( "Anthropic", claudeReply.Text );
		Assert.Single( claudeReply.ToolCalls );
		Assert.Equal( "client_status", claudeReply.ToolCalls[ 0 ].ToolName );

		// 3. Google Gemini native function calling + schema sanitization
		var geminiDecls = AiChatOrchestrator.BuildGeminiFunctionDeclarations( bundle.Server.GetToolDefinitions() );
		Assert.DoesNotContain( "additionalProperties", geminiDecls.ToJsonString() );

		int geminiCall = 0;
		using HttpClient geminiHttp = new( new StubHttpHandler( _ =>
		{
			geminiCall++;
			if( geminiCall == 1 )
			{
				return JsonResponse( """
				{
				  "candidates": [{
				    "content": {
				      "role": "model",
				      "parts": [{ "functionCall": { "name": "mcp_permissions_list_presets", "args": {} } }]
				    }
				  }]
				}
				""" );
			}
			return JsonResponse( """
			{
			  "candidates": [{
			    "content": {
			      "role": "model",
			      "parts": [{ "text": "Listed all presets via Gemini." }]
			    }
			  }]
			}
			""" );
		} ) );

		AiChatOrchestrator geminiOrch = new( geminiHttp );
		AiChatTurn geminiReply = await geminiOrch.SendMessageAsync(
			new AiProviderProfile { Kind = AiProviderKind.GoogleGemini, ApiKey = "AIza-test", Model = "gemini-2.5-flash" },
			"List presets",
			bundle.Server,
			bundle.Permissions );

		Assert.Contains( "Gemini", geminiReply.Text );
		Assert.Single( geminiReply.ToolCalls );
		Assert.Equal( "mcp_permissions_list_presets", geminiReply.ToolCalls[ 0 ].ToolName );
	}

	[Fact]
	public void McpClientAutoConfigurator_ConnectsPreservesExistingServers_CreatesBackup_AndDisconnects()
	{
		string fakeHome = Path.Combine( _tempRoot, "home" );
		string fakeAppData = Path.Combine( _tempRoot, "appdata" );
		string cursorDir = Path.Combine( fakeHome, ".cursor" );
		Directory.CreateDirectory( cursorDir );
		string cursorCfg = Path.Combine( cursorDir, "mcp.json" );

		// Seed an existing MCP server to prove we never clobber user's other servers
		File.WriteAllText( cursorCfg, """
		{
		  "mcpServers": {
		    "existing-server": { "command": "npx", "args": ["some-mcp"] }
		  }
		}
		""" );

		var before = McpClientAutoConfigurator.DetectClients( fakeHome, fakeAppData );
		var cursorBefore = before.First( c => c.ClientId == "cursor" );
		Assert.True( cursorBefore.IsDetected );
		Assert.False( cursorBefore.IsConnected );

		// 1-Click Connect Cursor & VS Code
		var connCursor = McpClientAutoConfigurator.ConnectClient( "cursor", "dotnet", [ "/path/HackPDM.McpServer.dll", "--mode", "auto" ], fakeHome, fakeAppData );
		Assert.True( connCursor.Success );
		Assert.NotNull( connCursor.BackupPath );
		Assert.True( File.Exists( connCursor.BackupPath! ) );

		var connVsCode = McpClientAutoConfigurator.ConnectClient( "vscode", "dotnet", [ "/path/HackPDM.McpServer.dll", "--mode", "auto" ], fakeHome, fakeAppData );
		Assert.True( connVsCode.Success );

		// Verify existing-server preserved AND hackpdm added
		using( JsonDocument doc = JsonDocument.Parse( File.ReadAllText( cursorCfg ) ) )
		{
			JsonElement servers = doc.RootElement.GetProperty( "mcpServers" );
			Assert.True( servers.TryGetProperty( "existing-server", out _ ) );
			Assert.True( servers.TryGetProperty( "hackpdm", out JsonElement hp ) );
			Assert.Equal( "dotnet", hp.GetProperty( "command" ).GetString() );
		}

		// Verify VS Code uses "servers" root key with "type": "stdio"
		using( JsonDocument vsDoc = JsonDocument.Parse( File.ReadAllText( connVsCode.ConfigPath ) ) )
		{
			JsonElement hp = vsDoc.RootElement.GetProperty( "servers" ).GetProperty( "hackpdm" );
			Assert.Equal( "stdio", hp.GetProperty( "type" ).GetString() );
		}

		var mid = McpClientAutoConfigurator.DetectClients( fakeHome, fakeAppData );
		Assert.True( mid.First( c => c.ClientId == "cursor" ).IsConnected );
		Assert.True( mid.First( c => c.ClientId == "vscode" ).IsConnected );

		// 1-Click Disconnect removes ONLY hackpdm
		var disc = McpClientAutoConfigurator.DisconnectClient( "cursor", fakeHome, fakeAppData );
		Assert.True( disc.Success );
		using( JsonDocument docAfter = JsonDocument.Parse( File.ReadAllText( cursorCfg ) ) )
		{
			JsonElement servers = docAfter.RootElement.GetProperty( "mcpServers" );
			Assert.True( servers.TryGetProperty( "existing-server", out _ ) );
			Assert.False( servers.TryGetProperty( "hackpdm", out _ ) );
		}
	}

	[Fact]
	public void ProtectedControls_BlockAiFromClickingLoginOrClientAutoConnectButtons()
	{
		string permFile = Path.Combine( _tempRoot, "mcp-permissions.json" );
		McpPermissionManager perms = new( permFile, defaultPresetWhenUnconfigured: "auto", persistChanges: false );

		string[] protectedIds =
		[
			"McpAiConnect_BrowserOAuthButton",
			"McpAiConnect_SignOutButton",
			"McpAiConnect_ApiKeyBox",
			"McpClientAutoConnect_claude-desktop",
			"McpCommandPaletteButton_SendAi",
		];

		foreach( string id in protectedIds )
		{
			using JsonDocument doc = JsonDocument.Parse( JsonSerializer.Serialize( new { name = id } ) );
			Assert.False( perms.Evaluate( "ui_click_control", doc.RootElement.Clone() ).Allowed, $"Expected '{id}' blocked." );
		}
	}

	private static HttpResponseMessage JsonResponse( string json ) => new( HttpStatusCode.OK )
	{
		Content = new StringContent( json, Encoding.UTF8, "application/json" ),
	};

	private sealed class StubHttpHandler( Func<HttpRequestMessage, HttpResponseMessage> handler ) : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync( HttpRequestMessage request, CancellationToken cancellationToken )
			=> Task.FromResult( handler( request ) );
	}
}

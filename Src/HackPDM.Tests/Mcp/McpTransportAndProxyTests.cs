using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using HackPDM.Mcp;
using HackPDM.Mcp.Bridge;
using HackPDM.Mcp.Protocol;

namespace HackPDM.Tests.Mcp;

public sealed class McpTransportAndProxyTests
{
	[Fact]
	public async Task StdioTransport_ProcessesNewlineDelimitedFrames_AndSkipsNotifications()
	{
		using var bundle = HackPdmMcpServerFactory.Create( McpServerMode.Headless );

		string inputLines = string.Join( "\n",
			JsonSerializer.Serialize( new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new { protocolVersion = "2025-06-18" } } ),
			JsonSerializer.Serialize( new { jsonrpc = "2.0", method = "notifications/initialized" } ),
			JsonSerializer.Serialize( new { jsonrpc = "2.0", id = 2, method = "ping" } ),
			JsonSerializer.Serialize( new { jsonrpc = "2.0", id = 3, method = "tools/call", @params = new { name = "client_status", arguments = new { } } } ) ) + "\n";

		using MemoryStream inStream = new( Encoding.UTF8.GetBytes( inputLines ) );
		using MemoryStream outStream = new();
		using StringWriter errLog = new();

		McpStdioTransport transport = new( bundle.Server, inStream, outStream, errLog );
		await transport.RunAsync();

		string[] outputLines = Encoding.UTF8.GetString( outStream.ToArray() )
			.Split( '\n', StringSplitOptions.RemoveEmptyEntries );

		Assert.Equal( 3, outputLines.Length );
		using JsonDocument r1 = JsonDocument.Parse( outputLines[ 0 ] );
		Assert.Equal( 1, r1.RootElement.GetProperty( "id" ).GetInt32() );
		using JsonDocument r2 = JsonDocument.Parse( outputLines[ 1 ] );
		Assert.Equal( 2, r2.RootElement.GetProperty( "id" ).GetInt32() );
		using JsonDocument r3 = JsonDocument.Parse( outputLines[ 2 ] );
		Assert.Equal( 3, r3.RootElement.GetProperty( "id" ).GetInt32() );
		Assert.Contains( "headless", r3.RootElement.GetProperty( "result" ).GetProperty( "content" )[ 0 ].GetProperty( "text" ).GetString()! );
	}

	[Fact]
	public async Task HttpServer_EnforcesBearerAuthAndLoopbackOrigin_AndSupportsProxyClient()
	{
		string token = McpHttpServer.GenerateSecureToken();
		using var embedded = HackPdmMcpServerFactory.Create(
			mode: McpServerMode.EmbeddedGui,
			localUiBridge: new FakeUiBridge() );
		using McpHttpServer httpServer = new( embedded.Server, port: 0, authToken: token );
		httpServer.Start();

		Assert.True( httpServer.Port > 0 );
		Assert.True( httpServer.IsRunning );

		using HttpClient http = new();

		// 1. /health succeeds without token
		HttpResponseMessage healthResp = await http.GetAsync( $"{httpServer.BaseUrl}/health" );
		Assert.Equal( HttpStatusCode.OK, healthResp.StatusCode );

		// 2. /mcp without token -> 401
		using HttpRequestMessage noAuthReq = new( HttpMethod.Post, httpServer.McpEndpointUrl )
		{
			Content = new StringContent( """{"jsonrpc":"2.0","id":1,"method":"ping"}""", Encoding.UTF8, "application/json" ),
		};
		HttpResponseMessage noAuthResp = await http.SendAsync( noAuthReq );
		Assert.Equal( HttpStatusCode.Unauthorized, noAuthResp.StatusCode );

		// 3. /mcp with wrong token -> 401
		using HttpRequestMessage wrongAuthReq = new( HttpMethod.Post, httpServer.McpEndpointUrl )
		{
			Content = new StringContent( """{"jsonrpc":"2.0","id":1,"method":"ping"}""", Encoding.UTF8, "application/json" ),
		};
		wrongAuthReq.Headers.Authorization = new AuthenticationHeaderValue( "Bearer", "wrong-token" );
		HttpResponseMessage wrongAuthResp = await http.SendAsync( wrongAuthReq );
		Assert.Equal( HttpStatusCode.Unauthorized, wrongAuthResp.StatusCode );

		// 4. /mcp with external Origin header -> 403 (DNS rebinding / browser CSRF defense)
		using HttpRequestMessage evilOriginReq = new( HttpMethod.Post, httpServer.McpEndpointUrl )
		{
			Content = new StringContent( """{"jsonrpc":"2.0","id":1,"method":"ping"}""", Encoding.UTF8, "application/json" ),
		};
		evilOriginReq.Headers.Authorization = new AuthenticationHeaderValue( "Bearer", token );
		evilOriginReq.Headers.Add( "Origin", "https://evil.example.com" );
		HttpResponseMessage evilOriginResp = await http.SendAsync( evilOriginReq );
		Assert.Equal( HttpStatusCode.Forbidden, evilOriginResp.StatusCode );

		// 5. /mcp with loopback Origin + valid Bearer token -> 200
		using HttpRequestMessage okOriginReq = new( HttpMethod.Post, httpServer.McpEndpointUrl )
		{
			Content = new StringContent( """{"jsonrpc":"2.0","id":1,"method":"ping"}""", Encoding.UTF8, "application/json" ),
		};
		okOriginReq.Headers.Authorization = new AuthenticationHeaderValue( "Bearer", token );
		okOriginReq.Headers.Add( "Origin", "http://localhost:9999" );
		HttpResponseMessage okOriginResp = await http.SendAsync( okOriginReq );
		Assert.Equal( HttpStatusCode.OK, okOriginResp.StatusCode );

		// 6. Proxy client end-to-end tool call & resource read
		using McpRemoteProxyClient proxy = new( httpServer.BaseUrl, token, Environment.ProcessId );
		JsonElement? health = await proxy.GetHealthAsync();
		Assert.NotNull( health );

		McpToolResult uiState = await proxy.CallToolAsync( "ui_get_state", null );
		Assert.NotEqual( true, uiState.IsError );
		Assert.Contains( "FakeMainPage", uiState.Content[ 0 ].Text! );

		McpResourceContent uiRes = await proxy.ReadResourceAsync( "hackpdm://ui/state" );
		Assert.Contains( "FakeMainPage", uiRes.Text! );
	}

	[Fact]
	public void BridgeDiscovery_WritesReadsAndCleansUpStalePidFiles()
	{
		string tempDir = Path.Combine( Path.GetTempPath(), $"hackpdm-mcp-disc-{Guid.NewGuid():N}" );
		Directory.CreateDirectory( tempDir );
		try
		{
			McpBridgeEndpointInfo live = new(
				Pid: Environment.ProcessId,
				Port: 41234,
				BaseUrl: "http://127.0.0.1:41234",
				McpUrl: "http://127.0.0.1:41234/mcp",
				Token: "abc123",
				StartedAtUtc: DateTimeOffset.UtcNow,
				AppVersion: "test",
				Platform: "test" );

			McpBridgeDiscovery.WriteDiscoveryFile( live, tempDir );
			McpBridgeEndpointInfo? readBack = McpBridgeDiscovery.TryReadLiveBridge( tempDir );
			Assert.NotNull( readBack );
			Assert.Equal( 41234, readBack!.Port );
			Assert.Equal( "abc123", readBack.Token );

			// Removing with non-matching PID must preserve the file
			McpBridgeDiscovery.RemoveDiscoveryFile( Environment.ProcessId + 999999, tempDir );
			Assert.NotNull( McpBridgeDiscovery.TryReadLiveBridge( tempDir ) );

			// Removing with matching PID removes the file
			McpBridgeDiscovery.RemoveDiscoveryFile( Environment.ProcessId, tempDir );
			Assert.Null( McpBridgeDiscovery.TryReadLiveBridge( tempDir ) );

			// Stale dead PID file is rejected and deleted automatically
			McpBridgeEndpointInfo stale = live with { Pid = 99999999 };
			McpBridgeDiscovery.WriteDiscoveryFile( stale, tempDir );
			Assert.Null( McpBridgeDiscovery.TryReadLiveBridge( tempDir ) );
			Assert.False( File.Exists( McpBridgeDiscovery.GetDiscoveryFilePath( tempDir ) ) );
		}
		finally
		{
			try { Directory.Delete( tempDir, true ); } catch { }
		}
	}

	[Fact]
	public async Task BridgeCoordinator_AutoAttaches_ProxiesUiTools_Detaches_AndFallsBackWhenGuiStops()
	{
		string tempDir = Path.Combine( Path.GetTempPath(), $"hackpdm-mcp-coord-{Guid.NewGuid():N}" );
		Directory.CreateDirectory( tempDir );
		try
		{
			string token = McpHttpServer.GenerateSecureToken();
			using var embedded = HackPdmMcpServerFactory.Create(
				mode: McpServerMode.EmbeddedGui,
				localUiBridge: new FakeUiBridge() );
			using McpHttpServer httpServer = new( embedded.Server, port: 0, authToken: token );
			httpServer.Start();

			McpBridgeDiscovery.WriteDiscoveryFile( new McpBridgeEndpointInfo(
				Pid: Environment.ProcessId,
				Port: httpServer.Port,
				BaseUrl: httpServer.BaseUrl,
				McpUrl: httpServer.McpEndpointUrl,
				Token: token,
				StartedAtUtc: DateTimeOffset.UtcNow,
				AppVersion: "test",
				Platform: "test" ), tempDir );

			using var standalone = HackPdmMcpServerFactory.Create(
				mode: McpServerMode.Auto,
				dataDirOverride: tempDir );

			// 1. Calling a UI tool auto-discovers and proxies to the embedded GUI bridge!
			string? uiCallResp = await standalone.Server.HandleMessageAsync( JsonSerializer.Serialize( new
			{
				jsonrpc = "2.0",
				id = 1,
				method = "tools/call",
				@params = new { name = "ui_get_state", arguments = new { } },
			} ) );
			using( JsonDocument doc = JsonDocument.Parse( uiCallResp! ) )
			{
				JsonElement result = doc.RootElement.GetProperty( "result" );
				Assert.False( result.TryGetProperty( "isError", out JsonElement err ) && err.GetBoolean() );
				Assert.Contains( "FakeMainPage", result.GetProperty( "content" )[ 0 ].GetProperty( "text" ).GetString()! );
			}
			Assert.Equal( "proxied_to_live_gui", standalone.Bridge.ActiveModeLabel );

			// 2. Detaching switches cleanly back to headless
			string? detachResp = await standalone.Server.HandleMessageAsync( JsonSerializer.Serialize( new
			{
				jsonrpc = "2.0",
				id = 2,
				method = "tools/call",
				@params = new { name = "client_detach", arguments = new { } },
			} ) );
			Assert.NotNull( detachResp );
			Assert.Equal( "headless", standalone.Bridge.ActiveModeLabel );

			// 3. Calling ui_get_state while explicitly detached reports error (does not auto-reattach until client_attach)
			string? detachedUiResp = await standalone.Server.HandleMessageAsync( JsonSerializer.Serialize( new
			{
				jsonrpc = "2.0",
				id = 3,
				method = "tools/call",
				@params = new { name = "ui_get_state", arguments = new { } },
			} ) );
			using( JsonDocument doc = JsonDocument.Parse( detachedUiResp! ) )
				Assert.True( doc.RootElement.GetProperty( "result" ).GetProperty( "isError" ).GetBoolean() );

			// 4. Explicit client_attach re-attaches
			string? attachResp = await standalone.Server.HandleMessageAsync( JsonSerializer.Serialize( new
			{
				jsonrpc = "2.0",
				id = 4,
				method = "tools/call",
				@params = new { name = "client_attach", arguments = new { } },
			} ) );
			Assert.NotNull( attachResp );
			Assert.Equal( "proxied_to_live_gui", standalone.Bridge.ActiveModeLabel );

			// 5. Simulate GUI shutting down -> non-UI tool gracefully falls back to headless in Auto mode
			httpServer.Dispose();
			McpBridgeDiscovery.RemoveDiscoveryFile( Environment.ProcessId, tempDir );

			string? fallbackResp = await standalone.Server.HandleMessageAsync( JsonSerializer.Serialize( new
			{
				jsonrpc = "2.0",
				id = 5,
				method = "tools/call",
				@params = new { name = "odoo_list_models", arguments = new { } },
			} ) );
			using( JsonDocument doc = JsonDocument.Parse( fallbackResp! ) )
			{
				JsonElement result = doc.RootElement.GetProperty( "result" );
				Assert.False( result.TryGetProperty( "isError", out JsonElement err ) && err.GetBoolean() );
				Assert.Contains( "hp.entry", result.GetProperty( "content" )[ 0 ].GetProperty( "text" ).GetString()! );
			}
			Assert.Equal( "headless", standalone.Bridge.ActiveModeLabel );
		}
		finally
		{
			try { Directory.Delete( tempDir, true ); } catch { }
		}
	}

	[Fact]
	public async Task BridgeCoordinator_LiveOnlyMode_ReturnsErrorWithoutLiveBridge()
	{
		string tempDir = Path.Combine( Path.GetTempPath(), $"hackpdm-mcp-liveonly-{Guid.NewGuid():N}" );
		Directory.CreateDirectory( tempDir );
		try
		{
			using var liveOnly = HackPdmMcpServerFactory.Create(
				mode: McpServerMode.LiveOnly,
				dataDirOverride: tempDir );

			string? resp = await liveOnly.Server.HandleMessageAsync( JsonSerializer.Serialize( new
			{
				jsonrpc = "2.0",
				id = 1,
				method = "tools/call",
				@params = new { name = "odoo_list_models", arguments = new { } },
			} ) );
			using JsonDocument doc = JsonDocument.Parse( resp! );
			Assert.True( doc.RootElement.GetProperty( "result" ).GetProperty( "isError" ).GetBoolean() );
			Assert.Contains( "live-only", doc.RootElement.GetProperty( "result" ).GetProperty( "content" )[ 0 ].GetProperty( "text" ).GetString()! );
		}
		finally
		{
			try { Directory.Delete( tempDir, true ); } catch { }
		}
	}
}

using System.Text.Json;

using HackPDM.Mcp;
using HackPDM.Mcp.Bridge;
using HackPDM.Mcp.Protocol;

namespace HackPDM.Tests.Mcp;

public sealed class McpProtocolTests
{
	private static async Task<JsonDocument> SendAsync( McpServer server, object payload )
	{
		string json = JsonSerializer.Serialize( payload );
		string? raw = await server.HandleMessageAsync( json );
		Assert.NotNull( raw );
		return JsonDocument.Parse( raw! );
	}

	[Fact]
	public async Task Initialize_NegotiatesSupportedVersions_AndAdvertisesCapabilities()
	{
		using var bundle = HackPdmMcpServerFactory.Create( McpServerMode.Headless );

		foreach( string version in new[] { "2025-06-18", "2025-03-26", "2024-11-05" } )
		{
			using JsonDocument resp = await SendAsync( bundle.Server, new
			{
				jsonrpc = "2.0",
				id = 1,
				method = "initialize",
				@params = new { protocolVersion = version, clientInfo = new { name = "test", version = "1.0" } },
			} );

			JsonElement result = resp.RootElement.GetProperty( "result" );
			Assert.Equal( version, result.GetProperty( "protocolVersion" ).GetString() );
			Assert.Equal( HackPdmMcpServerFactory.ServerName, result.GetProperty( "serverInfo" ).GetProperty( "name" ).GetString() );
			Assert.True( result.GetProperty( "capabilities" ).TryGetProperty( "tools", out _ ) );
			Assert.True( result.GetProperty( "capabilities" ).TryGetProperty( "resources", out _ ) );
			Assert.True( result.GetProperty( "capabilities" ).TryGetProperty( "prompts", out _ ) );
			Assert.False( string.IsNullOrWhiteSpace( result.GetProperty( "instructions" ).GetString() ) );
		}

		// Unknown protocol version falls back to latest supported version
		using JsonDocument fallback = await SendAsync( bundle.Server, new
		{
			jsonrpc = "2.0",
			id = 2,
			method = "initialize",
			@params = new { protocolVersion = "2099-01-01" },
		} );
		Assert.Equal( "2025-06-18", fallback.RootElement.GetProperty( "result" ).GetProperty( "protocolVersion" ).GetString() );
	}

	[Fact]
	public async Task Notifications_ReturnNull_AndPingReturnsEmptyObject()
	{
		using var bundle = HackPdmMcpServerFactory.Create( McpServerMode.Headless );

		string? notifResp = await bundle.Server.HandleMessageAsync(
			JsonSerializer.Serialize( new { jsonrpc = "2.0", method = "notifications/initialized" } ) );
		Assert.Null( notifResp );

		using JsonDocument pingResp = await SendAsync( bundle.Server, new { jsonrpc = "2.0", id = "p1", method = "ping" } );
		Assert.Equal( "p1", pingResp.RootElement.GetProperty( "id" ).GetString() );
		Assert.Equal( JsonValueKind.Object, pingResp.RootElement.GetProperty( "result" ).ValueKind );
	}

	[Fact]
	public async Task ToolsList_AllRegisteredToolsHaveUniqueNamesAndValidObjectSchemas()
	{
		using var bundle = HackPdmMcpServerFactory.Create( McpServerMode.Headless );
		using JsonDocument resp = await SendAsync( bundle.Server, new { jsonrpc = "2.0", id = 10, method = "tools/list" } );

		JsonElement tools = resp.RootElement.GetProperty( "result" ).GetProperty( "tools" );
		Assert.Equal( JsonValueKind.Array, tools.ValueKind );
		Assert.True( tools.GetArrayLength() >= 65, $"Expected at least 65 tools, got {tools.GetArrayLength()}" );

		HashSet<string> seen = new( StringComparer.Ordinal );
		foreach( JsonElement tool in tools.EnumerateArray() )
		{
			string? name = tool.GetProperty( "name" ).GetString();
			string? description = tool.GetProperty( "description" ).GetString();
			JsonElement schema = tool.GetProperty( "inputSchema" );

			Assert.False( string.IsNullOrWhiteSpace( name ) );
			Assert.False( string.IsNullOrWhiteSpace( description ), $"Tool '{name}' missing description" );
			Assert.True( seen.Add( name! ), $"Duplicate tool name '{name}'" );
			Assert.Equal( "object", schema.GetProperty( "type" ).GetString() );
		}

		// Verify key representative tools across all domains are present
		string[] expectedTools =
		[
			"client_status", "client_attach", "client_detach", "client_launch",
			"session_get_info", "session_configure", "session_login", "session_logout",
			"metadata_get_lookups", "directory_get_tree", "directory_get_entries",
			"entry_get_details", "version_get_preview", "dependency_graph_load",
			"pdm_checkout", "pdm_uncheckout", "pdm_commit_preview", "pdm_commit", "pdm_get_latest",
			"search_entries", "odoo_list_models", "odoo_model_schema", "odoo_search_read", "odoo_execute",
			"sw_inspect_file", "local_list_files", "local_read_file", "local_write_file",
			"settings_get", "settings_set", "diagnostics_get",
			"ui_get_state", "ui_list_windows", "ui_visual_tree", "ui_find_controls",
			"ui_get_control", "ui_click_control", "ui_set_control_value", "ui_invoke_command",
			"ui_navigate", "ui_file_manager_select_folder", "ui_file_manager_select_entries",
			"ui_file_manager_control", "ui_dependency_graph_control", "ui_search_control",
			"ui_status_panel", "ui_theme_control", "ui_dialog_respond", "ui_screenshot",
		];
		foreach( string expected in expectedTools )
			Assert.Contains( expected, seen );
	}

	[Fact]
	public async Task ResourcesAndTemplates_ListAndReadModelSchema()
	{
		using var bundle = HackPdmMcpServerFactory.Create( McpServerMode.Headless );

		using JsonDocument listResp = await SendAsync( bundle.Server, new { jsonrpc = "2.0", id = 20, method = "resources/list" } );
		JsonElement resources = listResp.RootElement.GetProperty( "result" ).GetProperty( "resources" );
		Assert.True( resources.GetArrayLength() >= 9 );

		using JsonDocument tmplResp = await SendAsync( bundle.Server, new { jsonrpc = "2.0", id = 21, method = "resources/templates/list" } );
		JsonElement templates = tmplResp.RootElement.GetProperty( "result" ).GetProperty( "resourceTemplates" );
		Assert.True( templates.GetArrayLength() >= 3 );

		// Read static models resource
		using JsonDocument modelsResp = await SendAsync( bundle.Server, new
		{
			jsonrpc = "2.0",
			id = 22,
			method = "resources/read",
			@params = new { uri = "hackpdm://models" },
		} );
		string modelsText = modelsResp.RootElement.GetProperty( "result" ).GetProperty( "contents" )[ 0 ].GetProperty( "text" ).GetString()!;
		Assert.Contains( "hp.entry", modelsText );
		Assert.Contains( "hp.directory", modelsText );

		// Read template resource hackpdm://model/{modelName}/schema
		using JsonDocument schemaResp = await SendAsync( bundle.Server, new
		{
			jsonrpc = "2.0",
			id = 23,
			method = "resources/read",
			@params = new { uri = "hackpdm://model/hp.entry/schema" },
		} );
		string schemaText = schemaResp.RootElement.GetProperty( "result" ).GetProperty( "contents" )[ 0 ].GetProperty( "text" ).GetString()!;
		Assert.Contains( "latest_version_id", schemaText );
	}

	[Fact]
	public async Task Prompts_ListAndGetAllPrompts()
	{
		using var bundle = HackPdmMcpServerFactory.Create( McpServerMode.Headless );

		using JsonDocument listResp = await SendAsync( bundle.Server, new { jsonrpc = "2.0", id = 30, method = "prompts/list" } );
		JsonElement prompts = listResp.RootElement.GetProperty( "result" ).GetProperty( "prompts" );
		Assert.True( prompts.GetArrayLength() >= 4 );

		using JsonDocument getResp = await SendAsync( bundle.Server, new
		{
			jsonrpc = "2.0",
			id = 31,
			method = "prompts/get",
			@params = new { name = "explore-vault", arguments = new { folder = "root/Projects" } },
		} );
		JsonElement messages = getResp.RootElement.GetProperty( "result" ).GetProperty( "messages" );
		Assert.True( messages.GetArrayLength() >= 1 );
		string text = messages[ 0 ].GetProperty( "content" ).GetProperty( "text" ).GetString()!;
		Assert.Contains( "root/Projects", text );
	}

	[Fact]
	public async Task ErrorHandling_ParseError_UnknownMethod_UnknownTool_BatchRequests()
	{
		using var bundle = HackPdmMcpServerFactory.Create( McpServerMode.Headless );

		// Malformed JSON -> -32700
		string? badJson = await bundle.Server.HandleMessageAsync( "{not valid json" );
		using( JsonDocument doc = JsonDocument.Parse( badJson! ) )
			Assert.Equal( -32700, doc.RootElement.GetProperty( "error" ).GetProperty( "code" ).GetInt32() );

		// Unknown method -> -32601
		using( JsonDocument doc = await SendAsync( bundle.Server, new { jsonrpc = "2.0", id = 40, method = "nonexistent/method" } ) )
			Assert.Equal( -32601, doc.RootElement.GetProperty( "error" ).GetProperty( "code" ).GetInt32() );

		// Unknown tool -> -32602
		using( JsonDocument doc = await SendAsync( bundle.Server, new { jsonrpc = "2.0", id = 41, method = "tools/call", @params = new { name = "no_such_tool" } } ) )
			Assert.Equal( -32602, doc.RootElement.GetProperty( "error" ).GetProperty( "code" ).GetInt32() );

		// Logging level
		using( JsonDocument doc = await SendAsync( bundle.Server, new { jsonrpc = "2.0", id = 42, method = "logging/setLevel", @params = new { level = "debug" } } ) )
			Assert.True( doc.RootElement.TryGetProperty( "result", out _ ) );

		// Batch request (one notification + two requests -> array of 2 responses)
		string batchPayload = JsonSerializer.Serialize( new object[]
		{
			new { jsonrpc = "2.0", method = "notifications/initialized" },
			new { jsonrpc = "2.0", id = 100, method = "ping" },
			new { jsonrpc = "2.0", id = 101, method = "ping" },
		} );
		string? batchRaw = await bundle.Server.HandleMessageAsync( batchPayload );
		Assert.NotNull( batchRaw );
		using JsonDocument batchDoc = JsonDocument.Parse( batchRaw! );
		Assert.Equal( JsonValueKind.Array, batchDoc.RootElement.ValueKind );
		Assert.Equal( 2, batchDoc.RootElement.GetArrayLength() );
	}
}

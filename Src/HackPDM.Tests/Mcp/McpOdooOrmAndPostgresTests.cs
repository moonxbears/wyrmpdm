using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

using HackPDM.Core.Configuration;
using HackPDM.Infrastructure.Odoo;
using HackPDM.Mcp;
using HackPDM.Mcp.Bridge;
using HackPDM.Mcp.Database;
using HackPDM.Mcp.Protocol;
using HackPDM.Mcp.Security;
using HackPDM.Tests.Characterization;

using Xunit;

namespace HackPDM.Tests.Mcp;

[Collection( SharedStateCollection.Name )]
public sealed class McpOdooOrmAndPostgresTests : IDisposable
{
	private readonly string _tempRoot;
	private readonly string _dataDir;
	private readonly CoreSettings _coreSettings;

	public McpOdooOrmAndPostgresTests()
	{
		_tempRoot = Path.Combine( Path.GetTempPath(), "hackpdm_mcp_ormpg_" + Guid.NewGuid().ToString( "N" )[ ..8 ] );
		_dataDir = Path.Combine( _tempRoot, "mcpdata" );
		Directory.CreateDirectory( _dataDir );

		string settingsPath = Path.Combine( _tempRoot, "settings.json" );
		_coreSettings = new CoreSettings( new JsonFileSettingsProvider( settingsPath ) );
		_ = new OdooDefaults( _coreSettings );
	}

	public void Dispose()
	{
		OdooDefaults.Instance.OdooId = 0;

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
	public void PostgresSqlClassifier_AllowsStrictlyReadOnlyAndBlocksWritesMultiStatementsAndCteSmuggling()
	{
		// Safe read-only queries (including tricky strings, comments, dollar-quoting, CTE SELECT, EXPLAIN)
		Assert.True( PostgresSqlClassifier.Analyze( "SELECT id, name FROM hp_entry WHERE name = '; DROP TABLE hp_entry; --'" ).IsStrictlyReadOnly );
		Assert.True( PostgresSqlClassifier.Analyze( "/* nested /* comment */ */ -- line\nWITH cte AS (SELECT id FROM hp_entry) SELECT * FROM cte;" ).IsStrictlyReadOnly );
		Assert.True( PostgresSqlClassifier.Analyze( "SELECT $$DELETE FROM hp_entry$$ AS literal_text" ).IsStrictlyReadOnly );
		Assert.True( PostgresSqlClassifier.Analyze( "EXPLAIN SELECT * FROM hp_version LIMIT 5" ).IsStrictlyReadOnly );
		Assert.True( PostgresSqlClassifier.Analyze( "SHOW server_version" ).IsStrictlyReadOnly );

		// Mutating, DDL, multi-statement, CTE write smuggling, SELECT INTO, and EXPLAIN ANALYZE write MUST be rejected
		string[] unsafeQueries =
		[
			"SELECT 1; DROP TABLE hp_entry",
			"WITH d AS (DELETE FROM hp_entry RETURNING *) SELECT * FROM d",
			"WITH u AS (UPDATE hp_entry SET deleted = true RETURNING id) SELECT * FROM u",
			"SELECT * INTO temp_copy FROM hp_entry",
			"EXPLAIN ANALYZE DELETE FROM hp_entry WHERE id = 1",
			"INSERT INTO hp_entry (name) VALUES ('x')",
			"UPDATE hp_entry SET name = 'y' WHERE id = 1",
			"DELETE FROM hp_entry WHERE id = 1",
			"DROP TABLE hp_entry",
			"TRUNCATE hp_entry",
			"ALTER TABLE hp_entry ADD COLUMN x int",
			"GRANT ALL ON hp_entry TO public",
		];
		foreach( string sql in unsafeQueries )
		{
			SqlClassificationResult res = PostgresSqlClassifier.Analyze( sql );
			Assert.False( res.IsStrictlyReadOnly, $"Expected '{sql}' to be rejected as non-read-only." );
		}
	}

	[Fact]
	public async Task OdooPostgresService_ConfigRedactsPassword_InheritsOdooDefaults_AndEnforcesWriteGuards()
	{
		OdooDefaults.Instance.OdooAddress = "https://pdm-db.internal:8069";
		OdooDefaults.Instance.OdooDb = "hackpdm_prod";

		string cfgFile = Path.Combine( _tempRoot, "mcp-postgres.json" );
		OdooPostgresService pg = new( cfgFile, persistChanges: true );

		pg.Configure(
			host: null,
			port: 5432,
			database: null,
			username: "odoo_ro",
			password: "super-secret-pg-password",
			clearPassword: false,
			sslMode: "Require",
			connectionString: null,
			clearConnectionString: false,
			allowWriteQueries: false,
			statementTimeoutSeconds: 15,
			maxRows: 250 );

		object status = await pg.GetStatusAsync( testConnection: false );
		string json = JsonSerializer.Serialize( status );
		Assert.DoesNotContain( "super-secret-pg-password", json );
		Assert.Contains( "\"host\":\"pdm-db.internal\"", json );
		Assert.Contains( "\"database\":\"hackpdm_prod\"", json );
		Assert.Contains( "\"username\":\"odoo_ro\"", json );
		Assert.Contains( "\"hasPassword\":true", json );
		Assert.Contains( "\"allowWriteQueries\":false", json );

		// Read-only query rejects modifying SQL before even touching the network
		await Assert.ThrowsAsync<InvalidOperationException>( () =>
			pg.QueryReadOnlyAsync( "DELETE FROM hp_entry WHERE id = 1", null, null, null ) );

		// Write query blocked while allowWriteQueries=false (unless dryRun=true)
		await Assert.ThrowsAsync<InvalidOperationException>( () =>
			pg.ExecuteWriteAsync( "UPDATE hp_entry SET name = 'a' WHERE id = 1", null, dryRun: false, confirm: true, null, null ) );

		// Enable writes, still blocked without confirm=true
		pg.Configure( null, null, null, null, null, false, null, null, false, allowWriteQueries: true, null, null );
		await Assert.ThrowsAsync<InvalidOperationException>( () =>
			pg.ExecuteWriteAsync( "UPDATE hp_entry SET name = 'a' WHERE id = 1", null, dryRun: false, confirm: false, null, null ) );
	}

	[Fact]
	public async Task OdooOrmJsonRpcTools_VerifyPermissions_RunReadGroup_AndExecuteKwModelMethods()
	{
		using MockOdooJsonRpcServer mockOdoo = new();
		OdooDefaults.Instance.OdooAddress = "127.0.0.1";
		OdooDefaults.Instance.OdooPort = mockOdoo.Port.ToString();
		OdooDefaults.Instance.OdooDb = "test_db";
		OdooDefaults.Instance.OdooUser = "engineer";
		OdooDefaults.Instance.OdooPass = "secret";
		OdooDefaults.Instance.OdooId = 7;
		OdooDefaults.Instance.OdooUseHttps = false;
		OdooDefaults.Instance.OdooUrl = $"http://127.0.0.1:{mockOdoo.Port}";

		using var bundle = HackPdmMcpServerFactory.Create(
			McpServerMode.Headless,
			dataDirOverride: _dataDir,
			defaultPermissionPresetWhenUnconfigured: "auto" );

		// 1. Check model access across specific models (hp.entry has read/write/create; mrp.bom is read-only)
		McpToolResult accessRes = await bundle.Server.CallToolAsync( "odoo_check_model_access", JsonArgs( new
		{
			models = new[] { "hp.entry", "mrp.bom" },
		} ) );
		Assert.NotEqual( true, accessRes.IsError );
		string accessJson = accessRes.Content[ 0 ].Text!;
		Assert.Contains( "\"accessibleCount\": 2", accessJson );
		Assert.Contains( "\"model\": \"hp.entry\"", accessJson );
		Assert.Contains( "\"model\": \"mrp.bom\"", accessJson );

		// 2. Discover accessible models via ir.model
		McpToolResult discoverModelsRes = await bundle.Server.CallToolAsync( "odoo_check_model_access", JsonArgs( new
		{
			modelFilter = "mrp.",
			discoverAccessibleModels = true,
		} ) );
		Assert.NotEqual( true, discoverModelsRes.IsError );
		Assert.True( discoverModelsRes.Content[ 0 ].Text!.Contains( "mrp.bom" ), $"Expected mrp.bom. Calls=[{string.Join( ", ", mockOdoo.RecordedCalls )}], Access={accessJson}, Discover={discoverModelsRes.Content[ 0 ].Text}" );

		// 3. Discover model API methods & permissions for mrp.bom
		McpToolResult methodsRes = await bundle.Server.CallToolAsync( "odoo_discover_model_methods", JsonArgs( new
		{
			model = "mrp.bom",
		} ) );
		Assert.NotEqual( true, methodsRes.IsError );
		string methodsJson = methodsRes.Content[ 0 ].Text!;
		Assert.Contains( "\"method\": \"read_group\"", methodsJson );
		Assert.Contains( "\"deniedMethods\"", methodsJson );
		Assert.Contains( "Recalculate BOM Cost", methodsJson );

		// 4. Execute ORM read_group via object.execute_kw
		McpToolResult groupRes = await bundle.Server.CallToolAsync( "odoo_read_group", JsonArgs( new
		{
			model = "hp.entry",
			domain = new object[] { new object[] { "deleted", "=", false } },
			fields = new[] { "directory_id", "size:sum" },
			groupby = new[] { "directory_id" },
			orderby = "size desc",
			limit = 10,
			context = new { active_test = false },
		} ) );
		Assert.NotEqual( true, groupRes.IsError );
		Assert.Contains( "execute_kw:hp.entry.read_group", mockOdoo.RecordedCalls );
		Assert.Contains( "\"groupCount\": 2", groupRes.Content[ 0 ].Text! );

		// 5. Execute permitted read-only model method via odoo_call_model_method
		McpToolResult nameSearchRes = await bundle.Server.CallToolAsync( "odoo_call_model_method", JsonArgs( new
		{
			model = "mrp.bom",
			method = "name_search",
			kwargs = new { name = "Drive", limit = 5 },
			context = new { lang = "en_US" },
		} ) );
		Assert.NotEqual( true, nameSearchRes.IsError );
		Assert.Contains( "execute_kw:mrp.bom.name_search", mockOdoo.RecordedCalls );

		// 6. Execute permitted recordset write method on hp.entry (ids + kwargs + context)
		McpToolResult actionRes = await bundle.Server.CallToolAsync( "odoo_call_model_method", JsonArgs( new
		{
			model = "hp.entry",
			method = "action_recompute_checksum",
			ids = new[] { 101, 102 },
			kwargs = new { force = true },
		} ) );
		Assert.NotEqual( true, actionRes.IsError );
		Assert.Contains( "execute_kw:hp.entry.action_recompute_checksum", mockOdoo.RecordedCalls );

		// 7. Calling write/unlink method on a model where Odoo check_access_rights denies it MUST fail BEFORE executing!
		McpToolResult deniedRes = await bundle.Server.CallToolAsync( "odoo_call_model_method", JsonArgs( new
		{
			model = "mrp.bom",
			method = "unlink",
			ids = new[] { 55 },
		} ) );
		Assert.True( deniedRes.IsError );
		string deniedError = JsonDocument.Parse( deniedRes.Content[ 0 ].Text! ).RootElement.GetProperty( "error" ).GetString()!;
		Assert.Contains( "lacks 'unlink' permission on model 'mrp.bom'", deniedError );
		Assert.DoesNotContain( "execute_kw:mrp.bom.unlink", mockOdoo.RecordedCalls );

		// 8. Private '_'-prefixed method blocked by default
		McpToolResult privRes = await bundle.Server.CallToolAsync( "odoo_call_model_method", JsonArgs( new
		{
			model = "hp.entry",
			method = "_compute_secret",
		} ) );
		Assert.True( privRes.IsError );
		Assert.Contains( "private Odoo method", privRes.Content[ 0 ].Text! );
	}

	[Fact]
	public void PermissionCatalog_ClassifiesAllOrmAndPostgresToolsAccuratelyAcrossModes()
	{
		string permFile = Path.Combine( _tempRoot, "mcp-permissions.json" );
		McpPermissionManager perms = new( permFile, defaultPresetWhenUnconfigured: "read-only", persistChanges: false );

		// Read-only mode: read-only ORM + PostgreSQL SELECT allowed; mutating methods & postgres writes blocked
		Assert.True( perms.Evaluate( "odoo_check_model_access" ).Allowed );
		Assert.True( perms.Evaluate( "odoo_discover_model_methods" ).Allowed );
		Assert.True( perms.Evaluate( "odoo_read_group" ).Allowed );
		Assert.True( perms.Evaluate( "odoo_call_model_method", JsonArgs( new { model = "mrp.bom", method = "name_search" } ) ).Allowed );
		Assert.False( perms.Evaluate( "odoo_call_model_method", JsonArgs( new { model = "mrp.bom", method = "action_confirm" } ) ).Allowed );
		Assert.True( perms.Evaluate( "postgres_status" ).Allowed );
		Assert.True( perms.Evaluate( "postgres_list_tables" ).Allowed );
		Assert.True( perms.Evaluate( "postgres_describe_table" ).Allowed );
		Assert.True( perms.Evaluate( "postgres_query" ).Allowed );
		Assert.False( perms.Evaluate( "postgres_configure" ).Allowed );
		Assert.False( perms.Evaluate( "postgres_execute", JsonArgs( new { sql = "UPDATE x SET y=1", dryRun = true } ) ).Allowed );

		// Local-only mode: blocks ALL remote Odoo RPC & live PostgreSQL queries, allows local redacted config status
		perms.SelectProfile( "local-only" );
		Assert.False( perms.Evaluate( "odoo_check_model_access" ).Allowed );
		Assert.False( perms.Evaluate( "odoo_read_group" ).Allowed );
		Assert.False( perms.Evaluate( "odoo_call_model_method", JsonArgs( new { model = "hp.entry", method = "read" } ) ).Allowed );
		Assert.False( perms.Evaluate( "postgres_query" ).Allowed );
		Assert.False( perms.Evaluate( "postgres_list_tables" ).Allowed );
		Assert.False( perms.Evaluate( "postgres_status", JsonArgs( new { testConnection = true } ) ).Allowed );
		Assert.True( perms.Evaluate( "postgres_status", JsonArgs( new { testConnection = false } ) ).Allowed );

		// Protected GUI controls for PostgreSQL credentials cannot be clicked/edited by AI even in auto mode
		perms.SelectProfile( "auto" );
		Assert.False( perms.Evaluate( "ui_click_control", JsonArgs( new { name = "McpPostgres_SaveAndTestButton" } ) ).Allowed );
		Assert.False( perms.Evaluate( "ui_set_control_value", JsonArgs( new { name = "McpPostgres_PasswordBox" } ) ).Allowed );
	}

	private static JsonElement JsonArgs( object obj )
	{
		using JsonDocument doc = JsonDocument.Parse( JsonSerializer.Serialize( obj ) );
		return doc.RootElement.Clone();
	}

	/// <summary>Minimal loopback JSON-RPC Odoo mock supporting object.execute_kw for ORM permission & method tests.</summary>
	private sealed class MockOdooJsonRpcServer : IDisposable
	{
		private readonly HttpListener _listener;
		private readonly CancellationTokenSource _cts = new();
		public int Port { get; }
		public List<string> RecordedCalls { get; } = [];

		public MockOdooJsonRpcServer()
		{
			using( TcpListener probe = new( IPAddress.Loopback, 0 ) )
			{
				probe.Start();
				Port = ( (IPEndPoint)probe.LocalEndpoint ).Port;
			}
			_listener = new HttpListener();
			_listener.Prefixes.Add( $"http://127.0.0.1:{Port}/" );
			_listener.Start();
			_ = Task.Run( ServeLoopAsync );
		}

		private async Task ServeLoopAsync()
		{
			while( !_cts.IsCancellationRequested )
			{
				HttpListenerContext ctx;
				try { ctx = await _listener.GetContextAsync(); }
				catch { break; }

				try
				{
					using StreamReader sr = new( ctx.Request.InputStream, Encoding.UTF8 );
					string body = await sr.ReadToEndAsync();
					using JsonDocument doc = JsonDocument.Parse( body );
					JsonElement reqParams = doc.RootElement.GetProperty( "params" );
					string rpcMethod = reqParams.GetProperty( "method" ).GetString() ?? "";
					JsonElement args = reqParams.GetProperty( "args" );

					object? result = HandleRpc( rpcMethod, args );
					string respJson = JsonSerializer.Serialize( new { jsonrpc = "2.0", id = "1", result } );
					byte[] bytes = Encoding.UTF8.GetBytes( respJson );
					ctx.Response.ContentType = "application/json";
					ctx.Response.ContentLength64 = bytes.Length;
					await ctx.Response.OutputStream.WriteAsync( bytes );
					ctx.Response.Close();
				}
				catch
				{
					try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { }
				}
			}
		}

		private object? HandleRpc( string rpcMethod, JsonElement args )
		{
			if( rpcMethod is not ( "execute_kw" or "execute" ) || args.GetArrayLength() < 5 )
				return true;

			string model = args[ 3 ].GetString() ?? "";
			string method = args[ 4 ].GetString() ?? "";
			RecordedCalls.Add( $"{rpcMethod}:{model}.{method}" );

			if( method == "check_access_rights" )
			{
				string op = rpcMethod == "execute_kw" ? args[ 5 ][ 0 ].GetString() ?? "" : args[ 5 ].GetString() ?? "";
				return model switch
				{
					"hp.entry" => op is "read" or "write" or "create",
					"mrp.bom" => op == "read",
					_ => false,
				};
			}

			if( model == "ir.model" && method == "search_read" )
			{
				return new object[]
				{
					new { model = "mrp.bom", name = "Bill of Material" },
					new { model = "mrp.secret", name = "Restricted Secret Model" },
				};
			}

			if( model == "ir.actions.server" && method == "search_read" )
			{
				return new object[]
				{
					new { id = 42, name = "Recalculate BOM Cost", state = "code", model_name = model },
				};
			}

			if( method == "fields_get" )
			{
				return new Dictionary<string, object>
				{
					[ "id" ] = new { @string = "ID", type = "integer", @readonly = true },
					[ "name" ] = new { @string = "Name", type = "char", required = true },
				};
			}

			if( method == "read_group" )
			{
				return new object[]
				{
					new { directory_id = new object[] { 10, "Projects" }, directory_id_count = 12, size = 409600 },
					new { directory_id = new object[] { 11, "Hardware" }, directory_id_count = 5, size = 102400 },
				};
			}

			if( method == "name_search" )
			{
				return new object[]
				{
					new object[] { 1, "Drive_Main BOM" },
				};
			}

			if( method == "action_recompute_checksum" )
				return new { recomputed = true, count = 2 };

			return true;
		}

		public void Dispose()
		{
			_cts.Cancel();
			try { _listener.Stop(); _listener.Close(); } catch { }
			_cts.Dispose();
		}
	}
}

using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

using HackPDM.Core.Configuration;
using HackPDM.Core.Hack;
using HackPDM.Domain.OdooModels;
using HackPDM.Infrastructure.Odoo;
using HackPDM.Mcp;
using HackPDM.Mcp.Bridge;
using HackPDM.Mcp.Protocol;
using HackPDM.Mcp.Services;
using HackPDM.Shared.GlobalData;
using HackPDM.Tests.Characterization;

namespace HackPDM.Tests.Mcp;

/// <summary>Fake live UI bridge used to verify every <c>ui_*</c> tool and <c>hackpdm://ui/*</c> resource without WinUI.</summary>
internal sealed class FakeUiBridge : IHackPdmUiBridge
{
	public List<string> Calls { get; } = [];
	public string AppConfigJson { get; set; } = """{"TestKey":true}""";

	public Task<object> GetUiStateAsync( CancellationToken cancellationToken = default )
	{
		Calls.Add( "GetUiStateAsync" );
		return Task.FromResult<object>( new { activePage = "FakeMainPage", isLoggedIn = true } );
	}

	public Task<object> ListWindowsAsync( CancellationToken cancellationToken = default )
	{
		Calls.Add( "ListWindowsAsync" );
		return Task.FromResult<object>( new { windows = new[] { new { id = "main", title = "Fake Window" } } } );
	}

	public Task<object> GetVisualTreeAsync( string? windowId, string? rootName, int maxDepth, bool includeInvisible, bool interactiveOnly, CancellationToken cancellationToken = default )
	{
		Calls.Add( $"GetVisualTreeAsync:{windowId}:{rootName}:{maxDepth}" );
		return Task.FromResult<object>( new { windowId = windowId ?? "main", root = new { type = "Grid", name = rootName ?? "Root" } } );
	}

	public Task<object> FindControlsAsync( string? windowId, string? name, string? typeName, string? text, string? automationId, bool visibleOnly, bool interactiveOnly, int maxResults, CancellationToken cancellationToken = default )
	{
		Calls.Add( $"FindControlsAsync:{name}:{typeName}:{text}" );
		return Task.FromResult<object>( new { count = 1, matches = new[] { new { name = name ?? "LoginButton", type = typeName ?? "Button" } } } );
	}

	public Task<object> GetControlAsync( string? windowId, string? name, string? automationId, string? path, CancellationToken cancellationToken = default )
	{
		Calls.Add( $"GetControlAsync:{name}:{path}" );
		return Task.FromResult<object>( new { name = name ?? "Target", path = path ?? "0/1", isEnabled = true } );
	}

	public Task<object> ClickControlAsync( string? windowId, string? name, string? automationId, string? path, string? text, CancellationToken cancellationToken = default )
	{
		Calls.Add( $"ClickControlAsync:{name}:{text}" );
		return Task.FromResult<object>( new { clicked = true, name, text } );
	}

	public Task<object> SetControlValueAsync( string? windowId, string? name, string? automationId, string? path, JsonElement? value, int? selectedIndex, bool? isChecked, CancellationToken cancellationToken = default )
	{
		Calls.Add( $"SetControlValueAsync:{name}:{selectedIndex}:{isChecked}" );
		return Task.FromResult<object>( new { ok = true, name, selectedIndex, isChecked } );
	}

	public Task<object> InvokeCommandAsync( string? target, string? commandName, JsonElement? parameter, bool listCommands, CancellationToken cancellationToken = default )
	{
		Calls.Add( $"InvokeCommandAsync:{target}:{commandName}:{listCommands}" );
		return Task.FromResult<object>( new { ok = true, target, commandName, listCommands } );
	}

	public Task<object> NavigateAsync( string? page, bool goBack, string? statusAction, string? searchAction, bool openFileTypes, string? settingsSection, CancellationToken cancellationToken = default )
	{
		Calls.Add( $"NavigateAsync:{page}:{goBack}:{statusAction}:{searchAction}" );
		return Task.FromResult<object>( new { ok = true, activePage = page ?? "HackFileManager" } );
	}

	public Task<object> FileManagerSelectFolderAsync( string? treePath, int? directoryId, bool? expand, bool waitForEntries, int timeoutMs, CancellationToken cancellationToken = default )
	{
		Calls.Add( $"FileManagerSelectFolderAsync:{treePath}:{directoryId}" );
		return Task.FromResult<object>( new { ok = true, treePath, directoryId } );
	}

	public Task<object> FileManagerSelectEntriesAsync( IReadOnlyList<string>? names, IReadOnlyList<int>? entryIds, bool clear, bool waitForDetails, int timeoutMs, CancellationToken cancellationToken = default )
	{
		Calls.Add( $"FileManagerSelectEntriesAsync:{names?.Count ?? 0}:{entryIds?.Count ?? 0}:{clear}" );
		return Task.FromResult<object>( new { ok = true, selectedCount = ( names?.Count ?? 0 ) + ( entryIds?.Count ?? 0 ) } );
	}

	public Task<object> FileManagerControlAsync( JsonElement? args, CancellationToken cancellationToken = default )
	{
		Calls.Add( "FileManagerControlAsync" );
		return Task.FromResult<object>( new { ok = true, controlled = "file_manager" } );
	}

	public Task<object> DependencyGraphControlAsync( JsonElement? args, CancellationToken cancellationToken = default )
	{
		Calls.Add( "DependencyGraphControlAsync" );
		return Task.FromResult<object>( new { ok = true, controlled = "dependency_graph" } );
	}

	public Task<object> SearchControlAsync( JsonElement? args, CancellationToken cancellationToken = default )
	{
		Calls.Add( "SearchControlAsync" );
		return Task.FromResult<object>( new { ok = true, controlled = "search" } );
	}

	public Task<object> StatusPanelControlAsync( JsonElement? args, CancellationToken cancellationToken = default )
	{
		Calls.Add( "StatusPanelControlAsync" );
		return Task.FromResult<object>( new { ok = true, controlled = "status" } );
	}

	public Task<object> ThemeControlAsync( JsonElement? args, CancellationToken cancellationToken = default )
	{
		Calls.Add( "ThemeControlAsync" );
		return Task.FromResult<object>( new { ok = true, palette = "Dark" } );
	}

	public Task<object> DialogRespondAsync( string? dialogId, string? response, bool listOnly, CancellationToken cancellationToken = default )
	{
		Calls.Add( $"DialogRespondAsync:{dialogId}:{response}:{listOnly}" );
		return Task.FromResult<object>( new { ok = true, dialogId, response, listOnly } );
	}

	public Task<McpToolResult> CaptureScreenshotAsync( string? windowId, string? elementName, string? savePath, bool includeImageContent, CancellationToken cancellationToken = default )
	{
		Calls.Add( $"CaptureScreenshotAsync:{windowId}:{elementName}" );
		byte[] tinyPng = Convert.FromBase64String( "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==" );
		object meta = new { windowId = windowId ?? "main", width = 1, height = 1 };
		return Task.FromResult( includeImageContent ? McpToolResult.OkImage( tinyPng, "image/png", meta ) : McpToolResult.Ok( meta ) );
	}

	public Task<string> GetAppConfigJsonAsync( CancellationToken cancellationToken = default ) => Task.FromResult( AppConfigJson );

	public Task SaveAppConfigJsonAsync( string json, CancellationToken cancellationToken = default )
	{
		AppConfigJson = json;
		return Task.CompletedTask;
	}

	public void NotifyLoginChanged( bool isLoggedIn, string? accountKey ) => Calls.Add( $"NotifyLoginChanged:{isLoggedIn}" );
	public void NotifyServerMutated( bool treeChanged, bool entriesChanged ) => Calls.Add( $"NotifyServerMutated:{treeChanged}:{entriesChanged}" );
	public void MirrorStatusLog( StatusMessage status, string message ) => Calls.Add( $"MirrorStatusLog:{status}" );
}

/// <summary>Minimal loopback HTTP/1.1 mock Odoo JSON-RPC server for end-to-end tests against real <see cref="OdooRpc"/>.</summary>
internal sealed class MockOdooJsonRpcServer : IDisposable
{
	private static readonly string TinyPngBase64 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

	private readonly TcpListener _listener;
	private readonly CancellationTokenSource _cts = new();
	private readonly Task _loop;
	private int _nextId = 900;

	public int Port { get; }
	public List<string> RecordedCalls { get; } = [];
	public Dictionary<int, Dictionary<string, object?>> CustomRecords { get; } = [];

	public MockOdooJsonRpcServer()
	{
		_listener = new TcpListener( IPAddress.Loopback, 0 );
		_listener.Start();
		Port = ( (IPEndPoint)_listener.LocalEndpoint ).Port;
		_loop = Task.Run( AcceptLoopAsync );
	}

	private async Task AcceptLoopAsync()
	{
		while( !_cts.IsCancellationRequested )
		{
			TcpClient client;
			try { client = await _listener.AcceptTcpClientAsync( _cts.Token ); }
			catch { break; }
			_ = Task.Run( () => HandleClientAsync( client ) );
		}
	}

	private async Task HandleClientAsync( TcpClient client )
	{
		using( client )
		using( NetworkStream stream = client.GetStream() )
		{
			byte[] buf = new byte[ 64 * 1024 ];
			MemoryStream raw = new();
			int headerEnd = -1;
			int contentLength = -1;
			bool chunked = false;

			while( true )
			{
				int read = await stream.ReadAsync( buf );
				if( read == 0 ) return;
				raw.Write( buf, 0, read );
				byte[] snap = raw.ToArray();
				if( headerEnd < 0 )
				{
					string txt = Encoding.ASCII.GetString( snap );
					int idx = txt.IndexOf( "\r\n\r\n", StringComparison.Ordinal );
					if( idx >= 0 )
					{
						headerEnd = idx + 4;
						foreach( string line in txt[ ..idx ].Split( "\r\n" ) )
						{
							if( line.StartsWith( "Content-Length:", StringComparison.OrdinalIgnoreCase )
								&& int.TryParse( line[ "Content-Length:".Length.. ].Trim(), out int cl ) )
								contentLength = cl;
							else if( line.StartsWith( "Transfer-Encoding:", StringComparison.OrdinalIgnoreCase )
								&& line.Contains( "chunked", StringComparison.OrdinalIgnoreCase ) )
								chunked = true;
						}
					}
				}
				if( headerEnd >= 0 )
				{
					if( chunked )
					{
						if( TryDecodeChunked( snap, headerEnd, out string decoded ) )
						{
							await WriteRpcResponseAsync( stream, decoded );
							return;
						}
					}
					else if( contentLength >= 0 && snap.Length - headerEnd >= contentLength )
					{
						string body = Encoding.UTF8.GetString( snap, headerEnd, contentLength );
						await WriteRpcResponseAsync( stream, body );
						return;
					}
				}
			}
		}
	}

	private async Task WriteRpcResponseAsync( NetworkStream stream, string body )
	{
		object? result = DispatchJsonRpc( body, out object? id );
		string respJson = JsonSerializer.Serialize( new { jsonrpc = "2.0", id, result } );
		byte[] respBytes = Encoding.UTF8.GetBytes( respJson );
		string httpHeader = $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {respBytes.Length}\r\nConnection: close\r\n\r\n";
		await stream.WriteAsync( Encoding.ASCII.GetBytes( httpHeader ) );
		await stream.WriteAsync( respBytes );
		await stream.FlushAsync();
	}

	private static bool TryDecodeChunked( byte[] buffer, int offset, out string decoded )
	{
		decoded = "";
		using MemoryStream output = new();
		int pos = offset;
		while( true )
		{
			int crlf = IndexOfCrlf( buffer, pos );
			if( crlf < 0 ) return false;
			string sizeText = Encoding.ASCII.GetString( buffer, pos, crlf - pos ).Split( ';' )[ 0 ].Trim();
			if( !int.TryParse( sizeText, System.Globalization.NumberStyles.HexNumber, null, out int chunkLen ) )
				return false;
			pos = crlf + 2;
			if( chunkLen == 0 )
			{
				if( pos + 2 > buffer.Length ) return false;
				decoded = Encoding.UTF8.GetString( output.ToArray() );
				return true;
			}
			if( pos + chunkLen + 2 > buffer.Length ) return false;
			output.Write( buffer, pos, chunkLen );
			pos += chunkLen + 2;
		}
	}

	private static int IndexOfCrlf( byte[] buffer, int start )
	{
		for( int i = start; i + 1 < buffer.Length; i++ )
			if( buffer[ i ] == '\r' && buffer[ i + 1 ] == '\n' )
				return i;
		return -1;
	}

	private object? DispatchJsonRpc( string body, out object? id )
	{
		using JsonDocument doc = JsonDocument.Parse( body );
		JsonElement root = doc.RootElement;
		id = root.TryGetProperty( "id", out JsonElement idEl ) && idEl.ValueKind == JsonValueKind.String
			? idEl.GetString()
			: null;

		JsonElement p = root.GetProperty( "params" );
		string service = p.GetProperty( "service" ).GetString() ?? "";
		string method = p.GetProperty( "method" ).GetString() ?? "";
		JsonElement args = p.GetProperty( "args" );

		if( service == "common" && method == "version" )
		{
			RecordedCalls.Add( "common.version" );
			return new { server_version = "16.0", protocol_version = 1 };
		}

		if( service == "common" && method == "login" )
		{
			RecordedCalls.Add( "common.login" );
			string user = args[ 1 ].GetString() ?? "";
			string pass = args[ 2 ].GetString() ?? "";
			return user == "demo" && pass == "secret" ? 7 : false;
		}

		if( service == "object" && ( method == "execute" || method == "execute_kw" ) )
		{
			string model = args[ 3 ].GetString() ?? "";
			string modelMethod = args[ 4 ].GetString() ?? "";
			RecordedCalls.Add( $"{model}.{modelMethod}" );
			return HandleModelCall( model, modelMethod, args );
		}

		return null;
	}

	private object? HandleModelCall( string model, string modelMethod, JsonElement args )
	{
		if( model == "res.users" && modelMethod == "has_group" )
			return true;

		if( modelMethod == "search_count" )
			return model == "hp.entry" ? 2 : 1;

		if( modelMethod == "create" )
		{
			int newId = Interlocked.Increment( ref _nextId );
			CustomRecords[ newId ] = new Dictionary<string, object?> { [ "id" ] = newId, [ "model" ] = model };
			return newId;
		}

		if( modelMethod == "write" )
			return true;

		if( modelMethod == "unlink" )
			return true;

		if( model == "hp.directory" && modelMethod == "get_children_directories_by_id" )
		{
			return new
			{
				id = 1,
				name = "root",
				directories = new Dictionary<string, object>
				{
					[ "Projects" ] = new
					{
						id = 2,
						name = "Projects",
						directories = new Dictionary<string, object>(),
						entries = new Dictionary<string, object>(),
					},
				},
				entries = new Dictionary<string, object>(),
			};
		}

		if( model == "hp.directory" && modelMethod == "get_entries" )
		{
			return new Dictionary<string, object>
			{
				[ "Asm1.SLDASM" ] = new
				{
					id = 100,
					type = "SLDASM",
					checkout = 0,
					category = "CAD",
					fullname = @"root\Projects\Asm1.SLDASM",
					size = 2048,
					latest = 200,
					release = 0,
					latest_checksum = "aaaa1111",
					latest_date = "2026-01-15 10:00:00",
					deleted = false,
				},
				[ "Part1.SLDPRT" ] = new
				{
					id = 101,
					type = "SLDPRT",
					checkout = 7,
					category = "CAD",
					fullname = @"root\Projects\Part1.SLDPRT",
					size = 1024,
					latest = 201,
					release = 0,
					latest_checksum = "bbbb2222",
					latest_date = "2026-01-14 09:00:00",
					deleted = false,
				},
			};
		}

		if( model == "hp.entry" && modelMethod == "dependency_graph" )
		{
			return new
			{
				roots = new[] { 100 },
				truncated = false,
				nodes = new object[]
				{
					new { id = 100, name = "Asm1.SLDASM", ext = ".sldasm", size = 2048, category = "CAD", version_id = 200, deleted = false, checked_out = false, path = @"root\Projects\Asm1.SLDASM", type = "SLDASM", versions = 1 },
					new { id = 101, name = "Part1.SLDPRT", ext = ".sldprt", size = 1024, category = "CAD", version_id = 201, deleted = false, checked_out = true, checkout_user = "Demo User", path = @"root\Projects\Part1.SLDPRT", type = "SLDPRT", versions = 1 },
				},
				edges = new[] { new[] { 100, 101 } },
			};
		}

		if( model == "hp.version" && modelMethod == "get_recursive_dependency_versions" )
			return new[] { 200, 201 };

		if( model == "hp.version" && modelMethod == "get_recursive_dependency_entries" )
			return new[] { 100, 101 };

		if( modelMethod == "related_browse" )
		{
			string relatedField = args.GetArrayLength() > 6 ? args[ 6 ].GetString() ?? "" : "";
			if( relatedField.Contains( "prop", StringComparison.OrdinalIgnoreCase ) )
				return HandleModelCall( "hp.version.property", "search_read", args );
			if( relatedField.Contains( "parent", StringComparison.OrdinalIgnoreCase ) || relatedField.Contains( "child", StringComparison.OrdinalIgnoreCase ) )
				return HandleModelCall( "hp.version.relationship", "search_read", args );
			if( relatedField.Contains( "version", StringComparison.OrdinalIgnoreCase ) || relatedField == "latest_version_id" )
				return HandleModelCall( "hp.version", "search_read", args );
			return Array.Empty<object>();
		}

		if( modelMethod is "search" )
		{
			return model switch
			{
				"hp.directory" => new[] { 1, 2 },
				"hp.entry" => new[] { 100, 101 },
				"hp.version" => new[] { 200, 201 },
				"hp.version.relationship" => new[] { 300 },
				"hp.version.property" => new[] { 400 },
				"hp.user" => new[] { 1 },
				"hp.node" => new[] { 1 },
				"hp.type" => new[] { 1, 2 },
				"hp.category" => new[] { 1 },
				"hp.property" => new[] { 1 },
				"hp.entry.name.filter" => Array.Empty<int>(),
				"hp.setting" => new[] { 1 },
				"res.users" => new[] { 7 },
				_ => Array.Empty<int>(),
			};
		}

		if( modelMethod is "search_read" or "read" or "smart_read" )
		{
			HashSet<int>? requestedIds = null;
			if( modelMethod is "read" or "smart_read" && args.GetArrayLength() > 5 && args[ 5 ].ValueKind == JsonValueKind.Array )
			{
				requestedIds = [];
				foreach( JsonElement el in args[ 5 ].EnumerateArray() )
					if( el.ValueKind == JsonValueKind.Number && el.TryGetInt32( out int rid ) )
						requestedIds.Add( rid );
			}

			object[] all = model switch
			{
				"res.users" => new object[]
				{
					new { id = 7, name = "Demo User", login = "demo" },
				},
				"hp.user" => new object[]
				{
					new { id = 1, name = "Demo User", login = "demo", user_id = new object[] { 7, "Demo User" } },
				},
				"hp.node" => new object[]
				{
					new { id = 1, name = Environment.MachineName.ToLowerInvariant() },
				},
				"hp.type" => new object[]
				{
					new { id = 1, name = "SLDASM", file_ext = "sldasm", type_re = @"\.sldasm$", cat_id = new object[] { 1, "CAD" } },
					new { id = 2, name = "SLDPRT", file_ext = "sldprt", type_re = @"\.sldprt$", cat_id = new object[] { 1, "CAD" } },
				},
				"hp.category" => new object[]
				{
					new { id = 1, name = "CAD", cat_description = "CAD Models", track_depends = true, track_version = true },
				},
				"hp.property" => new object[]
				{
					new { id = 1, name = "Material", prop_type = "text" },
				},
				"hp.entry.name.filter" => Array.Empty<object>(),
				"hp.setting" => new object[]
				{
					new { id = 1, name = "client_version", char_value = "0.0.1" },
				},
				"hp.directory" => new object[]
				{
					new { id = 1, name = "root", complete_name = "root", parent_id = false, default_cat = new object[] { 1, "CAD" }, sandboxed = false, deleted = false },
					new { id = 2, name = "Projects", complete_name = "root / Projects", parent_id = new object[] { 1, "root" }, default_cat = new object[] { 1, "CAD" }, sandboxed = false, deleted = false },
				},
				"hp.entry" => new object[]
				{
					new
					{
						id = 100, name = "Asm1.SLDASM", dir_id = new object[] { 2, "Projects" },
						cat_id = new object[] { 1, "CAD" }, type_id = new object[] { 1, "SLDASM" },
						latest_version_id = new object[] { 200, "200" },
						checkout_user = false, checkout_node = false, deleted = false,
						windows_complete_name = @"root\Projects\Asm1.SLDASM",
					},
					new
					{
						id = 101, name = "Part1.SLDPRT", dir_id = new object[] { 2, "Projects" },
						cat_id = new object[] { 1, "CAD" }, type_id = new object[] { 2, "SLDPRT" },
						latest_version_id = new object[] { 201, "201" },
						checkout_user = new object[] { 7, "Demo User" }, checkout_node = new object[] { 1, Environment.MachineName }, deleted = false,
						windows_complete_name = @"root\Projects\Part1.SLDPRT",
					},
				},
				"hp.version" => new object[]
				{
					new
					{
						id = 200, name = "200", entry_id = new object[] { 100, "Asm1.SLDASM" },
						dir_id = new object[] { 2, "Projects" }, node_id = new object[] { 1, Environment.MachineName.ToLowerInvariant() },
						file_modify_stamp = "2026-01-15 10:00:00", file_size = 2048,
						checksum = "aaaa1111", preview_image = TinyPngBase64,
						parent_ids = Array.Empty<int>(), child_ids = new[] { 201 },
						version_property_ids = new[] { 400 },
						create_date = "2026-01-15 10:00:00", create_uid = new object[] { 7, "Demo User" },
					},
					new
					{
						id = 201, name = "201", entry_id = new object[] { 101, "Part1.SLDPRT" },
						dir_id = new object[] { 2, "Projects" }, node_id = new object[] { 1, Environment.MachineName.ToLowerInvariant() },
						file_modify_stamp = "2026-01-14 09:00:00", file_size = 1024,
						checksum = "bbbb2222", preview_image = false,
						parent_ids = new[] { 200 }, child_ids = Array.Empty<int>(),
						create_date = "2026-01-14 09:00:00", create_uid = new object[] { 7, "Demo User" },
					},
				},
				"hp.version.relationship" => new object[]
				{
					new { id = 300, parent_id = new object[] { 200, "200" }, child_id = new object[] { 201, "201" } },
				},
				"hp.version.property" => new object[]
				{
					new { id = 400, version_id = new object[] { 200, "200" }, prop_id = new object[] { 1, "Material" }, prop_name = "Material", sw_config_name = "Default", text_value = "Aluminum 6061" },
				},
				_ => Array.Empty<object>(),
			};

			if( requestedIds is { Count: > 0 } )
			{
				return all.Where( item =>
				{
					var prop = item.GetType().GetProperty( "id" );
					return prop is not null && prop.GetValue( item ) is int itemId && requestedIds.Contains( itemId );
				} ).ToArray();
			}
			return all;
		}

		return new { echoModel = model, echoMethod = modelMethod };
	}

	public void Dispose()
	{
		_cts.Cancel();
		try { _listener.Stop(); } catch { }
		_cts.Dispose();
	}
}

[Collection( SharedStateCollection.Name )]
public sealed class HackPdmApiAndMockOdooTests
{
	private static async Task<JsonElement> CallToolAsync( McpServer server, string toolName, object? arguments = null )
	{
		string req = JsonSerializer.Serialize( new
		{
			jsonrpc = "2.0",
			id = 1,
			method = "tools/call",
			@params = new { name = toolName, arguments = arguments ?? new { } },
		} );
		string? raw = await server.HandleMessageAsync( req );
		Assert.NotNull( raw );
		using JsonDocument doc = JsonDocument.Parse( raw! );
		return doc.RootElement.GetProperty( "result" ).Clone();
	}

	private static JsonElement ParseToolJson( JsonElement toolResult )
	{
		Assert.False( toolResult.TryGetProperty( "isError", out JsonElement isErr ) && isErr.GetBoolean(),
			$"Expected tool success, got error: {toolResult}" );
		string text = toolResult.GetProperty( "content" )[ 0 ].GetProperty( "text" ).GetString()!;
		using JsonDocument doc = JsonDocument.Parse( text );
		return doc.RootElement.Clone();
	}

	[Fact]
	public async Task LocalVaultFiles_ReadWriteListChecksumDelete_AndRejectPathTraversal()
	{
		using PwaScope pwa = new( create: true );
		string settingsPath = Path.Combine( Path.GetDirectoryName( pwa.Absolute )!, "settings.json" );
		CoreSettings coreSettings = new( new JsonFileSettingsProvider( settingsPath ) );
		using var bundle = HackPdmMcpServerFactory.Create( McpServerMode.Headless, settingsStore: coreSettings );

		// Write file inside vault
		JsonElement written = ParseToolJson( await CallToolAsync( bundle.Server, "local_write_file", new
		{
			path = "Projects/notes.txt",
			content = "Hello HackPDM MCP!",
		} ) );
		Assert.True( written.GetProperty( "ok" ).GetBoolean() );

		// Read back
		JsonElement read = ParseToolJson( await CallToolAsync( bundle.Server, "local_read_file", new { path = "Projects/notes.txt" } ) );
		Assert.Equal( "Hello HackPDM MCP!", read.GetProperty( "content" ).GetString() );

		// Checksum
		JsonElement checksum = ParseToolJson( await CallToolAsync( bundle.Server, "local_checksum", new { paths = new[] { "Projects/notes.txt" } } ) );
		Assert.False( string.IsNullOrWhiteSpace( checksum.GetProperty( "files" )[ 0 ].GetProperty( "checksum" ).GetString() ) );

		// List files
		JsonElement listed = ParseToolJson( await CallToolAsync( bundle.Server, "local_list_files", new { treePath = "root/Projects", recursive = true } ) );
		Assert.Equal( 1, listed.GetProperty( "fileCount" ).GetInt32() );

		// Path traversal MUST be rejected for both read and write!
		JsonElement escapeRes = await CallToolAsync( bundle.Server, "local_read_file", new { path = "../../etc/passwd" } );
		Assert.True( escapeRes.GetProperty( "isError" ).GetBoolean() );

		JsonElement escapeWrite = await CallToolAsync( bundle.Server, "local_write_file", new { path = "../outside.txt", content = "nope" } );
		Assert.True( escapeWrite.GetProperty( "isError" ).GetBoolean() );

		// Delete file requires confirm=true
		JsonElement unconfirmed = await CallToolAsync( bundle.Server, "local_delete", new { filePaths = new[] { "Projects/notes.txt" }, confirm = false } );
		Assert.True( unconfirmed.GetProperty( "isError" ).GetBoolean() );

		JsonElement deleted = ParseToolJson( await CallToolAsync( bundle.Server, "local_delete", new { filePaths = new[] { "Projects/notes.txt" }, confirm = true } ) );
		Assert.Equal( 1, deleted.GetProperty( "deletedFiles" ).GetArrayLength() );
	}

	[Fact]
	public async Task Profile_PathConvert_Settings_Diagnostics_AndSolidWorksInspect()
	{
		using PwaScope pwa = new( create: true );
		string workDir = Path.GetDirectoryName( pwa.Absolute )!;
		string settingsPath = Path.Combine( workDir, "settings.json" );
		CoreSettings coreSettings = new( new JsonFileSettingsProvider( settingsPath ) );
		_ = new OdooDefaults( coreSettings );
		using var bundle = HackPdmMcpServerFactory.Create( McpServerMode.Headless, settingsStore: coreSettings );

		// Configure session + save & reload .hp profile
		ParseToolJson( await CallToolAsync( bundle.Server, "session_configure", new
		{
			address = "127.0.0.1",
			port = "8069",
			db = "pdm_test",
			user = "alice",
			useHttps = false,
			pwaPath = pwa.Absolute,
		} ) );

		string profileFile = Path.Combine( workDir, "test.hp" );
		JsonElement saved = ParseToolJson( await CallToolAsync( bundle.Server, "profile_save", new { path = profileFile } ) );
		Assert.True( File.Exists( profileFile ) );
		Assert.Equal( profileFile, saved.GetProperty( "savedTo" ).GetString() );

		JsonElement loaded = ParseToolJson( await CallToolAsync( bundle.Server, "profile_load", new { path = profileFile } ) );
		Assert.Equal( profileFile, loaded.GetProperty( "loadedFrom" ).GetString() );

		// Path convert
		string localSample = Path.Combine( pwa.Absolute, "Sub", "Bracket.SLDPRT" );
		JsonElement converted = ParseToolJson( await CallToolAsync( bundle.Server, "path_convert", new { path = localSample } ) );
		Assert.Contains( "Bracket.SLDPRT", converted.GetRawText() );

		// Settings set / get / list
		ParseToolJson( await CallToolAsync( bundle.Server, "settings_set", new { key = "McpTestKey", value = "hello123" } ) );
		JsonElement got = ParseToolJson( await CallToolAsync( bundle.Server, "settings_get", new { key = "McpTestKey" } ) );
		Assert.Equal( "hello123", got.GetProperty( "value" ).GetString() );
		JsonElement allSettings = ParseToolJson( await CallToolAsync( bundle.Server, "settings_list" ) );
		Assert.True( allSettings.GetProperty( "count" ).GetInt32() >= 1 );

		// Diagnostics
		JsonElement diag = ParseToolJson( await CallToolAsync( bundle.Server, "diagnostics_get" ) );
		Assert.Contains( "logDirectory", diag.GetRawText() );

		// Real SolidWorks fixture inspection (support_files/new.SLDPRT & new.SLDASM)
		string? repoRoot = FindRepoRoot();
		if( repoRoot is not null )
		{
			string partPath = Path.Combine( repoRoot, "support_files", "new.SLDPRT" );
			if( File.Exists( partPath ) )
			{
				JsonElement sw = ParseToolJson( await CallToolAsync( bundle.Server, "sw_inspect_file", new { path = partPath } ) );
				Assert.True( sw.GetProperty( "isSolidWorks" ).GetBoolean() );
				Assert.Equal( "Part", sw.GetProperty( "documentType" ).GetString() );
				Assert.True( sw.GetProperty( "fileSize" ).GetInt64() > 0 );
				Assert.False( string.IsNullOrWhiteSpace( sw.GetProperty( "checksum" ).GetString() ) );
			}
		}
	}

	[Fact]
	public async Task AllLiveUiTools_AndUiResources_DispatchToUiBridge()
	{
		FakeUiBridge fakeUi = new();
		using var bundle = HackPdmMcpServerFactory.Create( McpServerMode.EmbeddedGui, localUiBridge: fakeUi );

		ParseToolJson( await CallToolAsync( bundle.Server, "ui_get_state" ) );
		ParseToolJson( await CallToolAsync( bundle.Server, "ui_list_windows" ) );
		ParseToolJson( await CallToolAsync( bundle.Server, "ui_visual_tree", new { maxDepth = 4 } ) );
		ParseToolJson( await CallToolAsync( bundle.Server, "ui_find_controls", new { typeName = "Button" } ) );
		ParseToolJson( await CallToolAsync( bundle.Server, "ui_get_control", new { name = "LoginButton" } ) );
		ParseToolJson( await CallToolAsync( bundle.Server, "ui_click_control", new { name = "LoginButton" } ) );
		ParseToolJson( await CallToolAsync( bundle.Server, "ui_set_control_value", new { name = "AddressBox", value = "localhost" } ) );
		ParseToolJson( await CallToolAsync( bundle.Server, "ui_invoke_command", new { commandName = "RefreshCommand" } ) );
		ParseToolJson( await CallToolAsync( bundle.Server, "ui_navigate", new { page = "HackFileManager" } ) );
		ParseToolJson( await CallToolAsync( bundle.Server, "ui_file_manager_select_folder", new { treePath = "root/Projects" } ) );
		ParseToolJson( await CallToolAsync( bundle.Server, "ui_file_manager_select_entries", new { names = new[] { "Asm1.SLDASM" } } ) );
		ParseToolJson( await CallToolAsync( bundle.Server, "ui_file_manager_control", new { showInactive = true } ) );
		ParseToolJson( await CallToolAsync( bundle.Server, "ui_dependency_graph_control", new { fitToView = true } ) );
		ParseToolJson( await CallToolAsync( bundle.Server, "ui_search_control", new { nameContains = "Asm" } ) );
		ParseToolJson( await CallToolAsync( bundle.Server, "ui_status_panel", new { maxItems = 10 } ) );
		ParseToolJson( await CallToolAsync( bundle.Server, "ui_theme_control", new { palette = "Dark" } ) );
		ParseToolJson( await CallToolAsync( bundle.Server, "ui_dialog_respond", new { listOnly = true } ) );

		// Screenshot returns MCP image content block + JSON metadata
		JsonElement shot = await CallToolAsync( bundle.Server, "ui_screenshot", new { includeImageContent = true } );
		Assert.False( shot.TryGetProperty( "isError", out JsonElement e ) && e.GetBoolean() );
		JsonElement content = shot.GetProperty( "content" );
		Assert.Equal( 2, content.GetArrayLength() );
		Assert.Equal( "image", content[ 0 ].GetProperty( "type" ).GetString() );
		Assert.Equal( "image/png", content[ 0 ].GetProperty( "mimeType" ).GetString() );

		// AppConfig roundtrip via UI bridge
		ParseToolJson( await CallToolAsync( bundle.Server, "app_config_save", new { json = """{"Saved":42}""" } ) );
		JsonElement cfg = ParseToolJson( await CallToolAsync( bundle.Server, "app_config_get" ) );
		Assert.Contains( "42", cfg.GetRawText() );

		Assert.True( fakeUi.Calls.Count >= 18 );
	}

	[Fact]
	public async Task MockOdooEndToEnd_Login_DirectoryTree_Entries_Details_Preview_Graph_Search_GenericOrm()
	{
		using PwaScope pwa = new( create: true );
		using MockOdooJsonRpcServer mockOdoo = new();

		string settingsPath = Path.Combine( Path.GetDirectoryName( pwa.Absolute )!, "settings.json" );
		CoreSettings coreSettings = new( new JsonFileSettingsProvider( settingsPath ) );
		_ = new OdooDefaults( coreSettings );
		FakeUiBridge fakeUi = new();
		using var bundle = HackPdmMcpServerFactory.Create( McpServerMode.EmbeddedGui, localUiBridge: fakeUi, settingsStore: coreSettings );

		// 1. Configure session pointing at mock loopback Odoo server
		ParseToolJson( await CallToolAsync( bundle.Server, "session_configure", new
		{
			address = "127.0.0.1",
			port = mockOdoo.Port.ToString(),
			db = "mock_db",
			user = "demo",
			password = "secret",
			useHttps = false,
			pwaPath = pwa.Absolute,
		} ) );

		// 2. Test connection
		JsonElement conn = ParseToolJson( await CallToolAsync( bundle.Server, "session_test_connection" ) );
		Assert.True( conn.GetProperty( "ok" ).GetBoolean(), conn.GetRawText() );

		// 3. Login + hydrate lookups
		JsonElement login = ParseToolJson( await CallToolAsync( bundle.Server, "session_login", new { forceRelogin = true } ) );
		Assert.True( login.GetProperty( "ok" ).GetBoolean(), login.GetRawText() );
		Assert.Equal( 7, login.GetProperty( "odooId" ).GetInt32() );
		Assert.Contains( "NotifyLoginChanged:True", fakeUi.Calls );

		// 4. Lookups
		JsonElement lookups = ParseToolJson( await CallToolAsync( bundle.Server, "metadata_get_lookups" ) );
		Assert.True( lookups.GetProperty( "types" ).GetArrayLength() >= 2 );

		// 5. Directory tree
		JsonElement tree = ParseToolJson( await CallToolAsync( bundle.Server, "directory_get_tree" ) );
		Assert.Contains( "Projects", tree.GetRawText() );

		// 6. Directory entries (with a local file placed in Projects/ to verify local merge!)
		Directory.CreateDirectory( Path.Combine( pwa.Absolute, "Projects" ) );
		await File.WriteAllTextAsync( Path.Combine( pwa.Absolute, "Projects", "LocalOnly.txt" ), "local draft" );

		JsonElement entries = ParseToolJson( await CallToolAsync( bundle.Server, "directory_get_entries", new
		{
			directoryId = 2,
			includeLocalFiles = true,
		} ) );
		string entriesRaw = entries.GetRawText();
		Assert.Contains( "Asm1.SLDASM", entriesRaw );
		Assert.Contains( "Part1.SLDPRT", entriesRaw );
		Assert.Contains( "LocalOnly.txt", entriesRaw );

		// 7. Entry details
		JsonElement details = ParseToolJson( await CallToolAsync( bundle.Server, "entry_get_details", new { entryId = 100 } ) );
		string detailsRaw = details.GetRawText();
		Assert.Contains( "Asm1.SLDASM", detailsRaw );
		Assert.Contains( "Aluminum 6061", detailsRaw );

		// 8. Version preview (returns MCP image content block!)
		JsonElement preview = await CallToolAsync( bundle.Server, "version_get_preview", new { versionId = 200 } );
		Assert.False( preview.TryGetProperty( "isError", out JsonElement prevErr ) && prevErr.GetBoolean(), preview.GetRawText() );
		Assert.Equal( "image", preview.GetProperty( "content" )[ 0 ].GetProperty( "type" ).GetString() );
		Assert.Equal( "image/png", preview.GetProperty( "content" )[ 0 ].GetProperty( "mimeType" ).GetString() );

		// 9. Dependency graph
		JsonElement graph = ParseToolJson( await CallToolAsync( bundle.Server, "dependency_graph_load", new
		{
			entryIds = new[] { 100 },
		} ) );
		Assert.True( graph.GetProperty( "nodeCount" ).GetInt32() >= 2 );
		Assert.True( graph.GetProperty( "edgeCount" ).GetInt32() >= 1 );

		// 10. Search entries
		JsonElement search = ParseToolJson( await CallToolAsync( bundle.Server, "search_entries", new { nameContains = "Asm" } ) );
		Assert.True( search.GetProperty( "count" ).GetInt32() >= 1 );

		// 11. Generic Odoo ORM tools (search_read, search_count, create, write, unlink, execute)
		JsonElement sr = ParseToolJson( await CallToolAsync( bundle.Server, "odoo_search_read", new
		{
			model = "hp.entry",
			domain = Array.Empty<object>(),
			fields = new[] { "id", "name" },
		} ) );
		Assert.Equal( 2, sr.GetProperty( "count" ).GetInt32() );

		JsonElement cnt = ParseToolJson( await CallToolAsync( bundle.Server, "odoo_search_count", new { model = "hp.entry" } ) );
		Assert.Equal( 2, cnt.GetProperty( "count" ).GetInt32() );

		JsonElement created = ParseToolJson( await CallToolAsync( bundle.Server, "odoo_create", new
		{
			model = "hp.directory",
			values = new { name = "NewFolder", parent_id = 1 },
		} ) );
		int createdId = created.GetProperty( "id" ).GetInt32();
		Assert.True( createdId > 0 );

		JsonElement updated = ParseToolJson( await CallToolAsync( bundle.Server, "odoo_write", new
		{
			model = "hp.directory",
			ids = new[] { createdId },
			values = new { name = "RenamedFolder" },
		} ) );
		Assert.True( updated.GetProperty( "ok" ).GetBoolean() );

		JsonElement unlinked = ParseToolJson( await CallToolAsync( bundle.Server, "odoo_unlink", new
		{
			model = "hp.directory",
			ids = new[] { createdId },
			confirm = true,
		} ) );
		Assert.True( unlinked.GetProperty( "ok" ).GetBoolean() );

		JsonElement exec = ParseToolJson( await CallToolAsync( bundle.Server, "odoo_execute", new
		{
			model = "res.users",
			method = "has_group",
			args = new object[] { "hackpdm.group_pdm_admin" },
		} ) );
		Assert.True( exec.GetProperty( "result" ).GetBoolean() );

		// 12. Logout
		JsonElement logout = ParseToolJson( await CallToolAsync( bundle.Server, "session_logout" ) );
		Assert.False( logout.GetProperty( "isLoggedIn" ).GetBoolean() );
	}

	private static string? FindRepoRoot()
	{
		for( DirectoryInfo? dir = new( AppContext.BaseDirectory ); dir is not null; dir = dir.Parent )
		{
			if( File.Exists( Path.Combine( dir.FullName, "HackPDM.sln" ) ) )
				return dir.FullName;
		}
		return null;
	}
}

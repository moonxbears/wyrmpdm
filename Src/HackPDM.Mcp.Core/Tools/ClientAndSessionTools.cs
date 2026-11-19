using System.Runtime.InteropServices;

using HackPDM.Infrastructure.Operations;
using HackPDM.Mcp.Bridge;
using HackPDM.Mcp.Protocol;
using HackPDM.Mcp.Services;

namespace HackPDM.Mcp.Tools;

internal static class ClientAndSessionTools
{
	public static void Register( McpServer server, HackPdmApiService api, HackPdmBridgeCoordinator bridge )
	{
		// ---- Client / Bridge Lifecycle ----

		server.RegisterTool(
			"client_status",
			"Reports the HackPDM MCP server's runtime mode (live_gui, proxied_to_live_gui, or headless), live GUI attachment status, Odoo session summary, working folder path, and active UI summary when connected to a live HackPDM desktop client.",
			McpSchema.EmptyObject(),
			async ( _, ct ) =>
			{
				McpBridgeEndpointInfo? discovered = McpBridgeDiscovery.TryReadLiveBridge( bridge.DataDirOverride );
				object? remoteHealth = null;
				if( bridge.IsProxiedAttached && bridge.AttachedEndpoint is { } ep )
				{
					using McpRemoteProxyClient probe = new( ep.BaseUrl, ep.Token, ep.Pid > 0 ? ep.Pid : null );
					remoteHealth = await probe.GetHealthAsync( ct );
				}

				object? liveUiSummary = null;
				if( bridge.LocalUiBridge is not null )
				{
					liveUiSummary = await bridge.LocalUiBridge.GetUiStateAsync( ct );
				}

				return McpToolResult.Ok( new
				{
					mode = bridge.ActiveModeLabel,
					configuredMode = bridge.Mode.ToString(),
					hasLiveUiAccess = bridge.HasLiveLocalUi || bridge.IsProxiedAttached,
					pid = Environment.ProcessId,
					appVersion = ClientUpdate.CurrentText,
					platform = $"{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})",
					attachedBridge = bridge.AttachedEndpoint is { } attached
						? new { attached.Pid, attached.Port, attached.BaseUrl, attached.McpUrl, attached.AppVersion, attached.StartedAtUtc }
						: null,
					remoteHealth,
					discoveredLiveBridge = discovered is not null
						? new { discovered.Pid, discovered.Port, discovered.BaseUrl, discovered.AppVersion, discovered.StartedAtUtc }
						: null,
					session = api.GetSessionInfo(),
					liveUi = liveUiSummary,
				} );
			},
			new McpToolAnnotations( "Client Status", ReadOnlyHint: true, IdempotentHint: true ) );

		server.RegisterTool(
			"client_attach",
			"Attaches the standalone MCP server to a running HackPDM desktop GUI client (auto-discovered from mcp-bridge.json, or at an explicit localhost URL + Bearer token) so all UI and API tools operate against the live client.",
			McpSchema.Object()
				.Prop( "url", McpSchema.String( "Optional explicit HackPDM bridge base URL (e.g. http://127.0.0.1:51234). Omit to auto-discover." ) )
				.Prop( "token", McpSchema.String( "Optional Bearer token when connecting to an explicit URL." ) )
				.Build(),
			async ( raw, ct ) =>
			{
				McpToolArgs args = new( raw );
				var (attached, endpoint, error) = await bridge.TryAttachAsync( args.GetString( "url" ), args.GetString( "token" ), ct );
				if( !attached )
					return McpToolResult.Error( error ?? "Failed to attach to a live HackPDM desktop client." );
				return McpToolResult.Ok( new
				{
					attached = true,
					mode = bridge.ActiveModeLabel,
					endpoint = endpoint is null ? null : new { endpoint.Pid, endpoint.Port, endpoint.BaseUrl, endpoint.AppVersion },
				} );
			},
			new McpToolAnnotations( "Attach Live Client", ReadOnlyHint: false, IdempotentHint: true ) );

		server.RegisterTool(
			"client_detach",
			"Detaches the standalone MCP server from a live HackPDM desktop GUI client and switches to headless in-process API mode.",
			McpSchema.EmptyObject(),
			( _, _ ) =>
			{
				if( bridge.HasLiveLocalUi )
					return Task.FromResult( McpToolResult.Error( "Cannot detach when the MCP server is running embedded inside the HackPDM desktop client process." ) );
				bridge.Detach();
				return Task.FromResult( McpToolResult.Ok( new { detached = true, mode = bridge.ActiveModeLabel } ) );
			},
			new McpToolAnnotations( "Detach Live Client", ReadOnlyHint: false, IdempotentHint: true ) );

		server.RegisterTool(
			"client_launch",
			"Launches the HackPDM desktop GUI client with its embedded MCP bridge enabled, waits until it is ready, and attaches to it.",
			McpSchema.Object()
				.Prop( "executablePath", McpSchema.String( "Optional path to HackPDM.exe / HackPDM / HackPDM.dll. Auto-detected if omitted." ) )
				.Prop( "waitTimeoutSeconds", McpSchema.Integer( "Seconds to wait for the GUI MCP bridge to become ready.", 3, 120, 30 ) )
				.Prop( "port", McpSchema.Integer( "Optional loopback TCP port for the GUI MCP bridge (0 = automatic).", 0, 65535 ) )
				.Build(),
			async ( raw, ct ) =>
			{
				if( bridge.HasLiveLocalUi )
					return McpToolResult.Ok( new { alreadyRunningInProcess = true, pid = Environment.ProcessId, mode = bridge.ActiveModeLabel } );

				McpToolArgs args = new( raw );
				var launch = await LiveClientLauncher.LaunchAndWaitAsync(
					args.GetString( "executablePath" ),
					args.GetInt( "waitTimeoutSeconds", 30 ) ?? 30,
					args.GetInt( "port" ),
					bridge.DataDirOverride,
					ct );

				if( !launch.Launched || launch.Endpoint is null )
					return McpToolResult.Error( launch.Error ?? "Failed to launch HackPDM desktop client.", new { launch.Pid, launch.CommandLine } );

				var (attached, _, attachErr) = await bridge.TryAttachAsync( launch.Endpoint.BaseUrl, launch.Endpoint.Token, ct );
				return McpToolResult.Ok( new
				{
					launched = true,
					attached,
					attachError = attachErr,
					pid = launch.Pid,
					commandLine = launch.CommandLine,
					endpoint = new { launch.Endpoint.Port, launch.Endpoint.BaseUrl, launch.Endpoint.AppVersion },
				} );
			},
			new McpToolAnnotations( "Launch Live Client", ReadOnlyHint: false ) );

		// ---- Session, Auth, Connection & Profiles ----

		server.RegisterTool(
			"session_get_info",
			"Returns current Odoo connection settings (with password redacted), authentication status, registered workstation node, root directory, local working folder (PWA), temp folder, and client version.",
			McpSchema.EmptyObject(),
			( _, _ ) => Task.FromResult( McpToolResult.Ok( api.GetSessionInfo() ) ),
			new McpToolAnnotations( "Get Session Info", ReadOnlyHint: true, IdempotentHint: true ) );

		server.RegisterTool(
			"session_configure",
			"Configures HackPDM Odoo server settings and/or local workspace (PWA) and temp folders. Persists settings to settings.json and optionally saves credentials to the OS keychain.",
			McpSchema.Object()
				.Prop( "address", McpSchema.String( "Odoo host name or IP address." ) )
				.Prop( "port", McpSchema.String( "Odoo TCP port (e.g. '8069' or '443')." ) )
				.Prop( "db", McpSchema.String( "Odoo database name." ) )
				.Prop( "user", McpSchema.String( "Odoo login username." ) )
				.Prop( "password", McpSchema.String( "Odoo login password." ) )
				.Prop( "useHttps", McpSchema.Boolean( "Whether to connect over HTTPS (default true; disable only for trusted/loopback HTTP servers)." ) )
				.Prop( "url", McpSchema.String( "Optional explicit full Odoo base URL (overrides address/port/useHttps)." ) )
				.Prop( "swKey", McpSchema.String( "Optional SolidWorks Document Manager license key." ) )
				.Prop( "areaFactor", McpSchema.Number( "Optional area unit conversion factor." ) )
				.Prop( "credentialTarget", McpSchema.String( "Optional credential keyring target name." ) )
				.Prop( "pwaPath", McpSchema.String( "Absolute path to the local HackPDM working folder (PWA vault)." ) )
				.Prop( "tempPath", McpSchema.String( "Absolute path to the HackPDM temporary folder." ) )
				.Prop( "saveCredentials", McpSchema.Boolean( "Save username/password into the OS credential store.", false ) )
				.Prop( "createLocalFolders", McpSchema.Boolean( "Create pwaPath / tempPath directories if they do not exist.", true ) )
				.Build(),
			( raw, _ ) =>
			{
				McpToolArgs args = new( raw );
				return Task.FromResult( McpToolResult.Ok( api.ConfigureSession(
					address: args.GetString( "address" ),
					port: args.GetString( "port" ),
					db: args.GetString( "db" ),
					user: args.GetString( "user" ),
					password: args.GetString( "password" ),
					useHttps: args.GetNullableBool( "useHttps" ),
					explicitUrl: args.GetString( "url" ),
					swKey: args.GetString( "swKey" ),
					areaFactor: args.GetDecimal( "areaFactor" ),
					credentialTarget: args.GetString( "credentialTarget" ),
					pwaPath: args.GetString( "pwaPath" ),
					tempPath: args.GetString( "tempPath" ),
					saveCredentials: args.GetBool( "saveCredentials", false ),
					createLocalFolders: args.GetBool( "createLocalFolders", true ) ) ) );
			},
			new McpToolAnnotations( "Configure Session", ReadOnlyHint: false, IdempotentHint: true ) );

		server.RegisterTool(
			"session_test_connection",
			"Tests Odoo server reachability step by step: URL/TLS policy, DNS/ICMP host check, TCP port connect, and Odoo JSON-RPC common.version probe with latency.",
			McpSchema.EmptyObject(),
			async ( _, ct ) => McpToolResult.Ok( await api.TestConnectionAsync( ct ) ),
			new McpToolAnnotations( "Test Odoo Connection", ReadOnlyHint: true, IdempotentHint: true, OpenWorldHint: true ) );

		server.RegisterTool(
			"session_login",
			"Authenticates with Odoo, loads all HackPDM reference lookup tables (nodes, root directory, users, file types, categories, properties, filters, settings), registers this workstation node if new, and verifies client version compatibility.",
			McpSchema.Object()
				.Prop( "forceRelogin", McpSchema.Boolean( "Force re-authentication even if already logged in.", false ) )
				.Prop( "saveCredentials", McpSchema.Boolean( "Persist verified credentials to the OS credential store.", false ) )
				.Prop( "checkClientVersion", McpSchema.Boolean( "Verify client version against the server's client_version hp.settings rule.", true ) )
				.Build(),
			async ( raw, ct ) =>
			{
				McpToolArgs args = new( raw );
				return McpToolResult.Ok( await api.LoginAsync(
					args.GetBool( "forceRelogin", false ),
					args.GetBool( "saveCredentials", false ),
					args.GetBool( "checkClientVersion", true ),
					ct ) );
			},
			new McpToolAnnotations( "Odoo Login", ReadOnlyHint: false, OpenWorldHint: true ) );

		server.RegisterTool(
			"session_logout",
			"Signs out of the current Odoo session and clears cached server lookups and workspace state.",
			McpSchema.EmptyObject(),
			( _, _ ) => Task.FromResult( McpToolResult.Ok( api.Logout() ) ),
			new McpToolAnnotations( "Odoo Logout", ReadOnlyHint: false, IdempotentHint: true ) );

		server.RegisterTool(
			"session_check_permissions",
			"Checks whether the logged-in user belongs to hackpdm.group_pdm_admin and evaluates client version compatibility.",
			McpSchema.EmptyObject(),
			async ( _, ct ) => McpToolResult.Ok( await api.CheckPermissionsAsync( ct ) ),
			new McpToolAnnotations( "Check Permissions", ReadOnlyHint: true, IdempotentHint: true, OpenWorldHint: true ) );

		server.RegisterTool(
			"session_reload_lookups",
			"Reloads all HackPDM reference data (users, nodes, file types, categories, properties, name filters, settings, root directory) from Odoo.",
			McpSchema.EmptyObject(),
			async ( _, _ ) => McpToolResult.Ok( await api.ReloadLookupsAsync() ),
			new McpToolAnnotations( "Reload Lookups", ReadOnlyHint: true, IdempotentHint: true, OpenWorldHint: true ) );

		server.RegisterTool(
			"profile_get_current",
			"Returns the current HackPDM login profile JSON object (server, port, HTTPS, DB, PWA path, username; password omitted unless includePassword=true).",
			McpSchema.Object()
				.Prop( "includePassword", McpSchema.Boolean( "Include the plaintext password in the returned object (default false).", false ) )
				.Build(),
			( raw, _ ) =>
			{
				McpToolArgs args = new( raw );
				return Task.FromResult( McpToolResult.Ok( api.GetCurrentProfile( args.GetBool( "includePassword", false ) ) ) );
			},
			new McpToolAnnotations( "Get Current Profile", ReadOnlyHint: true, IdempotentHint: true ) );

		server.RegisterTool(
			"profile_load",
			"Loads a HackPDM JSON profile file from disk and applies its settings to the live client configuration (does not auto-login).",
			McpSchema.Object()
				.Prop( "path", McpSchema.String( "Path to the JSON profile file." ), required: true )
				.Build(),
			async ( raw, ct ) =>
			{
				McpToolArgs args = new( raw );
				return McpToolResult.Ok( await api.LoadProfileAsync( args.RequireString( "path" ), ct ) );
			},
			new McpToolAnnotations( "Load Profile", ReadOnlyHint: false ) );

		server.RegisterTool(
			"profile_save",
			"Saves the current HackPDM connection profile (or a blank template profile) to a JSON file.",
			McpSchema.Object()
				.Prop( "path", McpSchema.String( "Destination JSON file path." ), required: true )
				.Prop( "includePassword", McpSchema.Boolean( "Include password in the saved file (default false).", false ) )
				.Prop( "template", McpSchema.Boolean( "Write a placeholder template profile instead of current settings.", false ) )
				.Build(),
			async ( raw, ct ) =>
			{
				McpToolArgs args = new( raw );
				return McpToolResult.Ok( await api.SaveProfileAsync(
					args.RequireString( "path" ),
					args.GetBool( "includePassword", false ),
					args.GetBool( "template", false ),
					ct ) );
			},
			new McpToolAnnotations( "Save Profile", ReadOnlyHint: false ) );
	}
}

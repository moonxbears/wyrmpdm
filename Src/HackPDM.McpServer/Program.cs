using System.Text.Json;

using HackPDM.Core.Configuration;
using HackPDM.Core.Hack;
using HackPDM.Infrastructure.Odoo;
using HackPDM.Infrastructure.Operations;
using HackPDM.Mcp;
using HackPDM.Mcp.Bridge;
using HackPDM.Mcp.Protocol;
using HackPDM.Mcp.Security;
using HackPDM.Mcp.Services;
using HackPDM.Shared.GlobalData;

namespace HackPDM.McpServer;

internal static class Program
{
	private const int DefaultHttpPort = 38420;
	private static readonly JsonSerializerOptions PrettyJson = new( McpJson.Options ) { WriteIndented = true };

	public static async Task<int> Main( string[] args )
	{
		Options opts;
		try
		{
			opts = ParseArgs( args );
		}
		catch( ArgumentException ex )
		{
			await Console.Error.WriteLineAsync( $"Error: {ex.Message}" );
			await Console.Error.WriteLineAsync( "Run with --help for usage information." );
			return 2;
		}

		if( opts.ShowHelp )
		{
			PrintHelp( Console.Out );
			return 0;
		}

		if( opts.ShowVersion )
		{
			Console.WriteLine( $"{HackPdmMcpServerFactory.ServerName} {ClientUpdate.CurrentText}" );
			return 0;
		}

		if( opts.ResetUiDefaults || opts.ResetThemeDefaults )
		{
			CoreSettings settings = HackPdmApiService.EnsureHeadlessDefaultsInitialized();
			using HackPdmMcpServerFactory.ServerBundle resetBundle = HackPdmMcpServerFactory.Create(
				mode: McpServerMode.Headless,
				settingsStore: settings,
				dataDirOverride: opts.DataDir );
			object res = await resetBundle.Api.ResetUiDefaultsHeadlessAsync( includeFullAppConfig: opts.ResetUiDefaults );
			Console.WriteLine( JsonSerializer.Serialize( res, PrettyJson ) );
			return 0;
		}

		// Initialize permissions manager (loads <AppDataPaths.Config>/mcp-permissions.json or --permissions-file).
		McpPermissionManager permissions = new(
			configFilePathOverride: opts.PermissionsFile,
			defaultPresetWhenUnconfigured: "review-critical",
			persistChanges: true );

		try
		{
			// Handle one-shot CLI permission/profile management commands cleanly before starting transport.
			if( opts.ListPresets || opts.ShowPermissions || !string.IsNullOrWhiteSpace( opts.SaveProfileName )
				|| !string.IsNullOrWhiteSpace( opts.SetActiveProfileName ) || !string.IsNullOrWhiteSpace( opts.DeleteProfileName ) )
			{
				return RunPermissionManagementCommand( permissions, opts );
			}

			ApplyPermissionRuntimeOverrides( permissions, opts );
		}
		catch( Exception ex )
		{
			await Console.Error.WriteLineAsync( $"Permission configuration error: {ex.Message}" );
			return 2;
		}

		// In stdio mode, capture the raw stdout stream for MCP JSON-RPC frames and redirect
		// Console.Out to Console.Error so stray library writes can never corrupt JSON-RPC framing.
		Stream? rawStdout = null;
		if( !opts.UseHttp )
		{
			rawStdout = Console.OpenStandardOutput();
			Console.SetOut( Console.Error );
		}

		TextWriter log = Console.Error;

		CoreSettings coreSettings = HackPdmApiService.EnsureHeadlessDefaultsInitialized();
		ApplyEnvironmentOverrides();

		using HackPdmMcpServerFactory.ServerBundle bundle = HackPdmMcpServerFactory.Create(
			mode: opts.Mode,
			localUiBridge: null,
			settingsStore: coreSettings,
			explicitClientUrl: opts.ClientUrl,
			explicitClientToken: opts.ClientToken,
			dataDirOverride: opts.DataDir,
			sharedPermissions: permissions );

		McpPermissionProfile activePerm = permissions.ActiveProfile;
		await log.WriteLineAsync(
			$"[hackpdm-mcp] Permissions: mode={activePerm.Mode}, profile='{activePerm.Key}' ({activePerm.Name}), config='{permissions.ConfigFilePath}'{( permissions.HasRuntimeOverrides ? " [CLI overrides active]" : "" )}" );

		if( !string.IsNullOrWhiteSpace( opts.ProfilePath ) )
		{
			try
			{
				await bundle.Api.LoadProfileAsync( opts.ProfilePath, CancellationToken.None );
				await log.WriteLineAsync( $"[hackpdm-mcp] Loaded profile: {opts.ProfilePath}" );
			}
			catch( Exception ex )
			{
				await log.WriteLineAsync( $"[hackpdm-mcp] Warning: failed to load profile '{opts.ProfilePath}': {ex.Message}" );
			}
		}

		using CancellationTokenSource cts = new();
		Console.CancelKeyPress += ( _, e ) =>
		{
			e.Cancel = true;
			cts.Cancel();
		};
		AppDomain.CurrentDomain.ProcessExit += ( _, _ ) =>
		{
			try { cts.Cancel(); } catch { }
		};

		// Opportunistically detect an already-running HackPDM GUI bridge on startup.
		bool attachedOnStart = false;
		if( opts.Mode is McpServerMode.Auto or McpServerMode.LiveOnly )
		{
			try
			{
				var (attached, endpoint, _) = await bundle.Bridge.TryAttachAsync( opts.ClientUrl, opts.ClientToken, cts.Token );
				if( attached && endpoint is not null )
				{
					attachedOnStart = true;
					await log.WriteLineAsync( $"[hackpdm-mcp] Attached to live HackPDM GUI at {endpoint.McpUrl} (PID {endpoint.Pid})." );
				}
			}
			catch( Exception ex )
			{
				await log.WriteLineAsync( $"[hackpdm-mcp] Live GUI probe skipped: {ex.Message}" );
			}
		}

		if( opts.AutoLogin && !attachedOnStart )
		{
			try
			{
				object loginRes = await bundle.Api.LoginAsync(
					forceRelogin: true,
					saveCredentials: false,
					checkClientVersion: true,
					cancellationToken: cts.Token );
				await log.WriteLineAsync( $"[hackpdm-mcp] Headless auto-login completed: {JsonSerializer.Serialize( loginRes )}" );
			}
			catch( Exception ex )
			{
				await log.WriteLineAsync( $"[hackpdm-mcp] Warning: headless auto-login failed: {ex.Message}" );
			}
		}

		if( opts.UseHttp )
		{
			string? authToken = opts.NoAuth
				? null
				: ( !string.IsNullOrEmpty( opts.Token ) ? opts.Token : McpHttpServer.GenerateSecureToken() );

			using McpHttpServer httpServer = new(
				bundle.Server,
				port: opts.Port ?? DefaultHttpPort,
				authToken: authToken,
				healthProvider: () => new
				{
					status = "ok",
					mode = opts.Mode.ToString(),
					attached = bundle.Bridge.IsProxiedAttached,
					permissionMode = permissions.ActiveProfile.Mode,
					permissionProfile = permissions.ActiveProfile.Key,
					appVersion = ClientUpdate.CurrentText,
				} );

			httpServer.Start();
			await log.WriteLineAsync( $"[hackpdm-mcp] Streamable HTTP MCP server listening at {httpServer.McpEndpointUrl} (mode={opts.Mode})" );
			if( authToken is not null )
				await log.WriteLineAsync( $"[hackpdm-mcp] Authorization: Bearer {authToken}" );
			else
				await log.WriteLineAsync( "[hackpdm-mcp] WARNING: Running loopback HTTP without Bearer auth (--no-auth)." );

			try
			{
				await Task.Delay( Timeout.InfiniteTimeSpan, cts.Token );
			}
			catch( OperationCanceledException )
			{
				// Normal shutdown
			}

			await log.WriteLineAsync( "[hackpdm-mcp] HTTP server stopped." );
			return 0;
		}
		else
		{
			await log.WriteLineAsync( $"[hackpdm-mcp] Stdio MCP server ready (mode={opts.Mode}, attached={attachedOnStart})." );
			McpStdioTransport transport = new(
				bundle.Server,
				input: Console.OpenStandardInput(),
				output: rawStdout,
				errorLog: log );
			await transport.RunAsync( cts.Token );
			return 0;
		}
	}

	private static int RunPermissionManagementCommand( McpPermissionManager permissions, Options opts )
	{
		if( !string.IsNullOrWhiteSpace( opts.SetActiveProfileName ) )
		{
			McpPermissionProfile selected = permissions.SelectProfile( opts.SetActiveProfileName, persistOverride: true );
			Console.WriteLine( JsonSerializer.Serialize( new
			{
				ok = true,
				action = "set-active-profile",
				configFilePath = permissions.ConfigFilePath,
				activeProfile = selected,
			}, PrettyJson ) );
			return 0;
		}

		if( !string.IsNullOrWhiteSpace( opts.DeleteProfileName ) )
		{
			bool deleted = permissions.DeleteCustomProfile( opts.DeleteProfileName, persistOverride: true );
			Console.WriteLine( JsonSerializer.Serialize( new
			{
				ok = deleted,
				action = "delete-profile",
				deleted,
				target = opts.DeleteProfileName,
				configFilePath = permissions.ConfigFilePath,
				activeProfile = permissions.ActiveProfile.Key,
			}, PrettyJson ) );
			return deleted ? 0 : 1;
		}

		if( !string.IsNullOrWhiteSpace( opts.SaveProfileName ) )
		{
			ApplyPermissionRuntimeOverrides( permissions, opts );
			McpPermissionProfile saved = permissions.SaveCustomProfile(
				name: opts.SaveProfileName,
				description: opts.ProfileDescription,
				activate: true,
				persistOverride: true );
			Console.WriteLine( JsonSerializer.Serialize( new
			{
				ok = true,
				action = "save-profile",
				configFilePath = permissions.ConfigFilePath,
				profile = saved,
			}, PrettyJson ) );
			return 0;
		}

		if( opts.ListPresets )
		{
			Console.WriteLine( JsonSerializer.Serialize( new
			{
				configFilePath = permissions.ConfigFilePath,
				activeProfile = permissions.ActiveProfile.Key,
				activeMode = permissions.ActiveProfile.Mode,
				modes = Enum.GetValues<McpExecutionMode>().Select( m => new
				{
					mode = McpExecutionModeHelper.ToWireName( m ),
					description = McpExecutionModeHelper.Describe( m ),
				} ).ToList(),
				capabilities = McpPermissionManager.Capabilities,
				profiles = permissions.ListAllProfiles(),
			}, PrettyJson ) );
			return 0;
		}

		// --show-permissions
		ApplyPermissionRuntimeOverrides( permissions, opts );
		Console.WriteLine( JsonSerializer.Serialize( permissions.GetStatusSnapshot(), PrettyJson ) );
		return 0;
	}

	private static void ApplyPermissionRuntimeOverrides( McpPermissionManager permissions, Options opts )
	{
		bool hasAny = !string.IsNullOrWhiteSpace( opts.PermissionPresetOrProfile )
			|| opts.PermissionMode.HasValue
			|| opts.AllowCategories.Count > 0
			|| opts.DenyCategories.Count > 0
			|| opts.AllowTools.Count > 0
			|| opts.DenyTools.Count > 0
			|| opts.ReviewTools.Count > 0
			|| opts.ReviewTimeoutSeconds.HasValue;

		if( !hasAny )
			return;

		permissions.ApplyRuntimeOverrides(
			presetOrProfile: opts.PermissionPresetOrProfile,
			mode: opts.PermissionMode,
			allowCategories: opts.AllowCategories,
			denyCategories: opts.DenyCategories,
			allowTools: opts.AllowTools,
			denyTools: opts.DenyTools,
			requireReviewTools: opts.ReviewTools,
			reviewTimeoutSeconds: opts.ReviewTimeoutSeconds );
	}

	private static void ApplyEnvironmentOverrides()
	{
		bool endpointChanged = false;
		if( Environment.GetEnvironmentVariable( "HACKPDM_ODOO_ADDRESS" ) is { Length: > 0 } addr )
		{
			OdooDefaults.Instance.OdooAddress = addr;
			endpointChanged = true;
		}
		if( Environment.GetEnvironmentVariable( "HACKPDM_ODOO_PORT" ) is { Length: > 0 } port )
		{
			OdooDefaults.Instance.OdooPort = port;
			endpointChanged = true;
		}
		if( Environment.GetEnvironmentVariable( "HACKPDM_ODOO_DB" ) is { Length: > 0 } db )
			OdooDefaults.Instance.OdooDb = db;
		if( Environment.GetEnvironmentVariable( "HACKPDM_ODOO_USER" ) is { Length: > 0 } user )
			OdooDefaults.Instance.OdooUser = user;
		if( Environment.GetEnvironmentVariable( "HACKPDM_ODOO_PASSWORD" ) is { Length: > 0 } pass )
			OdooDefaults.Instance.OdooPass = pass;
		if( Environment.GetEnvironmentVariable( "HACKPDM_ODOO_HTTPS" ) is { Length: > 0 } httpsStr )
		{
			OdooDefaults.Instance.OdooUseHttps = httpsStr is "1" or "true" or "TRUE" or "yes" or "YES";
			endpointChanged = true;
		}
		if( endpointChanged )
		{
			OdooDefaults.Instance.OdooUrl = OdooEndpoint.BuildUrl(
				OdooDefaults.Instance.OdooAddress,
				OdooDefaults.Instance.OdooPort,
				OdooDefaults.Instance.OdooUseHttps );
		}
		if( Environment.GetEnvironmentVariable( "HACKPDM_PWA_PATH" ) is { Length: > 0 } pwa )
			HackDefaults.Instance.PwaPathAbsolute = pwa;
		if( Environment.GetEnvironmentVariable( "HACKPDM_TEMP_PATH" ) is { Length: > 0 } tmp )
			StorageBox.TemporaryPath = tmp;
	}

	private static Options ParseArgs( string[] args )
	{
		McpServerMode mode = ParseMode( Environment.GetEnvironmentVariable( "HACKPDM_MCP_MODE" ) ?? "auto" );
		bool useHttp = false;
		int? port = int.TryParse( Environment.GetEnvironmentVariable( "HACKPDM_MCP_PORT" ), out int envPort ) ? envPort : null;
		string? token = Environment.GetEnvironmentVariable( "HACKPDM_MCP_TOKEN" );
		bool noAuth = false;
		string? clientUrl = Environment.GetEnvironmentVariable( "HACKPDM_MCP_URL" );
		string? clientToken = Environment.GetEnvironmentVariable( "HACKPDM_MCP_CLIENT_TOKEN" );
		string? dataDir = null;
		string? profilePath = Environment.GetEnvironmentVariable( "HACKPDM_PROFILE" );
		bool autoLogin = Environment.GetEnvironmentVariable( "HACKPDM_AUTO_LOGIN" ) is "1" or "true" or "TRUE" or "yes";
		bool showHelp = false;
		bool showVersion = false;

		string? permissionsFile = Environment.GetEnvironmentVariable( "HACKPDM_MCP_PERMISSIONS_FILE" );
		string? permissionPresetOrProfile = Environment.GetEnvironmentVariable( "HACKPDM_MCP_PERMISSION_PROFILE" )
			?? Environment.GetEnvironmentVariable( "HACKPDM_MCP_PRESET" );
		McpExecutionMode? permissionMode = null;
		if( Environment.GetEnvironmentVariable( "HACKPDM_MCP_PERMISSION_MODE" ) is { Length: > 0 } envPermMode )
			permissionMode = ParsePermissionMode( envPermMode );
		int? reviewTimeoutSeconds = int.TryParse( Environment.GetEnvironmentVariable( "HACKPDM_MCP_REVIEW_TIMEOUT" ), out int envTimeout )
			? envTimeout
			: null;

		List<McpCapabilityCategory> allowCategories = [];
		List<McpCapabilityCategory> denyCategories = [];
		List<string> allowTools = [];
		List<string> denyTools = [];
		List<string> reviewTools = [];

		bool showPermissions = false;
		bool listPresets = false;
		string? saveProfileName = null;
		string? profileDescription = null;
		string? setActiveProfileName = null;
		string? deleteProfileName = null;
		bool resetUiDefaults = false;
		bool resetThemeDefaults = false;

		for( int i = 0; i < args.Length; i++ )
		{
			string arg = args[ i ];
			string NextValue( string flag )
			{
				if( i + 1 >= args.Length )
					throw new ArgumentException( $"Missing value for '{flag}'." );
				return args[ ++i ];
			}

			switch( arg )
			{
				case "-h":
				case "--help":
					showHelp = true;
					break;
				case "-v":
				case "--version":
					showVersion = true;
					break;
				case "--stdio":
					useHttp = false;
					break;
				case "--http":
					useHttp = true;
					break;
				case "--no-auth":
					noAuth = true;
					break;
				case "--auto-login":
					autoLogin = true;
					break;
				case "--mode":
					mode = ParseMode( NextValue( arg ) );
					break;
				case "--port":
					port = ParsePort( NextValue( arg ) );
					break;
				case "--token":
					token = NextValue( arg );
					break;
				case "--client-url":
					clientUrl = NextValue( arg );
					break;
				case "--client-token":
					clientToken = NextValue( arg );
					break;
				case "--data-dir":
					dataDir = NextValue( arg );
					break;
				case "--profile":
					profilePath = NextValue( arg );
					break;

				// Permission & profile flags
				case "--permission-mode":
					permissionMode = ParsePermissionMode( NextValue( arg ) );
					break;
				case "--preset":
				case "--permission-profile":
					permissionPresetOrProfile = NextValue( arg );
					break;
				case "--permissions-file":
					permissionsFile = NextValue( arg );
					break;
				case "--allow-category":
					AddCategories( allowCategories, NextValue( arg ) );
					break;
				case "--deny-category":
					AddCategories( denyCategories, NextValue( arg ) );
					break;
				case "--allow-tool":
					AddTokens( allowTools, NextValue( arg ) );
					break;
				case "--deny-tool":
					AddTokens( denyTools, NextValue( arg ) );
					break;
				case "--review-tool":
					AddTokens( reviewTools, NextValue( arg ) );
					break;
				case "--review-timeout":
					reviewTimeoutSeconds = ParseTimeout( NextValue( arg ) );
					break;

				// One-shot permission management commands
				case "--show-permissions":
					showPermissions = true;
					break;
				case "--list-presets":
					listPresets = true;
					break;
				case "--save-profile":
					saveProfileName = NextValue( arg );
					break;
				case "--profile-description":
					profileDescription = NextValue( arg );
					break;
				case "--set-active-profile":
					setActiveProfileName = NextValue( arg );
					break;
				case "--delete-profile":
					deleteProfileName = NextValue( arg );
					break;
				case "--reset-ui-defaults":
					resetUiDefaults = true;
					break;
				case "--reset-theme-defaults":
					resetThemeDefaults = true;
					break;

				default:
					if( TrySplitOption( arg, "--mode=", out string? v ) ) mode = ParseMode( v! );
					else if( TrySplitOption( arg, "--port=", out v ) ) port = ParsePort( v! );
					else if( TrySplitOption( arg, "--token=", out v ) ) token = v;
					else if( TrySplitOption( arg, "--client-url=", out v ) ) clientUrl = v;
					else if( TrySplitOption( arg, "--client-token=", out v ) ) clientToken = v;
					else if( TrySplitOption( arg, "--data-dir=", out v ) ) dataDir = v;
					else if( TrySplitOption( arg, "--profile=", out v ) ) profilePath = v;
					else if( TrySplitOption( arg, "--permission-mode=", out v ) ) permissionMode = ParsePermissionMode( v! );
					else if( TrySplitOption( arg, "--preset=", out v ) ) permissionPresetOrProfile = v;
					else if( TrySplitOption( arg, "--permission-profile=", out v ) ) permissionPresetOrProfile = v;
					else if( TrySplitOption( arg, "--permissions-file=", out v ) ) permissionsFile = v;
					else if( TrySplitOption( arg, "--allow-category=", out v ) ) AddCategories( allowCategories, v! );
					else if( TrySplitOption( arg, "--deny-category=", out v ) ) AddCategories( denyCategories, v! );
					else if( TrySplitOption( arg, "--allow-tool=", out v ) ) AddTokens( allowTools, v! );
					else if( TrySplitOption( arg, "--deny-tool=", out v ) ) AddTokens( denyTools, v! );
					else if( TrySplitOption( arg, "--review-tool=", out v ) ) AddTokens( reviewTools, v! );
					else if( TrySplitOption( arg, "--review-timeout=", out v ) ) reviewTimeoutSeconds = ParseTimeout( v! );
					else if( TrySplitOption( arg, "--save-profile=", out v ) ) saveProfileName = v;
					else if( TrySplitOption( arg, "--profile-description=", out v ) ) profileDescription = v;
					else if( TrySplitOption( arg, "--set-active-profile=", out v ) ) setActiveProfileName = v;
					else if( TrySplitOption( arg, "--delete-profile=", out v ) ) deleteProfileName = v;
					else
						throw new ArgumentException( $"Unknown argument '{arg}'." );
					break;
			}
		}

		return new Options(
			Mode: mode,
			UseHttp: useHttp,
			Port: port,
			Token: token,
			NoAuth: noAuth,
			ClientUrl: clientUrl,
			ClientToken: clientToken,
			DataDir: dataDir,
			ProfilePath: profilePath,
			AutoLogin: autoLogin,
			ShowHelp: showHelp,
			ShowVersion: showVersion,
			PermissionsFile: permissionsFile,
			PermissionPresetOrProfile: permissionPresetOrProfile,
			PermissionMode: permissionMode,
			ReviewTimeoutSeconds: reviewTimeoutSeconds,
			AllowCategories: allowCategories,
			DenyCategories: denyCategories,
			AllowTools: allowTools,
			DenyTools: denyTools,
			ReviewTools: reviewTools,
			ShowPermissions: showPermissions,
			ListPresets: listPresets,
			SaveProfileName: saveProfileName,
			ProfileDescription: profileDescription,
			SetActiveProfileName: setActiveProfileName,
			DeleteProfileName: deleteProfileName,
			ResetUiDefaults: resetUiDefaults,
			ResetThemeDefaults: resetThemeDefaults );
	}

	private static bool TrySplitOption( string arg, string prefix, out string? value )
	{
		if( arg.StartsWith( prefix, StringComparison.Ordinal ) )
		{
			value = arg[ prefix.Length.. ];
			return true;
		}
		value = null;
		return false;
	}

	private static int ParsePort( string raw )
	{
		if( !int.TryParse( raw, out int p ) || p < 0 || p > 65535 )
			throw new ArgumentException( "Invalid --port value (must be 0..65535)." );
		return p;
	}

	private static int ParseTimeout( string raw )
	{
		if( !int.TryParse( raw, out int sec ) || sec < 5 || sec > 3600 )
			throw new ArgumentException( "Invalid --review-timeout value (must be 5..3600 seconds)." );
		return sec;
	}

	private static McpExecutionMode ParsePermissionMode( string value )
	{
		if( !McpExecutionModeHelper.TryParse( value, out McpExecutionMode parsed ) )
			throw new ArgumentException( $"Invalid --permission-mode '{value}'. Expected 'auto', 'review', 'review-critical', 'read-only', or 'local-only'." );
		return parsed;
	}

	private static void AddCategories( List<McpCapabilityCategory> target, string csv )
	{
		foreach( string item in csv.Split( ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries ) )
		{
			if( !McpPermissionManager.TryParseCategory( item, out McpCapabilityCategory cat ) )
			{
				string valid = string.Join( ", ", Enum.GetNames<McpCapabilityCategory>() );
				throw new ArgumentException( $"Unknown capability category '{item}'. Valid categories: {valid}" );
			}
			target.Add( cat );
		}
	}

	private static void AddTokens( List<string> target, string csv )
	{
		foreach( string item in csv.Split( ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries ) )
			target.Add( item );
	}

	private static McpServerMode ParseMode( string value ) => value.Trim().ToLowerInvariant() switch
	{
		"auto" => McpServerMode.Auto,
		"live" or "live_only" or "liveonly" => McpServerMode.LiveOnly,
		"headless" => McpServerMode.Headless,
		_ => throw new ArgumentException( $"Invalid mode '{value}'. Expected 'auto', 'live', or 'headless'." ),
	};

	private static void PrintHelp( TextWriter writer )
	{
		writer.WriteLine(
			"""
			HackPDM MCP Server (Model Context Protocol)

			Usage:
			  HackPDM.McpServer [options]

			Transport options:
			  --stdio                         Run MCP JSON-RPC over stdin/stdout (default)
			  --http                          Run Streamable HTTP MCP server on 127.0.0.1
			  --port <port>                   Loopback TCP port for --http (default: 38420; 0 = ephemeral)
			  --token <token>                 Bearer token for --http (default: random token printed to stderr)
			  --no-auth                       Disable Bearer token check for --http (local dev only)

			Bridge & attachment mode options:
			  --mode <mode>                   auto (default) | live | headless
			                                    auto:     attach & proxy to running HackPDM GUI when available, else run headless
			                                    live:     require a running HackPDM GUI bridge
			                                    headless: execute all HackPDM API tools in-process without GUI
			  --client-url <url>              Explicit running HackPDM GUI MCP endpoint (e.g. http://127.0.0.1:43210/mcp)
			  --client-token <token>          Bearer token for --client-url
			  --data-dir <dir>                Override directory containing mcp-bridge.json

			Permission modes, presets & profiles (runtime overrides — do not overwrite config unless saving):
			  --permission-mode <mode>        auto | review | review-critical | read-only | local-only
			                                    auto:            execute all permitted actions without review
			                                    review:          require approval before any mutating action
			                                    review-critical: routine actions run automatically; critical ops (commit, delete,
			                                                     Odoo writes, CAD binary edits, rollbacks) require approval
			                                    read-only:       get/read operations only (local + server reads); no pushing/writing
			                                    local-only:      computer-local operations only (files, SolidWorks, config, local GUI);
			                                                     blocks all remote Odoo/PDM server RPC operations
			  --preset <name>                 Apply built-in preset (read-only | local-only | review | review-critical | local-cad-prep | auto)
			  --permission-profile <name>     Apply a built-in preset or user-saved custom profile by key or name
			  --permissions-file <path>       Override path to mcp-permissions.json
			  --allow-category <cat[,...]>    Enable capability category (repeatable; e.g. SolidWorksEditing, PdmCommitAndPush)
			  --deny-category <cat[,...]>     Disable capability category (repeatable)
			  --allow-tool <tool[,...]>       Allow specific MCP tool without review (repeatable; still blocked in read-only mode)
			  --deny-tool <tool[,...]>        Explicitly deny specific MCP tool (repeatable)
			  --review-tool <tool[,...]>      Always require human review for specific MCP tool (repeatable)
			  --review-timeout <seconds>      Interactive review timeout in seconds (5..3600, default 120)

			Permission & profile management commands (print JSON and exit):
			  --show-permissions              Show effective permissions, active mode, capabilities, and config file path
			  --list-presets                  List all execution modes, built-in presets, custom profiles, and capability categories
			  --save-profile <name>           Save current effective flags/preset as a persistent custom profile and activate it
			  --profile-description <text>    Optional description when using --save-profile
			  --set-active-profile <name>     Persist active built-in preset or custom profile to mcp-permissions.json
			  --delete-profile <name>         Delete a saved custom profile from mcp-permissions.json
			  --reset-theme-defaults          Reset AppConfig.json Theme back to factory defaults (captures backup restore point)
			  --reset-ui-defaults             Reset ALL AppConfig.json UI & Theme sections to factory defaults (captures backup)

			Headless session options:
			  --profile <path>                Load a HackPDM .hp JSON connection profile on startup
			  --auto-login                    Automatically log into Odoo on startup (when headless)

			Environment variables:
			  HACKPDM_MCP_MODE, HACKPDM_MCP_PORT, HACKPDM_MCP_TOKEN,
			  HACKPDM_MCP_URL, HACKPDM_MCP_CLIENT_TOKEN, HACKPDM_PROFILE, HACKPDM_AUTO_LOGIN,
			  HACKPDM_MCP_PERMISSION_MODE, HACKPDM_MCP_PRESET, HACKPDM_MCP_PERMISSION_PROFILE,
			  HACKPDM_MCP_PERMISSIONS_FILE, HACKPDM_MCP_REVIEW_TIMEOUT,
			  HACKPDM_ODOO_ADDRESS, HACKPDM_ODOO_PORT, HACKPDM_ODOO_DB,
			  HACKPDM_ODOO_USER, HACKPDM_ODOO_PASSWORD, HACKPDM_ODOO_HTTPS,
			  HACKPDM_PWA_PATH, HACKPDM_TEMP_PATH
			""" );
	}

	private sealed record Options(
		McpServerMode Mode,
		bool UseHttp,
		int? Port,
		string? Token,
		bool NoAuth,
		string? ClientUrl,
		string? ClientToken,
		string? DataDir,
		string? ProfilePath,
		bool AutoLogin,
		bool ShowHelp,
		bool ShowVersion,
		string? PermissionsFile,
		string? PermissionPresetOrProfile,
		McpExecutionMode? PermissionMode,
		int? ReviewTimeoutSeconds,
		List<McpCapabilityCategory> AllowCategories,
		List<McpCapabilityCategory> DenyCategories,
		List<string> AllowTools,
		List<string> DenyTools,
		List<string> ReviewTools,
		bool ShowPermissions,
		bool ListPresets,
		string? SaveProfileName,
		string? ProfileDescription,
		string? SetActiveProfileName,
		string? DeleteProfileName,
		bool ResetUiDefaults,
		bool ResetThemeDefaults );
}

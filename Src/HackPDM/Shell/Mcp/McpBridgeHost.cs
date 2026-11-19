using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

using HackPDM.Core.Logging;
using HackPDM.Infrastructure.Odoo;
using HackPDM.Infrastructure.Operations;
using HackPDM.Mcp;
using HackPDM.Mcp.Ai;
using HackPDM.Mcp.Bridge;
using HackPDM.Mcp.Database;
using HackPDM.Mcp.Protocol;
using HackPDM.Mcp.Security;
using HackPDM.Mcp.State;
using HackPDM.Mvvm.ViewModels.Common;
using HackPDM.Shell.Testing;
using HackPDM.Shared.GlobalData;

namespace HackPDM.Shell.Mcp;

/// <summary>
/// Hosts the embedded loopback MCP Streamable HTTP server inside the running HackPDM desktop client
/// and publishes its connection info to <c>AppDataPaths.Data/mcp-bridge.json</c> for automatic
/// discovery by <c>HackPDM.McpServer</c> (stdio) or direct Streamable HTTP MCP clients.
/// Also owns the process-wide <see cref="McpActivityLog"/>, <see cref="McpRestorePointManager"/>,
/// and <see cref="McpPermissionManager"/> displayed in the HackPDM MCP Settings GUI.
/// </summary>
internal static class McpBridgeHost
{
	public const string EnabledEnvVar = "HACKPDM_MCP_ENABLED";
	public const string PortEnvVar = "HACKPDM_MCP_PORT";
	public const string TokenEnvVar = "HACKPDM_MCP_TOKEN";
	public const string PermissionsFileEnvVar = "HACKPDM_MCP_PERMISSIONS_FILE";
	public const string PermissionModeEnvVar = "HACKPDM_MCP_PERMISSION_MODE";
	public const string PresetEnvVar = "HACKPDM_MCP_PRESET";
	public const string ProfileEnvVar = "HACKPDM_MCP_PERMISSION_PROFILE";

	private static readonly object Gate = new();
	private static HackPdmMcpServerFactory.ServerBundle? _bundle;
	private static McpHttpServer? _httpServer;
	private static McpActivityLog? _activityLog;
	private static McpRestorePointManager? _restorePoints;
	private static McpPermissionManager? _permissions;
	private static AiProviderStore? _aiProviders;
	private static AiChatOrchestrator? _aiChat;
	private static OdooPostgresService? _postgres;
	private static HackPdmMcpServerFactory.ServerBundle? _paletteBundle;
	private static string? _authToken;
	private static bool _processExitHooked;

	public static event Action? StatusChanged;

	/// <summary>Set by <c>McpSettingsView</c> while it is visible so live reviews render inline without modal popups.</summary>
	public static bool IsMcpViewActive { get; set; }

	public static int? Port => _httpServer?.Port;
	public static string? McpEndpointUrl => _httpServer?.McpEndpointUrl;
	public static string? AuthToken => _authToken;
	public static string DiscoveryFilePath => McpBridgeDiscovery.GetDiscoveryFilePath();
	public static bool IsRunning => _httpServer?.IsRunning is true;

	public static string MaskedAuthToken
	{
		get
		{
			string? token = _authToken;
			if( string.IsNullOrEmpty( token ) )
				return "(not running)";
			if( token.Length <= 10 )
				return new string( '•', token.Length );
			return $"{token[ ..6 ]}••••••••••{token[ ^4.. ]}";
		}
	}

	public static McpActivityLog ActivityLog
	{
		get
		{
			lock( Gate )
				return _activityLog ??= new McpActivityLog();
		}
	}

	public static AiProviderStore AiProviders
	{
		get
		{
			lock( Gate )
				return _aiProviders ??= new AiProviderStore();
		}
	}

	public static AiChatOrchestrator AiChat
	{
		get
		{
			lock( Gate )
				return _aiChat ??= new AiChatOrchestrator();
		}
	}

	public static OdooPostgresService Postgres
	{
		get
		{
			lock( Gate )
				return _postgres ??= new OdooPostgresService();
		}
	}

	/// <summary>
	/// Returns the live in-process <see cref="McpServer"/> (preferring the active HTTP bridge's server,
	/// or lazily creating an embedded UI-bridged instance for the Command Palette when the bridge is stopped).
	/// </summary>
	public static McpServer GetOrCreateEmbeddedMcpServer()
	{
		lock( Gate )
		{
			if( _bundle is not null )
				return _bundle.Server;
			if( _paletteBundle is not null )
				return _paletteBundle.Server;

			_paletteBundle = HackPdmMcpServerFactory.Create(
				mode: McpServerMode.EmbeddedGui,
				localUiBridge: new LiveGuiClientBridge(),
				settingsStore: AppComposition.Settings,
				sharedActivityLog: ActivityLog,
				sharedRestorePoints: RestorePoints,
				sharedPermissions: Permissions,
				sharedPostgres: Postgres );
			return _paletteBundle.Server;
		}
	}

	/// <summary>Returns the recommended stdio launch command + args for connecting external AI clients.</summary>
	public static (string Command, IReadOnlyList<string> Args) ResolveClientStdioLaunch()
	{
		string dllPath = ResolveMcpServerDllPath();
		return ( "dotnet", [ dllPath, "--mode", "auto" ] );
	}

	public static McpRestorePointManager RestorePoints
	{
		get
		{
			lock( Gate )
			{
				return _restorePoints ??= new McpRestorePointManager(
					settings: AppComposition.Settings,
					settingsFileResolver: () => Path.Combine( AppDataPaths.Config, "settings.json" ),
					appConfigFileResolver: () => Path.Combine(
						AppDataPaths.IsConfigOverridden ? AppDataPaths.Config : Environment.GetFolderPath( Environment.SpecialFolder.Personal ),
						"AppConfig.json" ) );
			}
		}
	}

	public static McpPermissionManager Permissions
	{
		get
		{
			lock( Gate )
			{
				if( _permissions is not null )
					return _permissions;

				string? permFile = Environment.GetEnvironmentVariable( PermissionsFileEnvVar );
				McpPermissionManager mgr = new(
					configFilePathOverride: string.IsNullOrWhiteSpace( permFile ) ? null : permFile,
					defaultPresetWhenUnconfigured: "review-critical",
					persistChanges: true )
				{
					HasInteractiveReviewer = true,
				};

				// Optional startup environment overrides for scripted/CI launches
				string? envPreset = Environment.GetEnvironmentVariable( ProfileEnvVar )
					?? Environment.GetEnvironmentVariable( PresetEnvVar );
				string? envModeStr = Environment.GetEnvironmentVariable( PermissionModeEnvVar );
				McpExecutionMode? envMode = !string.IsNullOrWhiteSpace( envModeStr ) && McpExecutionModeHelper.TryParse( envModeStr, out McpExecutionMode parsed )
					? parsed
					: null;
				if( !string.IsNullOrWhiteSpace( envPreset ) || envMode.HasValue )
				{
					try
					{
						mgr.ApplyRuntimeOverrides( presetOrProfile: envPreset, mode: envMode );
					}
					catch( Exception ex )
					{
						HackLogger.Warn( "MCP", $"Ignoring invalid MCP permission environment override: {ex.Message}" );
					}
				}

				mgr.ReviewRequested += OnReviewRequested;
				_permissions = mgr;
				return _permissions;
			}
		}
	}

	public static void Start( bool force = false )
	{
		if( !force && !ShouldStart() )
			return;

		lock( Gate )
		{
			if( _httpServer is not null )
				return;

			try
			{
				int configuredPort = ResolvePort();
				string token = Environment.GetEnvironmentVariable( TokenEnvVar ) is { Length: > 0 } envToken
					? envToken
					: McpHttpServer.GenerateSecureToken();

				McpActivityLog log = ActivityLog;
				McpRestorePointManager rp = RestorePoints;
				McpPermissionManager perms = Permissions;

				LiveGuiClientBridge uiBridge = new();
				_bundle = HackPdmMcpServerFactory.Create(
					mode: McpServerMode.EmbeddedGui,
					localUiBridge: uiBridge,
					settingsStore: AppComposition.Settings,
					sharedActivityLog: log,
					sharedRestorePoints: rp,
					sharedPermissions: perms,
					sharedPostgres: Postgres );

				_httpServer = new McpHttpServer(
					_bundle.Server,
					port: configuredPort,
					authToken: token,
					healthProvider: BuildHealth );
				_httpServer.Start();
				_authToken = token;

				McpBridgeEndpointInfo endpointInfo = new(
					Pid: Environment.ProcessId,
					Port: _httpServer.Port,
					BaseUrl: _httpServer.BaseUrl,
					McpUrl: _httpServer.McpEndpointUrl,
					Token: token,
					StartedAtUtc: DateTimeOffset.UtcNow,
					AppVersion: ClientUpdate.CurrentText,
					Platform: $"{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})" );

				McpBridgeDiscovery.WriteDiscoveryFile( endpointInfo );

				if( !_processExitHooked )
				{
					_processExitHooked = true;
					AppDomain.CurrentDomain.ProcessExit += ( _, _ ) => Stop();
				}

				HackLogger.Info(
					"MCP",
					$"Embedded HackPDM MCP bridge listening at {_httpServer.McpEndpointUrl} " +
					$"(mode={perms.ActiveProfile.Mode}, profile={perms.ActiveProfile.Key}, discovery={McpBridgeDiscovery.GetDiscoveryFilePath()})" );
			}
			catch( Exception ex )
			{
				HackLogger.Warn( "MCP", $"Embedded HackPDM MCP bridge could not start: {ex.Message}", ex );
				StopLocked();
			}
		}

		NotifyStatusChanged();
	}

	public static void Stop()
	{
		lock( Gate )
			StopLocked();
		NotifyStatusChanged();
	}

	public static void Restart()
	{
		lock( Gate )
			StopLocked();
		Start( force: true );
	}

	public static string BuildClientCommandPreview()
		=> $"dotnet \"{ResolveMcpServerDllPath()}\" --mode auto";

	public static string BuildClientConfigSnippet() => BuildStdioClientConfigSnippet();

	public static string BuildStdioClientConfigSnippet()
	{
		string mcpDll = ResolveMcpServerDllPath();
		return $$"""
			{
			  "mcpServers": {
			    "hackpdm": {
			      "command": "dotnet",
			      "args": ["{{mcpDll.Replace( "\\", "\\\\" )}}", "--mode", "auto"]
			    }
			  }
			}
			""";
	}

	public static string BuildHttpClientConfigSnippet()
	{
		string url = McpEndpointUrl ?? "http://127.0.0.1:<port>/mcp";
		string token = AuthToken ?? "<start-server-to-generate-token>";
		return $$"""
			{
			  "mcpServers": {
			    "hackpdm-live": {
			      "type": "http",
			      "url": "{{url}}",
			      "headers": {
			        "Authorization": "Bearer {{token}}"
			      }
			    }
			  }
			}
			""";
	}

	private static void OnReviewRequested( McpPendingReview review )
	{
		if( IsMcpViewActive || !Permissions.PromptDialogWhenHidden )
			return;

		UiDispatcher.Post( async () =>
		{
			try
			{
				// Re-check in case user navigated to the MCP panel or already resolved it
				if( IsMcpViewActive || !Permissions.PendingReviews.Any( r => r.Id == review.Id ) )
					return;

				string argsLine = string.IsNullOrWhiteSpace( review.ArgsSummary )
					? "(no arguments)"
					: review.ArgsSummary;
				string message =
					$"An MCP AI client requested a {review.RiskLevel} action:\n\n" +
					$"Tool: {review.ToolName}\n" +
					$"Category: {review.Category}\n" +
					$"Reason: {review.Reason}\n" +
					$"Arguments: {argsLine}\n\n" +
					"Select Yes to APPROVE ONCE, or No to DENY.\n" +
					"(Tip: Open the MCP Server sidebar page to also Approve for Session.)";

				bool approved = await AppComposition.Dialogs.ConfirmAsync(
					message,
					$"HackPDM MCP Action Review [{review.RiskLevel}]",
					MessageBoxIcon.Warning );

				Permissions.ResolveReview( review.Id, approved ? McpReviewDecision.ApprovedOnce : McpReviewDecision.Denied );
			}
			catch( Exception ex )
			{
				HackLogger.Warn( "MCP", $"Review popup prompt failed for '{review.ToolName}': {ex.Message}" );
			}
		} );
	}

	private static string ResolveMcpServerDllPath()
	{
		string local = Path.Combine( AppContext.BaseDirectory, "HackPDM.McpServer.dll" );
		if( File.Exists( local ) )
			return local;

		// Walk up in dev checkouts to locate Src/HackPDM.McpServer/bin/.../HackPDM.McpServer.dll
		for( DirectoryInfo? dir = new( AppContext.BaseDirectory ); dir is not null; dir = dir.Parent )
		{
			string candidateDebug = Path.Combine( dir.FullName, "Src", "HackPDM.McpServer", "bin", "Debug", "net10.0", "HackPDM.McpServer.dll" );
			if( File.Exists( candidateDebug ) )
				return candidateDebug;
			string candidateRelease = Path.Combine( dir.FullName, "Src", "HackPDM.McpServer", "bin", "Release", "net10.0", "HackPDM.McpServer.dll" );
			if( File.Exists( candidateRelease ) )
				return candidateRelease;
		}
		return local;
	}

	private static void StopLocked()
	{
		try { _httpServer?.Dispose(); } catch { }
		try { _bundle?.Dispose(); } catch { }
		_httpServer = null;
		_bundle = null;
		_authToken = null;
		McpBridgeDiscovery.RemoveDiscoveryFile( Environment.ProcessId );
	}

	private static object BuildHealth() => new
	{
		status = "ok",
		mode = "live_gui",
		pid = Environment.ProcessId,
		port = _httpServer?.Port ?? 0,
		appVersion = ClientUpdate.CurrentText,
		loggedIn = AppComposition.Session.IsLoggedIn && OdooDefaults.Instance.OdooId > 0,
		hasUiBridge = true,
		permissionMode = _permissions?.ActiveProfile.Mode ?? "review-critical",
		permissionProfile = _permissions?.ActiveProfile.Key ?? "review-critical",
		pendingReviewCount = _permissions?.PendingReviews.Count ?? 0,
		activityCount = _activityLog?.Count ?? 0,
		restorePointCount = _restorePoints?.ListRestorePoints( 500 ).Count ?? 0,
	};

	private static bool ShouldStart()
	{
		string? env = Environment.GetEnvironmentVariable( EnabledEnvVar );
		if( !string.IsNullOrWhiteSpace( env ) )
			return env is "1" or "true" or "TRUE" or "True" or "yes" or "YES";

		// Do not start automatically during CI smoke runs unless explicitly opted in
		if( SmokeTestRunner.IsEnabled )
			return false;

		return AppComposition.Settings.Get( "McpBridgeEnabled", true );
	}

	private static int ResolvePort()
	{
		if( Environment.GetEnvironmentVariable( PortEnvVar ) is { Length: > 0 } envPort
			&& int.TryParse( envPort, out int parsed )
			&& parsed is >= 0 and <= 65535 )
		{
			return parsed;
		}
		int saved = AppComposition.Settings.Get( "McpBridgePort", 0 );
		return saved is >= 0 and <= 65535 ? saved : 0;
	}

	private static void NotifyStatusChanged()
	{
		try { StatusChanged?.Invoke(); } catch { }
	}
}

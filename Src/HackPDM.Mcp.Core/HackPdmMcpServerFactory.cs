using HackPDM.Core.Configuration;
using HackPDM.Infrastructure.Operations;
using HackPDM.Mcp.Bridge;
using HackPDM.Mcp.Database;
using HackPDM.Mcp.Protocol;
using HackPDM.Mcp.Resources;
using HackPDM.Mcp.Security;
using HackPDM.Mcp.Services;
using HackPDM.Mcp.State;
using HackPDM.Mcp.Tools;

namespace HackPDM.Mcp;

/// <summary>
/// Builds a fully configured HackPDM <see cref="McpServer"/> with all tools, resources, prompts,
/// persistent activity log, restore point manager, permission/review policy manager, and either
/// live embedded GUI control or standalone/auto-proxying bridge coordination.
/// </summary>
public static class HackPdmMcpServerFactory
{
	public const string ServerName = "hackpdm-mcp";
	public const string ServerTitle = "HackPDM Client MCP Server";

	public const string DefaultInstructions =
		"HackPDM MCP Server provides complete API, filesystem, SolidWorks CAD editing, and live UI control over the HackPDM Product Data Management client. " +
		"Start with `client_status` and `mcp_permissions_get` to see whether you are attached to a running HackPDM desktop GUI (`live_gui` / `proxied_to_live_gui`) or running `headless`, and which permission mode (`auto`, `review`, `review-critical`, `read-only`, `local-only`) is active. " +
		"All PDM/Odoo/SolidWorks/local-vault/computer-wide-fs/config API tools work in both headless and live GUI modes (subject to the user's active permission profile). " +
		"Every MCP tool call is logged to the persistent activity log (`mcp_log_get`) and mutating file/CAD/config edits automatically capture undoable restore points (`mcp_restore_point_*`) when enabled. " +
		"Use `fs_import_to_pwa`, `sw_set_properties`, and `sw_set_dependencies` to move/copy CAD files from outside the PWA into the vault and update internal SolidWorks properties and dependency paths cross-platform. " +
		"All `ui_*` tools inspect and control the live WinUI/Uno desktop client (visual tree, buttons/inputs/selectors, MVVM commands, File Manager, Dependency Graph, Search, Status, Theme, MCP Server settings, Dialogs, Screenshots).";

	public sealed record ServerBundle(
		McpServer Server,
		HackPdmApiService Api,
		HackPdmBridgeCoordinator Bridge,
		McpActivityLog ActivityLog,
		McpRestorePointManager RestorePoints,
		McpPermissionManager Permissions ) : IDisposable
	{
		public void Dispose() => Bridge.Dispose();
	}

	public static ServerBundle Create(
		McpServerMode mode,
		IHackPdmUiBridge? localUiBridge = null,
		CoreSettings? settingsStore = null,
		string? explicitClientUrl = null,
		string? explicitClientToken = null,
		string? dataDirOverride = null,
		McpActivityLog? sharedActivityLog = null,
		McpRestorePointManager? sharedRestorePoints = null,
		McpPermissionManager? sharedPermissions = null,
		string? permissionsFilePathOverride = null,
		string defaultPermissionPresetWhenUnconfigured = "auto",
		OdooPostgresService? sharedPostgres = null,
		string? postgresConfigFilePathOverride = null )
	{
		HackPdmBridgeCoordinator bridge = new(
			mode: mode,
			localUiBridge: localUiBridge,
			explicitClientUrl: explicitClientUrl,
			explicitClientToken: explicitClientToken,
			dataDirOverride: dataDirOverride );

		HackPdmApiService api = new( localUiBridge, settingsStore );
		McpActivityLog activityLog = sharedActivityLog ?? new McpActivityLog( dataDirOverride );
		McpRestorePointManager restorePoints = sharedRestorePoints ?? new McpRestorePointManager(
			settings: settingsStore,
			dataDirOverride: dataDirOverride,
			settingsFileResolver: () => api.SettingsFilePath,
			appConfigFileResolver: () => api.AppConfigFilePath );
		api.RestorePoints = restorePoints;
		OdooPostgresService postgres = sharedPostgres ?? new OdooPostgresService(
			configFilePathOverride: postgresConfigFilePathOverride ?? ( dataDirOverride is not null ? Path.Combine( dataDirOverride, "mcp-postgres.json" ) : null ),
			persistChanges: true );
		api.Postgres = postgres;

		McpPermissionManager permissions;
		if( sharedPermissions is not null )
		{
			permissions = sharedPermissions;
		}
		else if( !string.IsNullOrWhiteSpace( permissionsFilePathOverride ) )
		{
			permissions = new McpPermissionManager(
				configFilePathOverride: permissionsFilePathOverride,
				defaultPresetWhenUnconfigured: defaultPermissionPresetWhenUnconfigured,
				persistChanges: true );
		}
		else
		{
			string isolatedPath = dataDirOverride is not null
				? Path.Combine( dataDirOverride, McpPermissionManager.ConfigFileName )
				: Path.Combine( Path.GetTempPath(), $"hackpdm-mcp-perm-{Guid.NewGuid():N}.json" );
			permissions = new McpPermissionManager(
				configFilePathOverride: isolatedPath,
				defaultPresetWhenUnconfigured: defaultPermissionPresetWhenUnconfigured,
				persistChanges: false );
		}

		McpServerInfo info = new( ServerName, ClientUpdate.CurrentText, ServerTitle );
		McpServer server = new( info, DefaultInstructions );

		ClientAndSessionTools.Register( server, api, bridge );
		DirectoryAndEntryTools.Register( server, api );
		PdmWorkflowTools.Register( server, api );
		OdooAndSearchTools.Register( server, api );
		PostgresTools.Register( server, postgres );
		LocalAndSolidWorksTools.Register( server, api );
		FileSystemAndSolidWorksEditTools.Register( server, api );
		McpLogAndRestoreTools.Register( server, activityLog, restorePoints, permissions );
		LiveUiTools.Register( server, bridge, api );
		HackPdmResourceRegistrar.Register( server, api, bridge, activityLog, restorePoints, permissions );

		server.PermissionGate = permissions.EnforceAsync;
		if( mode is McpServerMode.Auto or McpServerMode.LiveOnly && permissions.HasRuntimeOverrides )
			server.PreProxyPermissionGate = permissions.EnforcePreProxyDenyOnlyAsync;

		server.ToolCallObserver = ( toolName, rawArgs, result, durationMs, wasProxied, errorMessage ) =>
		{
			// Avoid duplicating log rows when proxying to the embedded GUI (which already writes to the shared activity log)
			// and avoid noisy self-logging when polling the log viewer.
			if( wasProxied || toolName is "mcp_log_get" or "mcp_log_clear" )
				return;

			bool ok = errorMessage is null && result?.IsError != true;
			activityLog.Record(
				toolName: toolName,
				success: ok,
				durationMs: durationMs,
				mode: bridge.ActiveModeLabel,
				rawArguments: rawArgs,
				result: result,
				errorMessage: errorMessage,
				restorePointId: McpRestorePointManager.CurrentCallRestorePointId );
		};

		if( mode is McpServerMode.Auto or McpServerMode.LiveOnly )
		{
			server.ToolCallInterceptor = bridge.InterceptToolCallAsync;
			server.ResourceReadInterceptor = bridge.InterceptResourceReadAsync;
		}

		return new ServerBundle( server, api, bridge, activityLog, restorePoints, permissions );
	}
}

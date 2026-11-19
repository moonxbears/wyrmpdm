using HackPDM.Mcp.Bridge;
using HackPDM.Mcp.Protocol;
using HackPDM.Mcp.Security;
using HackPDM.Mcp.Services;
using HackPDM.Mcp.State;

namespace HackPDM.Mcp.Resources;

/// <summary>Registers all HackPDM MCP resources, resource templates, and guided workflow prompts.</summary>
public static class HackPdmResourceRegistrar
{
	public static void Register(
		McpServer server,
		HackPdmApiService api,
		HackPdmBridgeCoordinator bridge,
		McpActivityLog activityLog,
		McpRestorePointManager restorePoints,
		McpPermissionManager permissions )
	{
		// ---- Static Resources ----

		server.RegisterResource(
			"hackpdm://status",
			"HackPDM Status",
			"Current MCP mode, live GUI bridge status, Odoo session status, and local working folder configuration.",
			async ( uri, ct ) =>
			{
				object? uiState = bridge.LocalUiBridge is not null ? await bridge.LocalUiBridge.GetUiStateAsync( ct ) : null;
				return new McpResourceContent( uri, "application/json", McpJson.SerializeResult( new
				{
					mode = bridge.ActiveModeLabel,
					hasLiveUiAccess = bridge.HasLiveLocalUi || bridge.IsProxiedAttached,
					session = api.GetSessionInfo(),
					liveUi = uiState,
				} ) );
			} );

		server.RegisterResource(
			"hackpdm://metadata",
			"Odoo Reference Metadata",
			"All cached HackPDM Odoo reference lookups (users, workstation nodes, file types, categories, properties, ignore filters, server settings).",
			( uri, _ ) => Task.FromResult( new McpResourceContent( uri, "application/json", McpJson.SerializeResult( api.GetLookups( "all" ) ) ) ) );

		server.RegisterResource(
			"hackpdm://models",
			"HackPDM Odoo Model Catalog",
			"Complete list of all HackPDM Odoo models and their mapped [OdooProp] fields.",
			( uri, _ ) => Task.FromResult( new McpResourceContent( uri, "application/json", McpJson.SerializeResult( OdooJsonHelper.ModelCatalog ) ) ) );

		server.RegisterResource(
			"hackpdm://tree",
			"Merged Directory Tree",
			"Complete merged remote Odoo + local working folder directory tree.",
			async ( uri, ct ) =>
			{
				object tree = await api.GetDirectoryTreeAsync( "merged", null, 0, false, null, ct );
				return new McpResourceContent( uri, "application/json", McpJson.SerializeResult( tree ) );
			} );

		server.RegisterResource(
			"hackpdm://config/settings",
			"App Settings (settings.json)",
			"All key/value settings stored in HackPDM's settings.json (secrets redacted).",
			( uri, _ ) => Task.FromResult( new McpResourceContent( uri, "application/json", McpJson.SerializeResult( api.ListSettings() ) ) ) );

		server.RegisterResource(
			"hackpdm://config/app",
			"UI Configuration (AppConfig.json)",
			"HackPDM's AppConfig.json UI configuration (columns, window presets, workspace layout, theme).",
			async ( uri, ct ) =>
			{
				object cfg = await api.GetAppConfigAsync( ct );
				return new McpResourceContent( uri, "application/json", McpJson.SerializeResult( cfg ) );
			} );

		server.RegisterResource(
			"hackpdm://logs/diagnostics",
			"Diagnostics & Recent Logs",
			"Redacted HackPDM diagnostic summary and tail of recent HackLogger lines.",
			( uri, _ ) => Task.FromResult( new McpResourceContent( uri, "application/json", McpJson.SerializeResult( api.GetDiagnostics( 150, null ) ) ) ) );

		server.RegisterResource(
			"hackpdm://mcp/log",
			"MCP Activity Log",
			"Recent HackPDM MCP tool invocations with redacted arguments, durations, status, and linked restore point IDs.",
			( uri, _ ) => Task.FromResult( new McpResourceContent( uri, "application/json", McpJson.SerializeResult( new
			{
				logFilePath = activityLog.LogFilePath,
				totalRecorded = activityLog.Count,
				entries = activityLog.GetEntries( 100 ),
			} ) ) ) );

		server.RegisterResource(
			"hackpdm://mcp/restore-points",
			"MCP Restore Points",
			"All stored HackPDM MCP restore points (pre-mutation snapshots and manual checkpoints) available for rollback.",
			( uri, _ ) => Task.FromResult( new McpResourceContent( uri, "application/json", McpJson.SerializeResult( new
			{
				storageDirectory = restorePoints.StorageDirectory,
				autoRestorePointsEnabled = restorePoints.AutoRestorePointsEnabled,
				restorePoints = restorePoints.ListRestorePoints( 100 ),
			} ) ) ) );

		server.RegisterResource(
			"hackpdm://mcp/permissions",
			"MCP Permissions & Active Mode",
			"Active HackPDM MCP permission mode (auto, review, review-critical, read-only, local-only), active profile, allowed capability categories, and pending review queue.",
			( uri, _ ) => Task.FromResult( new McpResourceContent( uri, "application/json", McpJson.SerializeResult( permissions.GetStatusSnapshot() ) ) ) );

		server.RegisterResource(
			"hackpdm://ui/theme",
			"Theme & Appearance Configuration",
			"Complete current HackPDM theme configuration: mode, palette, form, all brush colors/overrides, independent style/density/font overrides, dependency graph overrides, and saved custom themes.",
			async ( uri, ct ) =>
			{
				object snap = bridge.LocalUiBridge is not null
					? await bridge.LocalUiBridge.ThemeControlAsync( null, ct )
					: await api.ThemeControlHeadlessAsync( null, ct );
				return new McpResourceContent( uri, "application/json", McpJson.SerializeResult( snap ) );
			} );

		server.RegisterResource(
			"hackpdm://ui/state",
			"Live UI State",
			"Live desktop UI state snapshot (windows, active page, drawers, dialogs, ViewModel state). Requires a live HackPDM GUI.",
			async ( uri, ct ) =>
			{
				if( bridge.LocalUiBridge is null )
					throw new McpProtocolException( JsonRpcErrorCodes.ResourceNotFound, "Live HackPDM GUI is not attached; start HackPDM or call client_launch." );
				object state = await bridge.LocalUiBridge.GetUiStateAsync( ct );
				return new McpResourceContent( uri, "application/json", McpJson.SerializeResult( state ) );
			} );

		server.RegisterResource(
			"hackpdm://ui/visual-tree",
			"Live Visual Tree",
			"Live MainWindow XAML visual tree JSON. Requires a live HackPDM GUI.",
			async ( uri, ct ) =>
			{
				if( bridge.LocalUiBridge is null )
					throw new McpProtocolException( JsonRpcErrorCodes.ResourceNotFound, "Live HackPDM GUI is not attached; start HackPDM or call client_launch." );
				object tree = await bridge.LocalUiBridge.GetVisualTreeAsync( null, null, 12, false, false, ct );
				return new McpResourceContent( uri, "application/json", McpJson.SerializeResult( tree ) );
			} );

		// ---- Resource Templates ----

		server.RegisterResourceTemplate(
			"hackpdm://directory/{directoryId}/entries",
			"Directory Entries",
			"Merged remote + local entries with FileStatus codes for a specific Odoo hp.directory ID.",
			async ( uri, vars, ct ) =>
			{
				if( !vars.TryGetValue( "directoryId", out string? rawId ) || !int.TryParse( rawId, out int dirId ) )
					throw new McpProtocolException( JsonRpcErrorCodes.InvalidParams, "directoryId must be an integer." );
				object entries = await api.GetDirectoryEntriesAsync( dirId, null, false, true, null, null, "name", false, ct );
				return new McpResourceContent( uri, "application/json", McpJson.SerializeResult( entries ) );
			} );

		server.RegisterResourceTemplate(
			"hackpdm://entry/{entryId}/details",
			"Entry Details",
			"Full entry details (metadata, version history, where-used parents, dependents children, custom properties, latest version info) for an hp.entry ID.",
			async ( uri, vars, _ ) =>
			{
				if( !vars.TryGetValue( "entryId", out string? rawId ) || !int.TryParse( rawId, out int entryId ) )
					throw new McpProtocolException( JsonRpcErrorCodes.InvalidParams, "entryId must be an integer." );
				object details = await api.GetEntryDetailsAsync( entryId, true, true, true, true, true );
				return new McpResourceContent( uri, "application/json", McpJson.SerializeResult( details ) );
			} );

		server.RegisterResourceTemplate(
			"hackpdm://model/{modelName}/schema",
			"Odoo Model Schema",
			"Client [OdooProp] field schema for a HackPDM Odoo model (e.g. hp.entry, hp.version, hp.release).",
			async ( uri, vars, ct ) =>
			{
				string modelName = vars.GetValueOrDefault( "modelName", "" );
				object schema = await api.OdooModelSchemaAsync( modelName, fetchServerFields: false, ct );
				return new McpResourceContent( uri, "application/json", McpJson.SerializeResult( schema ) );
			} );

		// ---- Prompts ----

		server.RegisterPrompt(
			"explore-vault",
			"Inspect the HackPDM connection, directory tree, and entries in a folder.",
			[ new McpPromptArgument( "folder", "Optional folder name or tree path to inspect (e.g. 'Parts' or 'root\\Projects\\Parts').", Required: false ) ],
			( args, _ ) =>
			{
				string folder = args.GetValueOrDefault( "folder", "" );
				string text = string.IsNullOrWhiteSpace( folder )
					? "Check HackPDM status with `client_status` and `session_get_info`. If not logged in, check settings or ask before logging in. Then load the directory tree via `directory_get_tree` and summarize the top-level folders and their Synced/RemoteOnly/LocalOnly state."
					: $"Check HackPDM status with `client_status`, locate the folder '{folder}' via `directory_get_tree` (filter: '{folder}'), and list its entries using `directory_get_entries` with status breakdown (ok/ro/lo/lm/cm/co/nv/dt).";
				return Task.FromResult( new McpPromptResult(
					"Explore HackPDM vault structure and folder entries.",
					[ new McpPromptMessage( "user", McpContent.TextContent( text ) ) ] ) );
			} );

		server.RegisterPrompt(
			"commit-changes",
			"Safely preview and commit modified or new local files (including SolidWorks dependencies) to HackPDM.",
			[ new McpPromptArgument( "targetPath", "Local file or folder path (or vault tree path) to preview and commit.", Required: true ) ],
			( args, _ ) =>
			{
				string target = args.GetValueOrDefault( "targetPath", "root" );
				string text =
					$"Inspect '{target}' for HackPDM check-in:\n" +
					$"1. Run `pdm_commit_preview` on '{target}' to check new entries, changed checked-out entries, unchanged checked-out entries, and any broken SolidWorks dependencies.\n" +
					$"2. Summarize every file that will be created, versioned, released, or skipped.\n" +
					$"3. Only call `pdm_commit` once there are no unexpected broken references and the user confirms.";
				return Task.FromResult( new McpPromptResult(
					"Safe HackPDM commit preview and check-in workflow.",
					[ new McpPromptMessage( "user", McpContent.TextContent( text ) ) ] ) );
			} );

		server.RegisterPrompt(
			"diagnose-hackpdm",
			"Run a full HackPDM health check covering connection, auth, permissions, settings, local vault, and recent logs.",
			null,
			( _, _ ) => Task.FromResult( new McpPromptResult(
				"HackPDM diagnostic checklist.",
				[ new McpPromptMessage( "user", McpContent.TextContent(
					"Diagnose HackPDM by calling `client_status`, `session_get_info`, `session_test_connection`, `settings_list`, and `diagnostics_get`. " +
					"If connected to a live GUI client, also inspect `ui_get_state` and `ui_status_panel`. Summarize any errors, warnings, or misconfigurations." ) ) ] ) ) );

		server.RegisterPrompt(
			"automate-ui",
			"Guide for inspecting and controlling the live HackPDM desktop client UI.",
			[ new McpPromptArgument( "goal", "What you want to inspect or accomplish inside the HackPDM desktop UI.", Required: true ) ],
			( args, _ ) =>
			{
				string goal = args.GetValueOrDefault( "goal", "Inspect the active page" );
				string text =
					$"Goal inside HackPDM desktop client: {goal}\n" +
					"Workflow:\n" +
					"1. Call `client_status` to confirm live GUI attachment (if headless, call `client_attach` or `client_launch`).\n" +
					"2. Call `ui_get_state` (and `ui_visual_tree` / `ui_find_controls` as needed) to see the active page, controls, and open dialogs.\n" +
					"3. Prefer dedicated screen tools (`ui_navigate`, `ui_file_manager_*`, `ui_dependency_graph_control`, `ui_search_control`, `ui_theme_control`, `ui_dialog_respond`) or generic control tools (`ui_click_control`, `ui_set_control_value`, `ui_invoke_command`).\n" +
					"4. Verify results with `ui_get_state` or `ui_screenshot`.";
				return Task.FromResult( new McpPromptResult(
					"Automate HackPDM desktop UI safely.",
					[ new McpPromptMessage( "user", McpContent.TextContent( text ) ) ] ) );
			} );

		server.RegisterPrompt(
			"guide-user",
			"Guide the user visually inside HackPDM using temporary highlights, attention animations, interactive popups, and multi-step tours.",
			[ new McpPromptArgument( "workflow", "The HackPDM task or feature you want to guide the user through.", Required: true ) ],
			( args, _ ) =>
			{
				string wf = args.GetValueOrDefault( "workflow", "Explore HackPDM features" );
				string text =
					$"Visually guide the user inside HackPDM for: {wf}\n" +
					"Recommended guided-action workflow:\n" +
					"1. Call `ui_find_controls` (or `ui_visual_tree`) to confirm exact control `x:Name` or visible text on the target page.\n" +
					"2. For a single quick cue, call `ui_guide_highlight` with `animation` ('pulse', 'spotlight', 'bounce', 'shake', 'flash', or 'outline'), `badgeText`, `title`, `message`, and `dismissOnTargetClick: true`.\n" +
					"3. When you want the user to choose the next step, call `ui_guide_popup` with `targetName`, custom `buttons`, and `waitForUser: true`.\n" +
					"4. For multi-screen workflows, call `ui_guide_tour` with ordered `steps` (each specifying `page`/`settingsSection`, `targetName`, `animation`, `title`, and `message`).\n" +
					"5. Call `ui_guide_clear` whenever you want to remove temporary overlays cleanly.";
				return Task.FromResult( new McpPromptResult(
					"Visually guide the user through HackPDM.",
					[ new McpPromptMessage( "user", McpContent.TextContent( text ) ) ] ) );
			} );

		server.RegisterPrompt(
			"present-report",
			"Gather HackPDM data and present it in a richly formatted, interactive supplemental dynamic window.",
			[ new McpPromptArgument( "topic", "What information to gather, restructure, and display (e.g. 'Assembly BOM & Commit Readiness', 'SolidWorks Property Comparison', 'Vault Health Dashboard').", Required: true ) ],
			( args, _ ) =>
			{
				string topic = args.GetValueOrDefault( "topic", "HackPDM Vault & SolidWorks Summary" );
				string text =
					$"Gather relevant HackPDM data for '{topic}' and present it as a clean, well-structured supplemental window using `ui_window_create`:\n" +
					"1. Query the underlying data first using HackPDM tools (`directory_get_entries`, `entry_get_details`, `sw_inspect_file`, `sw_scan_dependencies`, `pdm_commit_preview`, `diagnostics_get`, etc.).\n" +
					"2. Design a readable hierarchy in `ui_window_create`:\n" +
					"   - Top `kpi_row` (3–4 high-signal summary metrics with severity accents)\n" +
					"   - A `callout` highlighting any blockers, broken references, or recommended actions\n" +
					"   - `tabs` or `grid` sections grouping detailed views: searchable `table` (with `isBadge: true` status columns), hierarchical `tree` (for BOMs/dependencies), `key_value` metadata panels, `code`/`json` snippets (copyable), or `image` thumbnails\n" +
					"   - Optional `input`/`checkbox`/`select` + `button_row` actions (`emit`, `copy`, `navigate`, `close`)\n" +
					"3. Give key elements stable `id` values so you can update them live via `ui_window_update` (`patches`) as status changes, and inspect user selections/clicks via `ui_window_events`.";
				return Task.FromResult( new McpPromptResult(
					"Build a dynamic supplemental window report in HackPDM.",
					[ new McpPromptMessage( "user", McpContent.TextContent( text ) ) ] ) );
			} );
	}
}

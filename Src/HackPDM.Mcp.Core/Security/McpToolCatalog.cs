using System.Text.Json;

using HackPDM.Mcp.Services;

namespace HackPDM.Mcp.Security;

/// <summary>Metadata classifying a single MCP tool into a capability category, risk tier, and remote-RPC flag.</summary>
public sealed record McpToolClassification(
	McpCapabilityCategory Category,
	McpToolRiskLevel RiskLevel,
	string Description,
	bool IsRemoteRpc = false );

/// <summary>
/// Authoritative classification of every HackPDM MCP tool into a capability category, risk level
/// (<see cref="McpToolRiskLevel.ReadOnly"/>, <see cref="McpToolRiskLevel.StandardAction"/>,
/// or <see cref="McpToolRiskLevel.CriticalAction"/>), and whether it performs remote Odoo/PDM server RPC
/// (<c>IsRemoteRpc</c>), including argument-aware refinements (e.g. <c>dryRun: true</c> or
/// <c>directory_get_tree(source: "local")</c>).
/// </summary>
public static class McpToolCatalog
{
	private static readonly Dictionary<string, McpToolClassification> Map = new( StringComparer.Ordinal )
	{
		// ---- Client & Bridge Lifecycle (Local IPC / Process) ----
		[ "client_status" ] = new( McpCapabilityCategory.ReadVaultAndOdoo, McpToolRiskLevel.ReadOnly, "Inspect MCP bridge & client status" ),
		[ "client_attach" ] = new( McpCapabilityCategory.SettingsAndSession, McpToolRiskLevel.StandardAction, "Attach MCP server to live HackPDM GUI" ),
		[ "client_detach" ] = new( McpCapabilityCategory.SettingsAndSession, McpToolRiskLevel.StandardAction, "Detach MCP server from live HackPDM GUI" ),
		[ "client_launch" ] = new( McpCapabilityCategory.SettingsAndSession, McpToolRiskLevel.StandardAction, "Launch HackPDM desktop client" ),

		// ---- Session, Auth & Profiles ----
		[ "session_get_info" ] = new( McpCapabilityCategory.ReadVaultAndOdoo, McpToolRiskLevel.ReadOnly, "Read local session configuration summary" ),
		[ "session_test_connection" ] = new( McpCapabilityCategory.ReadVaultAndOdoo, McpToolRiskLevel.ReadOnly, "Probe Odoo server connectivity", IsRemoteRpc: true ),
		[ "session_check_permissions" ] = new( McpCapabilityCategory.ReadVaultAndOdoo, McpToolRiskLevel.ReadOnly, "Check Odoo user admin permissions", IsRemoteRpc: true ),
		[ "session_reload_lookups" ] = new( McpCapabilityCategory.ReadVaultAndOdoo, McpToolRiskLevel.ReadOnly, "Reload Odoo lookup tables", IsRemoteRpc: true ),
		[ "profile_get_current" ] = new( McpCapabilityCategory.ReadVaultAndOdoo, McpToolRiskLevel.ReadOnly, "Read current connection profile" ),
		[ "session_configure" ] = new( McpCapabilityCategory.SettingsAndSession, McpToolRiskLevel.CriticalAction, "Modify local Odoo server / PWA configuration" ),
		[ "session_login" ] = new( McpCapabilityCategory.SettingsAndSession, McpToolRiskLevel.StandardAction, "Sign into Odoo server", IsRemoteRpc: true ),
		[ "session_logout" ] = new( McpCapabilityCategory.SettingsAndSession, McpToolRiskLevel.StandardAction, "Sign out of Odoo session", IsRemoteRpc: true ),
		[ "profile_load" ] = new( McpCapabilityCategory.SettingsAndSession, McpToolRiskLevel.CriticalAction, "Load and apply local connection profile file" ),
		[ "profile_save" ] = new( McpCapabilityCategory.SettingsAndSession, McpToolRiskLevel.StandardAction, "Save local connection profile file" ),

		// ---- Metadata, Directory Tree, Entries, Versions & Dependency Graph ----
		[ "metadata_get_lookups" ] = new( McpCapabilityCategory.ReadVaultAndOdoo, McpToolRiskLevel.ReadOnly, "Read Odoo reference lookups", IsRemoteRpc: true ),
		[ "directory_get_tree" ] = new( McpCapabilityCategory.ReadVaultAndOdoo, McpToolRiskLevel.ReadOnly, "Read vault directory tree", IsRemoteRpc: true ),
		[ "directory_get_entries" ] = new( McpCapabilityCategory.ReadVaultAndOdoo, McpToolRiskLevel.ReadOnly, "List entries in remote/merged directory", IsRemoteRpc: true ),
		[ "directory_get_entry_ids" ] = new( McpCapabilityCategory.ReadVaultAndOdoo, McpToolRiskLevel.ReadOnly, "List remote entry IDs in directory", IsRemoteRpc: true ),
		[ "directory_find_path" ] = new( McpCapabilityCategory.ReadVaultAndOdoo, McpToolRiskLevel.ReadOnly, "Resolve remote directory path", IsRemoteRpc: true ),
		[ "path_convert" ] = new( McpCapabilityCategory.ReadVaultAndOdoo, McpToolRiskLevel.ReadOnly, "Convert between local and vault paths" ),
		[ "directory_create" ] = new( McpCapabilityCategory.PdmCommitAndPush, McpToolRiskLevel.CriticalAction, "Create new remote/local vault directory", IsRemoteRpc: true ),
		[ "entry_get_details" ] = new( McpCapabilityCategory.ReadVaultAndOdoo, McpToolRiskLevel.ReadOnly, "Read full entry details from Odoo", IsRemoteRpc: true ),
		[ "entry_get_history" ] = new( McpCapabilityCategory.ReadVaultAndOdoo, McpToolRiskLevel.ReadOnly, "Read entry version history from Odoo", IsRemoteRpc: true ),
		[ "entry_get_relationships" ] = new( McpCapabilityCategory.ReadVaultAndOdoo, McpToolRiskLevel.ReadOnly, "Read entry parent/child relationships from Odoo", IsRemoteRpc: true ),
		[ "entry_get_properties" ] = new( McpCapabilityCategory.ReadVaultAndOdoo, McpToolRiskLevel.ReadOnly, "Read entry custom properties from Odoo", IsRemoteRpc: true ),
		[ "version_get_info" ] = new( McpCapabilityCategory.ReadVaultAndOdoo, McpToolRiskLevel.ReadOnly, "Read version metadata from Odoo", IsRemoteRpc: true ),
		[ "version_get_preview" ] = new( McpCapabilityCategory.ReadVaultAndOdoo, McpToolRiskLevel.ReadOnly, "Read version preview image from Odoo", IsRemoteRpc: true ),
		[ "version_read_contents" ] = new( McpCapabilityCategory.ReadVaultAndOdoo, McpToolRiskLevel.ReadOnly, "Read version file contents from Odoo", IsRemoteRpc: true ),
		[ "version_upload_preview" ] = new( McpCapabilityCategory.PdmCommitAndPush, McpToolRiskLevel.CriticalAction, "Upload preview thumbnail to Odoo server", IsRemoteRpc: true ),
		[ "dependency_graph_load" ] = new( McpCapabilityCategory.ReadVaultAndOdoo, McpToolRiskLevel.ReadOnly, "Load entry dependency graph from Odoo", IsRemoteRpc: true ),

		// ---- PDM Workflows (All Odoo Server RPCs) ----
		[ "pdm_get_latest" ] = new( McpCapabilityCategory.PdmWorkingActions, McpToolRiskLevel.StandardAction, "Download latest files from Odoo to local PWA", IsRemoteRpc: true ),
		[ "pdm_checkout" ] = new( McpCapabilityCategory.PdmWorkingActions, McpToolRiskLevel.StandardAction, "Check out vault entries on Odoo server", IsRemoteRpc: true ),
		[ "pdm_uncheckout" ] = new( McpCapabilityCategory.PdmWorkingActions, McpToolRiskLevel.StandardAction, "Release checkout lock on Odoo server", IsRemoteRpc: true ),
		[ "pdm_commit_preview" ] = new( McpCapabilityCategory.ReadVaultAndOdoo, McpToolRiskLevel.ReadOnly, "Preview pending commit/check-in changes against Odoo", IsRemoteRpc: true ),
		[ "pdm_commit" ] = new( McpCapabilityCategory.PdmCommitAndPush, McpToolRiskLevel.CriticalAction, "Commit/check-in files to Odoo PDM server", IsRemoteRpc: true ),
		[ "pdm_logical_delete" ] = new( McpCapabilityCategory.PdmDeleteAndAdmin, McpToolRiskLevel.CriticalAction, "Logically delete entries on Odoo server", IsRemoteRpc: true ),
		[ "pdm_restore_deleted" ] = new( McpCapabilityCategory.PdmWorkingActions, McpToolRiskLevel.StandardAction, "Restore logically deleted entries on Odoo server", IsRemoteRpc: true ),
		[ "pdm_permanent_delete" ] = new( McpCapabilityCategory.PdmDeleteAndAdmin, McpToolRiskLevel.CriticalAction, "Permanently destroy entries/versions on Odoo server", IsRemoteRpc: true ),
		[ "pdm_plan_version_download" ] = new( McpCapabilityCategory.ReadVaultAndOdoo, McpToolRiskLevel.ReadOnly, "Plan version download from Odoo", IsRemoteRpc: true ),
		[ "pdm_download_version" ] = new( McpCapabilityCategory.PdmWorkingActions, McpToolRiskLevel.StandardAction, "Download specific version from Odoo to local disk", IsRemoteRpc: true ),

		// ---- Search & Generic Odoo ORM / RPC ----
		[ "search_entries" ] = new( McpCapabilityCategory.ReadVaultAndOdoo, McpToolRiskLevel.ReadOnly, "Search HackPDM entries on Odoo server", IsRemoteRpc: true ),
		[ "search_get_options" ] = new( McpCapabilityCategory.ReadVaultAndOdoo, McpToolRiskLevel.ReadOnly, "List Odoo search properties and operators", IsRemoteRpc: true ),
		[ "odoo_list_models" ] = new( McpCapabilityCategory.ReadVaultAndOdoo, McpToolRiskLevel.ReadOnly, "List mapped local client Odoo models" ),
		[ "odoo_model_schema" ] = new( McpCapabilityCategory.ReadVaultAndOdoo, McpToolRiskLevel.ReadOnly, "Inspect Odoo model schema" ),
		[ "odoo_search" ] = new( McpCapabilityCategory.ReadVaultAndOdoo, McpToolRiskLevel.ReadOnly, "Run Odoo ORM search RPC", IsRemoteRpc: true ),
		[ "odoo_search_count" ] = new( McpCapabilityCategory.ReadVaultAndOdoo, McpToolRiskLevel.ReadOnly, "Run Odoo ORM search_count RPC", IsRemoteRpc: true ),
		[ "odoo_read" ] = new( McpCapabilityCategory.ReadVaultAndOdoo, McpToolRiskLevel.ReadOnly, "Run Odoo ORM read RPC", IsRemoteRpc: true ),
		[ "odoo_search_read" ] = new( McpCapabilityCategory.ReadVaultAndOdoo, McpToolRiskLevel.ReadOnly, "Run Odoo ORM search_read RPC", IsRemoteRpc: true ),
		[ "odoo_read_group" ] = new( McpCapabilityCategory.ReadVaultAndOdoo, McpToolRiskLevel.ReadOnly, "Run Odoo ORM read_group aggregation RPC", IsRemoteRpc: true ),
		[ "odoo_check_model_access" ] = new( McpCapabilityCategory.ReadVaultAndOdoo, McpToolRiskLevel.ReadOnly, "Check Odoo user model CRUD permissions via JSON-RPC", IsRemoteRpc: true ),
		[ "odoo_discover_model_methods" ] = new( McpCapabilityCategory.ReadVaultAndOdoo, McpToolRiskLevel.ReadOnly, "Discover callable Odoo model ORM methods & permissions", IsRemoteRpc: true ),
		[ "odoo_call_model_method" ] = new( McpCapabilityCategory.PdmDeleteAndAdmin, McpToolRiskLevel.CriticalAction, "Execute permission-verified Odoo model API method via execute_kw", IsRemoteRpc: true ),
		[ "odoo_smart_read" ] = new( McpCapabilityCategory.ReadVaultAndOdoo, McpToolRiskLevel.ReadOnly, "Run HackPDM smart_read RPC", IsRemoteRpc: true ),
		[ "odoo_related_browse" ] = new( McpCapabilityCategory.ReadVaultAndOdoo, McpToolRiskLevel.ReadOnly, "Run HackPDM related_browse RPC", IsRemoteRpc: true ),
		[ "odoo_create" ] = new( McpCapabilityCategory.PdmDeleteAndAdmin, McpToolRiskLevel.CriticalAction, "Create raw Odoo record(s) via RPC", IsRemoteRpc: true ),
		[ "odoo_write" ] = new( McpCapabilityCategory.PdmDeleteAndAdmin, McpToolRiskLevel.CriticalAction, "Update raw Odoo record(s) via RPC", IsRemoteRpc: true ),
		[ "odoo_unlink" ] = new( McpCapabilityCategory.PdmDeleteAndAdmin, McpToolRiskLevel.CriticalAction, "Delete raw Odoo record(s) via RPC", IsRemoteRpc: true ),
		[ "odoo_execute" ] = new( McpCapabilityCategory.PdmDeleteAndAdmin, McpToolRiskLevel.CriticalAction, "Execute arbitrary Odoo RPC method", IsRemoteRpc: true ),

		// ---- Direct Odoo PostgreSQL Queries ----
		[ "postgres_status" ] = new( McpCapabilityCategory.ReadVaultAndOdoo, McpToolRiskLevel.ReadOnly, "Read redacted Odoo PostgreSQL connection settings" ),
		[ "postgres_configure" ] = new( McpCapabilityCategory.SettingsAndSession, McpToolRiskLevel.CriticalAction, "Configure direct Odoo PostgreSQL connection settings" ),
		[ "postgres_list_tables" ] = new( McpCapabilityCategory.ReadVaultAndOdoo, McpToolRiskLevel.ReadOnly, "List PostgreSQL tables in Odoo database", IsRemoteRpc: true ),
		[ "postgres_describe_table" ] = new( McpCapabilityCategory.ReadVaultAndOdoo, McpToolRiskLevel.ReadOnly, "Describe columns, keys & indexes of an Odoo PostgreSQL table", IsRemoteRpc: true ),
		[ "postgres_query" ] = new( McpCapabilityCategory.ReadVaultAndOdoo, McpToolRiskLevel.ReadOnly, "Run read-only parameterized PostgreSQL query against Odoo database", IsRemoteRpc: true ),
		[ "postgres_execute" ] = new( McpCapabilityCategory.PdmDeleteAndAdmin, McpToolRiskLevel.CriticalAction, "Execute modifying/DDL PostgreSQL SQL against Odoo database", IsRemoteRpc: true ),

		// ---- SolidWorks Inspection, Local Vault, Settings & Diagnostics (All Local) ----
		[ "sw_inspect_file" ] = new( McpCapabilityCategory.ReadVaultAndOdoo, McpToolRiskLevel.ReadOnly, "Inspect local SolidWorks file properties & references" ),
		[ "sw_scan_dependencies" ] = new( McpCapabilityCategory.ReadVaultAndOdoo, McpToolRiskLevel.ReadOnly, "Scan local SolidWorks dependencies" ),
		[ "local_list_files" ] = new( McpCapabilityCategory.ReadVaultAndOdoo, McpToolRiskLevel.ReadOnly, "List local PWA vault files" ),
		[ "local_read_file" ] = new( McpCapabilityCategory.ReadVaultAndOdoo, McpToolRiskLevel.ReadOnly, "Read local PWA vault file" ),
		[ "local_checksum" ] = new( McpCapabilityCategory.ReadVaultAndOdoo, McpToolRiskLevel.ReadOnly, "Compute local file SHA-1 checksum" ),
		[ "local_write_file" ] = new( McpCapabilityCategory.LocalVaultFileEdit, McpToolRiskLevel.StandardAction, "Write file inside local PWA vault" ),
		[ "local_delete" ] = new( McpCapabilityCategory.LocalVaultFileEdit, McpToolRiskLevel.CriticalAction, "Delete file(s)/folder inside local PWA vault" ),
		[ "settings_list" ] = new( McpCapabilityCategory.ReadVaultAndOdoo, McpToolRiskLevel.ReadOnly, "List local settings.json entries" ),
		[ "settings_get" ] = new( McpCapabilityCategory.ReadVaultAndOdoo, McpToolRiskLevel.ReadOnly, "Read a local settings.json key" ),
		[ "settings_set" ] = new( McpCapabilityCategory.SettingsAndSession, McpToolRiskLevel.CriticalAction, "Modify local settings.json key" ),
		[ "app_config_get" ] = new( McpCapabilityCategory.ReadVaultAndOdoo, McpToolRiskLevel.ReadOnly, "Read local AppConfig.json" ),
		[ "app_config_save" ] = new( McpCapabilityCategory.SettingsAndSession, McpToolRiskLevel.CriticalAction, "Overwrite local AppConfig.json" ),
		[ "diagnostics_get" ] = new( McpCapabilityCategory.ReadVaultAndOdoo, McpToolRiskLevel.ReadOnly, "Read local HackPDM diagnostics & logs" ),

		// ---- Computer-Wide Filesystem & SolidWorks Editing (All Local) ----
		[ "fs_list" ] = new( McpCapabilityCategory.ExternalFilesystemAccess, McpToolRiskLevel.ReadOnly, "List files anywhere on disk" ),
		[ "fs_stat" ] = new( McpCapabilityCategory.ExternalFilesystemAccess, McpToolRiskLevel.ReadOnly, "Inspect file metadata anywhere on disk" ),
		[ "fs_mkdir" ] = new( McpCapabilityCategory.ExternalFilesystemAccess, McpToolRiskLevel.StandardAction, "Create directory on disk" ),
		[ "fs_copy" ] = new( McpCapabilityCategory.ExternalFilesystemAccess, McpToolRiskLevel.StandardAction, "Copy file/directory on disk" ),
		[ "fs_move" ] = new( McpCapabilityCategory.ExternalFilesystemAccess, McpToolRiskLevel.CriticalAction, "Move/rename file or directory on disk" ),
		[ "fs_delete" ] = new( McpCapabilityCategory.ExternalFilesystemAccess, McpToolRiskLevel.CriticalAction, "Delete file(s)/directory anywhere on disk" ),
		[ "fs_import_to_pwa" ] = new( McpCapabilityCategory.ExternalFilesystemAccess, McpToolRiskLevel.CriticalAction, "Import external CAD files into local PWA & repath references" ),
		[ "sw_set_properties" ] = new( McpCapabilityCategory.SolidWorksEditing, McpToolRiskLevel.CriticalAction, "Modify local SolidWorks custom properties" ),
		[ "sw_set_dependencies" ] = new( McpCapabilityCategory.SolidWorksEditing, McpToolRiskLevel.CriticalAction, "Rewrite local SolidWorks dependency paths" ),

		// ---- MCP Activity Log, Restore Points & Permissions (All Local) ----
		[ "mcp_log_get" ] = new( McpCapabilityCategory.ReadVaultAndOdoo, McpToolRiskLevel.ReadOnly, "Read local MCP activity log" ),
		[ "mcp_log_clear" ] = new( McpCapabilityCategory.McpAdminAndRestore, McpToolRiskLevel.CriticalAction, "Clear local MCP activity log" ),
		[ "mcp_restore_point_create" ] = new( McpCapabilityCategory.McpAdminAndRestore, McpToolRiskLevel.StandardAction, "Create manual local restore point snapshot" ),
		[ "mcp_restore_point_list" ] = new( McpCapabilityCategory.ReadVaultAndOdoo, McpToolRiskLevel.ReadOnly, "List local MCP restore points" ),
		[ "mcp_restore_point_inspect" ] = new( McpCapabilityCategory.ReadVaultAndOdoo, McpToolRiskLevel.ReadOnly, "Inspect local MCP restore point manifest" ),
		[ "mcp_restore_point_restore" ] = new( McpCapabilityCategory.McpAdminAndRestore, McpToolRiskLevel.CriticalAction, "Roll back local files/config from restore point" ),
		[ "mcp_restore_point_delete" ] = new( McpCapabilityCategory.McpAdminAndRestore, McpToolRiskLevel.CriticalAction, "Delete local MCP restore point backup" ),
		[ "mcp_settings_configure" ] = new( McpCapabilityCategory.McpAdminAndRestore, McpToolRiskLevel.CriticalAction, "Configure local MCP log & restore settings" ),
		[ "mcp_permissions_get" ] = new( McpCapabilityCategory.ReadVaultAndOdoo, McpToolRiskLevel.ReadOnly, "Inspect active MCP permissions & mode" ),
		[ "mcp_permissions_list_presets" ] = new( McpCapabilityCategory.ReadVaultAndOdoo, McpToolRiskLevel.ReadOnly, "List MCP permission presets & profiles" ),

		// ---- Live Desktop GUI Inspection & Control (Local GUI; RPC sub-actions refined below) ----
		[ "ui_get_state" ] = new( McpCapabilityCategory.LiveUiRead, McpToolRiskLevel.ReadOnly, "Read live UI state snapshot" ),
		[ "ui_list_windows" ] = new( McpCapabilityCategory.LiveUiRead, McpToolRiskLevel.ReadOnly, "List open HackPDM windows" ),
		[ "ui_visual_tree" ] = new( McpCapabilityCategory.LiveUiRead, McpToolRiskLevel.ReadOnly, "Walk live XAML visual tree" ),
		[ "ui_find_controls" ] = new( McpCapabilityCategory.LiveUiRead, McpToolRiskLevel.ReadOnly, "Search controls in live visual tree" ),
		[ "ui_get_control" ] = new( McpCapabilityCategory.LiveUiRead, McpToolRiskLevel.ReadOnly, "Inspect single live UI control" ),
		[ "ui_screenshot" ] = new( McpCapabilityCategory.LiveUiRead, McpToolRiskLevel.ReadOnly, "Capture live PNG screenshot" ),
		[ "ui_navigate" ] = new( McpCapabilityCategory.LiveUiControl, McpToolRiskLevel.StandardAction, "Navigate HackPDM GUI pages & drawers" ),
		[ "ui_file_manager_select_folder" ] = new( McpCapabilityCategory.LiveUiControl, McpToolRiskLevel.StandardAction, "Select folder in File Manager tree" ),
		[ "ui_file_manager_select_entries" ] = new( McpCapabilityCategory.LiveUiControl, McpToolRiskLevel.StandardAction, "Select entry rows in File Manager" ),
		[ "ui_file_manager_control" ] = new( McpCapabilityCategory.LiveUiControl, McpToolRiskLevel.StandardAction, "Adjust File Manager tabs, filter & layout" ),
		[ "ui_dependency_graph_control" ] = new( McpCapabilityCategory.LiveUiControl, McpToolRiskLevel.StandardAction, "Control Dependency Graph view" ),
		[ "ui_search_control" ] = new( McpCapabilityCategory.LiveUiControl, McpToolRiskLevel.StandardAction, "Control Search Vault panel" ),
		[ "ui_status_panel" ] = new( McpCapabilityCategory.LiveUiControl, McpToolRiskLevel.StandardAction, "Inspect or control Status Log panel" ),
		[ "ui_theme_control" ] = new( McpCapabilityCategory.LiveUiControl, McpToolRiskLevel.StandardAction, "Read or customize app theme, style, graph & custom themes" ),
		[ "ui_customize_config" ] = new( McpCapabilityCategory.LiveUiControl, McpToolRiskLevel.StandardAction, "Read or customize AppConfig UI layout, columns, entry colors & windows" ),
		[ "ui_app_restart" ] = new( McpCapabilityCategory.LiveUiControl, McpToolRiskLevel.CriticalAction, "Soft-reload or auto-restart HackPDM desktop client" ),
		[ "ui_guide_highlight" ] = new( McpCapabilityCategory.LiveUiControl, McpToolRiskLevel.StandardAction, "Highlight a live UI control with temporary attention animation" ),
		[ "ui_guide_popup" ] = new( McpCapabilityCategory.LiveUiControl, McpToolRiskLevel.StandardAction, "Show a guided action popup card inside HackPDM" ),
		[ "ui_guide_tour" ] = new( McpCapabilityCategory.LiveUiControl, McpToolRiskLevel.StandardAction, "Run or control an interactive multi-step guided UI walkthrough" ),
		[ "ui_guide_clear" ] = new( McpCapabilityCategory.LiveUiControl, McpToolRiskLevel.StandardAction, "Inspect or clear active temporary guided UI overlays" ),
		[ "ui_window_create" ] = new( McpCapabilityCategory.LiveUiControl, McpToolRiskLevel.StandardAction, "Create a dynamic supplemental AI display window" ),
		[ "ui_window_update" ] = new( McpCapabilityCategory.LiveUiControl, McpToolRiskLevel.StandardAction, "Update or patch an open dynamic supplemental window" ),
		[ "ui_window_list" ] = new( McpCapabilityCategory.LiveUiRead, McpToolRiskLevel.ReadOnly, "List open dynamic supplemental AI windows" ),
		[ "ui_window_events" ] = new( McpCapabilityCategory.LiveUiRead, McpToolRiskLevel.ReadOnly, "Read user interactions and input values from a dynamic window" ),
		[ "ui_window_close" ] = new( McpCapabilityCategory.LiveUiControl, McpToolRiskLevel.StandardAction, "Close one or all dynamic supplemental AI windows" ),
		[ "ui_click_control" ] = new( McpCapabilityCategory.LiveUiControl, McpToolRiskLevel.CriticalAction, "Click/invoke a live UI control" ),
		[ "ui_set_control_value" ] = new( McpCapabilityCategory.LiveUiControl, McpToolRiskLevel.StandardAction, "Set value on a live UI input control" ),
		[ "ui_invoke_command" ] = new( McpCapabilityCategory.LiveUiControl, McpToolRiskLevel.CriticalAction, "Invoke a live MVVM ViewModel ICommand" ),
		[ "ui_dialog_respond" ] = new( McpCapabilityCategory.LiveUiControl, McpToolRiskLevel.CriticalAction, "Respond to an active MessageBox dialog" ),
	};

	/// <summary>True if the tool has an explicit static entry in <see cref="Map"/>.</summary>
	public static bool IsClassified( string toolName ) => Map.ContainsKey( toolName );

	/// <summary>Returns all classified tool names.</summary>
	public static IReadOnlyCollection<string> KnownTools => Map.Keys;

	/// <summary>
	/// Classifies a tool invocation, refining risk level and <see cref="McpToolClassification.IsRemoteRpc"/>
	/// based on arguments (e.g., <c>dryRun: true</c>, <c>directory_get_tree(source: "local")</c>,
	/// <c>odoo_model_schema(fetchServerFields: true)</c>).
	/// </summary>
	public static McpToolClassification Classify( string toolName, JsonElement? rawArguments = null )
	{
		if( !Map.TryGetValue( toolName, out McpToolClassification? baseClass ) )
		{
			// Fail-closed: unknown tools default to CriticalAction + IsRemoteRpc under PdmDeleteAndAdmin
			return new McpToolClassification(
				McpCapabilityCategory.PdmDeleteAndAdmin,
				McpToolRiskLevel.CriticalAction,
				$"Unclassified tool '{toolName}'",
				IsRemoteRpc: true );
		}

		if( rawArguments is { ValueKind: JsonValueKind.Object } args )
		{
			if( toolName is "sw_set_dependencies" or "mcp_restore_point_restore" && GetBool( args, "dryRun" ) )
				return baseClass with { RiskLevel = McpToolRiskLevel.ReadOnly };

			if( toolName == "directory_get_tree"
				&& string.Equals( GetString( args, "source" ), "local", StringComparison.OrdinalIgnoreCase ) )
			{
				return baseClass with { IsRemoteRpc = false, Description = "Read local PWA directory tree" };
			}

			if( toolName == "odoo_model_schema" && GetBool( args, "fetchServerFields" ) )
				return baseClass with { IsRemoteRpc = true, Description = "Fetch live Odoo server model schema via RPC" };

			if( toolName == "odoo_call_model_method" && HackPdmApiService.IsReadOnlyOdooMethod( GetString( args, "method" ) ) )
			{
				return new McpToolClassification(
					McpCapabilityCategory.ReadVaultAndOdoo,
					McpToolRiskLevel.ReadOnly,
					$"Run read-only Odoo ORM method '{GetString( args, "method" )}' via execute_kw",
					IsRemoteRpc: true );
			}

			if( toolName == "postgres_status" && GetBool( args, "testConnection" ) )
				return baseClass with { IsRemoteRpc = true, Description = "Test live Odoo PostgreSQL database connection" };

			if( toolName == "postgres_execute" && GetBool( args, "dryRun" ) )
				return baseClass with { RiskLevel = McpToolRiskLevel.StandardAction, Description = "Dry-run modifying PostgreSQL SQL with automatic rollback" };

			if( toolName == "ui_search_control" && GetBool( args, "execute" ) )
				return baseClass with { IsRemoteRpc = true, Description = "Execute live Odoo vault search in GUI" };

			if( toolName == "ui_dependency_graph_control" && (
				( args.TryGetProperty( "loadEntryId", out JsonElement loadId ) && loadId.ValueKind == JsonValueKind.Number )
				|| ( args.TryGetProperty( "entryIds", out JsonElement entryIdsEl ) && entryIdsEl.ValueKind == JsonValueKind.Array && entryIdsEl.GetArrayLength() > 0 )
				|| ( args.TryGetProperty( "expandNodeId", out JsonElement expandId ) && expandId.ValueKind == JsonValueKind.Number )
				|| args.TryGetProperty( "folderPath", out _ )
				|| args.TryGetProperty( "directoryId", out _ )
				|| args.TryGetProperty( "viewMode", out _ ) ) )
			{
				return baseClass with { IsRemoteRpc = true, Description = "Load or expand remote Odoo node graph in GUI" };
			}

			if( toolName == "ui_dialog_respond" && ( GetBool( args, "listOnly" ) || string.IsNullOrWhiteSpace( GetString( args, "response" ) ) ) )
				return new McpToolClassification( McpCapabilityCategory.LiveUiRead, McpToolRiskLevel.ReadOnly, "List active MessageBox dialogs" );

			if( toolName == "ui_invoke_command" && GetBool( args, "listCommands" ) )
				return new McpToolClassification( McpCapabilityCategory.LiveUiRead, McpToolRiskLevel.ReadOnly, "List available ViewModel commands" );

			if( toolName == "ui_status_panel"
				&& !GetBool( args, "cancelOperation" )
				&& !GetBool( args, "clearLogs" )
				&& !args.TryGetProperty( "autoClose", out _ )
				&& !args.TryGetProperty( "historyLength", out _ ) )
			{
				return new McpToolClassification( McpCapabilityCategory.LiveUiRead, McpToolRiskLevel.ReadOnly, "Read live Status Log panel" );
			}

			if( toolName == "mcp_settings_configure"
				&& !args.TryGetProperty( "autoRestorePoints", out _ )
				&& !args.TryGetProperty( "maxRestorePoints", out _ ) )
			{
				return new McpToolClassification( McpCapabilityCategory.ReadVaultAndOdoo, McpToolRiskLevel.ReadOnly, "Read MCP settings" );
			}

			if( toolName == "ui_app_restart"
				&& string.Equals( GetString( args, "mode" ), "soft", StringComparison.OrdinalIgnoreCase ) )
			{
				return baseClass with { RiskLevel = McpToolRiskLevel.StandardAction, Description = "Soft-reload HackPDM UI in-process" };
			}

			if( toolName == "ui_theme_control" && IsReadOnlyThemeCall( args ) )
				return new McpToolClassification( McpCapabilityCategory.LiveUiRead, McpToolRiskLevel.ReadOnly, "Read HackPDM theme configuration" );

			if( toolName == "ui_customize_config" && IsReadOnlyCustomizeConfigCall( args ) )
				return new McpToolClassification( McpCapabilityCategory.LiveUiRead, McpToolRiskLevel.ReadOnly, "Read HackPDM UI configuration" );

			if( toolName == "ui_guide_clear" && GetBool( args, "listOnly" ) )
				return new McpToolClassification( McpCapabilityCategory.LiveUiRead, McpToolRiskLevel.ReadOnly, "List active guided overlays" );

			if( toolName == "ui_guide_tour" && string.Equals( GetString( args, "action" ), "status", StringComparison.OrdinalIgnoreCase ) )
				return new McpToolClassification( McpCapabilityCategory.LiveUiRead, McpToolRiskLevel.ReadOnly, "Read active guided tour status" );

			if( toolName == "ui_window_events" && GetBool( args, "waitForEvent" ) )
				return new McpToolClassification( McpCapabilityCategory.LiveUiControl, McpToolRiskLevel.StandardAction, "Wait for interactive user event in a dynamic supplemental window" );
		}
		else if( toolName == "ui_theme_control" )
		{
			return new McpToolClassification( McpCapabilityCategory.LiveUiRead, McpToolRiskLevel.ReadOnly, "Read HackPDM theme configuration" );
		}
		else if( toolName == "ui_customize_config" )
		{
			return new McpToolClassification( McpCapabilityCategory.LiveUiRead, McpToolRiskLevel.ReadOnly, "Read HackPDM UI configuration" );
		}

		return baseClass;
	}

	private static bool IsReadOnlyThemeCall( JsonElement args )
	{
		string[] mutatingProps =
		[
			"mode", "palette", "form", "showTreeBranchLines",
			"setColorKey", "setColorHex", "colors", "removeColors", "resetColorKey", "resetAll",
			"style", "resetStyle", "graph", "resetGraph",
			"saveCustomThemeName", "loadCustomThemeName", "deleteCustomThemeName",
			"resetToDefaults", "save", "revert", "autoApplyRestart",
		];
		foreach( string prop in mutatingProps )
		{
			if( !args.TryGetProperty( prop, out JsonElement val ) )
				continue;
			if( val.ValueKind is JsonValueKind.Null or JsonValueKind.False )
				continue;
			if( prop == "autoApplyRestart" && string.Equals( val.GetString(), "none", StringComparison.OrdinalIgnoreCase ) )
				continue;
			return false;
		}
		return true;
	}

	private static bool IsReadOnlyCustomizeConfigCall( JsonElement args )
	{
		string[] mutatingProps = [ "entryColors", "columns", "workspaceLayout", "windowSettings", "resetSection", "save", "softReload" ];
		foreach( string prop in mutatingProps )
		{
			if( !args.TryGetProperty( prop, out JsonElement val ) )
				continue;
			if( val.ValueKind is JsonValueKind.Null or JsonValueKind.False )
				continue;
			return false;
		}
		return true;
	}

	/// <summary>
	/// Prevents an AI from using generic live UI control tools (<c>ui_click_control</c>, <c>ui_set_control_value</c>, <c>ui_invoke_command</c>)
	/// to silently click its own approval buttons or escalate its own permission mode/profile inside <c>McpSettingsView</c>.
	/// </summary>
	public static bool TargetsProtectedPermissionControl( string toolName, JsonElement? rawArguments )
	{
		if( toolName is not ( "ui_click_control" or "ui_set_control_value" or "ui_invoke_command" ) )
			return false;
		if( rawArguments is not { ValueKind: JsonValueKind.Object } args )
			return false;

		string[] protectedNames =
		[
			"PermissionModeCombo", "PermissionPresetCombo", "ApplyPermissionPresetButton",
			"SavePermissionProfileButton", "DeletePermissionProfileButton", "PermissionCategory",
			"PermissionCapability", "ReviewTimeoutBox", "PromptDialogWhenHiddenCheck",
			"ApproveReview", "ApproveSessionReview", "DenyReview",
			"Approve Once", "Approve Session",
			"ApproveReviewCommand", "ApproveSessionReviewCommand", "DenyReviewCommand",
			"ApplyPresetCommand", "ApplySelectedProfileCommand",
			"SavePermissionProfileCommand", "SaveCurrentAsProfileCommand",
			"DeletePermissionProfileCommand", "DeleteSelectedProfileCommand",
			"ClearSessionApprovals",
			"McpGuidedPopupButton", "McpGuidedTourButton", "McpDynamicWindowButton",
			"McpAiConnect_", "McpClientAutoConnect_", "McpCommandPaletteButton_", "McpPostgres_",
			"EnableMcpBridgeToggle", "ToggleMcpBridgeButton", "ApplyMcpSettingsButton",
		];

		foreach( string prop in new[] { "name", "automationId", "text", "commandName" } )
		{
			if( GetString( args, prop ) is { Length: > 0 } val )
			{
				foreach( string p in protectedNames )
					if( val.Contains( p, StringComparison.OrdinalIgnoreCase ) )
						return true;
			}
		}
		return false;
	}

	private static bool GetBool( JsonElement obj, string name )
		=> obj.TryGetProperty( name, out JsonElement el ) && el.ValueKind == JsonValueKind.True;

	private static string? GetString( JsonElement obj, string name )
		=> obj.TryGetProperty( name, out JsonElement el ) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;
}

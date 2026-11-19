using HackPDM.Mcp.Protocol;
using HackPDM.Mcp.Security;
using HackPDM.Mcp.State;

namespace HackPDM.Mcp.Tools;

internal static class McpLogAndRestoreTools
{
	public static void Register(
		McpServer server,
		McpActivityLog activityLog,
		McpRestorePointManager restorePoints,
		McpPermissionManager permissions )
	{
		// ---- MCP Activity Log ----

		server.RegisterTool(
			"mcp_log_get",
			"Reads the persistent HackPDM MCP activity log (newest first) showing every MCP tool call, redacted arguments, duration, success/error status, and linked restore point IDs.",
			McpSchema.Object()
				.Prop( "limit", McpSchema.Integer( "Maximum log entries to return (default 50).", 1, 2000, 50 ) )
				.Prop( "toolFilter", McpSchema.String( "Optional substring filter on tool name (e.g. 'sw_', 'fs_', 'ui_')." ) )
				.Prop( "errorsOnly", McpSchema.Boolean( "Return only failed tool calls (default false).", false ) )
				.Prop( "search", McpSchema.String( "Optional substring search across tool name, arguments, results, errors, and restore point IDs." ) )
				.Prop( "sinceUtc", McpSchema.String( "Optional ISO-8601 UTC timestamp lower bound." ) )
				.Build(),
			( raw, _ ) =>
			{
				McpToolArgs args = new( raw );
				DateTimeOffset? since = args.GetString( "sinceUtc" ) is { Length: > 0 } s && DateTimeOffset.TryParse( s, out var dto ) ? dto : null;
				IReadOnlyList<McpActivityEntry> entries = activityLog.GetEntries(
					limit: args.GetInt( "limit", 50 ) ?? 50,
					toolFilter: args.GetString( "toolFilter" ),
					errorsOnly: args.GetBool( "errorsOnly", false ),
					sinceUtc: since,
					search: args.GetString( "search" ) );

				return Task.FromResult( McpToolResult.Ok( new
				{
					logFilePath = activityLog.LogFilePath,
					totalRecorded = activityLog.Count,
					returned = entries.Count,
					entries,
				} ) );
			},
			new McpToolAnnotations( "Get MCP Activity Log", ReadOnlyHint: true, IdempotentHint: true ) );

		server.RegisterTool(
			"mcp_log_clear",
			"Clears the persistent HackPDM MCP activity log.",
			McpSchema.EmptyObject(),
			( _, _ ) =>
			{
				activityLog.Clear();
				return Task.FromResult( McpToolResult.Ok( new { ok = true, cleared = true, logFilePath = activityLog.LogFilePath } ) );
			},
			new McpToolAnnotations( "Clear MCP Activity Log", ReadOnlyHint: false, DestructiveHint: true ) );

		// ---- Restore Points / Undo Snapshots ----

		server.RegisterTool(
			"mcp_restore_point_create",
			"Creates a persistent named restore point (snapshot backup) of one or more local files/directories (anywhere on disk or in the PWA vault), plus optionally settings.json and AppConfig.json.",
			McpSchema.Object()
				.Prop( "label", McpSchema.String( "Human-readable name for this restore point (e.g. 'Before repathing KitBot assembly')." ), required: true )
				.Prop( "description", McpSchema.String( "Optional detailed description." ) )
				.Prop( "paths", McpSchema.StringArray( "List of file or directory paths to snapshot. Non-existent paths are also tracked so restoring deletes files created there later." ) )
				.Prop( "includeSettings", McpSchema.Boolean( "Include HackPDM settings.json in the restore point (default false).", false ) )
				.Prop( "includeAppConfig", McpSchema.Boolean( "Include HackPDM AppConfig.json in the restore point (default false).", false ) )
				.Build(),
			async ( raw, ct ) =>
			{
				McpToolArgs args = new( raw );
				McpRestorePoint rp = await restorePoints.CreateRestorePointAsync(
					label: args.RequireString( "label" ),
					description: args.GetString( "description" ),
					paths: args.GetStringList( "paths" ),
					includeSettings: args.GetBool( "includeSettings", false ),
					includeAppConfig: args.GetBool( "includeAppConfig", false ),
					automatic: false,
					triggerTool: "mcp_restore_point_create",
					cancellationToken: ct );

				return McpToolResult.Ok( new
				{
					ok = true,
					restorePoint = SummarizeRestorePoint( rp ),
				} );
			},
			new McpToolAnnotations( "Create Restore Point", ReadOnlyHint: false ) );

		server.RegisterTool(
			"mcp_restore_point_list",
			"Lists all available HackPDM MCP restore points (newest first) with labels, trigger tools, entry counts, backup sizes, and restore history.",
			McpSchema.Object()
				.Prop( "limit", McpSchema.Integer( "Maximum restore points to return (default 50).", 1, 500, 50 ) )
				.Build(),
			( raw, _ ) =>
			{
				McpToolArgs args = new( raw );
				IReadOnlyList<McpRestorePoint> list = restorePoints.ListRestorePoints( args.GetInt( "limit", 50 ) ?? 50 );
				return Task.FromResult( McpToolResult.Ok( new
				{
					storageDirectory = restorePoints.StorageDirectory,
					autoRestorePointsEnabled = restorePoints.AutoRestorePointsEnabled,
					maxRestorePoints = restorePoints.MaxRestorePoints,
					count = list.Count,
					restorePoints = list.Select( SummarizeRestorePoint ).ToList(),
				} ) );
			},
			new McpToolAnnotations( "List Restore Points", ReadOnlyHint: true, IdempotentHint: true ) );

		server.RegisterTool(
			"mcp_restore_point_inspect",
			"Inspects the full manifest and every tracked file/directory entry inside a specific restore point.",
			McpSchema.Object()
				.Prop( "id", McpSchema.String( "Restore point ID." ), required: true )
				.Build(),
			( raw, _ ) =>
			{
				McpToolArgs args = new( raw );
				string id = args.RequireString( "id" );
				McpRestorePoint rp = restorePoints.GetRestorePoint( id )
					?? throw new FileNotFoundException( $"Restore point '{id}' not found." );
				return Task.FromResult( McpToolResult.Ok( rp ) );
			},
			new McpToolAnnotations( "Inspect Restore Point", ReadOnlyHint: true, IdempotentHint: true ) );

		server.RegisterTool(
			"mcp_restore_point_restore",
			"Rolls back files, SolidWorks documents, and/or settings to the exact state captured in a restore point (restoring modified/deleted files and removing newly created files). Creates a safety backup before restoring by default.",
			McpSchema.Object()
				.Prop( "id", McpSchema.String( "Restore point ID to restore." ), required: true )
				.Prop( "createSafetyBackup", McpSchema.Boolean( "Automatically create a pre-restore backup first so the restore itself can be undone (default true).", true ) )
				.Prop( "dryRun", McpSchema.Boolean( "Preview what files would be restored or removed without modifying disk (default false).", false ) )
				.Build(),
			async ( raw, ct ) =>
			{
				McpToolArgs args = new( raw );
				McpRestoreResult res = await restorePoints.RestoreAsync(
					id: args.RequireString( "id" ),
					createSafetyBackup: args.GetBool( "createSafetyBackup", true ),
					dryRun: args.GetBool( "dryRun", false ),
					cancellationToken: ct );
				return McpToolResult.Ok( res );
			},
			new McpToolAnnotations( "Restore From Restore Point", ReadOnlyHint: false ) );

		server.RegisterTool(
			"mcp_restore_point_delete",
			"Permanently deletes a stored restore point backup from disk.",
			McpSchema.Object()
				.Prop( "id", McpSchema.String( "Restore point ID to delete." ), required: true )
				.Build(),
			( raw, _ ) =>
			{
				McpToolArgs args = new( raw );
				string id = args.RequireString( "id" );
				bool deleted = restorePoints.DeleteRestorePoint( id );
				return Task.FromResult( McpToolResult.Ok( new { ok = deleted, id, deleted } ) );
			},
			new McpToolAnnotations( "Delete Restore Point", ReadOnlyHint: false, DestructiveHint: true ) );

		server.RegisterTool(
			"mcp_settings_configure",
			"Reads or configures HackPDM MCP logging and automatic restore point settings (autoRestorePoints, maxRestorePoints).",
			McpSchema.Object()
				.Prop( "autoRestorePoints", McpSchema.Boolean( "Automatically create a restore point before mutating file, SolidWorks, or settings operations." ) )
				.Prop( "maxRestorePoints", McpSchema.Integer( "Maximum number of automatic restore points to retain before pruning oldest (5..500).", 5, 500 ) )
				.Build(),
			( raw, _ ) =>
			{
				McpToolArgs args = new( raw );
				if( args.GetNullableBool( "autoRestorePoints" ) is bool enabled )
					restorePoints.AutoRestorePointsEnabled = enabled;
				if( args.GetInt( "maxRestorePoints" ) is int max )
					restorePoints.MaxRestorePoints = max;

				return Task.FromResult( McpToolResult.Ok( new
				{
					autoRestorePoints = restorePoints.AutoRestorePointsEnabled,
					maxRestorePoints = restorePoints.MaxRestorePoints,
					activityLogFile = activityLog.LogFilePath,
					restorePointsDirectory = restorePoints.StorageDirectory,
					activityEntryCount = activityLog.Count,
					restorePointCount = restorePoints.ListRestorePoints( 500 ).Count,
				} ) );
			},
			new McpToolAnnotations( "Configure MCP Log & Restore Settings", ReadOnlyHint: false, IdempotentHint: true ) );

		// ---- MCP Permissions & Profiles (Read-Only Introspection — AI cannot self-escalate) ----

		server.RegisterTool(
			"mcp_permissions_get",
			"Inspects the currently active HackPDM MCP permission mode (auto, review, review-critical, read-only, local-only), active profile, allowed/denied capability categories, per-tool overrides, and any pending review requests. Permissions can only be modified by the user via the GUI, mcp-permissions.json, or CLI.",
			McpSchema.EmptyObject(),
			( _, _ ) => Task.FromResult( McpToolResult.Ok( permissions.GetStatusSnapshot() ) ),
			new McpToolAnnotations( "Get Active MCP Permissions", ReadOnlyHint: true, IdempotentHint: true ) );

		server.RegisterTool(
			"mcp_permissions_list_presets",
			"Lists all built-in HackPDM MCP permission presets (read-only, local-only, review, review-critical, local-cad-prep, auto) and user-saved custom profiles.",
			McpSchema.EmptyObject(),
			( _, _ ) => Task.FromResult( McpToolResult.Ok( new
			{
				configFilePath = permissions.ConfigFilePath,
				activeProfileKey = permissions.ActiveProfile.Key,
				activeMode = permissions.ActiveProfile.Mode,
				modes = Enum.GetValues<McpExecutionMode>().Select( m => new
				{
					mode = McpExecutionModeHelper.ToWireName( m ),
					description = McpExecutionModeHelper.Describe( m ),
				} ).ToList(),
				profiles = permissions.ListAllProfiles(),
			} ) ),
			new McpToolAnnotations( "List MCP Permission Presets & Profiles", ReadOnlyHint: true, IdempotentHint: true ) );
	}

	private static object SummarizeRestorePoint( McpRestorePoint rp ) => new
	{
		rp.Id,
		rp.Label,
		rp.Description,
		rp.TriggerTool,
		rp.Automatic,
		rp.CreatedAtUtc,
		rp.RestoredAtUtc,
		rp.TotalBackupBytes,
		entryCount = rp.Entries.Count,
		fileCount = rp.Entries.Count( e => !e.IsDirectory ),
		samplePaths = rp.Entries.Take( 6 ).Select( e => e.OriginalPath ).ToList(),
	};
}

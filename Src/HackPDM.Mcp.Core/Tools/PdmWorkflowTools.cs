using HackPDM.Mcp.Protocol;
using HackPDM.Mcp.Services;

namespace HackPDM.Mcp.Tools;

internal static class PdmWorkflowTools
{
	public static void Register( McpServer server, HackPdmApiService api )
	{
		server.RegisterTool(
			"pdm_get_latest",
			"Downloads the latest version of specified entries (or all entries in a directory) along with all recursive dependencies into the local working folder, preserving local edits that do not match any server version.",
			McpSchema.Object()
				.Prop( "entryIds", McpSchema.IntegerArray( "Odoo hp.entry IDs to download." ) )
				.Prop( "directoryId", McpSchema.Integer( "Odoo hp.directory ID to download (alternative to entryIds)." ) )
				.Prop( "withSubdirectories", McpSchema.Boolean( "When using directoryId, also include subdirectories (default true).", true ) )
				.Prop( "withDeleted", McpSchema.Boolean( "When using directoryId, also include deleted entries (default false).", false ) )
				.Build(),
			async ( raw, ct ) =>
			{
				McpToolArgs args = new( raw );
				return McpToolResult.Ok( await api.GetLatestAsync(
					args.GetIntList( "entryIds" ),
					args.GetInt( "directoryId" ),
					args.GetBool( "withSubdirectories", true ),
					args.GetBool( "withDeleted", false ),
					ct ) );
			},
			new McpToolAnnotations( "Get Latest", ReadOnlyHint: false, OpenWorldHint: true ) );

		server.RegisterTool(
			"pdm_checkout",
			"Checks out specified entries (or a directory's entries) and their dependencies to the current user and workstation node, optionally downloading the latest copies first and making local files writable.",
			McpSchema.Object()
				.Prop( "entryIds", McpSchema.IntegerArray( "Odoo hp.entry IDs to check out." ) )
				.Prop( "directoryId", McpSchema.Integer( "Odoo hp.directory ID to check out (alternative to entryIds)." ) )
				.Prop( "withSubdirectories", McpSchema.Boolean( "When using directoryId, include subdirectories (default true).", true ) )
				.Prop( "getLatestFirst", McpSchema.Boolean( "Download latest versions before checking out (default true, matching HackPDM CheckoutLatest).", true ) )
				.Build(),
			async ( raw, ct ) =>
			{
				McpToolArgs args = new( raw );
				return McpToolResult.Ok( await api.CheckoutAsync(
					args.GetIntList( "entryIds" ),
					args.GetInt( "directoryId" ),
					args.GetBool( "withSubdirectories", true ),
					args.GetBool( "getLatestFirst", true ),
					ct ) );
			},
			new McpToolAnnotations( "Check Out Entries", ReadOnlyHint: false, OpenWorldHint: true ) );

		server.RegisterTool(
			"pdm_uncheckout",
			"Releases (undoes) the current user's check-out on specified entries (or a directory's entries) and their dependencies without creating a new version, making local files read-only again.",
			McpSchema.Object()
				.Prop( "entryIds", McpSchema.IntegerArray( "Odoo hp.entry IDs to release." ) )
				.Prop( "directoryId", McpSchema.Integer( "Odoo hp.directory ID to release (alternative to entryIds)." ) )
				.Prop( "withSubdirectories", McpSchema.Boolean( "When using directoryId, include subdirectories (default true).", true ) )
				.Build(),
			async ( raw, _ ) =>
			{
				McpToolArgs args = new( raw );
				return McpToolResult.Ok( await api.UnCheckoutAsync(
					args.GetIntList( "entryIds" ),
					args.GetInt( "directoryId" ),
					args.GetBool( "withSubdirectories", true ) ) );
			},
			new McpToolAnnotations( "Undo Check Out", ReadOnlyHint: false, OpenWorldHint: true ) );

		server.RegisterTool(
			"pdm_commit_preview",
			"Dry-run preview of what committing local file(s) or a folder would do: expands SolidWorks dependencies, applies ignore/type filters, and classifies files into localOnly (new entries), remoteExisting (new versions), unchangedCheckedOut (release only), and brokenDependencies.",
			McpSchema.Object()
				.Prop( "filePaths", McpSchema.StringArray( "Local absolute paths or vault tree paths (e.g. 'root\\\\Projects\\\\part.sldprt') to preview." ) )
				.Prop( "folderPath", McpSchema.String( "Local folder path or vault tree path whose direct files will be previewed." ) )
				.Build(),
			async ( raw, _ ) =>
			{
				McpToolArgs args = new( raw );
				return McpToolResult.Ok( await api.PreviewCommitAsync(
					args.GetStringList( "filePaths" ),
					args.GetString( "folderPath" ) ) );
			},
			new McpToolAnnotations( "Preview Commit", ReadOnlyHint: true, IdempotentHint: true, OpenWorldHint: true ) );

		server.RegisterTool(
			"pdm_commit",
			"Commits (checks in) local file(s) and/or a local folder to Odoo in a single atomic hp.pdm.commit transaction, automatically scanning SolidWorks references and custom properties, creating missing remote folders, staging records, finalizing on the server, and releasing unchanged checked-out files.",
			McpSchema.Object()
				.Prop( "filePaths", McpSchema.StringArray( "Local absolute paths or vault tree paths to commit." ) )
				.Prop( "folderPath", McpSchema.String( "Local folder path or vault tree path whose direct files will be committed." ) )
				.Build(),
			async ( raw, _ ) =>
			{
				McpToolArgs args = new( raw );
				return McpToolResult.Ok( await api.CommitAsync(
					args.GetStringList( "filePaths" ),
					args.GetString( "folderPath" ) ) );
			},
			new McpToolAnnotations( "Commit / Check In", ReadOnlyHint: false, OpenWorldHint: true ) );

		server.RegisterTool(
			"pdm_logical_delete",
			"Soft-deletes entries on the Odoo server (sets deleted=true; reversible via pdm_restore_deleted). Never deletes dependencies.",
			McpSchema.Object()
				.Prop( "entryIds", McpSchema.IntegerArray( "Odoo hp.entry IDs to soft-delete." ) )
				.Prop( "directoryId", McpSchema.Integer( "Odoo hp.directory ID whose entries will be soft-deleted." ) )
				.Prop( "withSubdirectories", McpSchema.Boolean( "When using directoryId, also include subdirectories (default true).", true ) )
				.Build(),
			async ( raw, _ ) =>
			{
				McpToolArgs args = new( raw );
				return McpToolResult.Ok( await api.LogicalDeleteAsync(
					args.GetIntList( "entryIds" ),
					args.GetInt( "directoryId" ),
					args.GetBool( "withSubdirectories", true ) ) );
			},
			new McpToolAnnotations( "Logical Delete", ReadOnlyHint: false, DestructiveHint: true, OpenWorldHint: true ) );

		server.RegisterTool(
			"pdm_restore_deleted",
			"Restores logically deleted entries on the Odoo server (sets deleted=false) by entryIds or directoryId.",
			McpSchema.Object()
				.Prop( "entryIds", McpSchema.IntegerArray( "Specific Odoo hp.entry IDs to restore." ) )
				.Prop( "directoryId", McpSchema.Integer( "Odoo hp.directory ID whose deleted entries will be restored." ) )
				.Prop( "withSubdirectories", McpSchema.Boolean( "When using directoryId, also restore deleted entries in subdirectories (default false).", false ) )
				.Build(),
			async ( raw, _ ) =>
			{
				McpToolArgs args = new( raw );
				return McpToolResult.Ok( await api.RestoreDeletedAsync(
					args.GetIntList( "entryIds" ),
					args.GetInt( "directoryId" ),
					args.GetBool( "withSubdirectories", false ) ) );
			},
			new McpToolAnnotations( "Restore Deleted Entries", ReadOnlyHint: false, OpenWorldHint: true ) );

		server.RegisterTool(
			"pdm_permanent_delete",
			"PDM Administrators only: irreversibly removes entries and ALL their versions, properties, relationships, and stored attachments from Odoo. Requires confirm=true.",
			McpSchema.Object()
				.Prop( "entryIds", McpSchema.IntegerArray( "Odoo hp.entry IDs to permanently destroy." ), required: true )
				.Prop( "confirm", McpSchema.Boolean( "Must be true to execute irreversible permanent deletion." ), required: true )
				.Build(),
			async ( raw, ct ) =>
			{
				McpToolArgs args = new( raw );
				if( !args.GetBool( "confirm", false ) )
					return McpToolResult.Error( "Permanent deletion aborted: pass confirm=true to proceed." );
				List<int> ids = args.GetIntList( "entryIds" ) ?? [];
				return McpToolResult.Ok( await api.PermanentDeleteAsync( ids, ct ) );
			},
			new McpToolAnnotations( "Permanent Delete", ReadOnlyHint: false, DestructiveHint: true, OpenWorldHint: true ) );

		server.RegisterTool(
			"pdm_plan_version_download",
			"Inspects what downloading a specific historical hp.version would require (latest status, recursive dependency version IDs, and local conflict check) without downloading anything.",
			McpSchema.Object()
				.Prop( "versionId", McpSchema.Integer( "Odoo hp.version ID.", 1 ), required: true )
				.Build(),
			async ( raw, _ ) =>
			{
				McpToolArgs args = new( raw );
				return McpToolResult.Ok( await api.PlanVersionDownloadAsync( args.RequireInt( "versionId" ) ) );
			},
			new McpToolAnnotations( "Plan Version Download", ReadOnlyHint: true, IdempotentHint: true, OpenWorldHint: true ) );

		server.RegisterTool(
			"pdm_download_version",
			"Downloads a specific historical hp.version (optionally with its recursive dependencies) to the working folder or temp folder, or moves an existing version file between working and temp folders.",
			McpSchema.Object()
				.Prop( "versionId", McpSchema.Integer( "Odoo hp.version ID.", 1 ), required: true )
				.Prop( "action", McpSchema.Enum( "Action to perform (default 'download').", "download", "moveToTemp", "moveToWorking" ) )
				.Prop( "toTemp", McpSchema.Boolean( "When action='download', write into the temp folder instead of the working folder.", false ) )
				.Prop( "withDependencies", McpSchema.Boolean( "Download this version and all of its recursive dependency versions into the working folder.", false ) )
				.Build(),
			async ( raw, _ ) =>
			{
				McpToolArgs args = new( raw );
				return McpToolResult.Ok( await api.DownloadOrMoveVersionAsync(
					args.RequireInt( "versionId" ),
					args.GetString( "action", "download" )!,
					args.GetBool( "toTemp", false ),
					args.GetBool( "withDependencies", false ) ) );
			},
			new McpToolAnnotations( "Download or Move Historical Version", ReadOnlyHint: false, OpenWorldHint: true ) );
	}
}

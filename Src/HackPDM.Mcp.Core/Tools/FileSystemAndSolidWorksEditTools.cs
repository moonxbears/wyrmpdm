using System.Text.Json;
using System.Text.Json.Nodes;

using HackPDM.Infrastructure.Odoo;
using HackPDM.Infrastructure.SldWrks;
using HackPDM.Mcp.Protocol;
using HackPDM.Mcp.Services;

namespace HackPDM.Mcp.Tools;

internal static class FileSystemAndSolidWorksEditTools
{
	public static void Register( McpServer server, HackPdmApiService api )
	{
		// ---- Computer-Wide Cross-Platform Filesystem Operations ----

		server.RegisterTool(
			"fs_list",
			"Lists files and folders anywhere on the computer (cross-platform), annotating whether each item is inside the HackPDM PWA vault (and therefore committable) and identifying SolidWorks CAD files.",
			McpSchema.Object()
				.Prop( "path", McpSchema.String( "Absolute directory path anywhere on disk, or vault tree path (e.g. 'root/Projects')." ), required: true )
				.Prop( "searchPattern", McpSchema.String( "Optional glob filter (e.g. '*.SLD*', '*.sldasm'; default '*')." ) )
				.Prop( "recursive", McpSchema.Boolean( "Search subdirectories recursively (default false).", false ) )
				.Prop( "computeChecksums", McpSchema.Boolean( "Compute SHA-1 checksums for listed files (default false).", false ) )
				.Prop( "maxEntries", McpSchema.Integer( "Maximum files/directories to return (default 500).", 1, 2000, 500 ) )
				.Build(),
			( raw, _ ) =>
			{
				McpToolArgs args = new( raw );
				return Task.FromResult( McpToolResult.Ok( api.FsList(
					args.RequireString( "path" ),
					args.GetString( "searchPattern" ),
					args.GetBool( "recursive", false ),
					args.GetBool( "computeChecksums", false ),
					args.GetInt( "maxEntries", 500 ) ?? 500 ) ) );
			},
			new McpToolAnnotations( "List Files Anywhere on Disk", ReadOnlyHint: true, IdempotentHint: true ) );

		server.RegisterTool(
			"fs_stat",
			"Inspects metadata for any file or directory on the computer, including whether it sits inside the configured HackPDM PWA vault (canCommitToPdm), its vault treePath, SolidWorks type, size, timestamps, and optional SHA-1.",
			McpSchema.Object()
				.Prop( "path", McpSchema.String( "Absolute path anywhere on disk, or vault tree path." ), required: true )
				.Prop( "computeChecksum", McpSchema.Boolean( "Compute SHA-1 checksum for files (default true).", true ) )
				.Build(),
			( raw, _ ) =>
			{
				McpToolArgs args = new( raw );
				return Task.FromResult( McpToolResult.Ok( api.FsStat(
					args.RequireString( "path" ),
					args.GetBool( "computeChecksum", true ) ) ) );
			},
			new McpToolAnnotations( "Inspect File/Folder Metadata", ReadOnlyHint: true, IdempotentHint: true ) );

		server.RegisterTool(
			"fs_mkdir",
			"Creates a directory (including intermediate parent directories) anywhere on disk or inside the PWA vault, recording an automatic restore point when enabled.",
			McpSchema.Object()
				.Prop( "path", McpSchema.String( "Absolute directory path or vault tree path to create." ), required: true )
				.Build(),
			async ( raw, ct ) =>
			{
				McpToolArgs args = new( raw );
				return McpToolResult.Ok( await api.FsMkdirAsync( args.RequireString( "path" ), ct ) );
			},
			new McpToolAnnotations( "Create Directory", ReadOnlyHint: false ) );

		server.RegisterTool(
			"fs_copy",
			"Copies a file or directory anywhere on disk (for example copying external CAD files into the HackPDM PWA vault), recording an automatic restore point before overwriting.",
			McpSchema.Object()
				.Prop( "sourcePath", McpSchema.String( "Source file or directory path." ), required: true )
				.Prop( "destinationPath", McpSchema.String( "Destination file or directory path (may be a vault tree path like 'root/Projects/Part1.SLDPRT')." ), required: true )
				.Prop( "overwrite", McpSchema.Boolean( "Overwrite existing destination file(s) (default false).", false ) )
				.Build(),
			async ( raw, ct ) =>
			{
				McpToolArgs args = new( raw );
				return McpToolResult.Ok( await api.FsCopyAsync(
					args.RequireString( "sourcePath" ),
					args.RequireString( "destinationPath" ),
					args.GetBool( "overwrite", false ),
					ct ) );
			},
			new McpToolAnnotations( "Copy File or Directory", ReadOnlyHint: false ) );

		server.RegisterTool(
			"fs_move",
			"Moves or renames a file or directory anywhere on disk (including moving external CAD files into the HackPDM PWA vault), recording an automatic restore point covering both source and destination.",
			McpSchema.Object()
				.Prop( "sourcePath", McpSchema.String( "Source file or directory path." ), required: true )
				.Prop( "destinationPath", McpSchema.String( "Destination file or directory path (may be a vault tree path)." ), required: true )
				.Prop( "overwrite", McpSchema.Boolean( "Overwrite existing destination file(s) (default false).", false ) )
				.Build(),
			async ( raw, ct ) =>
			{
				McpToolArgs args = new( raw );
				return McpToolResult.Ok( await api.FsMoveAsync(
					args.RequireString( "sourcePath" ),
					args.RequireString( "destinationPath" ),
					args.GetBool( "overwrite", false ),
					ct ) );
			},
			new McpToolAnnotations( "Move or Rename File/Directory", ReadOnlyHint: false ) );

		server.RegisterTool(
			"fs_delete",
			"Deletes file(s) or directory(s) anywhere on disk. Requires confirm=true and automatically creates a restore point before deleting so files can be recovered.",
			McpSchema.Object()
				.Prop( "paths", McpSchema.StringArray( "File or directory paths to delete." ), required: true )
				.Prop( "forceWritable", McpSchema.Boolean( "Clear ReadOnly attributes before deleting (default false).", false ) )
				.Prop( "confirm", McpSchema.Boolean( "Must be true to delete." ), required: true )
				.Build(),
			async ( raw, ct ) =>
			{
				McpToolArgs args = new( raw );
				if( !args.GetBool( "confirm", false ) )
					return McpToolResult.Error( "fs_delete aborted: pass confirm=true to proceed." );
				return McpToolResult.Ok( await api.FsDeleteAsync(
					args.GetStringList( "paths" ) ?? [],
					args.GetBool( "forceWritable", false ),
					ct ) );
			},
			new McpToolAnnotations( "Delete File or Directory", ReadOnlyHint: false, DestructiveHint: true ) );

		server.RegisterTool(
			"fs_import_to_pwa",
			"Imports external files (such as SolidWorks .SLDASM/.SLDDRW/.SLDPRT files located outside the PWA root) into a target folder inside the HackPDM PWA vault: optionally collects all local SolidWorks dependencies recursively, copies or moves them into the PWA, rewrites every imported SolidWorks file's internal dependency paths to point inside the PWA so they are ready to commit, and records one atomic restore point.",
			McpSchema.Object()
				.Prop( "sourcePaths", McpSchema.StringArray( "External file or folder paths to import into the PWA vault." ), required: true )
				.Prop( "targetPwaFolder", McpSchema.String( "Target folder inside PWA (vault tree path like 'root/Projects/Sub' or absolute path inside PWA).", "root" ), required: true )
				.Prop( "mode", McpSchema.Enum( "Whether to 'copy' (default, keeps originals) or 'move' files into the PWA.", "copy", "move" ) )
				.Prop( "includeDependencies", McpSchema.Boolean( "Recursively locate and import local SolidWorks dependencies referenced by imported assemblies/drawings (default true).", true ) )
				.Prop( "overwrite", McpSchema.Boolean( "Overwrite existing files in targetPwaFolder (default false).", false ) )
				.Prop( "rewriteDependencies", McpSchema.Boolean( "Rewrite internal dependency paths inside imported .SLDPRT/.SLDASM/.SLDDRW files to point to their new PWA locations (default true).", true ) )
				.Prop( "solidWorksPathPrefix", McpSchema.String( "Optional explicit SolidWorks directory prefix (e.g. 'C:\\PWA\\Projects\\Sub') to stamp into rewritten CAD references; defaults to the target PWA directory path." ) )
				.Build(),
			async ( raw, ct ) =>
			{
				McpToolArgs args = new( raw );
				return McpToolResult.Ok( await api.FsImportToPwaAsync(
					sourcePaths: args.GetStringList( "sourcePaths" ) ?? [],
					targetPwaFolder: args.RequireString( "targetPwaFolder" ),
					mode: args.GetString( "mode", "copy" )!,
					includeDependencies: args.GetBool( "includeDependencies", true ),
					overwrite: args.GetBool( "overwrite", false ),
					rewriteDependencies: args.GetBool( "rewriteDependencies", true ),
					solidWorksPathPrefix: args.GetString( "solidWorksPathPrefix" ),
					cancellationToken: ct ) );
			},
			new McpToolAnnotations( "Import CAD/Files to PWA & Repath", ReadOnlyHint: false ) );

		// ---- Cross-Platform SolidWorks Property & Dependency Editing ----

		JsonObject propEditSchema = McpSchema.Object()
			.Prop( "configuration", McpSchema.String( "Configuration name, or '' (empty string) for file-level custom properties." ) )
			.Prop( "name", McpSchema.String( "Custom property name (e.g. 'Description', 'PartNum', 'Material', 'Revision')." ), required: true )
			.Prop( "type", McpSchema.Enum( "Property type (auto-inferred if omitted).", "text", "number", "yesno", "date" ) )
			.Prop( "value", McpSchema.Any( "New property value (string, number, boolean, or ISO-8601 date string)." ) )
			.Prop( "remove", McpSchema.Boolean( "Set true to delete this custom property instead of setting it.", false ) )
			.Build();

		server.RegisterTool(
			"sw_set_properties",
			"Cross-platform SolidWorks custom property editor (.SLDPRT, .SLDASM, .SLDDRW; both 2015+ Modern and <=2014 OLE formats): adds, updates, or removes file-level and configuration-level custom properties without requiring SolidWorks or COM. Automatically creates a restore point before writing and verifies the updated properties.",
			McpSchema.Object()
				.Prop( "path", McpSchema.String( "Absolute path anywhere on disk or vault tree path to the SolidWorks document." ), required: true )
				.Prop( "properties", McpSchema.Array( "List of custom property edits to apply.", propEditSchema ), required: true )
				.Prop( "forceWritable", McpSchema.Boolean( "Clear ReadOnly attribute before writing (default false).", false ) )
				.Build(),
			async ( raw, ct ) =>
			{
				McpToolArgs args = new( raw );
				JsonElement? propsEl = args.GetElement( "properties" );
				if( propsEl is not { ValueKind: JsonValueKind.Array } arr || arr.GetArrayLength() == 0 )
					throw new ArgumentException( "Provide a non-empty 'properties' array." );

				List<SwPropertyEdit> edits = [];
				foreach( JsonElement item in arr.EnumerateArray() )
				{
					McpToolArgs itemArgs = new( item );
					string name = itemArgs.RequireString( "name" );
					string cfg = itemArgs.GetString( "configuration" ) ?? string.Empty;
					string? type = itemArgs.GetString( "type" );
					bool remove = itemArgs.GetBool( "remove", false );
					object? val = itemArgs.GetElement( "value" ) is { } ve ? OdooValues.FromJson( ve ) : null;
					edits.Add( new SwPropertyEdit( cfg, name, type, val, remove ) );
				}

				return McpToolResult.Ok( await api.SwSetPropertiesAsync(
					args.RequireString( "path" ),
					edits,
					args.GetBool( "forceWritable", false ),
					ct ) );
			},
			new McpToolAnnotations( "Set SolidWorks Custom Properties", ReadOnlyHint: false ) );

		JsonObject depRuleSchema = McpSchema.Object()
			.Prop( "oldPath", McpSchema.String( "Existing stored dependency path (e.g. 'C:\\temp\\Part1.SLDPRT') or bare filename ('Part1.SLDPRT') to match." ), required: true )
			.Prop( "newPath", McpSchema.String( "New dependency path to write into the SolidWorks file (e.g. 'C:\\PWA\\Projects\\Part1.SLDPRT')." ), required: true )
			.Build();

		server.RegisterTool(
			"sw_set_dependencies",
			"Cross-platform SolidWorks dependency path rewriter (.SLDPRT, .SLDASM, .SLDDRW; both 2015+ Modern and <=2014 OLE formats): updates stored reference paths across SwDocContentMgrInfo, COMPINSTANCETREE, Header2, and OLE Config streams using explicit replacement rules, prefix swapping (oldPrefix -> newPrefix), or directory remapping (remapToDirectory). Automatically creates a restore point before writing and verifies the rewritten paths.",
			McpSchema.Object()
				.Prop( "path", McpSchema.String( "Absolute path anywhere on disk or vault tree path to the SolidWorks file." ), required: true )
				.Prop( "replacements", McpSchema.Array( "Explicit oldPath -> newPath replacement rules (matches full stored path or bare filename).", depRuleSchema ) )
				.Prop( "oldPrefix", McpSchema.String( "Optional directory prefix to replace (e.g. 'C:\\Users\\me\\Downloads')." ) )
				.Prop( "newPrefix", McpSchema.String( "Replacement directory prefix paired with oldPrefix (e.g. 'C:\\PWA\\Projects')." ) )
				.Prop( "remapToDirectory", McpSchema.String( "Remap every direct dependency in the file to this directory (Windows path or vault tree path like 'root/Projects')." ) )
				.Prop( "dryRun", McpSchema.Boolean( "Preview planned path rewrites without modifying the file (default false).", false ) )
				.Prop( "forceWritable", McpSchema.Boolean( "Clear ReadOnly attribute before writing (default false).", false ) )
				.Build(),
			async ( raw, ct ) =>
			{
				McpToolArgs args = new( raw );
				List<SwDependencyReplacement>? replacements = null;
				if( args.GetElement( "replacements" ) is { ValueKind: JsonValueKind.Array } arr )
				{
					replacements = [];
					foreach( JsonElement item in arr.EnumerateArray() )
					{
						McpToolArgs itemArgs = new( item );
						replacements.Add( new SwDependencyReplacement(
							itemArgs.RequireString( "oldPath" ),
							itemArgs.RequireString( "newPath" ) ) );
					}
				}

				return McpToolResult.Ok( await api.SwSetDependenciesAsync(
					args.RequireString( "path" ),
					replacements,
					args.GetString( "oldPrefix" ),
					args.GetString( "newPrefix" ),
					args.GetString( "remapToDirectory" ),
					args.GetBool( "dryRun", false ),
					args.GetBool( "forceWritable", false ),
					ct ) );
			},
			new McpToolAnnotations( "Rewrite SolidWorks Dependency Paths", ReadOnlyHint: false ) );
	}
}

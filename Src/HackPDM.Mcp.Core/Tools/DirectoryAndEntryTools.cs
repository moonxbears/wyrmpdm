using HackPDM.Core;
using HackPDM.Mcp.Protocol;
using HackPDM.Mcp.Services;

namespace HackPDM.Mcp.Tools;

internal static class DirectoryAndEntryTools
{
	public static void Register( McpServer server, HackPdmApiService api )
	{
		server.RegisterTool(
			"metadata_get_lookups",
			"Returns cached HackPDM Odoo reference metadata: users, workstation nodes, registered file types, categories, custom properties, entry name ignore filters, server settings, and root directory.",
			McpSchema.Object()
				.Prop( "category", McpSchema.Enum( "Subset of metadata to return (default 'all').", "all", "users", "nodes", "types", "categories", "properties", "filters", "settings", "root" ) )
				.Build(),
			( raw, _ ) =>
			{
				McpToolArgs args = new( raw );
				return Task.FromResult( McpToolResult.Ok( api.GetLookups( args.GetString( "category", "all" )! ) ) );
			},
			new McpToolAnnotations( "Get Metadata Lookups", ReadOnlyHint: true, IdempotentHint: true ) );

		server.RegisterTool(
			"directory_get_tree",
			"Returns the HackPDM directory tree structure (merged remote Odoo folders + local working folder, remote-only, or local-only), with folder IDs, full tree paths, origin (Synced/RemoteOnly/LocalOnly), and optional filtering or remote entry inclusion.",
			McpSchema.Object()
				.Prop( "source", McpSchema.Enum( "Which tree to build (default 'merged').", "merged", "remote", "local" ) )
				.Prop( "filter", McpSchema.String( "Optional case-insensitive folder name substring filter (keeps matched nodes and their ancestors)." ) )
				.Prop( "maxDepth", McpSchema.Integer( "Optional maximum tree depth to return (0 = unlimited).", 0, 100, 0 ) )
				.Prop( "withEntries", McpSchema.Boolean( "When source='remote', also return each directory's entries inline from Odoo.", false ) )
				.Prop( "rootDirectoryId", McpSchema.Integer( "Optional Odoo hp.directory ID to root the remote tree at (defaults to root directory id 1).", 1 ) )
				.Build(),
			async ( raw, ct ) =>
			{
				McpToolArgs args = new( raw );
				return McpToolResult.Ok( await api.GetDirectoryTreeAsync(
					source: args.GetString( "source", "merged" )!,
					filter: args.GetString( "filter" ),
					maxDepth: args.GetInt( "maxDepth", 0 ) ?? 0,
					withEntries: args.GetBool( "withEntries", false ),
					rootDirectoryId: args.GetInt( "rootDirectoryId" ),
					cancellationToken: ct ) );
			},
			new McpToolAnnotations( "Get Directory Tree", ReadOnlyHint: true, IdempotentHint: true, OpenWorldHint: true ) );

		server.RegisterTool(
			"directory_get_entries",
			"Lists all entries in a HackPDM directory by joining Odoo entries with local vault files, resolving each entry's FileStatus code (ok, ro, lo, nv, cm, co, lm, dt, if, ft), reprType, size, dates, checkout user, and local path.",
			McpSchema.Object()
				.Prop( "directoryId", McpSchema.Integer( "Odoo hp.directory ID (optional if treePath is provided)." ) )
				.Prop( "treePath", McpSchema.String( "Directory tree path such as 'root', 'root\\\\Projects\\\\Parts', or 'root / Projects / Parts'." ) )
				.Prop( "includeDeleted", McpSchema.Boolean( "Include logically deleted (inactive) remote entries.", false ) )
				.Prop( "filterUnknownTypes", McpSchema.Boolean( "Mark unregistered local file extensions with status 'ft'.", true ) )
				.Prop( "nameFilter", McpSchema.String( "Optional case-insensitive file name substring filter." ) )
				.Prop( "statusFilter", McpSchema.String( "Optional status filter (e.g. 'ok', 'ro', 'lo', 'lm', 'cm', 'co', 'nv', 'dt')." ) )
				.Prop( "sortBy", McpSchema.Enum( "Field to sort results by.", "name", "id", "size", "type", "category", "status", "remoteDate", "localDate", "checkout", "latestVersionId" ) )
				.Prop( "descending", McpSchema.Boolean( "Sort descending instead of ascending.", false ) )
				.Build(),
			async ( raw, ct ) =>
			{
				McpToolArgs args = new( raw );
				return McpToolResult.Ok( await api.GetDirectoryEntriesAsync(
					directoryId: args.GetInt( "directoryId" ),
					treePath: args.GetString( "treePath" ),
					includeDeleted: args.GetBool( "includeDeleted", false ),
					filterUnknownTypes: args.GetBool( "filterUnknownTypes", true ),
					nameFilter: args.GetString( "nameFilter" ),
					statusFilter: args.GetString( "statusFilter" ),
					sortBy: args.GetString( "sortBy", "name" ),
					descending: args.GetBool( "descending", false ),
					cancellationToken: ct ) );
			},
			new McpToolAnnotations( "List Directory Entries", ReadOnlyHint: true, IdempotentHint: true, OpenWorldHint: true ) );

		server.RegisterTool(
			"directory_get_entry_ids",
			"Lists all Odoo hp.entry IDs inside a directory, optionally recursing into subdirectories and/or including deleted entries.",
			McpSchema.Object()
				.Prop( "directoryId", McpSchema.Integer( "Odoo hp.directory ID.", 1 ), required: true )
				.Prop( "withSubdirectories", McpSchema.Boolean( "Recurse into all descendant directories.", false ) )
				.Prop( "withDeleted", McpSchema.Boolean( "Include logically deleted entries.", false ) )
				.Build(),
			async ( raw, _ ) =>
			{
				McpToolArgs args = new( raw );
				return McpToolResult.Ok( await api.GetDirectoryEntryIdsAsync(
					args.RequireInt( "directoryId" ),
					args.GetBool( "withSubdirectories", false ),
					args.GetBool( "withDeleted", false ) ) );
			},
			new McpToolAnnotations( "Get Directory Entry IDs", ReadOnlyHint: true, IdempotentHint: true, OpenWorldHint: true ) );

		server.RegisterTool(
			"directory_find_path",
			"Checks how deep a folder path already exists on the Odoo server using hp.directory.last_available_directory.",
			McpSchema.Object()
				.Prop( "path", McpSchema.String( "Folder path such as 'root\\\\Projects\\\\NewFolder' or 'Projects/NewFolder'." ) )
				.Prop( "segments", McpSchema.StringArray( "Folder segments alternative to 'path', e.g. [\"Projects\", \"NewFolder\"]." ) )
				.Build(),
			async ( raw, _ ) =>
			{
				McpToolArgs args = new( raw );
				IReadOnlyList<string> segs = ResolveSegments( args );
				return McpToolResult.Ok( await api.FindLastAvailableDirectoryAsync( segs ) );
			},
			new McpToolAnnotations( "Find Directory Path", ReadOnlyHint: true, IdempotentHint: true, OpenWorldHint: true ) );

		server.RegisterTool(
			"directory_create",
			"Creates missing directory records on the Odoo server (via HpDirectory.CreateNew) and/or creates the corresponding local folder inside the PWA working folder.",
			McpSchema.Object()
				.Prop( "path", McpSchema.String( "Folder path such as 'root\\\\Projects\\\\SubFolder' or 'Projects/SubFolder'." ) )
				.Prop( "segments", McpSchema.StringArray( "Folder segments below root, alternative to 'path'." ) )
				.Prop( "createRemote", McpSchema.Boolean( "Create missing hp.directory records in Odoo (default true).", true ) )
				.Prop( "createLocal", McpSchema.Boolean( "Also create the folder in the local PWA vault (default true).", true ) )
				.Build(),
			async ( raw, _ ) =>
			{
				McpToolArgs args = new( raw );
				IReadOnlyList<string> segs = ResolveSegments( args );
				return McpToolResult.Ok( await api.CreateDirectoryAsync(
					segs,
					args.GetBool( "createRemote", true ),
					args.GetBool( "createLocal", true ) ) );
			},
			new McpToolAnnotations( "Create Directory", ReadOnlyHint: false, OpenWorldHint: true ) );

		server.RegisterTool(
			"path_convert",
			"Converts a path between HackPDM's three path forms using PwaPaths: local absolute path, server name (root\\a\\b\\file), and server directory (root / a / b).",
			McpSchema.Object()
				.Prop( "path", McpSchema.String( "Local absolute path, server name, or server directory string." ), required: true )
				.Build(),
			( raw, _ ) =>
			{
				McpToolArgs args = new( raw );
				return Task.FromResult( McpToolResult.Ok( api.ConvertPath( args.RequireString( "path" ) ) ) );
			},
			new McpToolAnnotations( "Convert HackPDM Path", ReadOnlyHint: true, IdempotentHint: true ) );

		// ---- Entries, Versions, Previews & Dependency Graph ----

		server.RegisterTool(
			"entry_get_details",
			"Retrieves comprehensive details for an Odoo hp.entry: entry metadata, checkout status, local file/checksum status, plus optional version history, parents (where-used), children (dependents), custom properties, and latest version metadata.",
			McpSchema.Object()
				.Prop( "entryId", McpSchema.Integer( "Odoo hp.entry ID.", 1 ), required: true )
				.Prop( "includeHistory", McpSchema.Boolean( "Include version history rows (default true).", true ) )
				.Prop( "includeParents", McpSchema.Boolean( "Include parent versions / where-used (default true).", true ) )
				.Prop( "includeChildren", McpSchema.Boolean( "Include child versions / dependents (default true).", true ) )
				.Prop( "includeProperties", McpSchema.Boolean( "Include stored SolidWorks/custom properties across versions (default true).", true ) )
				.Prop( "includeVersionInfo", McpSchema.Boolean( "Include detailed latest version metadata (default true).", true ) )
				.Build(),
			async ( raw, _ ) =>
			{
				McpToolArgs args = new( raw );
				return McpToolResult.Ok( await api.GetEntryDetailsAsync(
					args.RequireInt( "entryId" ),
					args.GetBool( "includeHistory", true ),
					args.GetBool( "includeParents", true ),
					args.GetBool( "includeChildren", true ),
					args.GetBool( "includeProperties", true ),
					args.GetBool( "includeVersionInfo", true ) ) );
			},
			new McpToolAnnotations( "Get Entry Details", ReadOnlyHint: true, IdempotentHint: true, OpenWorldHint: true ) );

		server.RegisterTool(
			"entry_get_history",
			"Lists every saved hp.version of an entry (newest version ID first) with size, checksum, modifying user, node, and timestamps.",
			McpSchema.Object()
				.Prop( "entryId", McpSchema.Integer( "Odoo hp.entry ID.", 1 ), required: true )
				.Build(),
			async ( raw, _ ) =>
			{
				McpToolArgs args = new( raw );
				return McpToolResult.Ok( await api.GetEntryHistoryAsync( args.RequireInt( "entryId" ) ) );
			},
			new McpToolAnnotations( "Get Entry History", ReadOnlyHint: true, IdempotentHint: true, OpenWorldHint: true ) );

		server.RegisterTool(
			"entry_get_relationships",
			"Lists parent versions (where-used) and/or child versions (dependents) of an entry or version, with optional recursive dependency ID expansion.",
			McpSchema.Object()
				.Prop( "entryId", McpSchema.Integer( "Odoo hp.entry ID (uses its latest version if versionId is omitted)." ) )
				.Prop( "versionId", McpSchema.Integer( "Specific Odoo hp.version ID." ) )
				.Prop( "direction", McpSchema.Enum( "Which relationship direction to query.", "both", "parents", "children" ) )
				.Prop( "recursive", McpSchema.Boolean( "Also query recursive dependency entry/version IDs from the server.", false ) )
				.Build(),
			async ( raw, _ ) =>
			{
				McpToolArgs args = new( raw );
				return McpToolResult.Ok( await api.GetEntryRelationshipsAsync(
					args.GetInt( "entryId" ),
					args.GetInt( "versionId" ),
					args.GetString( "direction", "both" )!,
					args.GetBool( "recursive", false ) ) );
			},
			new McpToolAnnotations( "Get Entry Relationships", ReadOnlyHint: true, IdempotentHint: true, OpenWorldHint: true ) );

		server.RegisterTool(
			"entry_get_properties",
			"Lists stored custom properties (hp.version.property) across all versions of an entry or for specific version IDs.",
			McpSchema.Object()
				.Prop( "entryId", McpSchema.Integer( "Odoo hp.entry ID." ) )
				.Prop( "versionIds", McpSchema.IntegerArray( "Specific hp.version IDs to query (overrides entryId)." ) )
				.Build(),
			async ( raw, _ ) =>
			{
				McpToolArgs args = new( raw );
				return McpToolResult.Ok( await api.GetEntryPropertiesAsync(
					args.GetInt( "entryId" ),
					args.GetIntList( "versionIds" ) ) );
			},
			new McpToolAnnotations( "Get Entry Properties", ReadOnlyHint: true, IdempotentHint: true, OpenWorldHint: true ) );

		server.RegisterTool(
			"version_get_info",
			"Reads detailed metadata for one or more hp.version records including checksum, file size, directory path, local copy conflict status, parent/child IDs, and attachment ID.",
			McpSchema.Object()
				.Prop( "versionIds", McpSchema.IntegerArray( "One or more hp.version IDs." ), required: true )
				.Build(),
			async ( raw, _ ) =>
			{
				McpToolArgs args = new( raw );
				List<int> ids = args.GetIntList( "versionIds" ) ?? [];
				return McpToolResult.Ok( await api.GetVersionsInfoAsync( ids ) );
			},
			new McpToolAnnotations( "Get Version Info", ReadOnlyHint: true, IdempotentHint: true, OpenWorldHint: true ) );

		server.RegisterTool(
			"version_get_preview",
			"Fetches the preview thumbnail image for a committed Odoo hp.version and/or a local SolidWorks document. Returns an MCP image content block plus metadata, and optionally saves the image to disk.",
			McpSchema.Object()
				.Prop( "versionId", McpSchema.Integer( "Odoo hp.version ID." ) )
				.Prop( "localFilePath", McpSchema.String( "Local SolidWorks file path (used directly or as fallback when version has no server preview)." ) )
				.Prop( "saveToPath", McpSchema.String( "Optional output path to save the retrieved image file." ) )
				.Prop( "includeImageContent", McpSchema.Boolean( "Return the image as an inline MCP image content block (default true).", true ) )
				.Build(),
			async ( raw, ct ) =>
			{
				McpToolArgs args = new( raw );
				var (bytes, mime, source, savedPath) = await api.GetPreviewImageAsync(
					args.GetInt( "versionId" ),
					args.GetString( "localFilePath" ),
					args.GetString( "saveToPath" ),
					ct );

				if( bytes is not { Length: > 0 } )
					return McpToolResult.Ok( new { hasPreview = false, source, savedPath } );

				object meta = new { hasPreview = true, source, mimeType = mime, byteLength = bytes.Length, savedPath };
				return args.GetBool( "includeImageContent", true )
					? McpToolResult.OkImage( bytes, mime ?? "image/png", meta )
					: McpToolResult.Ok( meta );
			},
			new McpToolAnnotations( "Get Preview Image", ReadOnlyHint: true, IdempotentHint: true, OpenWorldHint: true ) );

		server.RegisterTool(
			"version_upload_preview",
			"Uploads or replaces the preview thumbnail image on a committed hp.version (validates PNG/JPEG/BMP/GIF/WebP up to 4 MiB).",
			McpSchema.Object()
				.Prop( "versionId", McpSchema.Integer( "Odoo hp.version ID.", 1 ), required: true )
				.Prop( "filePath", McpSchema.String( "Path to a local image file to upload." ) )
				.Prop( "base64Data", McpSchema.String( "Base64-encoded image bytes (alternative to filePath)." ) )
				.Build(),
			async ( raw, ct ) =>
			{
				McpToolArgs args = new( raw );
				return McpToolResult.Ok( await api.UploadPreviewImageAsync(
					args.RequireInt( "versionId" ),
					args.GetString( "filePath" ),
					args.GetString( "base64Data" ),
					ct ) );
			},
			new McpToolAnnotations( "Upload Preview Image", ReadOnlyHint: false, OpenWorldHint: true ) );

		server.RegisterTool(
			"version_read_contents",
			"Downloads and reads the stored file contents of a specific hp.version (returned as UTF-8 text or base64, and optionally saved to disk).",
			McpSchema.Object()
				.Prop( "versionId", McpSchema.Integer( "Odoo hp.version ID.", 1 ), required: true )
				.Prop( "encoding", McpSchema.Enum( "Output encoding ('auto', 'utf8', or 'base64').", "auto", "utf8", "base64" ) )
				.Prop( "maxBytes", McpSchema.Integer( "Maximum bytes to return inline (default 262144).", 1024, 10 * 1024 * 1024, 262144 ) )
				.Prop( "saveToPath", McpSchema.String( "Optional file path to write the full downloaded bytes to." ) )
				.Build(),
			async ( raw, ct ) =>
			{
				McpToolArgs args = new( raw );
				return McpToolResult.Ok( await api.ReadVersionContentsAsync(
					args.RequireInt( "versionId" ),
					args.GetString( "encoding", "auto" )!,
					args.GetInt( "maxBytes", 262144 ) ?? 262144,
					args.GetString( "saveToPath" ),
					ct ) );
			},
			new McpToolAnnotations( "Read Version Contents", ReadOnlyHint: true, IdempotentHint: true, OpenWorldHint: true ) );

		server.RegisterTool(
			"dependency_graph_load",
			"Builds a HackPDM node graph in any viewMode: 'Dependencies' (Children, Parents, Both, or recursive 'FullNetwork' showing each parent's and child's parents & children), 'FolderStructure', 'FoldersAndFiles', 'ByProperty' (cluster by custom property or attribute), or 'SearchFilter'. Supports independent depth loading (maxDepth, maxChildDepth, maxParentDepth), per-node branch limits (maxChildBranches, maxParentBranches), file-type/extension filtering (e.g. sldprt only, sldasm, slddrw), wildcard name/path/property filters, edge bridging, and optional GraphLayout coordinates.",
			McpSchema.Object()
				.Prop( "entryIds", McpSchema.IntegerArray( "Root hp.entry IDs (required for viewMode='Dependencies'; optional seed entries for ByProperty)." ) )
				.Prop( "viewMode", McpSchema.Enum( "Graph construction mode (default 'Dependencies').", "Dependencies", "FolderStructure", "FoldersAndFiles", "ByProperty", "SearchFilter" ) )
				.Prop( "direction", McpSchema.Enum( "Dependency traversal direction (default 'children'; 'full_network' expands each parent's and child's parents & children recursively).", "children", "parents", "both", "full_network", "FullNetwork" ) )
				.Prop( "maxDepth", McpSchema.Integer( "Overall maximum hop/level depth (0 = unlimited).", 0, 20, 0 ) )
				.Prop( "maxChildDepth", McpSchema.Integer( "Maximum offspring/child depth (omit or null = inherit maxDepth; 0 = no offspring).", 0, 20 ) )
				.Prop( "maxParentDepth", McpSchema.Integer( "Maximum parent depth (omit or null = inherit maxDepth; 0 = no parents).", 0, 20 ) )
				.Prop( "maxChildBranches", McpSchema.Integer( "Maximum offspring/child branches expanded per node (omit or null = unlimited; 0 = none).", 0, 1000 ) )
				.Prop( "maxParentBranches", McpSchema.Integer( "Maximum parent branches expanded per node (omit or null = unlimited; 0 = none).", 0, 1000 ) )
				.Prop( "maxNodes", McpSchema.Integer( "Maximum total nodes in the graph (0 = unlimited; default 250).", 0, 5000, 250 ) )
				.Prop( "directoryId", McpSchema.Integer( "Optional Odoo hp.directory ID to scope FolderStructure, FoldersAndFiles, ByProperty, or SearchFilter.", 1 ) )
				.Prop( "folderPath", McpSchema.String( "Optional server folder path (e.g. 'root\\\\Projects\\\\Drive') to scope Folder/Property/Search views." ) )
				.Prop( "recursiveFolders", McpSchema.Boolean( "Include subdirectories when collecting files in ByProperty mode (default true).", true ) )
				.Prop( "extensions", McpSchema.StringArray( "Whitelist file extensions or aliases (e.g. [\"sldprt\"], [\"sldasm\", \"slddrw\"], [\"cad\"], [\"pdf\"])." ) )
				.Prop( "fileTypes", McpSchema.StringArray( "Alias for 'extensions' (e.g. [\"sldprt\"] for parts only, [\"sldasm\"] for assemblies only)." ) )
				.Prop( "excludeExtensions", McpSchema.StringArray( "File extensions or aliases to exclude." ) )
				.Prop( "nameFilter", McpSchema.String( "Case-insensitive substring or wildcard (*, ?) match on node name." ) )
				.Prop( "pathFilter", McpSchema.String( "Case-insensitive substring or wildcard (*, ?) match on server path." ) )
				.Prop( "categories", McpSchema.StringArray( "Whitelist of HackPDM file categories." ) )
				.Prop( "checkedOutOnly", McpSchema.Boolean( "Filter by checkout state (true = checked out only, false = available only)." ) )
				.Prop( "checkoutUser", McpSchema.String( "Filter by checkout user name substring." ) )
				.Prop( "includeDeleted", McpSchema.Boolean( "Include logically deleted entries (default true).", true ) )
				.Prop( "minSizeBytes", McpSchema.Integer( "Minimum file size in bytes.", 0 ) )
				.Prop( "maxSizeBytes", McpSchema.Integer( "Maximum file size in bytes.", 0 ) )
				.Prop( "propertyName", McpSchema.String( "Custom property or built-in attribute name to filter by (e.g. 'Material', 'Revision', 'Category', 'Extension', 'Folder')." ) )
				.Prop( "propertyOperator", McpSchema.Enum( "Comparison operator for propertyName (default 'contains').", "contains", "=", "!=", "starts_with", "ends_with", "exists", "not_exists", ">", ">=", "<", "<=" ) )
				.Prop( "propertyValue", McpSchema.String( "Target value for propertyName filter." ) )
				.Prop( "groupByProperty", McpSchema.String( "Property or built-in attribute to cluster files by when viewMode='ByProperty' (e.g. 'Material', 'Revision', 'Category', 'Extension', 'CheckoutUser', 'Folder', 'Status')." ) )
				.Prop( "includeDependencyLinks", McpSchema.Boolean( "Overlay file-to-file dependency links in FoldersAndFiles, ByProperty, or SearchFilter views (default false).", false ) )
				.Prop( "keepRoots", McpSchema.Boolean( "Keep selected root nodes even when attribute filters do not match them (default true; set false to strictly keep only matching file types).", true ) )
				.Prop( "bridgeFilteredEdges", McpSchema.Boolean( "Transitively bridge edges across hidden intermediate nodes so dependency lineage stays connected when filtering (default true).", true ) )
				.Prop( "includeProperties", McpSchema.Boolean( "Attach custom hp.version.property maps onto returned file nodes (auto-enabled when filtering/grouping by custom properties).", false ) )
				.Prop( "includeLayout", McpSchema.Boolean( "Compute and include HackPDM GraphLayout coordinates/layers for every node.", false ) )
				.Build(),
			async ( raw, ct ) =>
			{
				McpToolArgs args = new( raw );
				HackPDM.Core.Graph.GraphFilterCriteria criteria = HackPdmApiService.ParseGraphFilterCriteria( args );
				HackPDM.Infrastructure.Operations.GraphBuildRequest request = new()
				{
					EntryIds = args.GetIntList( "entryIds" ),
					DirectoryId = args.GetInt( "directoryId" ),
					FolderPath = args.GetString( "folderPath" ),
					RecursiveFolders = args.GetBool( "recursiveFolders", true ),
					Criteria = criteria,
					LoadProperties = args.GetBool( "includeProperties", false ),
				};
				return McpToolResult.Ok( await api.LoadDependencyGraphAsync(
					request,
					args.GetBool( "includeLayout", false ),
					ct ) );
			},
			new McpToolAnnotations( "Load Node / Dependency Graph", ReadOnlyHint: true, IdempotentHint: true, OpenWorldHint: true ) );
	}

	private static IReadOnlyList<string> ResolveSegments( McpToolArgs args )
	{
		if( args.GetStringList( "segments" ) is { Count: > 0 } segs )
			return segs;
		if( args.GetString( "path" ) is { Length: > 0 } path )
			return PwaPaths.Segments( path );
		throw new McpProtocolException( JsonRpcErrorCodes.InvalidParams, "Provide either 'path' or 'segments'." );
	}
}

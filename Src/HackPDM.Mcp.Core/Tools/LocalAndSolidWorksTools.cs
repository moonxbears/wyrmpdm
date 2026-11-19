using HackPDM.Mcp.Protocol;
using HackPDM.Mcp.Services;

namespace HackPDM.Mcp.Tools;

internal static class LocalAndSolidWorksTools
{
	public static void Register( McpServer server, HackPdmApiService api )
	{
		// ---- SolidWorks Document Inspection ----

		server.RegisterTool(
			"sw_inspect_file",
			"Inspects a local SolidWorks file (.sldprt, .sldasm, .slddrw) using HackPDM's cross-platform OLE SwFileReader (or SWDocMgr on Windows): reads direct references, resolves them to local vault paths, extracts all file-level and configuration custom properties, checksum, and checks embedded PNG preview.",
			McpSchema.Object()
				.Prop( "path", McpSchema.String( "Absolute local path or vault tree path to the SolidWorks document." ), required: true )
				.Prop( "resolveReferences", McpSchema.Boolean( "Resolve stored Windows references to local vault files via SwReferenceResolver (default true).", true ) )
				.Build(),
			async ( raw, ct ) =>
			{
				McpToolArgs args = new( raw );
				return McpToolResult.Ok( await api.InspectSolidWorksFileAsync(
					args.RequireString( "path" ),
					args.GetBool( "resolveReferences", true ),
					ct ) );
			},
			new McpToolAnnotations( "Inspect SolidWorks File", ReadOnlyHint: true, IdempotentHint: true ) );

		server.RegisterTool(
			"sw_scan_dependencies",
			"Recursively scans a local file or local folder via HackPDM's DependencyScanner, collecting all transitive SolidWorks references and reporting any broken, missing, or out-of-vault references.",
			McpSchema.Object()
				.Prop( "filePath", McpSchema.String( "Local path or vault tree path of a single file to scan." ) )
				.Prop( "folderPath", McpSchema.String( "Local folder path or vault tree path whose direct files will be scanned." ) )
				.Build(),
			async ( raw, _ ) =>
			{
				McpToolArgs args = new( raw );
				return McpToolResult.Ok( await api.ScanLocalDependenciesAsync(
					args.GetString( "filePath" ),
					args.GetString( "folderPath" ) ) );
			},
			new McpToolAnnotations( "Scan Local Dependencies", ReadOnlyHint: true, IdempotentHint: true ) );

		// ---- Local Vault File Operations ----

		server.RegisterTool(
			"local_list_files",
			"Lists local files and subdirectories in a working folder path (by vault treePath or absolute localPath), with sizes, read-only flags, timestamps, and optional SHA-1 checksums.",
			McpSchema.Object()
				.Prop( "treePath", McpSchema.String( "Vault tree path (e.g. 'root' or 'root\\\\Projects\\\\Parts'). Defaults to vault root." ) )
				.Prop( "localPath", McpSchema.String( "Explicit absolute local directory path (overrides treePath)." ) )
				.Prop( "recursive", McpSchema.Boolean( "Recurse into subdirectories (default false).", false ) )
				.Prop( "computeChecksums", McpSchema.Boolean( "Compute HackPDM SHA-1 checksums for each file (default false).", false ) )
				.Build(),
			( raw, _ ) =>
			{
				McpToolArgs args = new( raw );
				return Task.FromResult( McpToolResult.Ok( api.ListLocalFiles(
					args.GetString( "treePath" ),
					args.GetString( "localPath" ),
					args.GetBool( "recursive", false ),
					args.GetBool( "computeChecksums", false ) ) ) );
			},
			new McpToolAnnotations( "List Local Vault Files", ReadOnlyHint: true, IdempotentHint: true ) );

		server.RegisterTool(
			"local_read_file",
			"Reads a local file strictly inside the configured HackPDM working folder (PWA) as UTF-8 text or base64, along with size, read-only status, modified time, and optional SHA-1 checksum.",
			McpSchema.Object()
				.Prop( "path", McpSchema.String( "Absolute local path or vault tree path (e.g. 'root\\\\Projects\\\\spec.txt')." ), required: true )
				.Prop( "encoding", McpSchema.Enum( "Output encoding ('auto', 'utf8', or 'base64').", "auto", "utf8", "base64" ) )
				.Prop( "maxBytes", McpSchema.Integer( "Maximum bytes to return inline (default 262144).", 1024, 10 * 1024 * 1024, 262144 ) )
				.Prop( "includeChecksum", McpSchema.Boolean( "Compute and include SHA-1 checksum (default true).", true ) )
				.Build(),
			async ( raw, ct ) =>
			{
				McpToolArgs args = new( raw );
				return McpToolResult.Ok( await api.ReadLocalFileAsync(
					args.RequireString( "path" ),
					args.GetString( "encoding", "auto" )!,
					args.GetInt( "maxBytes", 262144 ) ?? 262144,
					args.GetBool( "includeChecksum", true ),
					ct ) );
			},
			new McpToolAnnotations( "Read Local File", ReadOnlyHint: true, IdempotentHint: true ) );

		server.RegisterTool(
			"local_write_file",
			"Creates or updates a file strictly inside the configured HackPDM local working folder (PWA). Respects read-only status unless forceWritable=true.",
			McpSchema.Object()
				.Prop( "path", McpSchema.String( "Vault tree path (e.g. 'root\\\\Projects\\\\notes.txt') or absolute path inside the PWA." ), required: true )
				.Prop( "content", McpSchema.String( "File content (UTF-8 text or base64 depending on 'encoding')." ), required: true )
				.Prop( "encoding", McpSchema.Enum( "Input encoding ('utf8' or 'base64'; default 'utf8').", "utf8", "base64" ) )
				.Prop( "forceWritable", McpSchema.Boolean( "Clear ReadOnly attribute before overwriting a read-only local copy (default false).", false ) )
				.Build(),
			async ( raw, ct ) =>
			{
				McpToolArgs args = new( raw );
				return McpToolResult.Ok( await api.WriteLocalFileAsync(
					args.RequireString( "path" ),
					args.GetString( "content", "" )!,
					args.GetString( "encoding", "utf8" )!,
					args.GetBool( "forceWritable", false ),
					ct ) );
			},
			new McpToolAnnotations( "Write Local Vault File", ReadOnlyHint: false ) );

		server.RegisterTool(
			"local_delete",
			"Deletes local file(s) and/or a local subdirectory strictly inside the configured HackPDM working folder (never modifies the Odoo server). Requires confirm=true.",
			McpSchema.Object()
				.Prop( "filePaths", McpSchema.StringArray( "Vault tree paths or absolute paths inside PWA of files to delete." ) )
				.Prop( "directoryPath", McpSchema.String( "Vault tree path or absolute path inside PWA of a subdirectory to delete recursively." ) )
				.Prop( "forceWritable", McpSchema.Boolean( "Clear ReadOnly attributes before deleting (default false).", false ) )
				.Prop( "confirm", McpSchema.Boolean( "Must be true to delete local files/folders." ), required: true )
				.Build(),
			( raw, _ ) =>
			{
				McpToolArgs args = new( raw );
				if( !args.GetBool( "confirm", false ) )
					return Task.FromResult( McpToolResult.Error( "local_delete aborted: pass confirm=true to proceed." ) );
				return Task.FromResult( McpToolResult.Ok( api.DeleteLocal(
					args.GetStringList( "filePaths" ),
					args.GetString( "directoryPath" ),
					args.GetBool( "forceWritable", false ) ) ) );
			},
			new McpToolAnnotations( "Delete Local Vault Files", ReadOnlyHint: false, DestructiveHint: true ) );

		server.RegisterTool(
			"local_checksum",
			"Computes HackPDM SHA-1 checksum(s) for one or more local files and optionally compares against an expected server checksum.",
			McpSchema.Object()
				.Prop( "paths", McpSchema.StringArray( "Local absolute paths or vault tree paths." ), required: true )
				.Prop( "expectedChecksum", McpSchema.String( "Optional SHA-1 hex string to compare each file against." ) )
				.Build(),
			( raw, _ ) =>
			{
				McpToolArgs args = new( raw );
				List<string> paths = args.GetStringList( "paths" ) ?? [];
				return Task.FromResult( McpToolResult.Ok( api.ComputeLocalChecksums( paths, args.GetString( "expectedChecksum" ) ) ) );
			},
			new McpToolAnnotations( "Compute File Checksum", ReadOnlyHint: true, IdempotentHint: true ) );

		// ---- Settings, AppConfig & Diagnostics ----

		server.RegisterTool(
			"settings_list",
			"Lists all key/value settings stored in HackPDM's settings.json (with sensitive keys redacted).",
			McpSchema.EmptyObject(),
			( _, _ ) => Task.FromResult( McpToolResult.Ok( api.ListSettings() ) ),
			new McpToolAnnotations( "List App Settings", ReadOnlyHint: true, IdempotentHint: true ) );

		server.RegisterTool(
			"settings_get",
			"Reads a single setting key from HackPDM's CoreSettings / settings.json.",
			McpSchema.Object()
				.Prop( "key", McpSchema.String( "Setting key name (case-sensitive, e.g. 'PWAPathAbsolute', 'OdooAddress', 'StatusAutoClose')." ), required: true )
				.Build(),
			( raw, _ ) =>
			{
				McpToolArgs args = new( raw );
				return Task.FromResult( McpToolResult.Ok( api.GetSetting( args.RequireString( "key" ) ) ) );
			},
			new McpToolAnnotations( "Get App Setting", ReadOnlyHint: true, IdempotentHint: true ) );

		server.RegisterTool(
			"settings_set",
			"Sets or removes a setting key in HackPDM's CoreSettings / settings.json.",
			McpSchema.Object()
				.Prop( "key", McpSchema.String( "Setting key name (case-sensitive)." ), required: true )
				.Prop( "value", McpSchema.Any( "JSON value to store (string, number, boolean, array, or object). Omit or pass null to remove." ) )
				.Prop( "remove", McpSchema.Boolean( "Remove the key from settings.json.", false ) )
				.Build(),
			( raw, _ ) =>
			{
				McpToolArgs args = new( raw );
				return Task.FromResult( McpToolResult.Ok( api.SetSetting(
					args.RequireString( "key" ),
					args.GetElement( "value" ),
					args.GetBool( "remove", false ) ) ) );
			},
			new McpToolAnnotations( "Set App Setting", ReadOnlyHint: false, IdempotentHint: true ) );

		server.RegisterTool(
			"app_config_get",
			"Reads HackPDM's AppConfig.json UI configuration (columns, window presets, workspace layout, theme colors/presets).",
			McpSchema.EmptyObject(),
			async ( _, ct ) => McpToolResult.Ok( await api.GetAppConfigAsync( ct ) ),
			new McpToolAnnotations( "Get AppConfig.json", ReadOnlyHint: true, IdempotentHint: true ) );

		server.RegisterTool(
			"app_config_save",
			"Validates and saves a JSON document to HackPDM's AppConfig.json.",
			McpSchema.Object()
				.Prop( "json", McpSchema.String( "Full JSON string for AppConfig.json (must be a valid JSON object)." ), required: true )
				.Build(),
			async ( raw, ct ) =>
			{
				McpToolArgs args = new( raw );
				return McpToolResult.Ok( await api.SaveAppConfigAsync( args.RequireString( "json" ), ct ) );
			},
			new McpToolAnnotations( "Save AppConfig.json", ReadOnlyHint: false ) );

		server.RegisterTool(
			"diagnostics_get",
			"Returns HackPDM's redacted diagnostic summary, rolling log file inventory, recent error count, and the tail of recent HackLogger lines.",
			McpSchema.Object()
				.Prop( "tailLines", McpSchema.Integer( "Number of most recent log lines to return (default 100).", 0, 2000, 100 ) )
				.Prop( "logLevel", McpSchema.Enum( "Optional level tag filter.", "TRC", "DBG", "INF", "WRN", "ERR", "CRT" ) )
				.Build(),
			( raw, _ ) =>
			{
				McpToolArgs args = new( raw );
				return Task.FromResult( McpToolResult.Ok( api.GetDiagnostics(
					args.GetInt( "tailLines", 100 ) ?? 100,
					args.GetString( "logLevel" ) ) ) );
			},
			new McpToolAnnotations( "Get Diagnostics & Logs", ReadOnlyHint: true, IdempotentHint: true ) );
	}
}

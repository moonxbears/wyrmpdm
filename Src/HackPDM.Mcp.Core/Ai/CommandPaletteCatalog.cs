using HackPDM.Mcp.Protocol;
using HackPDM.Mcp.Security;

namespace HackPDM.Mcp.Ai;

/// <summary>Execution kind for a curated Command Palette item.</summary>
public enum PaletteCommandKind
{
	/// <summary>Navigates to a HackPDM screen, settings section, or drawer.</summary>
	Navigate,
	/// <summary>Directly invokes a HackPDM MCP API tool (with optional inline parameter prompts).</summary>
	McpTool,
	/// <summary>Switches the active MCP execution mode / permission profile.</summary>
	PermissionMode,
	/// <summary>Opens the Ask AI tab prefilled with a helpful prompt.</summary>
	AiPrompt,
	/// <summary>Switches the Command Palette tab (e.g. Connect AI or MCP Tools).</summary>
	PaletteMode,
}

/// <summary>Inline input field requested before executing a parameterized palette command.</summary>
public sealed record PaletteParamPrompt(
	string Key,
	string Label,
	string Placeholder,
	string? DefaultValue = null,
	bool Required = true,
	bool IsJsonArrayOfStrings = false );

/// <summary>Single command entry searchable in HackPDM's GUI Command Palette.</summary>
public sealed record PaletteCommandItem(
	string Id,
	string Title,
	string Subtitle,
	string Category,
	string Badge,
	string Severity,
	PaletteCommandKind Kind,
	string? Target = null,
	string? SecondaryTarget = null,
	string? DefaultArgsJson = null,
	IReadOnlyList<PaletteParamPrompt>? Prompts = null,
	string? ShortcutHint = null,
	string? SearchKeywords = null );

/// <summary>
/// Curated HackPDM API &amp; GUI command registry plus fast fuzzy matching across both curated
/// commands and all registered MCP tools.
/// </summary>
public static class CommandPaletteCatalog
{
	public static IReadOnlyList<PaletteCommandItem> CuratedCommands { get; } =
	[
		// ---- AI & MCP Quick Jumps ----
		new( "ai.ask", "Ask HackPDM AI Assistant...", "Chat directly with your connected AI using live HackPDM MCP tools", "AI Assistant", "AI CHAT", "tip", PaletteCommandKind.PaletteMode, Target: "ai", ShortcutHint: "? / @", SearchKeywords: "chat assistant llm prompt claude gpt gemini ollama" ),
		new( "ai.connect", "Sign In / Connect AI Provider & Clients...", "Browser OAuth login (OpenRouter), OpenAI, Claude, Gemini, Ollama auto-detect, and 1-click client setup", "AI Assistant", "LOGIN", "action", PaletteCommandKind.PaletteMode, Target: "connect", SearchKeywords: "login oauth api key claude desktop cursor vscode windsurf ollama" ),
		new( "ai.tools", "Browse & Run Any MCP Tool (125 Tools)...", "Search every registered HackPDM MCP tool and invoke with JSON arguments", "AI Assistant", "MCP API", "info", PaletteCommandKind.PaletteMode, Target: "tools", ShortcutHint: "/", SearchKeywords: "api rpc tool raw json invoke" ),

		// ---- Odoo JSON-RPC ORM & PostgreSQL Queries ----
		new( "odoo.model_access", "Odoo ORM: Check Accessible Models & Permissions...", "Verify read/write/create/unlink permissions via JSON-RPC check_access_rights", "Odoo ORM & SQL", "ORM", "info", PaletteCommandKind.McpTool, Target: "odoo_check_model_access",
			Prompts: [ new( "modelFilter", "Model Filter (optional, e.g. hp. or mrp.)", "Leave empty for HackPDM models or enter filter like 'mrp.'", DefaultValue: "hp.", Required: false ) ],
			SearchKeywords: "odoo jsonrpc permissions models ir.model access rights" ),
		new( "odoo.discover_methods", "Odoo ORM: Discover Model API Methods...", "Inspect live fields, server actions, and callable ORM methods for any Odoo model", "Odoo ORM & SQL", "ORM", "tip", PaletteCommandKind.McpTool, Target: "odoo_discover_model_methods",
			Prompts: [ new( "model", "Odoo Model Technical Name", "e.g. hp.entry, mrp.bom, product.template", DefaultValue: "hp.entry", Required: true ) ],
			SearchKeywords: "odoo jsonrpc api methods fields_get" ),
		new( "odoo.call_method", "Odoo ORM: Call Model API Method (execute_kw)...", "Execute a permission-verified Odoo ORM or custom model method through JSON-RPC", "Odoo ORM & SQL", "RPC", "warning", PaletteCommandKind.McpTool, Target: "odoo_call_model_method",
			Prompts:
			[
				new( "model", "Odoo Model Name", "e.g. hp.entry or product.template", DefaultValue: "hp.entry", Required: true ),
				new( "method", "ORM / API Method Name", "e.g. search_count, read_group, fields_get, name_search", DefaultValue: "search_count", Required: true ),
			],
			SearchKeywords: "execute_kw rpc invoke orm api" ),
		new( "pg.status", "PostgreSQL: Inspect & Test Odoo DB Connection", "Check redacted PostgreSQL connection settings and test live DB connectivity", "Odoo ORM & SQL", "SQL", "info", PaletteCommandKind.McpTool, Target: "postgres_status", DefaultArgsJson: "{\"testConnection\":true}", SearchKeywords: "postgresql database test ping" ),
		new( "pg.tables", "PostgreSQL: List Odoo Database Tables...", "List PostgreSQL tables with row counts, sizes, and inferred Odoo model names", "Odoo ORM & SQL", "SQL", "info", PaletteCommandKind.McpTool, Target: "postgres_list_tables",
			Prompts: [ new( "nameFilter", "Table Name Filter (optional)", "e.g. hp_% or mrp_% or leave empty for all", DefaultValue: "hp_%", Required: false ) ],
			SearchKeywords: "postgresql schema tables" ),
		new( "pg.query", "PostgreSQL: Run Read-Only SQL Query...", "Run a SELECT / WITH / EXPLAIN query inside an engine-enforced READ ONLY transaction", "Odoo ORM & SQL", "SQL", "tip", PaletteCommandKind.McpTool, Target: "postgres_query",
			Prompts: [ new( "sql", "Read-Only SQL Query", "e.g. SELECT id, name FROM hp_entry LIMIT 20", Required: true ) ],
			SearchKeywords: "select sql query postgres database" ),

		// ---- Node Graph Views & Filters ----
		new( "graph.full_network", "Node Graph: Full Bidirectional Network (Parents & Children of Neighbors)", "Switch the live node graph to Full Network mode showing each parent's and child's parents & children", "Node Graph", "GRAPH", "tip", PaletteCommandKind.McpTool, Target: "ui_dependency_graph_control", DefaultArgsJson: "{\"viewMode\":\"Dependencies\",\"direction\":\"FullNetwork\",\"maxDepth\":2}", SearchKeywords: "dependency graph siblings co-parents full network recursive" ),
		new( "graph.sldprt_only", "Node Graph: Filter to SLDPRT Parts Only (Bridged Links)", "Filter the live node graph to show only .SLDPRT files while keeping dependency lineage bridged", "Node Graph", "FILTER", "info", PaletteCommandKind.McpTool, Target: "ui_dependency_graph_control", DefaultArgsJson: "{\"extensions\":[\"sldprt\"],\"bridgeFilteredEdges\":true,\"keepRoots\":false}", SearchKeywords: "solidworks parts filter sldprt" ),
		new( "graph.folders", "Node Graph: View Folder Structure", "Render the vault directory hierarchy as an interactive node graph", "Node Graph", "GRAPH", "info", PaletteCommandKind.McpTool, Target: "ui_dependency_graph_control", DefaultArgsJson: "{\"viewMode\":\"FolderStructure\"}", SearchKeywords: "folder tree hierarchy structure" ),
		new( "graph.folders_files", "Node Graph: View Folders & Files Structure", "Render folders and the files inside them as an interactive node graph", "Node Graph", "GRAPH", "info", PaletteCommandKind.McpTool, Target: "ui_dependency_graph_control", DefaultArgsJson: "{\"viewMode\":\"FoldersAndFiles\"}", SearchKeywords: "files folders directory structure" ),
		new( "graph.by_property", "Node Graph: Group Files by Property...", "Cluster vault files into property hub nodes (e.g. Material, Revision, Category, Extension, CheckoutUser)", "Node Graph", "GRAPH", "tip", PaletteCommandKind.McpTool, Target: "ui_dependency_graph_control",
			DefaultArgsJson: "{\"viewMode\":\"ByProperty\"}",
			Prompts: [ new( "groupByProperty", "Property or Attribute to Group By", "e.g. Material, Revision, Category, Extension, CheckoutUser, Folder", DefaultValue: "Material", Required: true ) ],
			SearchKeywords: "property cluster group material category extension" ),

		// ---- Vault, Odoo & PDM API Commands ----
		new( "api.status", "Inspect HackPDM & MCP Status", "Run client_status to inspect live GUI bridge, Odoo session, and local PWA state", "Vault & API", "READ", "info", PaletteCommandKind.McpTool, Target: "client_status", DefaultArgsJson: "{}", SearchKeywords: "bridge health session info" ),
		new( "api.test_conn", "Test Odoo Server Connection", "Probe live connectivity and authentication against the configured Odoo server", "Vault & API", "RPC", "info", PaletteCommandKind.McpTool, Target: "session_test_connection", DefaultArgsJson: "{}", SearchKeywords: "ping odoo network check" ),
		new( "api.reload_lookups", "Reload Odoo Reference Lookups", "Refresh cached users, categories, file types, properties, and ignore filters", "Vault & API", "RPC", "info", PaletteCommandKind.McpTool, Target: "session_reload_lookups", DefaultArgsJson: "{}", SearchKeywords: "cache metadata refresh" ),
		new( "api.search_vault", "Search Vault Entries...", "Search remote HackPDM entries in Odoo by filename pattern", "Vault & API", "RPC", "info", PaletteCommandKind.McpTool, Target: "search_entries",
			Prompts: [ new( "fileName", "Filename Pattern", "e.g. Bracket or %.SLDASM", Required: true ) ],
			SearchKeywords: "find query odoo entry" ),
		new( "api.commit_preview", "Preview Pending Commit Changes...", "Check new, modified, and broken SolidWorks dependencies before committing", "Vault & API", "SAFE", "success", PaletteCommandKind.McpTool, Target: "pdm_commit_preview",
			Prompts: [ new( "targetPath", "Local File or Folder Path (optional)", "Leave empty for whole PWA or enter folder/file path", DefaultValue: "", Required: false ) ],
			SearchKeywords: "checkin diff status broken references" ),
		new( "api.dir_tree", "Load Local PWA Directory Tree", "List local working folder directory structure without remote RPC", "Vault & API", "LOCAL", "info", PaletteCommandKind.McpTool, Target: "directory_get_tree", DefaultArgsJson: "{\"source\":\"local\"}", SearchKeywords: "folders tree pwa" ),

		// ---- SolidWorks & Local Files ----
		new( "sw.local_files", "List Local PWA Files", "List CAD and working files inside the configured local PWA directory", "SolidWorks & Files", "LOCAL", "info", PaletteCommandKind.McpTool, Target: "local_list_files", DefaultArgsJson: "{}", SearchKeywords: "pwa disk workspace" ),
		new( "sw.inspect", "Inspect SolidWorks File Properties & References...", "Read custom properties, configurations, and dependencies from any .SLDPRT/.SLDASM/.SLDDRW", "SolidWorks & Files", "CAD", "tip", PaletteCommandKind.McpTool, Target: "sw_inspect_file",
			Prompts: [ new( "filePath", "SolidWorks File Path", "Absolute or PWA-relative path to .SLDPRT / .SLDASM / .SLDDRW", Required: true ) ],
			SearchKeywords: "sldprt sldasm slddrw properties metadata" ),
		new( "sw.scan_deps", "Scan SolidWorks Dependencies...", "Recursively scan a SolidWorks assembly or folder for missing or external references", "SolidWorks & Files", "CAD", "tip", PaletteCommandKind.McpTool, Target: "sw_scan_dependencies",
			Prompts: [ new( "filePath", "Assembly or File Path", "Path to .SLDASM / .SLDDRW / .SLDPRT", Required: true ) ],
			SearchKeywords: "bom tree references broken external" ),
		new( "sw.import_pwa", "Import External CAD Files Into PWA & Repath...", "Copy/move external files + transitive SolidWorks dependencies into the PWA and rewrite stored paths", "SolidWorks & Files", "CRITICAL", "warning", PaletteCommandKind.McpTool, Target: "fs_import_to_pwa",
			Prompts:
			[
				new( "sourcePaths", "Source File/Folder Paths (comma-separated)", "e.g. /path/to/Assembly.SLDASM", Required: true, IsJsonArrayOfStrings: true ),
				new( "targetPwaFolder", "Target Folder Inside PWA", "e.g. Projects\\Drive_V2", Required: true ),
			],
			SearchKeywords: "external move copy repath dependencies" ),

		// ---- MCP Safety, Restore Points & Diagnostics ----
		new( "mcp.checkpoint", "Create Manual MCP Restore Point...", "Snapshot files and HackPDM configs before making edits", "MCP & Safety", "BACKUP", "success", PaletteCommandKind.McpTool, Target: "mcp_restore_point_create",
			Prompts:
			[
				new( "label", "Restore Point Label", "e.g. Before BOM Property Batch Edit", DefaultValue: "Manual Command Palette Checkpoint", Required: true ),
			],
			SearchKeywords: "undo snapshot rollback backup" ),
		new( "mcp.list_rp", "List MCP Restore Points", "View all stored automatic and manual restore points available for rollback", "MCP & Safety", "READ", "info", PaletteCommandKind.McpTool, Target: "mcp_restore_point_list", DefaultArgsJson: "{\"limit\":25}", SearchKeywords: "undo rollback history" ),
		new( "mcp.log", "Inspect Recent MCP Activity Log", "Show recent AI & API tool calls with timings, status, and linked restore points", "MCP & Safety", "READ", "info", PaletteCommandKind.McpTool, Target: "mcp_log_get", DefaultArgsJson: "{\"limit\":25}", SearchKeywords: "audit history calls" ),
		new( "mcp.diagnostics", "Run HackPDM Diagnostics", "Inspect local HackPDM environment, settings health, and recent logs", "MCP & Safety", "READ", "info", PaletteCommandKind.McpTool, Target: "diagnostics_get", DefaultArgsJson: "{\"logLines\":60}", SearchKeywords: "logs health debug error" ),
		new( "perm.review_critical", "Permission Mode: Review Critical (Default)", "Routine actions run automatically; commits, deletes & CAD writes pause for review", "MCP & Safety", "MODE", "action", PaletteCommandKind.PermissionMode, Target: "review-critical", SearchKeywords: "security mode preset" ),
		new( "perm.local_only", "Permission Mode: Local-Only (No RPC)", "Allow local files, SolidWorks & GUI while blocking all remote Odoo server RPCs", "MCP & Safety", "MODE", "tip", PaletteCommandKind.PermissionMode, Target: "local-only", SearchKeywords: "offline local computer security" ),
		new( "perm.read_only", "Permission Mode: Read-Only (Get Only)", "Allow inspection and queries only; block all edits, pushes, and deletes", "MCP & Safety", "MODE", "info", PaletteCommandKind.PermissionMode, Target: "read-only", SearchKeywords: "readonly safe audit" ),
		new( "perm.review", "Permission Mode: Review All Actions", "Require explicit approval before any write, download, or mutating UI action", "MCP & Safety", "MODE", "warning", PaletteCommandKind.PermissionMode, Target: "review", SearchKeywords: "strict confirm approve" ),
		new( "perm.auto", "Permission Mode: Auto (No Review)", "Execute all enabled capabilities immediately without review prompts", "MCP & Safety", "MODE", "critical", PaletteCommandKind.PermissionMode, Target: "auto", SearchKeywords: "autonomous fast" ),

		// ---- Theme, Guided & Dynamic Windows ----
		new( "theme.dark", "Theme: Switch to Dark Palette", "Apply HackPDM Dark palette immediately", "Theme & UI", "THEME", "info", PaletteCommandKind.McpTool, Target: "ui_theme_control", DefaultArgsJson: "{\"palette\":\"Dark\"}", SearchKeywords: "appearance color dark mode" ),
		new( "theme.navy", "Theme: Switch to NavyBlue Palette", "Apply HackPDM NavyBlue palette immediately", "Theme & UI", "THEME", "info", PaletteCommandKind.McpTool, Target: "ui_theme_control", DefaultArgsJson: "{\"palette\":\"NavyBlue\"}", SearchKeywords: "appearance color blue navy" ),
		new( "theme.light", "Theme: Switch to Light Palette", "Apply HackPDM Light palette immediately", "Theme & UI", "THEME", "info", PaletteCommandKind.McpTool, Target: "ui_theme_control", DefaultArgsJson: "{\"palette\":\"Light\"}", SearchKeywords: "appearance color light mode" ),
		new( "theme.reset", "Theme: Restore Factory Defaults", "Reset all theme, style, color, and graph overrides back to HackPDM factory defaults", "Theme & UI", "RESET", "warning", PaletteCommandKind.McpTool, Target: "ui_theme_control", DefaultArgsJson: "{\"resetToDefaults\":true,\"save\":true}", SearchKeywords: "default reset restore colors" ),
		new( "ui.clear_guides", "Clear Active Guided Overlays & Tours", "Dismiss any temporary guided highlights, animations, popups, or walkthroughs", "Theme & UI", "UI", "info", PaletteCommandKind.McpTool, Target: "ui_guide_clear", DefaultArgsJson: "{}", SearchKeywords: "spotlight highlight dismiss" ),
		new( "ui.close_windows", "Close All Dynamic Supplemental Windows", "Close every open AI-created dynamic supplemental window", "Theme & UI", "UI", "info", PaletteCommandKind.McpTool, Target: "ui_window_close", DefaultArgsJson: "{\"closeAll\":true}", SearchKeywords: "dynamic supplemental close" ),

		// ---- Navigation & Drawers ----
		new( "nav.files", "Go to Hack File Manager", "Open the vault directory tree, entry list, details, and CAD preview workspace", "Navigation", "PAGE", "info", PaletteCommandKind.Navigate, Target: "HackFileManager", ShortcutHint: "Ctrl+1", SearchKeywords: "vault browser explorer" ),
		new( "nav.graph", "Go to Dependency Graph", "Open the interactive parent/child SolidWorks dependency visualizer", "Navigation", "PAGE", "info", PaletteCommandKind.Navigate, Target: "DependencyGraph", ShortcutHint: "Ctrl+2", SearchKeywords: "bom tree links" ),
		new( "nav.profile", "Go to Profile Manager & Login", "Sign in to Odoo, test connections, and manage connection profiles", "Navigation", "PAGE", "info", PaletteCommandKind.Navigate, Target: "ProfileManager", ShortcutHint: "Ctrl+3", SearchKeywords: "account login server" ),
		new( "nav.config", "Go to Server & Folder Configurations", "Edit Odoo server settings and local PWA folder path", "Navigation", "PAGE", "info", PaletteCommandKind.Navigate, Target: "Configuration", ShortcutHint: "Ctrl+4", SearchKeywords: "pwa settings odoo" ),
		new( "nav.json", "Go to JSON Schema Editor", "Edit AppConfig.json and HackPDM configuration visually", "Navigation", "PAGE", "info", PaletteCommandKind.Navigate, Target: "JsonEditor", ShortcutHint: "Ctrl+5", SearchKeywords: "appconfig raw json" ),
		new( "nav.theme", "Go to Settings → Theme Editor", "Customize palettes, forms, colors, fonts, and Dependency Graph appearance", "Navigation", "SETTINGS", "info", PaletteCommandKind.Navigate, Target: "Settings", SecondaryTarget: "Theme", SearchKeywords: "appearance style" ),
		new( "nav.mcp", "Go to Settings → MCP Server Dashboard", "Manage MCP bridge, connection JSON, permissions, review queue, restore points, and activity log", "Navigation", "SETTINGS", "info", PaletteCommandKind.Navigate, Target: "Settings", SecondaryTarget: "Mcp", SearchKeywords: "bridge token permissions" ),
		new( "nav.search_drawer", "Toggle Search Vault Panel", "Show or hide the Search Vault side drawer", "Navigation", "DRAWER", "info", PaletteCommandKind.Navigate, Target: "toggle_search", ShortcutHint: "Ctrl+F", SearchKeywords: "find panel" ),
		new( "nav.status_drawer", "Toggle Status Log Panel", "Show or hide the bottom Status Log drawer", "Navigation", "DRAWER", "info", PaletteCommandKind.Navigate, Target: "toggle_status", ShortcutHint: "Alt+S", SearchKeywords: "output log console" ),

		// ---- One-Click AI Workflows ----
		new( "ai.wf.health", "AI: Build HackPDM Health & Status Dashboard Window", "Ask the AI to gather diagnostics, permissions & session status and open a dynamic summary window", "AI Workflows", "AI REPORT", "tip", PaletteCommandKind.AiPrompt, Target: "Inspect HackPDM client status, MCP permissions, recent activity log, and diagnostics, then display a clean structured dashboard using ui_window_create with KPI cards, callouts, and key-value metadata.", SearchKeywords: "report summary dashboard window" ),
		new( "ai.wf.pwa", "AI: Analyze Local PWA Files in Supplemental Window", "Ask the AI to inspect local PWA files and present a searchable summary table", "AI Workflows", "AI REPORT", "tip", PaletteCommandKind.AiPrompt, Target: "List the files in my local PWA folder (using local_list_files) and present them in a clean supplemental window via ui_window_create with KPI counts and a searchable table.", SearchKeywords: "workspace cad bom audit" ),
	];

	/// <summary>Filters and ranks curated palette commands using fuzzy scoring.</summary>
	public static IReadOnlyList<PaletteCommandItem> SearchCommands( string? query )
	{
		string q = ( query ?? string.Empty ).Trim();
		if( q.StartsWith( '>' ) )
			q = q[ 1.. ].Trim();
		if( string.IsNullOrEmpty( q ) )
			return CuratedCommands;

		return CuratedCommands
			.Select( item => ( Item: item, Score: ComputeFuzzyScore( q, item.Title, item.Category, item.Subtitle, item.Target, item.SearchKeywords ) ) )
			.Where( x => x.Score > 0 )
			.OrderByDescending( x => x.Score )
			.ThenBy( x => x.Item.Title, StringComparer.OrdinalIgnoreCase )
			.Select( x => x.Item )
			.ToList();
	}

	/// <summary>Filters and ranks all registered MCP tools using fuzzy scoring.</summary>
	public static IReadOnlyList<(McpToolDefinition Tool, McpToolClassification Classification)> SearchMcpTools(
		IReadOnlyList<McpToolDefinition> tools,
		string? query )
	{
		string q = ( query ?? string.Empty ).Trim();
		if( q.StartsWith( '/' ) )
			q = q[ 1.. ].Trim();

		IEnumerable<(McpToolDefinition Tool, McpToolClassification Class, int Score)> scored = tools.Select( t =>
		{
			McpToolClassification cls = McpToolCatalog.Classify( t.Name );
			int score = string.IsNullOrEmpty( q )
				? 1
				: ComputeFuzzyScore( q, t.Name, t.Annotations?.Title, cls.Category.ToString(), t.Description );
			return ( t, cls, score );
		} );

		if( !string.IsNullOrEmpty( q ) )
			scored = scored.Where( x => x.Score > 0 ).OrderByDescending( x => x.Score ).ThenBy( x => x.Tool.Name, StringComparer.OrdinalIgnoreCase );
		else
			scored = scored.OrderBy( x => x.Tool.Name, StringComparer.OrdinalIgnoreCase );

		return scored.Select( x => ( x.Tool, x.Class ) ).ToList();
	}

	internal static int ComputeFuzzyScore( string query, params string?[] fields )
	{
		string[] tokens = query.Split( ' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries );
		if( tokens.Length == 0 )
			return 1;

		int total = 0;
		for( int t = 0; t < tokens.Length; t++ )
		{
			string tok = tokens[ t ];
			int bestField = 0;
			for( int f = 0; f < fields.Length; f++ )
			{
				string? hay = fields[ f ];
				if( string.IsNullOrWhiteSpace( hay ) )
					continue;

				int weight = f == 0 ? 4 : ( f == 1 ? 2 : 1 );
				int idx = hay.IndexOf( tok, StringComparison.OrdinalIgnoreCase );
				if( idx >= 0 )
				{
					int s = idx == 0 ? 100 : ( idx > 0 && !char.IsLetterOrDigit( hay[ idx - 1 ] ) ? 75 : 45 );
					bestField = Math.Max( bestField, s * weight );
				}
				else if( IsSubsequence( tok, hay ) )
				{
					bestField = Math.Max( bestField, 18 * weight );
				}
			}

			if( bestField == 0 )
				return 0; // every whitespace token must match something
			total += bestField;
		}
		return total;
	}

	private static bool IsSubsequence( string needle, string haystack )
	{
		int ni = 0;
		for( int hi = 0; hi < haystack.Length && ni < needle.Length; hi++ )
		{
			if( char.ToLowerInvariant( haystack[ hi ] ) == char.ToLowerInvariant( needle[ ni ] ) )
				ni++;
		}
		return ni == needle.Length;
	}
}

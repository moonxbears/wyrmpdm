using HackPDM.Mcp.Bridge;
using HackPDM.Mcp.Protocol;
using HackPDM.Mcp.Services;

namespace HackPDM.Mcp.Tools;

internal static class LiveUiTools
{
	public static void Register( McpServer server, HackPdmBridgeCoordinator bridge, HackPdmApiService api )
	{
		McpToolResult RequireBridgeError( string toolName ) => McpToolResult.Error(
			$"Tool '{toolName}' requires a live HackPDM desktop GUI client, but the MCP server is currently running in headless mode. " +
			"Start HackPDM (or call 'client_launch' / 'client_attach') to enable live UI inspection and control." );

		server.RegisterTool(
			"ui_get_state",
			"Returns a comprehensive live snapshot of the running HackPDM desktop UI: open windows, active navigation page and back stack, Search & Status drawer states, open MessageBox dialogs, and live ViewModel state for the active page (FileManager, DependencyGraph, ProfileManager, Search, Status, Theme, JsonEditor).",
			McpSchema.EmptyObject(),
			async ( _, ct ) => bridge.LocalUiBridge is { } ui
				? McpToolResult.Ok( await ui.GetUiStateAsync( ct ) )
				: RequireBridgeError( "ui_get_state" ),
			new McpToolAnnotations( "Get Live UI State", ReadOnlyHint: true, IdempotentHint: true ) );

		server.RegisterTool(
			"ui_list_windows",
			"Lists all currently open HackPDM desktop windows (MainWindow, detached Status/Search windows, File Types window, PiP preview window, MessageBox dialogs) with window IDs, titles, sizes, and root page types.",
			McpSchema.EmptyObject(),
			async ( _, ct ) => bridge.LocalUiBridge is { } ui
				? McpToolResult.Ok( await ui.ListWindowsAsync( ct ) )
				: RequireBridgeError( "ui_list_windows" ),
			new McpToolAnnotations( "List Open Windows", ReadOnlyHint: true, IdempotentHint: true ) );

		server.RegisterTool(
			"ui_visual_tree",
			"Walks and returns the live WinUI/Uno XAML visual tree of a window or named control subtree, including element type, Name, AutomationId, text/content/header, visibility, enabled state, value/checked/selected state, bounding rect, DataContext type, and stable visual path.",
			McpSchema.Object()
				.Prop( "windowId", McpSchema.String( "Window ID from ui_list_windows (defaults to MainWindow)." ) )
				.Prop( "rootName", McpSchema.String( "Optional x:Name of a subtree root control to start from." ) )
				.Prop( "maxDepth", McpSchema.Integer( "Maximum traversal depth (default 10).", 1, 64, 10 ) )
				.Prop( "includeInvisible", McpSchema.Boolean( "Include Collapsed/invisible elements (default false).", false ) )
				.Prop( "interactiveOnly", McpSchema.Boolean( "Include only interactive/named/text-bearing controls while still traversing containers (default false).", false ) )
				.Build(),
			async ( raw, ct ) =>
			{
				if( bridge.LocalUiBridge is not { } ui )
					return RequireBridgeError( "ui_visual_tree" );
				McpToolArgs args = new( raw );
				return McpToolResult.Ok( await ui.GetVisualTreeAsync(
					args.GetString( "windowId" ),
					args.GetString( "rootName" ),
					args.GetInt( "maxDepth", 10 ) ?? 10,
					args.GetBool( "includeInvisible", false ),
					args.GetBool( "interactiveOnly", false ),
					ct ) );
			},
			new McpToolAnnotations( "Inspect XAML Visual Tree", ReadOnlyHint: true, IdempotentHint: true ) );

		server.RegisterTool(
			"ui_find_controls",
			"Searches the live HackPDM visual tree for controls matching any combination of x:Name, control type, visible text/content/header, or AutomationId.",
			McpSchema.Object()
				.Prop( "windowId", McpSchema.String( "Window ID from ui_list_windows (defaults to MainWindow)." ) )
				.Prop( "name", McpSchema.String( "Substring or exact match against FrameworkElement.Name." ) )
				.Prop( "type", McpSchema.String( "Substring or exact match against CLR type name (e.g. 'Button', 'TextBox', 'TreeView', 'TableView')." ) )
				.Prop( "text", McpSchema.String( "Substring match against displayed Text/Content/Header/Placeholder." ) )
				.Prop( "automationId", McpSchema.String( "Match against AutomationProperties.AutomationId." ) )
				.Prop( "visibleOnly", McpSchema.Boolean( "Only return visible controls (default true).", true ) )
				.Prop( "interactiveOnly", McpSchema.Boolean( "Only return interactive controls (default false).", false ) )
				.Prop( "maxResults", McpSchema.Integer( "Maximum matching controls to return (default 50).", 1, 500, 50 ) )
				.Build(),
			async ( raw, ct ) =>
			{
				if( bridge.LocalUiBridge is not { } ui )
					return RequireBridgeError( "ui_find_controls" );
				McpToolArgs args = new( raw );
				return McpToolResult.Ok( await ui.FindControlsAsync(
					args.GetString( "windowId" ),
					args.GetString( "name" ),
					args.GetString( "type" ),
					args.GetString( "text" ),
					args.GetString( "automationId" ),
					args.GetBool( "visibleOnly", true ),
					args.GetBool( "interactiveOnly", false ),
					args.GetInt( "maxResults", 50 ) ?? 50,
					ct ) );
			},
			new McpToolAnnotations( "Find UI Controls", ReadOnlyHint: true, IdempotentHint: true ) );

		server.RegisterTool(
			"ui_get_control",
			"Inspects a single live UI control in depth (properties, value, items/options for lists/combos/tabs/trees, bounds, command info, parent chain, and direct children).",
			McpSchema.Object()
				.Prop( "windowId", McpSchema.String( "Window ID from ui_list_windows (defaults to MainWindow)." ) )
				.Prop( "name", McpSchema.String( "Control x:Name." ) )
				.Prop( "automationId", McpSchema.String( "Control AutomationId." ) )
				.Prop( "path", McpSchema.String( "Visual tree index path returned by ui_visual_tree / ui_find_controls (e.g. '0/2/1/0')." ) )
				.Build(),
			async ( raw, ct ) =>
			{
				if( bridge.LocalUiBridge is not { } ui )
					return RequireBridgeError( "ui_get_control" );
				McpToolArgs args = new( raw );
				return McpToolResult.Ok( await ui.GetControlAsync(
					args.GetString( "windowId" ),
					args.GetString( "name" ),
					args.GetString( "automationId" ),
					args.GetString( "path" ),
					ct ) );
			},
			new McpToolAnnotations( "Inspect UI Control", ReadOnlyHint: true, IdempotentHint: true ) );

		server.RegisterTool(
			"ui_click_control",
			"Clicks or invokes an interactive control in the live HackPDM UI (Button, AppBarButton, HyperlinkButton, ToggleButton, CheckBox, RadioButton, ToggleSwitch, NavigationViewItem, TabViewItem, SelectorBarItem, ListViewItem, TreeViewItem, MenuFlyoutItem).",
			McpSchema.Object()
				.Prop( "windowId", McpSchema.String( "Window ID from ui_list_windows (defaults to MainWindow)." ) )
				.Prop( "name", McpSchema.String( "Control x:Name." ) )
				.Prop( "automationId", McpSchema.String( "Control AutomationId." ) )
				.Prop( "path", McpSchema.String( "Visual tree path from ui_visual_tree / ui_find_controls." ) )
				.Prop( "text", McpSchema.String( "Visible label/text of an interactive control to click when name/path is unknown." ) )
				.Build(),
			async ( raw, ct ) =>
			{
				if( bridge.LocalUiBridge is not { } ui )
					return RequireBridgeError( "ui_click_control" );
				McpToolArgs args = new( raw );
				return McpToolResult.Ok( await ui.ClickControlAsync(
					args.GetString( "windowId" ),
					args.GetString( "name" ),
					args.GetString( "automationId" ),
					args.GetString( "path" ),
					args.GetString( "text" ),
					ct ) );
			},
			new McpToolAnnotations( "Click UI Control", ReadOnlyHint: false ) );

		server.RegisterTool(
			"ui_set_control_value",
			"Sets text, numeric value, toggle/checked state, or selected index/item on a live HackPDM UI control (TextBox, PasswordBox, AutoSuggestBox, NumberBox, Slider, CheckBox, ToggleButton, ToggleSwitch, RadioButton, ComboBox, SelectorBar, TabView, ListView, TableView).",
			McpSchema.Object()
				.Prop( "windowId", McpSchema.String( "Window ID from ui_list_windows (defaults to MainWindow)." ) )
				.Prop( "name", McpSchema.String( "Control x:Name." ) )
				.Prop( "automationId", McpSchema.String( "Control AutomationId." ) )
				.Prop( "path", McpSchema.String( "Visual tree path from ui_visual_tree / ui_find_controls." ) )
				.Prop( "value", McpSchema.Any( "Value to set (string for TextBox/PasswordBox/AutoSuggestBox/ComboBox match, number for Slider/NumberBox)." ) )
				.Prop( "selectedIndex", McpSchema.Integer( "Zero-based SelectedIndex for ComboBox, ListView, TabView, SelectorBar, or TableView.", -1 ) )
				.Prop( "isChecked", McpSchema.Boolean( "Checked / IsOn state for CheckBox, ToggleButton, RadioButton, or ToggleSwitch." ) )
				.Build(),
			async ( raw, ct ) =>
			{
				if( bridge.LocalUiBridge is not { } ui )
					return RequireBridgeError( "ui_set_control_value" );
				McpToolArgs args = new( raw );
				return McpToolResult.Ok( await ui.SetControlValueAsync(
					args.GetString( "windowId" ),
					args.GetString( "name" ),
					args.GetString( "automationId" ),
					args.GetString( "path" ),
					args.GetElement( "value" ),
					args.GetInt( "selectedIndex" ),
					args.GetNullableBool( "isChecked" ),
					ct ) );
			},
			new McpToolAnnotations( "Set UI Control Value", ReadOnlyHint: false ) );

		server.RegisterTool(
			"ui_invoke_command",
			"Lists or invokes any MVVM ICommand / IAsyncRelayCommand on the active page's ViewModel or a named ViewModel target ('active', 'fileManager', 'profileManager', 'search', 'status', 'theme', 'jsonEditor', 'odooSettings', 'hackSettings').",
			McpSchema.Object()
				.Prop( "target", McpSchema.Enum( "Target ViewModel (default 'active').", "active", "fileManager", "profileManager", "search", "status", "theme", "jsonEditor", "odooSettings", "hackSettings" ) )
				.Prop( "commandName", McpSchema.String( "Command property name (e.g. 'Refresh', 'GetLatestSelectedCommand', 'LoginCommand', 'SearchCommand', 'GroupByCommand')." ) )
				.Prop( "parameter", McpSchema.Any( "Optional command parameter." ) )
				.Prop( "listCommands", McpSchema.Boolean( "When true, returns all available commands and their CanExecute status without invoking anything.", false ) )
				.Build(),
			async ( raw, ct ) =>
			{
				if( bridge.LocalUiBridge is not { } ui )
					return RequireBridgeError( "ui_invoke_command" );
				McpToolArgs args = new( raw );
				return McpToolResult.Ok( await ui.InvokeCommandAsync(
					args.GetString( "target", "active" ),
					args.GetString( "commandName" ),
					args.GetElement( "parameter" ),
					args.GetBool( "listCommands", false ),
					ct ) );
			},
			new McpToolAnnotations( "Invoke ViewModel Command", ReadOnlyHint: false ) );

		server.RegisterTool(
			"ui_navigate",
			"Navigates the live HackPDM desktop window to a page (HackFileManager, DependencyGraph, ProfileManager, Configuration, JsonEditor, Settings), navigates back, selects a Settings section (Theme/Status/Mcp), opens File Types window, or controls the Search and Status drawers (show/hide/toggle/dock/detach/minimal/full/swapSide).",
			McpSchema.Object()
				.Prop( "page", McpSchema.Enum( "Destination page in the main NavigationView.", "HackFileManager", "DependencyGraph", "ProfileManager", "Configuration", "JsonEditor", "Settings" ) )
				.Prop( "goBack", McpSchema.Boolean( "Navigate back in the main content frame.", false ) )
				.Prop( "statusAction", McpSchema.Enum( "Action for the Status Log panel.", "show", "hide", "toggle", "dock", "detach", "minimal", "full" ) )
				.Prop( "searchAction", McpSchema.Enum( "Action for the Search Vault panel.", "show", "hide", "toggle", "dock", "detach", "minimal", "full", "swapSide" ) )
				.Prop( "openFileTypes", McpSchema.Boolean( "Open the File Types & Ignore Filters window.", false ) )
				.Prop( "settingsSection", McpSchema.Enum( "When on Settings page, switch to 'Theme', 'Status', or 'Mcp' section.", "Theme", "Status", "Mcp" ) )
				.Prop( "commandPalette", McpSchema.Enum( "Open, switch tab in, toggle, or close the GUI Command Palette & AI Assistant overlay.", "open", "commands", "tools", "ai", "connect", "toggle", "close" ) )
				.Prop( "paletteQuery", McpSchema.String( "Optional initial search filter when opening the Command Palette." ) )
				.Build(),
			async ( raw, ct ) =>
			{
				if( bridge.LocalUiBridge is not { } ui )
					return RequireBridgeError( "ui_navigate" );
				McpToolArgs args = new( raw );
				object navRes = await ui.NavigateAsync(
					args.GetString( "page" ),
					args.GetBool( "goBack", false ),
					args.GetString( "statusAction" ),
					args.GetString( "searchAction" ),
					args.GetBool( "openFileTypes", false ),
					args.GetString( "settingsSection" ),
					ct );
				if( args.GetString( "commandPalette" ) is { Length: > 0 } cpAction )
				{
					object cpRes = await ui.CommandPaletteAsync( cpAction, args.GetString( "paletteQuery" ), ct );
					return McpToolResult.Ok( new { navigation = navRes, commandPalette = cpRes } );
				}
				return McpToolResult.Ok( navRes );
			},
			new McpToolAnnotations( "Navigate UI & Drawers", ReadOnlyHint: false ) );

		server.RegisterTool(
			"ui_file_manager_select_folder",
			"Selects (and optionally expands/collapses) a folder in the live HackFileManagerView directory tree by treePath or directoryId, optionally waiting for its entries to finish loading, and returns the live folder entries.",
			McpSchema.Object()
				.Prop( "treePath", McpSchema.String( "Folder tree path such as 'root' or 'root\\\\Projects\\\\Parts'." ) )
				.Prop( "directoryId", McpSchema.Integer( "Odoo hp.directory ID to locate in the tree." ) )
				.Prop( "expand", McpSchema.Boolean( "Explicitly expand (true) or collapse (false) the target node." ) )
				.Prop( "waitForEntries", McpSchema.Boolean( "Wait until the folder's entries finish loading in the UI (default true).", true ) )
				.Prop( "timeoutMs", McpSchema.Integer( "Wait timeout in milliseconds (default 30000).", 1000, 120000, 30000 ) )
				.Build(),
			async ( raw, ct ) =>
			{
				if( bridge.LocalUiBridge is not { } ui )
					return RequireBridgeError( "ui_file_manager_select_folder" );
				McpToolArgs args = new( raw );
				return McpToolResult.Ok( await ui.FileManagerSelectFolderAsync(
					args.GetString( "treePath" ),
					args.GetInt( "directoryId" ),
					args.GetNullableBool( "expand" ),
					args.GetBool( "waitForEntries", true ),
					args.GetInt( "timeoutMs", 30000 ) ?? 30000,
					ct ) );
			},
			new McpToolAnnotations( "Select File Manager Folder", ReadOnlyHint: false ) );

		server.RegisterTool(
			"ui_file_manager_select_entries",
			"Selects one or more rows in the live HackFileManagerView entry table by filename or entry ID (or clears selection), waits for details/preview if requested, and returns the selected rows and active detail tab rows.",
			McpSchema.Object()
				.Prop( "names", McpSchema.StringArray( "Entry filenames in the currently listed folder to select." ) )
				.Prop( "entryIds", McpSchema.IntegerArray( "Odoo hp.entry IDs in the currently listed folder to select." ) )
				.Prop( "clear", McpSchema.Boolean( "Clear entry selection.", false ) )
				.Prop( "waitForDetails", McpSchema.Boolean( "Wait for the detail tab and preview load to complete (default true).", true ) )
				.Prop( "timeoutMs", McpSchema.Integer( "Wait timeout in milliseconds (default 15000).", 1000, 120000, 15000 ) )
				.Build(),
			async ( raw, ct ) =>
			{
				if( bridge.LocalUiBridge is not { } ui )
					return RequireBridgeError( "ui_file_manager_select_entries" );
				McpToolArgs args = new( raw );
				return McpToolResult.Ok( await ui.FileManagerSelectEntriesAsync(
					args.GetStringList( "names" ),
					args.GetIntList( "entryIds" ),
					args.GetBool( "clear", false ),
					args.GetBool( "waitForDetails", true ),
					args.GetInt( "timeoutMs", 15000 ) ?? 15000,
					ct ) );
			},
			new McpToolAnnotations( "Select File Manager Entries", ReadOnlyHint: false ) );

		server.RegisterTool(
			"ui_file_manager_control",
			"Controls live HackFileManagerView UI features: top tab (Entries/Changes), detail tab (HistoryTab/ParentTab/ChildTab/PropertiesTab/InfoTab), folderFilter text, showInactive, groupingEnabled, groupBy field, sortBy + descending, workspace layout preset (Balanced/RightInspector/RightSidebar/FocusEntries), pane visibility, expandAll/collapseAll, refreshFromServer, or togglePip.",
			McpSchema.Object()
				.Prop( "topTab", McpSchema.Enum( "Switch top tab.", "Entries", "Changes" ) )
				.Prop( "detailTab", McpSchema.Enum( "Switch lower detail tab.", "HistoryTab", "ParentTab", "ChildTab", "PropertiesTab", "InfoTab" ) )
				.Prop( "folderFilter", McpSchema.String( "Set the folder tree filter box text (empty string clears filter)." ) )
				.Prop( "showInactive", McpSchema.Boolean( "Toggle Show Inactive (deleted) entries." ) )
				.Prop( "groupingEnabled", McpSchema.Boolean( "Enable or disable entry list grouping." ) )
				.Prop( "groupBy", McpSchema.String( "EntryRow field name to group by (e.g. 'Status', 'Type', 'Category', 'Checkout')." ) )
				.Prop( "sortBy", McpSchema.String( "EntryRow field name to sort by (e.g. 'Name', 'Size', 'Status', 'RemoteDate', 'LocalDate')." ) )
				.Prop( "descending", McpSchema.Boolean( "Sort descending when sortBy is specified.", false ) )
				.Prop( "layoutPreset", McpSchema.Enum( "Apply a File Manager workspace layout preset.", "Balanced", "RightInspector", "RightSidebar", "FocusEntries" ) )
				.Prop( "showFoldersPane", McpSchema.Boolean( "Show or hide the Folders pane." ) )
				.Prop( "showDetailsPane", McpSchema.Boolean( "Show or hide the Details pane." ) )
				.Prop( "showPreviewPane", McpSchema.Boolean( "Show or hide the Preview pane." ) )
				.Prop( "expandAll", McpSchema.Boolean( "Expand all folder tree nodes.", false ) )
				.Prop( "collapseAll", McpSchema.Boolean( "Collapse folder tree nodes.", false ) )
				.Prop( "refresh", McpSchema.Boolean( "Trigger RefreshFromServer() on the File Manager.", false ) )
				.Prop( "togglePip", McpSchema.Boolean( "Toggle the Picture-in-Picture preview window.", false ) )
				.Build(),
			async ( raw, ct ) => bridge.LocalUiBridge is { } ui
				? McpToolResult.Ok( await ui.FileManagerControlAsync( raw, ct ) )
				: RequireBridgeError( "ui_file_manager_control" ),
			new McpToolAnnotations( "Control File Manager UI", ReadOnlyHint: false ) );

		server.RegisterTool(
			"ui_dependency_graph_control",
			"Controls every aspect of the live DependencyGraphView: switch viewMode (Dependencies, FolderStructure, FoldersAndFiles, ByProperty, SearchFilter), switch direction (Children, Parents, Both, or recursive FullNetwork showing each parent's and child's parents & children), set depth loading (maxDepth, maxChildDepth, maxParentDepth), set per-node branch limits (maxChildBranches, maxParentBranches, maxNodes), filter by file types/extensions (e.g. ['sldprt'] only, ['sldasm']), wildcard name/path/category/checkout/size/custom property, toggle edge bridging & root preservation, expand any node's neighbors live, open folders/entries, zoom/pan/fit, or inspect details.",
			McpSchema.Object()
				.Prop( "viewMode", McpSchema.Enum( "Node graph construction mode.", "Dependencies", "FolderStructure", "FoldersAndFiles", "ByProperty", "SearchFilter" ) )
				.Prop( "direction", McpSchema.Enum( "Dependency traversal direction ('FullNetwork' recursively shows each parent's and child's parents & children).", "Children", "Parents", "Both", "FullNetwork" ) )
				.Prop( "maxDepth", McpSchema.Integer( "Overall maximum depth/hops to load (0 = unlimited).", 0, 20 ) )
				.Prop( "maxChildDepth", McpSchema.Integer( "Maximum offspring/child depth (null clears to inherit maxDepth; 0 = no offspring).", 0, 20 ) )
				.Prop( "maxParentDepth", McpSchema.Integer( "Maximum parent depth (null clears to inherit maxDepth; 0 = no parents).", 0, 20 ) )
				.Prop( "maxChildBranches", McpSchema.Integer( "Maximum offspring/child branches per node (null = unlimited; 0 = none).", 0, 1000 ) )
				.Prop( "maxParentBranches", McpSchema.Integer( "Maximum parent branches per node (null = unlimited; 0 = none).", 0, 1000 ) )
				.Prop( "maxNodes", McpSchema.Integer( "Maximum total nodes displayed (0 = unlimited; default 250).", 0, 5000 ) )
				.Prop( "folderPath", McpSchema.String( "Folder tree path or folder name to open/scope in DependencyGraphView." ) )
				.Prop( "directoryId", McpSchema.Integer( "Optional Odoo hp.directory ID to scope Folder/Property/Search modes.", 1 ) )
				.Prop( "entryIds", McpSchema.IntegerArray( "Direct hp.entry IDs to draw the graph around (even across folders)." ) )
				.Prop( "selectEntries", McpSchema.StringArray( "Filenames in the selected folder to draw the graph around." ) )
				.Prop( "selectAll", McpSchema.Boolean( "Tick or untick Select All in the folder file list." ) )
				.Prop( "extensions", McpSchema.StringArray( "Whitelist of file extensions or aliases (e.g. [\"sldprt\"] for parts only, [\"sldasm\", \"slddrw\"], [\"cad\"], or null/[] to clear)." ) )
				.Prop( "fileTypes", McpSchema.StringArray( "Alias for 'extensions'." ) )
				.Prop( "excludeExtensions", McpSchema.StringArray( "Extensions or aliases to exclude." ) )
				.Prop( "nameFilter", McpSchema.String( "Substring or wildcard (*, ?) node name filter (empty string or null clears)." ) )
				.Prop( "pathFilter", McpSchema.String( "Substring or wildcard (*, ?) path filter (empty string or null clears)." ) )
				.Prop( "categories", McpSchema.StringArray( "Whitelist of HackPDM categories." ) )
				.Prop( "checkedOutOnly", McpSchema.Boolean( "Filter by checkout state (true = checked out only, false = available only, null clears)." ) )
				.Prop( "checkoutUser", McpSchema.String( "Filter by checkout user substring." ) )
				.Prop( "includeDeleted", McpSchema.Boolean( "Include logically deleted nodes." ) )
				.Prop( "minSizeBytes", McpSchema.Integer( "Minimum file size in bytes.", 0 ) )
				.Prop( "maxSizeBytes", McpSchema.Integer( "Maximum file size in bytes.", 0 ) )
				.Prop( "propertyName", McpSchema.String( "Custom property or built-in attribute name to filter by (e.g. 'Material', 'Revision', 'Category')." ) )
				.Prop( "propertyOperator", McpSchema.Enum( "Operator for propertyName.", "contains", "=", "!=", "starts_with", "ends_with", "exists", "not_exists", ">", ">=", "<", "<=" ) )
				.Prop( "propertyValue", McpSchema.String( "Target value for propertyName filter." ) )
				.Prop( "groupByProperty", McpSchema.String( "Property or built-in attribute to group by when viewMode='ByProperty' (e.g. 'Material', 'Category', 'Extension', 'CheckoutUser', 'Folder')." ) )
				.Prop( "includeDependencyLinks", McpSchema.Boolean( "Overlay file-to-file dependency links in Folder, Property, or Search views." ) )
				.Prop( "keepRoots", McpSchema.Boolean( "Keep selected root nodes even when filters exclude them." ) )
				.Prop( "bridgeFilteredEdges", McpSchema.Boolean( "Transitively bridge dependency links across filtered-out intermediate nodes." ) )
				.Prop( "expandNodeId", McpSchema.Integer( "Fetch and merge the parents & children of a specific file node ID live into the current graph.", 1 ) )
				.Prop( "expandDirection", McpSchema.Enum( "Direction when using expandNodeId (default 'Both').", "Children", "Parents", "Both", "FullNetwork" ) )
				.Prop( "resetFilters", McpSchema.Boolean( "Reset all depth, branch limits, and filter criteria back to defaults before applying other arguments.", false ) )
				.Prop( "zoomBy", McpSchema.Number( "Relative zoom multiplier (e.g. 1.25 or 0.8)." ) )
				.Prop( "panX", McpSchema.Number( "Horizontal pan delta in pixels." ) )
				.Prop( "panY", McpSchema.Number( "Vertical pan delta in pixels." ) )
				.Prop( "fit", McpSchema.Boolean( "Fit entire graph into view.", false ) )
				.Prop( "selectNodeId", McpSchema.Integer( "Node ID in the current graph to click/select and open details." ) )
				.Prop( "detailTab", McpSchema.Enum( "Detail tab to open on the selected graph node.", "History", "Where Used", "Dependents", "Properties", "Info" ) )
				.Prop( "closeDetails", McpSchema.Boolean( "Close the graph details side panel.", false ) )
				.Build(),
			async ( raw, ct ) => bridge.LocalUiBridge is { } ui
				? McpToolResult.Ok( await ui.DependencyGraphControlAsync( raw, ct ) )
				: RequireBridgeError( "ui_dependency_graph_control" ),
			new McpToolAnnotations( "Control Node / Dependency Graph UI", ReadOnlyHint: false ) );

		server.RegisterTool(
			"ui_search_control",
			"Controls the live Search Vault UI (SearchOdooView / SearchOdooViewModel): open panel, set filters, execute search, select results, locate a result in File Manager, or reset.",
			McpSchema.Object()
				.Prop( "openPanel", McpSchema.Boolean( "Ensure the Search panel is visible before running.", true ) )
				.Prop( "fileName", McpSchema.String( "Set the filename filter text." ) )
				.Prop( "comparer", McpSchema.String( "Set the filename operator (e.g. 'ilike', '=', 'like')." ) )
				.Prop( "checkedOutMe", McpSchema.Boolean( "Toggle Checked Out To Me." ) )
				.Prop( "deletedRemotely", McpSchema.Boolean( "Toggle Deleted Remotely." ) )
				.Prop( "localOnly", McpSchema.Boolean( "Toggle Local Only." ) )
				.Prop( "maxResults", McpSchema.Integer( "Set Max Results.", 1, 5000 ) )
				.Prop( "runSearch", McpSchema.Boolean( "Execute the search and return results.", false ) )
				.Prop( "locateResultName", McpSchema.String( "Locate a matching search result row by filename in the File Manager." ) )
				.Prop( "reset", McpSchema.Boolean( "Reset the search form and results.", false ) )
				.Build(),
			async ( raw, ct ) => bridge.LocalUiBridge is { } ui
				? McpToolResult.Ok( await ui.SearchControlAsync( raw, ct ) )
				: RequireBridgeError( "ui_search_control" ),
			new McpToolAnnotations( "Control Search UI", ReadOnlyHint: false ) );

		server.RegisterTool(
			"ui_status_panel",
			"Reads or controls the live Status Log panel (StatusDialogViewModel): progress counters, byte counts, log entries (status/info/warnings/errors), clearLogs, cancelOperation, setAutoClose, or setHistoryLength.",
			McpSchema.Object()
				.Prop( "maxItems", McpSchema.Integer( "Maximum log lines per bucket to return (default 50).", 0, 500, 50 ) )
				.Prop( "clearLogs", McpSchema.Boolean( "Clear all status logs when idle.", false ) )
				.Prop( "cancelOperation", McpSchema.Boolean( "Cancel any currently running operation.", false ) )
				.Prop( "autoClose", McpSchema.Boolean( "Set AutoClose preference." ) )
				.Prop( "historyLength", McpSchema.Integer( "Resize ring buffer history length.", 100, 100000 ) )
				.Build(),
			async ( raw, ct ) => bridge.LocalUiBridge is { } ui
				? McpToolResult.Ok( await ui.StatusPanelControlAsync( raw, ct ) )
				: RequireBridgeError( "ui_status_panel" ),
			new McpToolAnnotations( "Read/Control Status Panel", ReadOnlyHint: false ) );

		server.RegisterTool(
			"ui_theme_control",
			"Reads or independently customizes ANY HackPDM theme setting — base mode, palette, form preset, branch lines, any Fluent/system brush color (batch or single, targeting Dark/Light/both), independent style/corner/border/density/font/size/padding overrides, independent dependency graph colors/shapes/layout, named custom theme save/load/delete, full factory default reset, save to AppConfig.json (with auto restore point), revert, or optional soft/full auto-restart. Works both live in the running GUI and headlessly on AppConfig.json.",
			McpSchema.Object()
				.Prop( "mode", McpSchema.Enum( "Base light/dark/system mode.", "Dark", "Light", "System" ) )
				.Prop( "palette", McpSchema.String( "Built-in color palette preset ('Dark', 'Light', 'NavyBlue')." ) )
				.Prop( "form", McpSchema.String( "Built-in shape/density preset ('Compact', 'Default', 'Modern', 'Fancy')." ) )
				.Prop( "showTreeBranchLines", McpSchema.Boolean( "Show or hide folder tree branching guide lines." ) )
				.Prop( "targetTheme", McpSchema.Enum( "Which color dictionary to edit ('active', 'dark', 'light', 'both'; default 'active').", "active", "dark", "light", "both" ) )
				.Prop( "setColorKey", McpSchema.String( "Single theme/system SolidColorBrush resource key to override." ) )
				.Prop( "setColorHex", McpSchema.String( "Hex color '#AARRGGBB' or '#RRGGBB' for setColorKey." ) )
				.Prop( "colors", McpSchema.AnyObject( "Batch map of Fluent/system SolidColorBrush resource key -> '#AARRGGBB' / '#RRGGBB' (or null to clear a key)." ) )
				.Prop( "removeColors", McpSchema.StringArray( "List of custom brush color override keys to clear back to preset." ) )
				.Prop( "resetColorKey", McpSchema.String( "Reset a single theme color key to its preset default." ) )
				.Prop( "resetAll", McpSchema.Boolean( "Reset all custom brush color overrides for targetTheme.", false ) )
				.Prop( "style", McpSchema.AnyObject( "Independent style overrides (any subset; null resets field): compactDensity, controlCornerRadius, overlayCornerRadius, panelCornerRadius, panelBorderThickness, accentPanels, panelStrokeColor, fontFamily, baseFontSize, contentPadding." ) )
				.Prop( "resetStyle", McpSchema.Boolean( "Clear all independent style/corner/density/font overrides back to the Form preset.", false ) )
				.Prop( "graph", McpSchema.AnyObject( "Independent Dependency Graph overrides (any subset; null resets field): backdrop, gridLine, body, bodyBorder, text, headerText, subText, link, linkHighlight, port, rootBorder, selectedBorder, shadow, headers {Assembly,Part,Drawing,Pdf,Other,Deleted}, headerStyle (Band|Strip|Gradient), linkStyle (Curve|Elbow|GlowCurve), headerHeight, cornerRadius, borderThickness, rootBorderThickness, titleSize, showFolder, showSizeLine, portSize, hollowPorts, linkThickness, shadowDepth, showGrid, columnGap, rowGap, margin, minWidth, minHeight, maxWidth, maxHeight." ) )
				.Prop( "resetGraph", McpSchema.Boolean( "Clear all independent Dependency Graph overrides back to preset.", false ) )
				.Prop( "saveCustomThemeName", McpSchema.String( "Save the current complete theme (mode, palette, form, colors, style, graph) as a named custom theme in AppConfig.json." ) )
				.Prop( "customThemeDescription", McpSchema.String( "Optional description when saving a named custom theme." ) )
				.Prop( "loadCustomThemeName", McpSchema.String( "Load and apply a previously saved named custom theme from AppConfig.json." ) )
				.Prop( "deleteCustomThemeName", McpSchema.String( "Delete a saved custom theme by name from AppConfig.json." ) )
				.Prop( "resetToDefaults", McpSchema.Boolean( "Reset EVERY theme setting (palette, form, mode, branch lines, colors, style, graph) back to HackPDM factory defaults.", false ) )
				.Prop( "clearCustomThemes", McpSchema.Boolean( "When resetToDefaults=true, also remove saved custom theme presets (default false).", false ) )
				.Prop( "save", McpSchema.Boolean( "Persist changes to AppConfig.json (automatically captures an MCP restore point first).", false ) )
				.Prop( "revert", McpSchema.Boolean( "Discard unsaved changes and reload from AppConfig.json (live GUI).", false ) )
				.Prop( "autoApplyRestart", McpSchema.Enum( "Automatically apply changes that need UI rebuild or restart after saving: 'none' (default), 'soft' (in-process UI reload, stays connected), 'auto' (soft unless base Dark/Light switched), or 'full' (process restart with MCP reconnect).", "none", "soft", "auto", "full" ) )
				.Prop( "preserveNavigation", McpSchema.Boolean( "Return to the same active page/tab after autoApplyRestart (default true).", true ) )
				.Prop( "preserveConnection", McpSchema.Boolean( "Keep same MCP bridge port and Bearer token across full restart (default true).", true ) )
				.Prop( "waitTimeoutSeconds", McpSchema.Integer( "Seconds for standalone MCP proxy to wait and auto-reattach on full restart (default 30).", 3, 120, 30 ) )
				.Build(),
			async ( raw, ct ) => bridge.LocalUiBridge is { } ui
				? McpToolResult.Ok( await ui.ThemeControlAsync( raw, ct ) )
				: McpToolResult.Ok( await api.ThemeControlHeadlessAsync( raw, ct ) ),
			new McpToolAnnotations( "Read/Customize App Theme & Graph", ReadOnlyHint: false ) );

		server.RegisterTool(
			"ui_customize_config",
			"Reads, customizes, or resets ANY HackPDM UI configuration section stored in AppConfig.json: OdooEntryColors (all 11 entry status colors), ColumnSettings (rowHeight, fontSize, grouping, per-column header/width/order/autoSize), WorkspaceLayout (all 17 docking, pane sizing, drawer, and visibility settings or layout presets), and WindowSettings (per-window titles, sizes, positions, presenter kinds — optionally applied live to open windows). Supports factory reset of any section or all sections, auto restore points, and optional softReload. Works both live and headlessly.",
			McpSchema.Object()
				.Prop( "entryColors", McpSchema.AnyObject( "Map of ColorSettings property name (White, Black, LightGray, Gray, MustardYellow, DarkGray, DarkRed, DarkBlue, DarkOliveGreen, OrangeEntry, BlueEntry) -> '#AARRGGBB' / '#RRGGBB'." ) )
				.Prop( "columns", McpSchema.AnyObject( "OdooEntryColumns overrides: rowHeight, fontSize, groupingEnabled, groupCollapsed, groupByField, groupByDirection (Ascending|Descending), columns { Name|Type|Size|Status|Checkout|LocalDate|RemoteDate|FullName: { displayIndex, header, width, autoSize } }." ) )
				.Prop( "workspaceLayout", McpSchema.AnyObject( "WorkspaceLayoutSettings overrides: preset (Balanced|RightInspector|RightSidebar|FocusEntries) and/or any individual property (showFoldersPane, foldersDock, foldersPaneWidth, showDetailsPane, detailsDock, detailsPaneHeight, detailsRightWidth, showPreviewPane, previewPlacement, previewPaneSize, graphFoldersWidth, graphFilesWidth, searchDock, searchDrawerWidth, statusDrawerHeight, searchMinimalMode, statusMinimalMode)." ) )
				.Prop( "windowSettings", McpSchema.AnyObject( "Map of window key (e.g. 'main', 'HackFileManager', 'DependencyGraph', 'StatusDialogView', 'SearchOdooView') -> { title, x, y, width, height, windowKind }." ) )
				.Prop( "applyToOpenWindows", McpSchema.Boolean( "Live-resize and live-retitle matching currently open windows (default true).", true ) )
				.Prop( "resetSection", McpSchema.Enum( "Reset one AppConfig.json UI section or all sections back to HackPDM factory defaults.", "entryColors", "columns", "workspaceLayout", "windowSettings", "theme", "all" ) )
				.Prop( "clearCustomThemes", McpSchema.Boolean( "When resetSection is 'theme' or 'all', also clear saved custom themes (default false).", false ) )
				.Prop( "save", McpSchema.Boolean( "Persist changes to AppConfig.json (defaults to true when modifying; captures restore point first)." ) )
				.Prop( "softReload", McpSchema.Boolean( "Perform an in-process soft UI reload after saving so column/density/color changes immediately rebuild live views (default false).", false ) )
				.Build(),
			async ( raw, ct ) => bridge.LocalUiBridge is { } ui
				? McpToolResult.Ok( await ui.CustomizeConfigAsync( raw, ct ) )
				: McpToolResult.Ok( await api.CustomizeConfigHeadlessAsync( raw, ct ) ),
			new McpToolAnnotations( "Customize HackPDM UI Configuration", ReadOnlyHint: false ) );

		server.RegisterTool(
			"ui_app_restart",
			"Applies pending theme/style/configuration changes by performing either an in-process Soft UI Reload ('soft' — rebuilds views without closing HackPDM or dropping MCP/Odoo) or a Full Process Auto-Restart ('full' — saves AppConfig.json, relaunches HackPDM preserving port/token/active page, and automatically re-attaches standalone MCP proxy clients). In default 'auto' mode, uses Soft Reload unless a base Dark/Light application theme change requires a Full Restart.",
			McpSchema.Object()
				.Prop( "mode", McpSchema.Enum( "Restart strategy: 'auto' (default: soft unless base Dark/Light changed), 'soft' (in-process UI rebuild, stays connected), or 'full' (OS process restart with seamless MCP reconnect).", "auto", "soft", "full" ) )
				.Prop( "saveConfig", McpSchema.Boolean( "Save AppConfig.json (with pre-restart restore point) before restarting (default true).", true ) )
				.Prop( "preserveNavigation", McpSchema.Boolean( "Restore the exact active page & settings tab after restart (default true).", true ) )
				.Prop( "preserveConnection", McpSchema.Boolean( "Preserve the live MCP bridge port and Bearer token across a full restart so both stdio and direct HTTP clients stay connected (default true).", true ) )
				.Prop( "waitTimeoutSeconds", McpSchema.Integer( "Seconds for standalone MCP proxy to wait and auto-reattach after a full restart (default 30).", 3, 120, 30 ) )
				.Build(),
			async ( raw, ct ) =>
			{
				if( bridge.LocalUiBridge is not { } ui )
					return RequireBridgeError( "ui_app_restart" );
				McpToolArgs args = new( raw );
				return McpToolResult.Ok( await ui.RestartAppAsync(
					args.GetString( "mode", "auto" )!,
					args.GetBool( "saveConfig", true ),
					args.GetBool( "preserveNavigation", true ),
					args.GetBool( "preserveConnection", true ),
					ct ) );
			},
			new McpToolAnnotations( "Soft or Auto Restart HackPDM UI", ReadOnlyHint: false ) );

		// ---- Guided Actions, Attention Animations, Popups & Multi-Step Tours ----

		server.RegisterTool(
			"ui_guide_highlight",
			"Draws a temporary animated attention cue (pulse, spotlight cutout, bounce arrow, shake, flash, or steady outline) and optional callout message on any live HackPDM UI control to guide the user's attention without mutating data. Optionally navigates to a page first, auto-expires after durationMs, or auto-dismisses when the user clicks the highlighted control.",
			McpSchema.Object()
				.Prop( "name", McpSchema.String( "x:Name of the target control to highlight (e.g. 'CommitButton', 'CopyMcpConfigButton', 'PalettePicker')." ) )
				.Prop( "automationId", McpSchema.String( "Optional AutomationProperties.AutomationId of the target control." ) )
				.Prop( "text", McpSchema.String( "Optional visible text/content substring of the target control." ) )
				.Prop( "controlType", McpSchema.String( "Optional XAML type filter (e.g. 'Button', 'ComboBox', 'TextBox')." ) )
				.Prop( "page", McpSchema.String( "Optional page to navigate to before highlighting (HackFileManager, DependencyGraph, ProfileManager, Configuration, JsonEditor, Settings)." ) )
				.Prop( "settingsSection", McpSchema.String( "Optional Settings tab to open before highlighting ('Theme', 'Status', 'Mcp')." ) )
				.Prop( "animation", McpSchema.Enum( "Attention animation style (default 'pulse').", "pulse", "spotlight", "bounce", "shake", "flash", "outline" ) )
				.Prop( "title", McpSchema.String( "Optional callout card title shown beside the highlighted control." ) )
				.Prop( "message", McpSchema.String( "Optional guidance text explaining what the user should notice or click." ) )
				.Prop( "badgeText", McpSchema.String( "Optional short floating badge pill above the control (e.g. 'Click Here', 'Step 1')." ) )
				.Prop( "severity", McpSchema.Enum( "Visual accent style (default 'action').", "action", "info", "tip", "warning", "success", "critical" ) )
				.Prop( "accentColor", McpSchema.String( "Optional custom hex accent color ('#RRGGBB' or '#AARRGGBB')." ) )
				.Prop( "durationMs", McpSchema.Integer( "Auto-dismiss duration in milliseconds (0 = stay until dismissed/clicked/cleared; default 8000).", 0, 300000, 8000 ) )
				.Prop( "dismissOnTargetClick", McpSchema.Boolean( "Automatically clear the highlight as soon as the user clicks the target control (default true).", true ) )
				.Prop( "replaceExisting", McpSchema.Boolean( "Replace previous highlight cues before showing this one (default true).", true ) )
				.Build(),
			async ( raw, ct ) => bridge.LocalUiBridge is { } ui
				? McpToolResult.Ok( await ui.GuideHighlightAsync( raw, ct ) )
				: RequireBridgeError( "ui_guide_highlight" ),
			new McpToolAnnotations( "Highlight Control with Guided Animation", ReadOnlyHint: false ) );

		server.RegisterTool(
			"ui_guide_popup",
			"Displays a rich guided action popup or coachmark card inside HackPDM (anchored to a highlighted control or floating at top/center/bottom-right/bottom-center) with custom action buttons. Set waitForUser=true to pause until the user clicks one of the buttons and receive their exact choice.",
			McpSchema.Object()
				.Prop( "title", McpSchema.String( "Popup headline." ), required: true )
				.Prop( "message", McpSchema.String( "Guidance instructions or question for the user." ), required: true )
				.Prop( "badge", McpSchema.String( "Optional header pill label (e.g. 'AI GUIDE', 'STEP 2', 'REVIEW')." ) )
				.Prop( "severity", McpSchema.Enum( "Visual accent style (default 'action').", "action", "info", "tip", "warning", "success", "critical" ) )
				.Prop( "accentColor", McpSchema.String( "Optional custom hex accent color ('#RRGGBB' or '#AARRGGBB')." ) )
				.Prop( "placement", McpSchema.Enum( "Popup position ('anchor' when targeting a control, or 'top', 'center', 'bottom-right', 'bottom-center').", "anchor", "top", "center", "bottom-right", "bottom-center" ) )
				.Prop( "targetName", McpSchema.String( "Optional x:Name of a control to highlight and anchor this popup beside." ) )
				.Prop( "targetAutomationId", McpSchema.String( "Optional AutomationId of the anchor control." ) )
				.Prop( "targetText", McpSchema.String( "Optional visible text substring of the anchor control." ) )
				.Prop( "targetControlType", McpSchema.String( "Optional XAML type filter for the anchor control." ) )
				.Prop( "page", McpSchema.String( "Optional page to navigate to first." ) )
				.Prop( "settingsSection", McpSchema.String( "Optional Settings tab to open first ('Theme', 'Status', 'Mcp')." ) )
				.Prop( "highlightAnimation", McpSchema.Enum( "Animation on the anchor control when a target is specified (default 'spotlight').", "spotlight", "pulse", "bounce", "shake", "flash", "outline" ) )
				.Prop( "buttons", McpSchema.AnyArray( "Optional buttons: strings (e.g. ['Import Now', 'Preview', 'Cancel']) or objects [{ id, label, accent }]. Defaults to ['Got It']." ) )
				.Prop( "durationMs", McpSchema.Integer( "Auto-dismiss duration in ms when waitForUser=false (0 = stay until clicked/dismissed; default 12000).", 0, 300000, 12000 ) )
				.Prop( "waitForUser", McpSchema.Boolean( "Wait until the user clicks one of the popup buttons or dismisses it, returning their choice (default false).", false ) )
				.Prop( "timeoutSeconds", McpSchema.Integer( "Timeout in seconds when waitForUser=true (default 120).", 3, 600, 120 ) )
				.Prop( "replaceExisting", McpSchema.Boolean( "Replace any existing guided popup first (default true).", true ) )
				.Build(),
			async ( raw, ct ) => bridge.LocalUiBridge is { } ui
				? McpToolResult.Ok( await ui.GuidePopupAsync( raw, ct ) )
				: RequireBridgeError( "ui_guide_popup" ),
			new McpToolAnnotations( "Show Guided Action Popup", ReadOnlyHint: false ) );

		server.RegisterTool(
			"ui_guide_tour",
			"Starts, navigates, inspects, or stops an interactive multi-step guided walkthrough inside HackPDM. Each step can auto-navigate to a page/settings tab, spotlight or animate a target control, and display Back / Next / Finish buttons so the user moves through the workflow interactively.",
			McpSchema.Object()
				.Prop( "action", McpSchema.Enum( "Tour control action (default 'start').", "start", "next", "previous", "goto", "status", "stop" ) )
				.Prop( "title", McpSchema.String( "Walkthrough title shown in the progress header (e.g. 'First-Time MCP Setup')." ) )
				.Prop( "stepIndex", McpSchema.Integer( "0-based step index when action='goto'.", 0, 100 ) )
				.Prop( "steps", McpSchema.AnyArray( "Array of tour step objects when action='start': [{ title, message, page?, settingsSection?, targetName?, targetText?, targetAutomationId?, targetControlType?, animation?, badgeText?, severity?, accentColor? }]." ) )
				.Build(),
			async ( raw, ct ) => bridge.LocalUiBridge is { } ui
				? McpToolResult.Ok( await ui.GuideTourAsync( raw, ct ) )
				: RequireBridgeError( "ui_guide_tour" ),
			new McpToolAnnotations( "Interactive Multi-Step Guided Tour", ReadOnlyHint: false ) );

		server.RegisterTool(
			"ui_guide_clear",
			"Lists or dismisses active temporary guided highlights, animations, popups, and tours from the HackPDM window.",
			McpSchema.Object()
				.Prop( "cueId", McpSchema.String( "Optional specific cueId to clear; omit to clear all active guided overlays." ) )
				.Prop( "listOnly", McpSchema.Boolean( "Only list active guided cues and tour status without clearing anything (default false).", false ) )
				.Build(),
			async ( raw, ct ) =>
			{
				if( bridge.LocalUiBridge is not { } ui )
					return RequireBridgeError( "ui_guide_clear" );
				McpToolArgs args = new( raw );
				return McpToolResult.Ok( await ui.GuideClearAsync( args.GetString( "cueId" ), args.GetBool( "listOnly", false ), ct ) );
			},
			new McpToolAnnotations( "Inspect or Clear Guided Overlays", ReadOnlyHint: false, IdempotentHint: true ) );

		// ---- Dynamic Supplemental AI Windows ----

		server.RegisterTool(
			"ui_window_create",
			"Creates (or replaces) a native HackPDM supplemental dynamic window declaratively composed by the AI to present restructured, supplemental, or helpful additional information (e.g. BOM rollups, SolidWorks property diffs, commit readiness audits, dashboards, preview galleries). Automatically inherits HackPDM's live theme, registers in ui_list_windows/ui_screenshot, includes built-in Copy Summary & Close buttons, and supports 21 composable component types: section/card, stack, grid/columns, tabs, text, markdown, kpi_row, key_value, table (with built-in live search & status pills), tree, list, badge_row, callout, progress, code/json (with Copy button), image (base64 or filePath), divider, input, checkbox, select, and button_row.",
			McpSchema.Object()
				.Prop( "windowId", McpSchema.String( "Optional stable window identifier (auto-generated if omitted; reuse the same ID to replace/update the window)." ) )
				.Prop( "title", McpSchema.String( "Window title displayed in the native title bar and header." ), required: true )
				.Prop( "subtitle", McpSchema.String( "Optional subtitle displayed beneath the window title." ) )
				.Prop( "badge", McpSchema.String( "Optional header pill label (default 'AI SUPPLEMENTAL')." ) )
				.Prop( "severity", McpSchema.Enum( "Header accent color theme (default 'info').", "info", "action", "tip", "success", "warning", "critical" ) )
				.Prop( "accentColor", McpSchema.String( "Optional custom hex accent color ('#RRGGBB' or '#AARRGGBB')." ) )
				.Prop( "width", McpSchema.Integer( "Window width in pixels (default 940).", 360, 2400, 940 ) )
				.Prop( "height", McpSchema.Integer( "Window height in pixels (default 720).", 260, 1600, 720 ) )
				.Prop( "x", McpSchema.Integer( "Optional X screen coordinate (0 = default placement)." ) )
				.Prop( "y", McpSchema.Integer( "Optional Y screen coordinate (0 = default placement)." ) )
				.Prop( "alwaysOnTop", McpSchema.Boolean( "Keep window floating as CompactOverlay when supported (default false).", false ) )
				.Prop( "replaceExisting", McpSchema.Boolean( "If a dynamic window with the same windowId is already open, replace its contents cleanly (default true).", true ) )
				.Prop( "components", McpSchema.AnyArray( "Ordered array of declarative component objects ({ type, id?, ... }). Supported types: section, stack, grid, tabs, text, markdown, kpi_row, key_value, table, tree, list, badge_row, callout, progress, code, image, divider, input, checkbox, select, button_row." ), required: true )
				.Build(),
			async ( raw, ct ) => bridge.LocalUiBridge is { } ui
				? McpToolResult.Ok( await ui.DynamicWindowCreateAsync( raw, ct ) )
				: RequireBridgeError( "ui_window_create" ),
			new McpToolAnnotations( "Create Dynamic Supplemental Window", ReadOnlyHint: false ) );

		server.RegisterTool(
			"ui_window_update",
			"Updates an already-open dynamic supplemental window live without closing or flickering: change title/subtitle/badge/size, replace all components, append new components, or surgically patch elements by id (updating text, callout message/severity, progress value/statusText, KPI tiles, table rows, active tab, input/checkbox/select values, or visibility).",
			McpSchema.Object()
				.Prop( "windowId", McpSchema.String( "Window ID of an open dynamic window created with ui_window_create." ), required: true )
				.Prop( "title", McpSchema.String( "Optional new window title." ) )
				.Prop( "subtitle", McpSchema.String( "Optional new window subtitle (empty string clears it)." ) )
				.Prop( "badge", McpSchema.String( "Optional new header badge label." ) )
				.Prop( "severity", McpSchema.Enum( "Optional new header accent severity.", "info", "action", "tip", "success", "warning", "critical" ) )
				.Prop( "accentColor", McpSchema.String( "Optional custom hex header accent color." ) )
				.Prop( "width", McpSchema.Integer( "Optional new window width in pixels.", 360, 2400 ) )
				.Prop( "height", McpSchema.Integer( "Optional new window height in pixels.", 260, 1600 ) )
				.Prop( "focus", McpSchema.Boolean( "Bring the dynamic window to the foreground (default false).", false ) )
				.Prop( "components", McpSchema.AnyArray( "Replace the entire component body while preserving existing user input values." ) )
				.Prop( "appendComponents", McpSchema.AnyArray( "Append additional components to the bottom of the window." ) )
				.Prop( "patches", McpSchema.AnyArray( "Targeted live updates by element id: [{ id, text?, title?, subtitle?, message?, value?, numericValue?, max?, indeterminate?, statusText?, severity?, accentColor?, badge?, visible?, checked?, selected?, rows? }]." ) )
				.Build(),
			async ( raw, ct ) => bridge.LocalUiBridge is { } ui
				? McpToolResult.Ok( await ui.DynamicWindowUpdateAsync( raw, ct ) )
				: RequireBridgeError( "ui_window_update" ),
			new McpToolAnnotations( "Update Dynamic Supplemental Window", ReadOnlyHint: false ) );

		server.RegisterTool(
			"ui_window_list",
			"Lists all currently open AI-created dynamic supplemental windows with their IDs, titles, sizes, patchable element IDs, live input values, and pending event counts.",
			McpSchema.EmptyObject(),
			async ( _, ct ) => bridge.LocalUiBridge is { } ui
				? McpToolResult.Ok( await ui.DynamicWindowListAsync( ct ) )
				: RequireBridgeError( "ui_window_list" ),
			new McpToolAnnotations( "List Dynamic Supplemental Windows", ReadOnlyHint: true, IdempotentHint: true ) );

		server.RegisterTool(
			"ui_window_events",
			"Reads user interactions (button clicks, selectable table row clicks, window close) and live values of all input/checkbox/select controls inside an open dynamic supplemental window. Set waitForEvent=true to pause until the user clicks a button or closes the window.",
			McpSchema.Object()
				.Prop( "windowId", McpSchema.String( "Window ID of an open dynamic window." ), required: true )
				.Prop( "waitForEvent", McpSchema.Boolean( "Wait until a new user event occurs if none are queued (default false).", false ) )
				.Prop( "clearEvents", McpSchema.Boolean( "Clear returned events from the window's event queue (default true).", true ) )
				.Prop( "timeoutSeconds", McpSchema.Integer( "Wait timeout in seconds when waitForEvent=true (default 120).", 1, 600, 120 ) )
				.Build(),
			async ( raw, ct ) =>
			{
				if( bridge.LocalUiBridge is not { } ui )
					return RequireBridgeError( "ui_window_events" );
				McpToolArgs args = new( raw );
				string windowId = args.RequireString( "windowId" );
				return McpToolResult.Ok( await ui.DynamicWindowEventsAsync(
					windowId,
					args.GetBool( "waitForEvent", false ),
					args.GetBool( "clearEvents", true ),
					args.GetInt( "timeoutSeconds", 120 ) ?? 120,
					ct ) );
			},
			new McpToolAnnotations( "Read or Wait for Dynamic Window Events", ReadOnlyHint: true ) );

		server.RegisterTool(
			"ui_window_close",
			"Closes one specific AI-created dynamic supplemental window by windowId, or closes all open dynamic windows when closeAll=true (or windowId is omitted).",
			McpSchema.Object()
				.Prop( "windowId", McpSchema.String( "Window ID of the dynamic window to close; omit or set closeAll=true to close all dynamic windows." ) )
				.Prop( "closeAll", McpSchema.Boolean( "Close all open AI-created dynamic windows (default false).", false ) )
				.Build(),
			async ( raw, ct ) =>
			{
				if( bridge.LocalUiBridge is not { } ui )
					return RequireBridgeError( "ui_window_close" );
				McpToolArgs args = new( raw );
				return McpToolResult.Ok( await ui.DynamicWindowCloseAsync(
					args.GetString( "windowId" ),
					args.GetBool( "closeAll", false ),
					ct ) );
			},
			new McpToolAnnotations( "Close Dynamic Supplemental Window", ReadOnlyHint: false, IdempotentHint: true ) );

		server.RegisterTool(
			"ui_dialog_respond",
			"Lists currently open HackPDM MessageBox dialogs (title, message, available buttons) and optionally responds by clicking a button (OK, Cancel, Yes, No, Retry, Ignore, Abort, TryAgain, Continue).",
			McpSchema.Object()
				.Prop( "dialogId", McpSchema.String( "Optional dialog ID (defaults to the most recently opened active dialog)." ) )
				.Prop( "response", McpSchema.Enum( "Button result to click on the dialog.", "OK", "Cancel", "Yes", "No", "Retry", "Ignore", "Abort", "TryAgain", "Continue" ) )
				.Prop( "listOnly", McpSchema.Boolean( "Only list open dialogs without dismissing any.", false ) )
				.Build(),
			async ( raw, ct ) =>
			{
				if( bridge.LocalUiBridge is not { } ui )
					return RequireBridgeError( "ui_dialog_respond" );
				McpToolArgs args = new( raw );
				return McpToolResult.Ok( await ui.DialogRespondAsync(
					args.GetString( "dialogId" ),
					args.GetString( "response" ),
					args.GetBool( "listOnly", false ),
					ct ) );
			},
			new McpToolAnnotations( "Inspect/Respond to UI Dialogs", ReadOnlyHint: false ) );

		server.RegisterTool(
			"ui_screenshot",
			"Captures a live PNG screenshot of the HackPDM desktop window, a detached window, or a specific named control via RenderTargetBitmap, returning an MCP image block and optionally saving to disk.",
			McpSchema.Object()
				.Prop( "windowId", McpSchema.String( "Window ID from ui_list_windows (defaults to MainWindow)." ) )
				.Prop( "elementName", McpSchema.String( "Optional x:Name of a specific UIElement to capture instead of the whole window." ) )
				.Prop( "savePath", McpSchema.String( "Optional output PNG path to save the screenshot to disk." ) )
				.Prop( "includeImageContent", McpSchema.Boolean( "Return inline MCP image content (default true).", true ) )
				.Build(),
			async ( raw, ct ) =>
			{
				if( bridge.LocalUiBridge is not { } ui )
					return RequireBridgeError( "ui_screenshot" );
				McpToolArgs args = new( raw );
				return await ui.CaptureScreenshotAsync(
					args.GetString( "windowId" ),
					args.GetString( "elementName" ),
					args.GetString( "savePath" ),
					args.GetBool( "includeImageContent", true ),
					ct );
			},
			new McpToolAnnotations( "Capture UI Screenshot", ReadOnlyHint: true, IdempotentHint: true ) );
	}
}

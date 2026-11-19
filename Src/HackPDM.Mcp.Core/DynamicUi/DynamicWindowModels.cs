using System.Text;
using System.Text.Json;

namespace HackPDM.Mcp.DynamicUi;

/// <summary>Declarative specification for an AI-constructed supplemental HackPDM window.</summary>
public sealed class DynamicWindowSpec
{
	public string WindowId { get; set; } = string.Empty;
	public string Title { get; set; } = string.Empty;
	public string? Subtitle { get; set; }
	public string Badge { get; set; } = "AI SUPPLEMENTAL";
	public string Severity { get; set; } = "info";
	public string? AccentColor { get; set; }
	public int Width { get; set; } = 920;
	public int Height { get; set; } = 700;
	public int X { get; set; }
	public int Y { get; set; }
	public bool AlwaysOnTop { get; set; }
	public bool ReplaceExisting { get; set; } = true;
	public List<DynamicComponentSpec> Components { get; set; } = [];
}

/// <summary>Declarative specification for a single component node inside a dynamic supplemental window.</summary>
public sealed class DynamicComponentSpec
{
	public string Type { get; set; } = "text";
	public string? Id { get; set; }
	public bool Visible { get; set; } = true;

	// Common headings & copy
	public string? Title { get; set; }
	public string? Subtitle { get; set; }
	public string? Text { get; set; }
	public string? Content { get; set; }
	public string? Message { get; set; }
	public string? Label { get; set; }
	public string? Badge { get; set; }
	public string? Severity { get; set; }
	public string? AccentColor { get; set; }
	public string? Color { get; set; }
	public string? Variant { get; set; }
	public bool? Bold { get; set; }
	public bool Selectable { get; set; } = true;
	public bool Wrap { get; set; } = true;

	// Layout & containers
	public string Orientation { get; set; } = "vertical";
	public double Spacing { get; set; } = 10;
	public double ColumnSpacing { get; set; } = 12;
	public double RowSpacing { get; set; } = 12;
	public int ColumnCount { get; set; }
	public List<string> GridColumnDefs { get; set; } = [];
	public bool Collapsible { get; set; }
	public bool Expanded { get; set; } = true;
	public List<DynamicComponentSpec> Children { get; set; } = [];

	// Tabs
	public int SelectedIndex { get; set; }
	public List<DynamicTabSpec> Tabs { get; set; } = [];

	// Structured display payloads
	public List<DynamicKpiItem> KpiItems { get; set; } = [];
	public List<DynamicKeyValueItem> KeyValueItems { get; set; } = [];
	public List<DynamicTableColumn> TableColumns { get; set; } = [];
	public List<Dictionary<string, string>> TableRows { get; set; } = [];
	public bool? Searchable { get; set; }
	public string? SearchPlaceholder { get; set; }
	public bool Striped { get; set; } = true;
	public bool RowSelectable { get; set; }
	public string? EmptyText { get; set; }
	public double MaxHeight { get; set; }
	public List<DynamicTreeNode> TreeNodes { get; set; } = [];
	public List<DynamicListItem> ListItems { get; set; } = [];
	public List<DynamicBadgeItem> Badges { get; set; } = [];

	// Progress
	public double ProgressValue { get; set; }
	public double ProgressMax { get; set; } = 100;
	public bool Indeterminate { get; set; }
	public string? StatusText { get; set; }

	// Code / JSON
	public string? Language { get; set; }
	public bool Copyable { get; set; } = true;

	// Image
	public string? Base64 { get; set; }
	public string? FilePath { get; set; }
	public string? Caption { get; set; }
	public double? Width { get; set; }
	public double? Height { get; set; }

	// Interactive inputs & buttons
	public string? Placeholder { get; set; }
	public string? Value { get; set; }
	public bool Multiline { get; set; }
	public bool Checked { get; set; }
	public List<string> Options { get; set; } = [];
	public string? Selected { get; set; }
	public string Align { get; set; } = "right";
	public List<DynamicButtonSpec> Buttons { get; set; } = [];
}

public sealed record DynamicTabSpec(
	string Id,
	string Title,
	string? Badge,
	List<DynamicComponentSpec> Children );

public sealed record DynamicKpiItem(
	string? Id,
	string Label,
	string Value,
	string? Subtitle,
	string? Badge,
	string? Severity,
	string? AccentColor );

public sealed record DynamicKeyValueItem(
	string Key,
	string Value,
	string? Badge,
	bool Monospace,
	string? Severity,
	string? AccentColor );

public sealed record DynamicTableColumn(
	string Key,
	string Header,
	string? Width,
	string Align,
	bool Monospace,
	bool IsBadge );

public sealed record DynamicTreeNode(
	string Id,
	string Label,
	string? Subtitle,
	string? Badge,
	string? Severity,
	bool Expanded,
	List<DynamicTreeNode> Children );

public sealed record DynamicListItem(
	string? Id,
	string Title,
	string? Subtitle,
	string? Value,
	string? Badge,
	string? Severity,
	string? AccentColor );

public sealed record DynamicBadgeItem(
	string Label,
	string? Severity,
	string? AccentColor );

public sealed record DynamicButtonSpec(
	string Id,
	string Label,
	bool IsAccent,
	string? Severity,
	string Action,
	string? CopyText,
	string? NavigatePage,
	string? SettingsSection );

/// <summary>Live in-place patch targeting an existing component or KPI element by <see cref="Id"/>.</summary>
public sealed class DynamicWindowPatch
{
	public string Id { get; set; } = string.Empty;
	public string? Text { get; set; }
	public string? Title { get; set; }
	public string? Subtitle { get; set; }
	public string? Message { get; set; }
	public string? Value { get; set; }
	public double? NumericValue { get; set; }
	public double? Max { get; set; }
	public bool? Indeterminate { get; set; }
	public string? StatusText { get; set; }
	public string? Severity { get; set; }
	public string? AccentColor { get; set; }
	public string? Badge { get; set; }
	public bool? Visible { get; set; }
	public bool? Checked { get; set; }
	public string? Selected { get; set; }
	public List<Dictionary<string, string>>? Rows { get; set; }
}

/// <summary>Live update request parsed from <c>ui_window_update</c> arguments.</summary>
public sealed class DynamicWindowUpdateSpec
{
	public string WindowId { get; set; } = string.Empty;
	public string? Title { get; set; }
	public string? Subtitle { get; set; }
	public string? Badge { get; set; }
	public string? Severity { get; set; }
	public string? AccentColor { get; set; }
	public int? Width { get; set; }
	public int? Height { get; set; }
	public bool Focus { get; set; }
	public List<DynamicComponentSpec>? ReplaceComponents { get; set; }
	public List<DynamicComponentSpec>? AppendComponents { get; set; }
	public List<DynamicWindowPatch>? Patches { get; set; }
}

/// <summary>
/// Parses, normalizes, and validates declarative dynamic window JSON specifications into strongly-typed
/// models with clear diagnostics before anything reaches XAML rendering.
/// </summary>
public static class DynamicWindowSpecParser
{
	private const int MaxComponentDepth = 16;
	private const int MaxComponentCount = 500;

	public static readonly IReadOnlyList<string> SupportedComponentTypes =
	[
		"section", "stack", "grid", "tabs", "text", "markdown",
		"kpi_row", "key_value", "table", "tree", "list", "badge_row",
		"callout", "progress", "code", "image", "divider",
		"input", "checkbox", "select", "button_row",
	];

	public static DynamicWindowSpec ParseCreate( JsonElement? raw )
	{
		if( raw is not { ValueKind: JsonValueKind.Object } obj )
			throw new ArgumentException( "ui_window_create requires a JSON object with 'title' and 'components'." );

		string title = OptStr( obj, "title" ) ?? string.Empty;
		if( string.IsNullOrWhiteSpace( title ) )
			throw new ArgumentException( "ui_window_create requires a non-empty 'title'." );

		string windowId = OptStr( obj, "windowId" ) ?? OptStr( obj, "id" ) ?? $"dyn-{Guid.NewGuid():N}"[ ..12 ];
		windowId = SanitizeWindowId( windowId );

		if( !TryGetProp( obj, "components", out JsonElement compsEl ) || compsEl.ValueKind != JsonValueKind.Array )
			throw new ArgumentException( "ui_window_create requires a 'components' array containing at least one component." );

		int counter = 0;
		List<DynamicComponentSpec> components = ParseComponentList( compsEl, 0, ref counter );
		if( components.Count == 0 )
			throw new ArgumentException( "ui_window_create requires at least one component in 'components'." );

		int width = Math.Clamp( OptInt( obj, "width" ) ?? 940, 360, 2400 );
		int height = Math.Clamp( OptInt( obj, "height" ) ?? 720, 260, 1600 );

		return new DynamicWindowSpec
		{
			WindowId = windowId,
			Title = title.Trim(),
			Subtitle = OptStr( obj, "subtitle" ),
			Badge = OptStr( obj, "badge" ) ?? "AI SUPPLEMENTAL",
			Severity = NormalizeSeverity( OptStr( obj, "severity" ) ?? "info" ),
			AccentColor = OptStr( obj, "accentColor" ),
			Width = width,
			Height = height,
			X = OptInt( obj, "x" ) ?? 0,
			Y = OptInt( obj, "y" ) ?? 0,
			AlwaysOnTop = OptBool( obj, "alwaysOnTop" ) ?? false,
			ReplaceExisting = OptBool( obj, "replaceExisting" ) ?? true,
			Components = components,
		};
	}

	public static DynamicWindowUpdateSpec ParseUpdate( JsonElement? raw )
	{
		if( raw is not { ValueKind: JsonValueKind.Object } obj )
			throw new ArgumentException( "ui_window_update requires a JSON object with 'windowId'." );

		string windowId = OptStr( obj, "windowId" ) ?? OptStr( obj, "id" ) ?? string.Empty;
		if( string.IsNullOrWhiteSpace( windowId ) )
			throw new ArgumentException( "ui_window_update requires 'windowId'." );

		int counter = 0;
		List<DynamicComponentSpec>? replace = TryGetProp( obj, "components", out JsonElement rEl ) && rEl.ValueKind == JsonValueKind.Array
			? ParseComponentList( rEl, 0, ref counter )
			: null;
		List<DynamicComponentSpec>? append = TryGetProp( obj, "appendComponents", out JsonElement aEl ) && aEl.ValueKind == JsonValueKind.Array
			? ParseComponentList( aEl, 0, ref counter )
			: null;
		List<DynamicWindowPatch>? patches = TryGetProp( obj, "patches", out JsonElement pEl ) && pEl.ValueKind == JsonValueKind.Array
			? ParsePatches( pEl )
			: null;

		return new DynamicWindowUpdateSpec
		{
			WindowId = windowId.Trim(),
			Title = OptStr( obj, "title" ),
			Subtitle = TryGetProp( obj, "subtitle", out JsonElement subEl )
				? ( subEl.ValueKind == JsonValueKind.Null ? string.Empty : subEl.GetString() )
				: null,
			Badge = OptStr( obj, "badge" ),
			Severity = OptStr( obj, "severity" ) is { } sev ? NormalizeSeverity( sev ) : null,
			AccentColor = OptStr( obj, "accentColor" ),
			Width = OptInt( obj, "width" ) is { } w ? Math.Clamp( w, 360, 2400 ) : null,
			Height = OptInt( obj, "height" ) is { } h ? Math.Clamp( h, 260, 1600 ) : null,
			Focus = OptBool( obj, "focus" ) ?? false,
			ReplaceComponents = replace,
			AppendComponents = append,
			Patches = patches,
		};
	}

	public static string NormalizeComponentType( string? rawType )
	{
		string t = ( rawType ?? string.Empty ).Trim().ToLowerInvariant().Replace( "-", "_" );
		return t switch
		{
			"section" or "card" or "panel" or "group" or "box" => "section",
			"stack" or "row" or "column" or "vstack" or "hstack" => "stack",
			"grid" or "columns" or "split" => "grid",
			"tabs" or "tab_view" or "tabview" => "tabs",
			"text" or "label" or "heading" or "paragraph" => "text",
			"markdown" or "rich_text" or "richtext" or "md" => "markdown",
			"kpi_row" or "kpi" or "kpis" or "metrics" or "stats" or "summary_cards" => "kpi_row",
			"key_value" or "kv" or "properties" or "metadata" or "details" => "key_value",
			"table" or "data_table" or "datatable" or "grid_table" => "table",
			"tree" or "hierarchy" or "bom_tree" or "treeview" => "tree",
			"list" or "items" or "checklist" => "list",
			"badge_row" or "badges" or "tags" or "chips" or "pills" => "badge_row",
			"callout" or "alert" or "banner" or "notice" => "callout",
			"progress" or "progress_bar" or "meter" or "status_bar" => "progress",
			"code" or "json" or "snippet" or "pre" => "code",
			"image" or "preview" or "thumbnail" or "img" => "image",
			"divider" or "separator" or "hr" => "divider",
			"input" or "textbox" or "text_box" or "field" => "input",
			"checkbox" or "check_box" or "toggle" => "checkbox",
			"select" or "dropdown" or "combobox" or "combo_box" => "select",
			"button_row" or "actions" or "buttons" => "button_row",
			_ => throw new ArgumentException(
				$"Unsupported dynamic window component type '{rawType}'. Supported types: {string.Join( ", ", SupportedComponentTypes )}." ),
		};
	}

	public static string NormalizeSeverity( string? raw ) => ( raw ?? "info" ).Trim().ToLowerInvariant() switch
	{
		"action" or "primary" or "accent" => "action",
		"tip" or "purple" or "hint" => "tip",
		"success" or "ok" or "green" or "good" => "success",
		"warning" or "warn" or "amber" or "yellow" => "warning",
		"critical" or "error" or "danger" or "red" => "critical",
		_ => "info",
	};

	/// <summary>Produces a clean Markdown summary of a dynamic window for one-click clipboard copy.</summary>
	public static string BuildTextSummary( DynamicWindowSpec spec )
	{
		StringBuilder sb = new();
		sb.Append( "# " ).AppendLine( spec.Title );
		if( !string.IsNullOrWhiteSpace( spec.Subtitle ) )
			sb.AppendLine( spec.Subtitle );
		sb.AppendLine();

		AppendComponentSummary( sb, spec.Components, 0 );
		return sb.ToString().TrimEnd();
	}

	private static void AppendComponentSummary( StringBuilder sb, IReadOnlyList<DynamicComponentSpec> comps, int depth )
	{
		string indent = new( ' ', depth * 2 );
		foreach( DynamicComponentSpec c in comps )
		{
			if( !c.Visible )
				continue;

			switch( c.Type )
			{
				case "section":
					if( !string.IsNullOrWhiteSpace( c.Title ) )
						sb.Append( "## " ).AppendLine( c.Title );
					if( !string.IsNullOrWhiteSpace( c.Subtitle ) )
						sb.AppendLine( c.Subtitle );
					AppendComponentSummary( sb, c.Children, depth );
					sb.AppendLine();
					break;

				case "stack":
				case "grid":
					AppendComponentSummary( sb, c.Children, depth );
					break;

				case "tabs":
					foreach( DynamicTabSpec tab in c.Tabs )
					{
						sb.Append( "### " ).AppendLine( tab.Title );
						AppendComponentSummary( sb, tab.Children, depth );
						sb.AppendLine();
					}
					break;

				case "text":
					if( !string.IsNullOrWhiteSpace( c.Text ?? c.Content ) )
						sb.Append( indent ).AppendLine( c.Text ?? c.Content );
					break;

				case "markdown":
					if( !string.IsNullOrWhiteSpace( c.Content ?? c.Text ) )
					{
						sb.AppendLine( c.Content ?? c.Text );
						sb.AppendLine();
					}
					break;

				case "callout":
					sb.Append( "> **[" ).Append( ( c.Severity ?? "info" ).ToUpperInvariant() ).Append( "] " );
					if( !string.IsNullOrWhiteSpace( c.Title ) )
						sb.Append( c.Title ).Append( ":** " );
					else
						sb.Append( "** " );
					sb.AppendLine( c.Message ?? c.Text ?? string.Empty );
					sb.AppendLine();
					break;

				case "kpi_row":
					foreach( DynamicKpiItem k in c.KpiItems )
					{
						sb.Append( "- **" ).Append( k.Label ).Append( ":** " ).Append( k.Value );
						if( !string.IsNullOrWhiteSpace( k.Subtitle ) )
							sb.Append( " (" ).Append( k.Subtitle ).Append( ')' );
						sb.AppendLine();
					}
					sb.AppendLine();
					break;

				case "key_value":
					if( !string.IsNullOrWhiteSpace( c.Title ) )
						sb.Append( "**" ).Append( c.Title ).AppendLine( "**" );
					foreach( DynamicKeyValueItem kv in c.KeyValueItems )
						sb.Append( "- " ).Append( kv.Key ).Append( ": " ).AppendLine( kv.Value );
					sb.AppendLine();
					break;

				case "table":
					if( !string.IsNullOrWhiteSpace( c.Title ) )
						sb.Append( "**" ).Append( c.Title ).AppendLine( "**" );
					if( c.TableColumns.Count > 0 )
					{
						sb.Append( "| " ).Append( string.Join( " | ", c.TableColumns.Select( col => col.Header ) ) ).AppendLine( " |" );
						sb.Append( "| " ).Append( string.Join( " | ", c.TableColumns.Select( _ => "---" ) ) ).AppendLine( " |" );
						foreach( Dictionary<string, string> row in c.TableRows )
						{
							sb.Append( "| " )
								.Append( string.Join( " | ", c.TableColumns.Select( col => row.GetValueOrDefault( col.Key, "" ).Replace( "|", "\\|" ) ) ) )
								.AppendLine( " |" );
						}
						sb.AppendLine();
					}
					break;

				case "tree":
					if( !string.IsNullOrWhiteSpace( c.Title ) )
						sb.Append( "**" ).Append( c.Title ).AppendLine( "**" );
					AppendTreeSummary( sb, c.TreeNodes, 0 );
					sb.AppendLine();
					break;

				case "list":
					if( !string.IsNullOrWhiteSpace( c.Title ) )
						sb.Append( "**" ).Append( c.Title ).AppendLine( "**" );
					foreach( DynamicListItem item in c.ListItems )
					{
						sb.Append( "- " ).Append( item.Title );
						if( !string.IsNullOrWhiteSpace( item.Value ) )
							sb.Append( " — " ).Append( item.Value );
						if( !string.IsNullOrWhiteSpace( item.Subtitle ) )
							sb.Append( " (" ).Append( item.Subtitle ).Append( ')' );
						sb.AppendLine();
					}
					sb.AppendLine();
					break;

				case "badge_row":
					sb.AppendLine( string.Join( " · ", c.Badges.Select( b => $"[{b.Label}]" ) ) );
					break;

				case "progress":
					sb.Append( "- " ).Append( c.Label ?? "Progress" ).Append( ": " );
					sb.AppendLine( c.Indeterminate
						? ( c.StatusText ?? "In progress..." )
						: $"{c.ProgressValue:0.##}/{c.ProgressMax:0.##}" + ( string.IsNullOrWhiteSpace( c.StatusText ) ? "" : $" ({c.StatusText})" ) );
					break;

				case "code":
					if( !string.IsNullOrWhiteSpace( c.Title ) )
						sb.Append( "**" ).Append( c.Title ).AppendLine( "**" );
					sb.Append( "```" ).AppendLine( c.Language ?? string.Empty );
					sb.AppendLine( c.Content ?? c.Text ?? string.Empty );
					sb.AppendLine( "```" );
					sb.AppendLine();
					break;

				case "divider":
					sb.AppendLine( "---" );
					break;
			}
		}
	}

	private static void AppendTreeSummary( StringBuilder sb, IReadOnlyList<DynamicTreeNode> nodes, int depth )
	{
		string pad = new( ' ', depth * 2 );
		foreach( DynamicTreeNode n in nodes )
		{
			sb.Append( pad ).Append( "- " ).Append( n.Label );
			if( !string.IsNullOrWhiteSpace( n.Badge ) )
				sb.Append( " [" ).Append( n.Badge ).Append( ']' );
			if( !string.IsNullOrWhiteSpace( n.Subtitle ) )
				sb.Append( " — " ).Append( n.Subtitle );
			sb.AppendLine();
			if( n.Children.Count > 0 )
				AppendTreeSummary( sb, n.Children, depth + 1 );
		}
	}

	private static List<DynamicComponentSpec> ParseComponentList( JsonElement arr, int depth, ref int counter )
	{
		if( depth > MaxComponentDepth )
			throw new ArgumentException( $"Dynamic window component tree exceeds maximum nesting depth of {MaxComponentDepth}." );

		List<DynamicComponentSpec> list = [];
		foreach( JsonElement item in arr.EnumerateArray() )
		{
			if( item.ValueKind == JsonValueKind.String )
			{
				counter++;
				EnsureMaxComponents( counter );
				list.Add( new DynamicComponentSpec { Type = "text", Text = item.GetString() ?? string.Empty } );
				continue;
			}
			if( item.ValueKind != JsonValueKind.Object )
				throw new ArgumentException( "Each entry in 'components' must be a JSON object with a 'type' property." );

			list.Add( ParseComponent( item, depth, ref counter ) );
		}
		return list;
	}

	private static DynamicComponentSpec ParseComponent( JsonElement obj, int depth, ref int counter )
	{
		counter++;
		EnsureMaxComponents( counter );

		string? rawType = OptStr( obj, "type" );
		if( string.IsNullOrWhiteSpace( rawType ) )
			throw new ArgumentException( "Every dynamic window component object must specify a 'type'." );

		string normalized = NormalizeComponentType( rawType );
		string rawLower = rawType.Trim().ToLowerInvariant();

		DynamicComponentSpec comp = new()
		{
			Type = normalized,
			Id = OptStr( obj, "id" ),
			Visible = OptBool( obj, "visible" ) ?? true,
			Title = OptStr( obj, "title" ),
			Subtitle = OptStr( obj, "subtitle" ),
			Text = OptStr( obj, "text" ),
			Content = OptStr( obj, "content" ),
			Message = OptStr( obj, "message" ),
			Label = OptStr( obj, "label" ),
			Badge = OptStr( obj, "badge" ),
			Severity = OptStr( obj, "severity" ) is { } s ? NormalizeSeverity( s ) : null,
			AccentColor = OptStr( obj, "accentColor" ),
			Color = OptStr( obj, "color" ),
			Variant = OptStr( obj, "variant" ) ?? ( rawLower == "heading" ? "heading" : null ),
			Bold = OptBool( obj, "bold" ),
			Selectable = OptBool( obj, "selectable" ) ?? true,
			Wrap = OptBool( obj, "wrap" ) ?? true,
			Spacing = Math.Clamp( OptDouble( obj, "spacing" ) ?? 10, 0, 48 ),
			ColumnSpacing = Math.Clamp( OptDouble( obj, "columnSpacing" ) ?? 12, 0, 48 ),
			RowSpacing = Math.Clamp( OptDouble( obj, "rowSpacing" ) ?? 12, 0, 48 ),
			Collapsible = OptBool( obj, "collapsible" ) ?? false,
			Expanded = OptBool( obj, "expanded" ) ?? true,
			SelectedIndex = Math.Max( 0, OptInt( obj, "selectedIndex" ) ?? 0 ),
			Searchable = OptBool( obj, "searchable" ),
			SearchPlaceholder = OptStr( obj, "searchPlaceholder" ),
			Striped = OptBool( obj, "striped" ) ?? true,
			RowSelectable = OptBool( obj, "rowSelectable" ) ?? ( normalized == "table" && ( OptBool( obj, "selectable" ) ?? false ) ),
			EmptyText = OptStr( obj, "emptyText" ),
			MaxHeight = Math.Clamp( OptDouble( obj, "maxHeight" ) ?? 0, 0, 1200 ),
			ProgressValue = OptDouble( obj, "value" ) ?? 0,
			ProgressMax = Math.Max( 0.0001, OptDouble( obj, "max" ) ?? 100 ),
			Indeterminate = OptBool( obj, "indeterminate" ) ?? false,
			StatusText = OptStr( obj, "statusText" ),
			Language = OptStr( obj, "language" ) ?? ( rawLower == "json" ? "json" : null ),
			Copyable = OptBool( obj, "copyable" ) ?? true,
			Base64 = OptStr( obj, "base64" ),
			FilePath = OptStr( obj, "filePath" ) ?? OptStr( obj, "path" ),
			Caption = OptStr( obj, "caption" ),
			Width = OptDouble( obj, "width" ),
			Height = OptDouble( obj, "height" ),
			Placeholder = OptStr( obj, "placeholder" ),
			Value = OptScalarString( obj, "value" ),
			Multiline = OptBool( obj, "multiline" ) ?? false,
			Checked = OptBool( obj, "checked" ) ?? false,
			Selected = OptStr( obj, "selected" ),
			Align = ( OptStr( obj, "align" ) ?? "right" ).Trim().ToLowerInvariant(),
		};

		// Orientation defaults for stack aliases ('row'/'hstack' => horizontal)
		string defaultOrientation = rawLower is "row" or "hstack" ? "horizontal" : "vertical";
		comp.Orientation = ( OptStr( obj, "orientation" ) ?? defaultOrientation ).Trim().ToLowerInvariant() == "horizontal"
			? "horizontal"
			: "vertical";

		// Children for container types
		if( TryGetProp( obj, "children", out JsonElement chEl ) && chEl.ValueKind == JsonValueKind.Array )
			comp.Children = ParseComponentList( chEl, depth + 1, ref counter );
		else if( TryGetProp( obj, "components", out JsonElement cmpEl ) && cmpEl.ValueKind == JsonValueKind.Array )
			comp.Children = ParseComponentList( cmpEl, depth + 1, ref counter );

		// Type-specific structured payloads
		switch( normalized )
		{
			case "grid":
				ParseGridColumns( obj, comp );
				break;

			case "tabs":
				ParseTabs( obj, comp, depth, ref counter );
				break;

			case "kpi_row":
				ParseKpis( obj, comp );
				break;

			case "key_value":
				comp.ColumnCount = Math.Clamp( OptInt( obj, "columns" ) ?? 1, 1, 3 );
				ParseKeyValues( obj, comp );
				break;

			case "table":
				ParseTable( obj, comp );
				break;

			case "tree":
				ParseTree( obj, comp );
				break;

			case "list":
				ParseList( obj, comp );
				break;

			case "badge_row":
				ParseBadges( obj, comp );
				break;

			case "code":
				ParseCodePayload( obj, comp );
				break;

			case "markdown":
				comp.Content ??= OptStr( obj, "markdown" ) ?? comp.Text ?? string.Empty;
				break;

			case "callout":
				comp.Severity ??= "info";
				comp.Message ??= comp.Text ?? comp.Content ?? string.Empty;
				break;

			case "image":
				if( string.IsNullOrWhiteSpace( comp.Base64 ) && string.IsNullOrWhiteSpace( comp.FilePath ) )
					throw new ArgumentException( "Component 'image' requires either 'base64' or 'filePath'." );
				break;

			case "input":
			case "checkbox":
			case "select":
				comp.Id ??= $"{normalized}_{counter}";
				if( normalized == "select" && TryGetProp( obj, "options", out JsonElement optEl ) && optEl.ValueKind == JsonValueKind.Array )
				{
					foreach( JsonElement o in optEl.EnumerateArray() )
						if( ScalarToString( o ) is { Length: > 0 } os )
							comp.Options.Add( os );
				}
				break;

			case "button_row":
				ParseButtons( obj, comp, ref counter );
				break;
		}

		return comp;
	}

	private static void ParseGridColumns( JsonElement obj, DynamicComponentSpec comp )
	{
		if( TryGetProp( obj, "columns", out JsonElement colEl ) )
		{
			if( colEl.ValueKind == JsonValueKind.Number && colEl.TryGetInt32( out int count ) )
			{
				comp.ColumnCount = Math.Clamp( count, 1, 6 );
			}
			else if( colEl.ValueKind == JsonValueKind.Array )
			{
				foreach( JsonElement def in colEl.EnumerateArray() )
				{
					string s = ScalarToString( def );
					if( !string.IsNullOrWhiteSpace( s ) )
						comp.GridColumnDefs.Add( s.Trim() );
				}
				comp.ColumnCount = Math.Clamp( comp.GridColumnDefs.Count, 1, 6 );
			}
		}

		if( comp.ColumnCount <= 0 )
			comp.ColumnCount = Math.Clamp( OptInt( obj, "columnCount" ) ?? 2, 1, 6 );
	}

	private static void ParseTabs( JsonElement obj, DynamicComponentSpec comp, int depth, ref int counter )
	{
		if( !TryGetProp( obj, "tabs", out JsonElement tabsEl ) || tabsEl.ValueKind != JsonValueKind.Array )
			throw new ArgumentException( "Component 'tabs' requires a 'tabs' array of { title, children }." );

		int idx = 0;
		foreach( JsonElement t in tabsEl.EnumerateArray() )
		{
			idx++;
			if( t.ValueKind != JsonValueKind.Object )
				continue;

			string title = OptStr( t, "title" ) ?? OptStr( t, "header" ) ?? $"Tab {idx}";
			string id = OptStr( t, "id" ) ?? $"tab_{idx}";
			string? badge = OptStr( t, "badge" );

			List<DynamicComponentSpec> children = [];
			if( TryGetProp( t, "children", out JsonElement ch ) && ch.ValueKind == JsonValueKind.Array )
				children = ParseComponentList( ch, depth + 1, ref counter );
			else if( TryGetProp( t, "components", out JsonElement cmp ) && cmp.ValueKind == JsonValueKind.Array )
				children = ParseComponentList( cmp, depth + 1, ref counter );

			comp.Tabs.Add( new DynamicTabSpec( id, title, badge, children ) );
		}

		if( comp.Tabs.Count == 0 )
			throw new ArgumentException( "Component 'tabs' requires at least one tab entry." );
	}

	private static void ParseKpis( JsonElement obj, DynamicComponentSpec comp )
	{
		comp.ColumnCount = Math.Clamp( OptInt( obj, "columns" ) ?? 0, 0, 6 );
		JsonElement arr;
		if( !TryGetProp( obj, "items", out arr ) && !TryGetProp( obj, "metrics", out arr ) && !TryGetProp( obj, "kpis", out arr ) )
			return;
		if( arr.ValueKind != JsonValueKind.Array )
			return;

		foreach( JsonElement item in arr.EnumerateArray() )
		{
			if( item.ValueKind != JsonValueKind.Object )
				continue;
			string label = OptStr( item, "label" ) ?? OptStr( item, "title" ) ?? "Metric";
			string val = OptScalarString( item, "value" ) ?? "—";
			comp.KpiItems.Add( new DynamicKpiItem(
				Id: OptStr( item, "id" ),
				Label: label,
				Value: val,
				Subtitle: OptStr( item, "subtitle" ) ?? OptStr( item, "delta" ),
				Badge: OptStr( item, "badge" ),
				Severity: OptStr( item, "severity" ) is { } s ? NormalizeSeverity( s ) : null,
				AccentColor: OptStr( item, "accentColor" ) ) );
		}
	}

	private static void ParseKeyValues( JsonElement obj, DynamicComponentSpec comp )
	{
		if( TryGetProp( obj, "items", out JsonElement itemsEl ) || TryGetProp( obj, "properties", out itemsEl ) || TryGetProp( obj, "entries", out itemsEl ) )
		{
			if( itemsEl.ValueKind == JsonValueKind.Array )
			{
				foreach( JsonElement item in itemsEl.EnumerateArray() )
				{
					if( item.ValueKind != JsonValueKind.Object )
						continue;
					string key = OptStr( item, "key" ) ?? OptStr( item, "label" ) ?? OptStr( item, "name" ) ?? "";
					string val = OptScalarString( item, "value" ) ?? "";
					comp.KeyValueItems.Add( new DynamicKeyValueItem(
						Key: key,
						Value: val,
						Badge: OptStr( item, "badge" ),
						Monospace: OptBool( item, "monospace" ) ?? false,
						Severity: OptStr( item, "severity" ) is { } s ? NormalizeSeverity( s ) : null,
						AccentColor: OptStr( item, "accentColor" ) ) );
				}
			}
			else if( itemsEl.ValueKind == JsonValueKind.Object )
			{
				foreach( JsonProperty prop in itemsEl.EnumerateObject() )
					comp.KeyValueItems.Add( new DynamicKeyValueItem( prop.Name, ScalarToString( prop.Value ), null, false, null, null ) );
			}
		}
	}

	private static void ParseTable( JsonElement obj, DynamicComponentSpec comp )
	{
		if( TryGetProp( obj, "columns", out JsonElement colsEl ) && colsEl.ValueKind == JsonValueKind.Array )
		{
			int idx = 0;
			foreach( JsonElement c in colsEl.EnumerateArray() )
			{
				idx++;
				if( c.ValueKind == JsonValueKind.String )
				{
					string name = c.GetString() ?? $"col_{idx}";
					comp.TableColumns.Add( new DynamicTableColumn( name, name, null, "left", false, false ) );
				}
				else if( c.ValueKind == JsonValueKind.Object )
				{
					string key = OptStr( c, "key" ) ?? OptStr( c, "field" ) ?? OptStr( c, "header" ) ?? $"col_{idx}";
					string header = OptStr( c, "header" ) ?? OptStr( c, "title" ) ?? key;
					string align = ( OptStr( c, "align" ) ?? "left" ).Trim().ToLowerInvariant();
					if( align is not ( "left" or "center" or "right" ) )
						align = "left";
					comp.TableColumns.Add( new DynamicTableColumn(
						Key: key,
						Header: header,
						Width: OptScalarString( c, "width" ),
						Align: align,
						Monospace: OptBool( c, "monospace" ) ?? false,
						IsBadge: OptBool( c, "isBadge" ) ?? OptBool( c, "badge" ) ?? false ) );
				}
			}
		}

		if( TryGetProp( obj, "rows", out JsonElement rowsEl ) && rowsEl.ValueKind == JsonValueKind.Array )
		{
			comp.TableRows = ParseTableRows( rowsEl, comp.TableColumns );
		}

		// Auto-infer columns from first row if omitted
		if( comp.TableColumns.Count == 0 && comp.TableRows.Count > 0 )
		{
			foreach( string key in comp.TableRows[ 0 ].Keys )
				comp.TableColumns.Add( new DynamicTableColumn( key, key, null, "left", false, false ) );
		}
	}

	internal static List<Dictionary<string, string>> ParseTableRows( JsonElement rowsEl, IReadOnlyList<DynamicTableColumn> columns )
	{
		List<Dictionary<string, string>> rows = [];
		foreach( JsonElement r in rowsEl.EnumerateArray() )
		{
			Dictionary<string, string> row = new( StringComparer.OrdinalIgnoreCase );
			if( r.ValueKind == JsonValueKind.Object )
			{
				foreach( JsonProperty p in r.EnumerateObject() )
					row[ p.Name ] = ScalarToString( p.Value );
			}
			else if( r.ValueKind == JsonValueKind.Array )
			{
				int i = 0;
				foreach( JsonElement cell in r.EnumerateArray() )
				{
					string colKey = i < columns.Count ? columns[ i ].Key : $"col_{i + 1}";
					row[ colKey ] = ScalarToString( cell );
					i++;
				}
			}
			rows.Add( row );
		}
		return rows;
	}

	private static void ParseTree( JsonElement obj, DynamicComponentSpec comp )
	{
		JsonElement nodesEl;
		if( !TryGetProp( obj, "nodes", out nodesEl ) && !TryGetProp( obj, "items", out nodesEl ) && !TryGetProp( obj, "roots", out nodesEl ) )
			return;
		if( nodesEl.ValueKind != JsonValueKind.Array )
			return;

		int nodeSeq = 0;
		comp.TreeNodes = ParseTreeNodes( nodesEl, 0, ref nodeSeq );
	}

	private static List<DynamicTreeNode> ParseTreeNodes( JsonElement arr, int depth, ref int seq )
	{
		if( depth > MaxComponentDepth )
			return [];
		List<DynamicTreeNode> list = [];
		foreach( JsonElement item in arr.EnumerateArray() )
		{
			seq++;
			if( item.ValueKind == JsonValueKind.String )
			{
				list.Add( new DynamicTreeNode( $"node_{seq}", item.GetString() ?? "", null, null, null, true, [] ) );
				continue;
			}
			if( item.ValueKind != JsonValueKind.Object )
				continue;

			string id = OptStr( item, "id" ) ?? $"node_{seq}";
			string label = OptStr( item, "label" ) ?? OptStr( item, "name" ) ?? OptStr( item, "title" ) ?? $"Item {seq}";
			List<DynamicTreeNode> children = TryGetProp( item, "children", out JsonElement ch ) && ch.ValueKind == JsonValueKind.Array
				? ParseTreeNodes( ch, depth + 1, ref seq )
				: [];

			list.Add( new DynamicTreeNode(
				Id: id,
				Label: label,
				Subtitle: OptStr( item, "subtitle" ) ?? OptStr( item, "detail" ),
				Badge: OptStr( item, "badge" ),
				Severity: OptStr( item, "severity" ) is { } s ? NormalizeSeverity( s ) : null,
				Expanded: OptBool( item, "expanded" ) ?? ( depth < 2 ),
				Children: children ) );
		}
		return list;
	}

	private static void ParseList( JsonElement obj, DynamicComponentSpec comp )
	{
		if( !TryGetProp( obj, "items", out JsonElement arr ) || arr.ValueKind != JsonValueKind.Array )
			return;

		foreach( JsonElement item in arr.EnumerateArray() )
		{
			if( item.ValueKind == JsonValueKind.String )
			{
				comp.ListItems.Add( new DynamicListItem( null, item.GetString() ?? "", null, null, null, null, null ) );
				continue;
			}
			if( item.ValueKind != JsonValueKind.Object )
				continue;

			comp.ListItems.Add( new DynamicListItem(
				Id: OptStr( item, "id" ),
				Title: OptStr( item, "title" ) ?? OptStr( item, "label" ) ?? OptStr( item, "text" ) ?? "",
				Subtitle: OptStr( item, "subtitle" ) ?? OptStr( item, "description" ),
				Value: OptScalarString( item, "value" ),
				Badge: OptStr( item, "badge" ),
				Severity: OptStr( item, "severity" ) is { } s ? NormalizeSeverity( s ) : null,
				AccentColor: OptStr( item, "accentColor" ) ) );
		}
	}

	private static void ParseBadges( JsonElement obj, DynamicComponentSpec comp )
	{
		JsonElement arr;
		if( !TryGetProp( obj, "items", out arr ) && !TryGetProp( obj, "badges", out arr ) && !TryGetProp( obj, "tags", out arr ) )
			return;
		if( arr.ValueKind != JsonValueKind.Array )
			return;

		foreach( JsonElement item in arr.EnumerateArray() )
		{
			if( item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } str )
			{
				comp.Badges.Add( new DynamicBadgeItem( str, null, null ) );
			}
			else if( item.ValueKind == JsonValueKind.Object )
			{
				string label = OptStr( item, "label" ) ?? OptStr( item, "text" ) ?? "";
				if( !string.IsNullOrWhiteSpace( label ) )
				{
					comp.Badges.Add( new DynamicBadgeItem(
						label,
						OptStr( item, "severity" ) is { } s ? NormalizeSeverity( s ) : null,
						OptStr( item, "accentColor" ) ) );
				}
			}
		}
	}

	private static void ParseCodePayload( JsonElement obj, DynamicComponentSpec comp )
	{
		if( TryGetProp( obj, "content", out JsonElement cEl )
			|| TryGetProp( obj, "code", out cEl )
			|| TryGetProp( obj, "json", out cEl )
			|| TryGetProp( obj, "data", out cEl ) )
		{
			if( cEl.ValueKind == JsonValueKind.String )
				comp.Content = cEl.GetString() ?? string.Empty;
			else if( cEl.ValueKind is JsonValueKind.Object or JsonValueKind.Array )
				comp.Content = JsonSerializer.Serialize( cEl, new JsonSerializerOptions { WriteIndented = true } );
			else
				comp.Content = ScalarToString( cEl );
		}
		else
		{
			comp.Content = comp.Text ?? string.Empty;
		}
	}

	private static void ParseButtons( JsonElement obj, DynamicComponentSpec comp, ref int counter )
	{
		JsonElement arr;
		if( !TryGetProp( obj, "buttons", out arr ) && !TryGetProp( obj, "actions", out arr ) )
			return;
		if( arr.ValueKind != JsonValueKind.Array )
			return;

		int idx = 0;
		foreach( JsonElement item in arr.EnumerateArray() )
		{
			idx++;
			if( item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } label )
			{
				comp.Buttons.Add( new DynamicButtonSpec(
					Id: Slugify( label, $"btn_{counter}_{idx}" ),
					Label: label,
					IsAccent: idx == 1,
					Severity: null,
					Action: "emit",
					CopyText: null,
					NavigatePage: null,
					SettingsSection: null ) );
			}
			else if( item.ValueKind == JsonValueKind.Object )
			{
				string lbl = OptStr( item, "label" ) ?? $"Action {idx}";
				string id = OptStr( item, "id" ) ?? Slugify( lbl, $"btn_{counter}_{idx}" );
				string action = ( OptStr( item, "action" ) ?? "emit" ).Trim().ToLowerInvariant();
				if( action is not ( "emit" or "copy" or "close" or "navigate" ) )
					action = "emit";

				comp.Buttons.Add( new DynamicButtonSpec(
					Id: id,
					Label: lbl,
					IsAccent: OptBool( item, "accent" ) ?? ( idx == 1 && action != "close" ),
					Severity: OptStr( item, "severity" ) is { } s ? NormalizeSeverity( s ) : null,
					Action: action,
					CopyText: OptStr( item, "copyText" ),
					NavigatePage: OptStr( item, "navigatePage" ) ?? OptStr( item, "page" ),
					SettingsSection: OptStr( item, "settingsSection" ) ) );
			}
		}
	}

	private static List<DynamicWindowPatch> ParsePatches( JsonElement arr )
	{
		List<DynamicWindowPatch> list = [];
		foreach( JsonElement item in arr.EnumerateArray() )
		{
			if( item.ValueKind != JsonValueKind.Object )
				continue;
			string id = OptStr( item, "id" ) ?? string.Empty;
			if( string.IsNullOrWhiteSpace( id ) )
				throw new ArgumentException( "Each patch object in 'patches' requires a non-empty 'id'." );

			List<Dictionary<string, string>>? rows = TryGetProp( item, "rows", out JsonElement rEl ) && rEl.ValueKind == JsonValueKind.Array
				? ParseTableRows( rEl, [] )
				: null;

			list.Add( new DynamicWindowPatch
			{
				Id = id.Trim(),
				Text = OptStr( item, "text" ),
				Title = OptStr( item, "title" ),
				Subtitle = OptStr( item, "subtitle" ),
				Message = OptStr( item, "message" ),
				Value = OptScalarString( item, "value" ),
				NumericValue = OptDouble( item, "numericValue" ) ?? ( TryGetProp( item, "value", out JsonElement vEl ) && vEl.ValueKind == JsonValueKind.Number ? vEl.GetDouble() : null ),
				Max = OptDouble( item, "max" ),
				Indeterminate = OptBool( item, "indeterminate" ),
				StatusText = OptStr( item, "statusText" ),
				Severity = OptStr( item, "severity" ) is { } s ? NormalizeSeverity( s ) : null,
				AccentColor = OptStr( item, "accentColor" ),
				Badge = OptStr( item, "badge" ),
				Visible = OptBool( item, "visible" ),
				Checked = OptBool( item, "checked" ),
				Selected = OptStr( item, "selected" ),
				Rows = rows,
			} );
		}
		return list;
	}

	private static void EnsureMaxComponents( int count )
	{
		if( count > MaxComponentCount )
			throw new ArgumentException( $"Dynamic window exceeds maximum of {MaxComponentCount} components." );
	}

	private static string SanitizeWindowId( string raw )
	{
		string cleaned = new( raw.Trim().Select( ch => char.IsLetterOrDigit( ch ) || ch is '-' or '_' or '.' ? ch : '-' ).ToArray() );
		return string.IsNullOrWhiteSpace( cleaned ) ? $"dyn-{Guid.NewGuid():N}"[ ..12 ] : cleaned;
	}

	private static string Slugify( string label, string fallback )
	{
		string s = new( label.ToLowerInvariant().Select( ch => char.IsLetterOrDigit( ch ) ? ch : '_' ).ToArray() );
		s = s.Trim( '_' );
		return string.IsNullOrWhiteSpace( s ) ? fallback : s;
	}

	private static bool TryGetProp( JsonElement obj, string name, out JsonElement value )
	{
		if( obj.TryGetProperty( name, out value ) )
			return true;
		foreach( JsonProperty p in obj.EnumerateObject() )
		{
			if( string.Equals( p.Name, name, StringComparison.OrdinalIgnoreCase ) )
			{
				value = p.Value;
				return true;
			}
		}
		value = default;
		return false;
	}

	private static string? OptStr( JsonElement obj, string name )
		=> TryGetProp( obj, name, out JsonElement el ) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

	private static string? OptScalarString( JsonElement obj, string name )
		=> TryGetProp( obj, name, out JsonElement el ) && el.ValueKind != JsonValueKind.Null ? ScalarToString( el ) : null;

	private static int? OptInt( JsonElement obj, string name )
	{
		if( !TryGetProp( obj, name, out JsonElement el ) )
			return null;
		if( el.ValueKind == JsonValueKind.Number && el.TryGetInt32( out int i ) )
			return i;
		if( el.ValueKind == JsonValueKind.String && int.TryParse( el.GetString(), out int p ) )
			return p;
		return null;
	}

	private static double? OptDouble( JsonElement obj, string name )
	{
		if( !TryGetProp( obj, name, out JsonElement el ) )
			return null;
		if( el.ValueKind == JsonValueKind.Number && el.TryGetDouble( out double d ) )
			return d;
		if( el.ValueKind == JsonValueKind.String && double.TryParse( el.GetString(), out double p ) )
			return p;
		return null;
	}

	private static bool? OptBool( JsonElement obj, string name )
	{
		if( !TryGetProp( obj, name, out JsonElement el ) )
			return null;
		return el.ValueKind switch
		{
			JsonValueKind.True => true,
			JsonValueKind.False => false,
			JsonValueKind.String when bool.TryParse( el.GetString(), out bool b ) => b,
			_ => null,
		};
	}

	private static string ScalarToString( JsonElement el ) => el.ValueKind switch
	{
		JsonValueKind.String => el.GetString() ?? string.Empty,
		JsonValueKind.Number => el.GetRawText(),
		JsonValueKind.True => "true",
		JsonValueKind.False => "false",
		JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
		_ => el.GetRawText(),
	};
}

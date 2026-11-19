using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using HackPDM.Core.Graph;
using HackPDM.Core.Theming;
using HackPDM.Mcp.Protocol;
using HackPDM.Mcp.State;
using HackPDM.Mcp.Tools;

namespace HackPDM.Mcp.Services;

public sealed partial class HackPdmApiService
{
	private static readonly JsonSerializerOptions ThemeConfigJsonOptions = new()
	{
		WriteIndented = true,
		PropertyNameCaseInsensitive = true,
	};

	/// <summary>
	/// Headless (file-backed) implementation of <c>ui_theme_control</c> operating directly on
	/// <c>AppConfig.json</c> when no live HackPDM desktop GUI is attached. Every independent color,
	/// style, graph, and custom-theme override is persisted so HackPDM loads it on next launch.
	/// </summary>
	public async Task<object> ThemeControlHeadlessAsync( JsonElement? rawArgs, CancellationToken cancellationToken = default )
	{
		McpToolArgs args = new( rawArgs );
		string path = AppConfigFilePath;
		JsonObject root = LoadOrCreateAppConfigObject( path );
		HeadlessThemeDoc theme = DeserializeThemeSection( root );

		bool mutated = false;

		if( args.GetBool( "resetToDefaults", false ) )
		{
			bool clearCustom = args.GetBool( "clearCustomThemes", false );
			theme.Mode = "Dark";
			theme.Palette = PaletteName.Dark;
			theme.Form = FormName.Default;
			theme.ShowTreeBranchLines = true;
			theme.Dark.Clear();
			theme.Light.Clear();
			theme.Style.Clear();
			theme.Graph.Clear();
			theme.ActiveCustomTheme = null;
			if( clearCustom )
				theme.CustomThemes.Clear();
			mutated = true;
		}

		if( args.GetString( "loadCustomThemeName" ) is { Length: > 0 } loadName )
		{
			if( !theme.CustomThemes.TryGetValue( loadName.Trim(), out SavedThemePreset? preset ) )
				throw new KeyNotFoundException( $"Custom theme '{loadName}' not found in AppConfig.json." );
			theme.Mode = preset.Mode;
			theme.Palette = preset.Palette;
			theme.Form = preset.Form;
			theme.ShowTreeBranchLines = preset.ShowTreeBranchLines;
			theme.Dark = new Dictionary<string, string>( preset.Dark ?? [] );
			theme.Light = new Dictionary<string, string>( preset.Light ?? [] );
			theme.Style = ( preset.Style ?? new() ).DeepClone();
			theme.Graph = ( preset.Graph ?? new() ).DeepClone();
			theme.ActiveCustomTheme = preset.Name;
			mutated = true;
		}

		if( args.GetString( "palette" ) is { Length: > 0 } paletteName )
		{
			if( !Enum.TryParse<PaletteName>( paletteName, ignoreCase: true, out PaletteName pal ) )
				throw new ArgumentException( $"Unknown palette '{paletteName}'. Valid: {string.Join( ", ", Enum.GetNames<PaletteName>() )}" );
			theme.Palette = pal;
			theme.Mode = ThemePresets.Palette( pal ).IsDark ? "Dark" : "Light";
			theme.ActiveCustomTheme = null;
			mutated = true;
		}

		if( args.GetString( "mode" ) is { Length: > 0 } modeStr )
		{
			if( modeStr.Trim().ToLowerInvariant() is not ( "dark" or "light" or "system" ) )
				throw new ArgumentException( $"Unknown theme mode '{modeStr}'. Expected 'Dark', 'Light', or 'System'." );
			theme.Mode = char.ToUpperInvariant( modeStr.Trim()[ 0 ] ) + modeStr.Trim()[ 1.. ].ToLowerInvariant();
			theme.ActiveCustomTheme = null;
			mutated = true;
		}

		if( args.GetString( "form" ) is { Length: > 0 } formName )
		{
			if( !Enum.TryParse<FormName>( formName, ignoreCase: true, out FormName fm ) )
				throw new ArgumentException( $"Unknown form '{formName}'. Valid: {string.Join( ", ", Enum.GetNames<FormName>() )}" );
			theme.Form = fm;
			theme.ActiveCustomTheme = null;
			mutated = true;
		}

		if( args.GetNullableBool( "showTreeBranchLines" ) is bool branchLines )
		{
			theme.ShowTreeBranchLines = branchLines;
			mutated = true;
		}

		string targetTheme = args.GetString( "targetTheme", "active" )!;

		if( args.GetBool( "resetAll", false ) )
		{
			ClearColorTarget( theme, targetTheme );
			mutated = true;
		}

		if( args.GetString( "resetColorKey" ) is { Length: > 0 } resetKey )
		{
			RemoveColorKey( theme, resetKey, targetTheme );
			mutated = true;
		}

		if( args.GetString( "setColorKey" ) is { Length: > 0 } singleKey )
		{
			string hex = args.RequireString( "setColorHex" );
			if( !ThemeColorParse.TryParseHex( hex, out uint argb ) )
				throw new ArgumentException( $"Invalid hex color '{hex}'. Expected '#AARRGGBB' or '#RRGGBB'." );
			SetColorKey( theme, singleKey, ThemeColorParse.FormatHex( argb ), targetTheme );
			mutated = true;
		}

		if( args.GetElement( "colors" ) is { ValueKind: JsonValueKind.Object } colorMap )
		{
			foreach( JsonProperty prop in colorMap.EnumerateObject() )
			{
				if( prop.Value.ValueKind is JsonValueKind.Null )
				{
					RemoveColorKey( theme, prop.Name, targetTheme );
					mutated = true;
				}
				else if( prop.Value.ValueKind == JsonValueKind.String )
				{
					string hex = prop.Value.GetString()!;
					if( !ThemeColorParse.TryParseHex( hex, out uint argb ) )
						throw new ArgumentException( $"Invalid hex color '{hex}' for key '{prop.Name}'." );
					SetColorKey( theme, prop.Name, ThemeColorParse.FormatHex( argb ), targetTheme );
					mutated = true;
				}
			}
		}

		if( args.GetStringList( "removeColors" ) is { Count: > 0 } removeKeys )
		{
			foreach( string k in removeKeys )
				RemoveColorKey( theme, k, targetTheme );
			mutated = true;
		}

		if( args.GetBool( "resetStyle", false ) )
		{
			theme.Style.Clear();
			mutated = true;
		}

		if( args.GetElement( "style" ) is { ValueKind: JsonValueKind.Object } styleObj )
		{
			ApplyStylePatch( theme.Style, styleObj );
			theme.ActiveCustomTheme = null;
			mutated = true;
		}

		if( args.GetBool( "resetGraph", false ) )
		{
			theme.Graph.Clear();
			mutated = true;
		}

		if( args.GetElement( "graph" ) is { ValueKind: JsonValueKind.Object } graphObj )
		{
			ApplyGraphPatch( theme.Graph, graphObj );
			theme.ActiveCustomTheme = null;
			mutated = true;
		}

		if( args.GetString( "deleteCustomThemeName" ) is { Length: > 0 } delName )
		{
			theme.CustomThemes.Remove( delName.Trim() );
			if( string.Equals( theme.ActiveCustomTheme, delName.Trim(), StringComparison.OrdinalIgnoreCase ) )
				theme.ActiveCustomTheme = null;
			mutated = true;
		}

		if( args.GetString( "saveCustomThemeName" ) is { Length: > 0 } saveName )
		{
			string trimmed = saveName.Trim();
			theme.CustomThemes[ trimmed ] = new SavedThemePreset
			{
				Name = trimmed,
				Description = args.GetString( "customThemeDescription" )?.Trim() ?? string.Empty,
				Mode = theme.Mode,
				Palette = theme.EffectivePalette,
				Form = theme.Form,
				ShowTreeBranchLines = theme.ShowTreeBranchLines,
				Dark = new Dictionary<string, string>( theme.Dark ),
				Light = new Dictionary<string, string>( theme.Light ),
				Style = theme.Style.DeepClone(),
				Graph = theme.Graph.DeepClone(),
			};
			theme.ActiveCustomTheme = trimmed;
			mutated = true;
		}

		bool shouldSave = args.GetBool( "save", mutated );
		string? restorePointId = null;
		if( mutated && shouldSave )
		{
			McpRestorePoint? rp = RestorePoints is null ? null
				: await RestorePoints.CapturePreMutationAsync( "ui_theme_control", "Update Theme in AppConfig.json (headless)", [], includeAppConfig: true, cancellationToken: cancellationToken );
			restorePointId = rp?.Id;

			root[ "Theme" ] = JsonSerializer.SerializeToNode( theme, ThemeConfigJsonOptions );
			await WriteAppConfigObjectAsync( path, root, cancellationToken );
		}

		AppForm effForm = theme.Style.ApplyTo( ThemePresets.Form( theme.Form ) );
		GraphTheme effGraph = theme.Graph.ApplyTo( GraphThemes.From( theme.EffectivePalette, theme.Form ) );

		return new
		{
			source = "headless_config",
			saved = mutated && shouldSave,
			restorePointId,
			savePath = path,
			mode = theme.Mode,
			palette = theme.EffectivePalette.ToString(),
			form = theme.Form.ToString(),
			showTreeBranchLines = theme.ShowTreeBranchLines,
			activeCustomTheme = theme.ActiveCustomTheme,
			darkOverrides = theme.Dark,
			lightOverrides = theme.Light,
			styleOverrides = theme.Style,
			effectiveStyle = new
			{
				effForm.CompactDensity,
				effForm.ControlCornerRadius,
				effForm.OverlayCornerRadius,
				effForm.PanelCornerRadius,
				effForm.PanelBorderThickness,
				effForm.AccentPanels,
				panelStrokeColor = ThemeColorParse.FormatHex( theme.Style.ResolvePanelStroke( ThemePresets.Palette( theme.EffectivePalette ), effForm ) ),
				fontFamily = theme.Style.FontFamily,
				baseFontSize = theme.Style.BaseFontSize,
				contentPadding = theme.Style.ContentPadding,
			},
			graphOverrides = theme.Graph,
			effectiveGraph = SummarizeGraphTheme( effGraph ),
			customThemes = theme.CustomThemes.Values.Select( t => new { t.Name, t.Description, t.Mode, palette = t.Palette.ToString(), form = t.Form.ToString() } ).ToList(),
			availablePalettes = Enum.GetNames<PaletteName>(),
			availableForms = Enum.GetNames<FormName>(),
		};
	}

	/// <summary>
	/// Headless (file-backed) implementation of <c>ui_customize_config</c> allowing independent edits or
	/// factory resets of <c>OdooEntryColors</c>, <c>ColumnSettings</c>, <c>WorkspaceLayout</c>,
	/// <c>WindowSettings</c>, and <c>Theme</c> in <c>AppConfig.json</c>.
	/// </summary>
	public async Task<object> CustomizeConfigHeadlessAsync( JsonElement? rawArgs, CancellationToken cancellationToken = default )
	{
		McpToolArgs args = new( rawArgs );
		string path = AppConfigFilePath;
		JsonObject root = LoadOrCreateAppConfigObject( path );
		bool mutated = false;

		if( args.GetString( "resetSection" ) is { Length: > 0 } section )
		{
			ApplySectionResetToJson( root, section );
			mutated = true;
		}

		if( args.GetElement( "entryColors" ) is { ValueKind: JsonValueKind.Object } entryColors )
		{
			JsonObject ec = EnsureChildObject( root, "OdooEntryColors" );
			foreach( JsonProperty prop in entryColors.EnumerateObject() )
			{
				if( prop.Value.ValueKind == JsonValueKind.String )
				{
					string hex = prop.Value.GetString()!;
					if( !ThemeColorParse.TryParseHex( hex, out uint argb ) )
						throw new ArgumentException( $"Invalid hex color '{hex}' for entryColors.{prop.Name}." );
					ec[ prop.Name ] = ThemeColorParse.FormatHex( argb );
					mutated = true;
				}
			}
		}

		if( args.GetElement( "columns" ) is { ValueKind: JsonValueKind.Object } cols )
		{
			JsonObject cs = EnsureChildObject( root, "ColumnSettings" );
			CopyPrimitiveIfPresent( cols, cs, "rowHeight", "RowHeight" );
			CopyPrimitiveIfPresent( cols, cs, "fontSize", "FontSize" );
			CopyPrimitiveIfPresent( cols, cs, "groupingEnabled", "GroupingEnabled" );
			CopyPrimitiveIfPresent( cols, cs, "groupCollapsed", "GroupCollapsed" );
			if( cols.TryGetProperty( "groupByField", out JsonElement gbf ) && gbf.ValueKind == JsonValueKind.String )
			{
				JsonObject gb = EnsureChildObject( cs, "GroupBy" );
				gb[ "FieldName" ] = gbf.GetString();
			}
			if( cols.TryGetProperty( "columns", out JsonElement perCol ) && perCol.ValueKind == JsonValueKind.Object )
			{
				JsonObject colMap = EnsureChildObject( cs, "Columns" );
				foreach( JsonProperty p in perCol.EnumerateObject() )
				{
					if( p.Value.ValueKind != JsonValueKind.Object )
						continue;
					JsonObject entry = EnsureChildObject( colMap, p.Name );
					entry[ "BindingPath" ] ??= p.Name;
					JsonObject colObj = EnsureChildObject( entry, "Column" );
					CopyPrimitiveIfPresent( p.Value, colObj, "displayIndex", "DisplayIndex" );
					CopyPrimitiveIfPresent( p.Value, colObj, "header", "Header" );
					CopyPrimitiveIfPresent( p.Value, colObj, "width", "Width" );
					CopyPrimitiveIfPresent( p.Value, colObj, "autoSize", "AutoSize" );
				}
			}
			mutated = true;
		}

		if( args.GetElement( "workspaceLayout" ) is { ValueKind: JsonValueKind.Object } layout )
		{
			JsonObject wl = EnsureChildObject( root, "WorkspaceLayout" );
			foreach( JsonProperty p in layout.EnumerateObject() )
			{
				string pascal = char.ToUpperInvariant( p.Name[ 0 ] ) + p.Name[ 1.. ];
				wl[ pascal ] = JsonNode.Parse( p.Value.GetRawText() );
			}
			mutated = true;
		}

		if( args.GetElement( "windowSettings" ) is { ValueKind: JsonValueKind.Object } winSettings )
		{
			JsonObject ws = EnsureChildObject( root, "WindowSettings" );
			JsonObject windows = EnsureChildObject( ws, "Windows" );
			foreach( JsonProperty p in winSettings.EnumerateObject() )
			{
				if( p.Value.ValueKind != JsonValueKind.Object )
					continue;
				JsonObject win = EnsureChildObject( windows, p.Name );
				CopyPrimitiveIfPresent( p.Value, win, "title", "Title" );
				if( p.Value.TryGetProperty( "x", out _) || p.Value.TryGetProperty( "y", out _ )
					|| p.Value.TryGetProperty( "width", out _ ) || p.Value.TryGetProperty( "height", out _ ) )
				{
					int[] cur = [ 0, 0, 1280, 820 ];
					if( win[ "PositionAndSize" ] is JsonArray existing && existing.Count == 4 )
					{
						for( int i = 0; i < 4; i++ )
							cur[ i ] = existing[ i ]?.GetValue<int>() ?? cur[ i ];
					}
					if( p.Value.TryGetProperty( "x", out JsonElement x ) && x.TryGetInt32( out int xi ) ) cur[ 0 ] = xi;
					if( p.Value.TryGetProperty( "y", out JsonElement y ) && y.TryGetInt32( out int yi ) ) cur[ 1 ] = yi;
					if( p.Value.TryGetProperty( "width", out JsonElement w ) && w.TryGetInt32( out int wi ) ) cur[ 2 ] = wi;
					if( p.Value.TryGetProperty( "height", out JsonElement h ) && h.TryGetInt32( out int hi ) ) cur[ 3 ] = hi;
					win[ "PositionAndSize" ] = new JsonArray( cur[ 0 ], cur[ 1 ], cur[ 2 ], cur[ 3 ] );
				}
			}
			mutated = true;
		}

		bool shouldSave = args.GetBool( "save", mutated );
		string? restorePointId = null;
		if( mutated && shouldSave )
		{
			McpRestorePoint? rp = RestorePoints is null ? null
				: await RestorePoints.CapturePreMutationAsync( "ui_customize_config", "Update UI config in AppConfig.json (headless)", [], includeAppConfig: true, cancellationToken: cancellationToken );
			restorePointId = rp?.Id;
			await WriteAppConfigObjectAsync( path, root, cancellationToken );
		}

		return new
		{
			source = "headless_config",
			saved = mutated && shouldSave,
			restorePointId,
			filePath = path,
			config = JsonDocument.Parse( root.ToJsonString() ).RootElement.Clone(),
		};
	}

	/// <summary>Resets <c>AppConfig.json</c> theme (or all UI sections) to HackPDM factory defaults.</summary>
	public async Task<object> ResetUiDefaultsHeadlessAsync( bool includeFullAppConfig, CancellationToken cancellationToken = default )
	{
		string path = AppConfigFilePath;
		McpRestorePoint? rp = RestorePoints is null ? null
			: await RestorePoints.CapturePreMutationAsync( "reset_ui_defaults", "Reset UI & Theme to Factory Defaults", [], includeAppConfig: true, cancellationToken: cancellationToken );

		JsonObject root = LoadOrCreateAppConfigObject( path );
		ApplySectionResetToJson( root, includeFullAppConfig ? "all" : "theme" );
		await WriteAppConfigObjectAsync( path, root, cancellationToken );

		return new
		{
			ok = true,
			filePath = path,
			resetScope = includeFullAppConfig ? "all" : "theme",
			restorePointId = rp?.Id,
		};
	}

	/// <summary>Shared JSON patch helper applying any independent <see cref="ThemeStyleOverrides"/> fields.</summary>
	public static void ApplyStylePatch( ThemeStyleOverrides target, JsonElement obj )
	{
		if( obj.TryGetProperty( "compactDensity", out JsonElement cd ) )
			target.CompactDensity = cd.ValueKind == JsonValueKind.Null ? null : cd.GetBoolean();
		if( obj.TryGetProperty( "controlCornerRadius", out JsonElement ccr ) )
			target.ControlCornerRadius = ccr.ValueKind == JsonValueKind.Null ? null : ccr.GetDouble();
		if( obj.TryGetProperty( "overlayCornerRadius", out JsonElement ocr ) )
			target.OverlayCornerRadius = ocr.ValueKind == JsonValueKind.Null ? null : ocr.GetDouble();
		if( obj.TryGetProperty( "panelCornerRadius", out JsonElement pcr ) )
			target.PanelCornerRadius = pcr.ValueKind == JsonValueKind.Null ? null : pcr.GetDouble();
		if( obj.TryGetProperty( "panelBorderThickness", out JsonElement pbt ) )
			target.PanelBorderThickness = pbt.ValueKind == JsonValueKind.Null ? null : pbt.GetDouble();
		if( obj.TryGetProperty( "accentPanels", out JsonElement ap ) )
			target.AccentPanels = ap.ValueKind == JsonValueKind.Null ? null : ap.GetBoolean();
		if( obj.TryGetProperty( "panelStrokeColor", out JsonElement psc ) )
		{
			if( psc.ValueKind == JsonValueKind.Null || string.IsNullOrWhiteSpace( psc.GetString() ) )
				target.PanelStrokeColor = null;
			else if( ThemeColorParse.TryParseHex( psc.GetString(), out uint argb ) )
				target.PanelStrokeColor = ThemeColorParse.FormatHex( argb );
			else
				throw new ArgumentException( $"Invalid hex color '{psc.GetString()}' for style.panelStrokeColor." );
		}
		if( obj.TryGetProperty( "fontFamily", out JsonElement ff ) )
			target.FontFamily = ff.ValueKind == JsonValueKind.Null || string.IsNullOrWhiteSpace( ff.GetString() ) ? null : ff.GetString()!.Trim();
		if( obj.TryGetProperty( "baseFontSize", out JsonElement bfs ) )
			target.BaseFontSize = bfs.ValueKind == JsonValueKind.Null ? null : Math.Clamp( bfs.GetDouble(), 8, 32 );
		if( obj.TryGetProperty( "contentPadding", out JsonElement cp ) )
			target.ContentPadding = cp.ValueKind == JsonValueKind.Null ? null : Math.Clamp( cp.GetDouble(), 0, 48 );
	}

	/// <summary>Shared JSON patch helper applying any independent <see cref="GraphThemeOverrides"/> fields.</summary>
	public static void ApplyGraphPatch( GraphThemeOverrides target, JsonElement obj )
	{
		target.Backdrop = PatchHex( obj, "backdrop", target.Backdrop );
		target.GridLine = PatchHex( obj, "gridLine", target.GridLine );
		target.Body = PatchHex( obj, "body", target.Body );
		target.BodyBorder = PatchHex( obj, "bodyBorder", target.BodyBorder );
		target.Text = PatchHex( obj, "text", target.Text );
		target.HeaderText = PatchHex( obj, "headerText", target.HeaderText );
		target.SubText = PatchHex( obj, "subText", target.SubText );
		target.Link = PatchHex( obj, "link", target.Link );
		target.LinkHighlight = PatchHex( obj, "linkHighlight", target.LinkHighlight );
		target.Port = PatchHex( obj, "port", target.Port );
		target.RootBorder = PatchHex( obj, "rootBorder", target.RootBorder );
		target.SelectedBorder = PatchHex( obj, "selectedBorder", target.SelectedBorder );
		target.Shadow = PatchHex( obj, "shadow", target.Shadow );

		if( obj.TryGetProperty( "headers", out JsonElement headersEl ) && headersEl.ValueKind == JsonValueKind.Object )
		{
			target.NodeHeaders ??= new( StringComparer.OrdinalIgnoreCase );
			foreach( JsonProperty p in headersEl.EnumerateObject() )
			{
				if( !Enum.TryParse<NodeKind>( p.Name, ignoreCase: true, out NodeKind kind ) )
					throw new ArgumentException( $"Unknown graph node kind '{p.Name}'. Valid: {string.Join( ", ", Enum.GetNames<NodeKind>() )}" );
				if( p.Value.ValueKind == JsonValueKind.Null || string.IsNullOrWhiteSpace( p.Value.GetString() ) )
					target.NodeHeaders.Remove( kind.ToString() );
				else if( ThemeColorParse.TryParseHex( p.Value.GetString(), out uint argb ) )
					target.NodeHeaders[ kind.ToString() ] = ThemeColorParse.FormatHex( argb );
				else
					throw new ArgumentException( $"Invalid hex color '{p.Value.GetString()}' for graph.headers.{p.Name}." );
			}
		}

		if( obj.TryGetProperty( "headerStyle", out JsonElement hs ) )
		{
			if( hs.ValueKind == JsonValueKind.Null )
				target.Header = null;
			else if( Enum.TryParse<HeaderStyle>( hs.GetString(), ignoreCase: true, out HeaderStyle parsed ) )
				target.Header = parsed;
			else
				throw new ArgumentException( $"Unknown graph.headerStyle '{hs.GetString()}'. Valid: {string.Join( ", ", Enum.GetNames<HeaderStyle>() )}" );
		}

		if( obj.TryGetProperty( "linkStyle", out JsonElement ls ) )
		{
			if( ls.ValueKind == JsonValueKind.Null )
				target.Links = null;
			else if( Enum.TryParse<LinkStyle>( ls.GetString(), ignoreCase: true, out LinkStyle parsed ) )
				target.Links = parsed;
			else
				throw new ArgumentException( $"Unknown graph.linkStyle '{ls.GetString()}'. Valid: {string.Join( ", ", Enum.GetNames<LinkStyle>() )}" );
		}

		target.HeaderHeight = PatchDouble( obj, "headerHeight", target.HeaderHeight );
		target.CornerRadius = PatchDouble( obj, "cornerRadius", target.CornerRadius );
		target.BorderThickness = PatchDouble( obj, "borderThickness", target.BorderThickness );
		target.RootBorderThickness = PatchDouble( obj, "rootBorderThickness", target.RootBorderThickness );
		target.TitleSize = PatchDouble( obj, "titleSize", target.TitleSize );
		target.ShowFolder = PatchBool( obj, "showFolder", target.ShowFolder );
		target.ShowSizeLine = PatchBool( obj, "showSizeLine", target.ShowSizeLine );
		target.PortSize = PatchDouble( obj, "portSize", target.PortSize );
		target.HollowPorts = PatchBool( obj, "hollowPorts", target.HollowPorts );
		target.LinkThickness = PatchDouble( obj, "linkThickness", target.LinkThickness );
		target.ShadowDepth = PatchDouble( obj, "shadowDepth", target.ShadowDepth );
		target.ShowGrid = PatchBool( obj, "showGrid", target.ShowGrid );
		target.ColumnGap = PatchDouble( obj, "columnGap", target.ColumnGap );
		target.RowGap = PatchDouble( obj, "rowGap", target.RowGap );
		target.Margin = PatchDouble( obj, "margin", target.Margin );
		target.MinWidth = PatchDouble( obj, "minWidth", target.MinWidth );
		target.MinHeight = PatchDouble( obj, "minHeight", target.MinHeight );
		target.MaxWidth = PatchDouble( obj, "maxWidth", target.MaxWidth );
		target.MaxHeight = PatchDouble( obj, "maxHeight", target.MaxHeight );
	}

	/// <summary>Produces a human/AI-readable summary of a resolved <see cref="GraphTheme"/>.</summary>
	public static object SummarizeGraphTheme( GraphTheme g ) => new
	{
		palette = new
		{
			backdrop = ThemeColorParse.FormatHex( g.Palette.Backdrop ),
			gridLine = ThemeColorParse.FormatHex( g.Palette.GridLine ),
			body = ThemeColorParse.FormatHex( g.Palette.Body ),
			bodyBorder = ThemeColorParse.FormatHex( g.Palette.BodyBorder ),
			text = ThemeColorParse.FormatHex( g.Palette.Text ),
			headerText = ThemeColorParse.FormatHex( g.Palette.HeaderText ),
			subText = ThemeColorParse.FormatHex( g.Palette.SubText ),
			link = ThemeColorParse.FormatHex( g.Palette.Link ),
			linkHighlight = ThemeColorParse.FormatHex( g.Palette.LinkHighlight ),
			port = ThemeColorParse.FormatHex( g.Palette.Port ),
			rootBorder = ThemeColorParse.FormatHex( g.Palette.RootBorder ),
			selectedBorder = ThemeColorParse.FormatHex( g.Palette.SelectedBorder ),
			shadow = ThemeColorParse.FormatHex( g.Palette.Shadow ),
			headers = g.Palette.Headers.ToDictionary( kv => kv.Key.ToString(), kv => ThemeColorParse.FormatHex( kv.Value ) ),
		},
		form = new
		{
			headerStyle = g.Form.Header.ToString(),
			linkStyle = g.Form.Links.ToString(),
			g.Form.HeaderHeight,
			g.Form.CornerRadius,
			g.Form.BorderThickness,
			g.Form.RootBorderThickness,
			g.Form.TitleSize,
			g.Form.ShowFolder,
			g.Form.ShowSizeLine,
			g.Form.PortSize,
			g.Form.HollowPorts,
			g.Form.LinkThickness,
			g.Form.ShadowDepth,
			g.Form.ShowGrid,
			layout = g.Form.Layout,
		},
	};

	private static string? PatchHex( JsonElement obj, string name, string? current )
	{
		if( !obj.TryGetProperty( name, out JsonElement el ) )
			return current;
		if( el.ValueKind == JsonValueKind.Null || string.IsNullOrWhiteSpace( el.GetString() ) )
			return null;
		if( !ThemeColorParse.TryParseHex( el.GetString(), out uint argb ) )
			throw new ArgumentException( $"Invalid hex color '{el.GetString()}' for graph.{name}." );
		return ThemeColorParse.FormatHex( argb );
	}

	private static double? PatchDouble( JsonElement obj, string name, double? current )
	{
		if( !obj.TryGetProperty( name, out JsonElement el ) )
			return current;
		return el.ValueKind == JsonValueKind.Null ? null : el.GetDouble();
	}

	private static bool? PatchBool( JsonElement obj, string name, bool? current )
	{
		if( !obj.TryGetProperty( name, out JsonElement el ) )
			return current;
		return el.ValueKind == JsonValueKind.Null ? null : el.GetBoolean();
	}

	private static JsonObject LoadOrCreateAppConfigObject( string path )
	{
		if( !File.Exists( path ) )
			return [];
		string text = File.ReadAllText( path, Encoding.UTF8 );
		return JsonNode.Parse( text ) as JsonObject ?? [];
	}

	private static async Task WriteAppConfigObjectAsync( string path, JsonObject root, CancellationToken ct )
	{
		if( Path.GetDirectoryName( path ) is { Length: > 0 } dir )
			Directory.CreateDirectory( dir );
		string json = root.ToJsonString( ThemeConfigJsonOptions );
		await File.WriteAllTextAsync( path, json, Encoding.UTF8, ct );
	}

	private static JsonObject EnsureChildObject( JsonObject parent, string key )
	{
		if( parent[ key ] is JsonObject existing )
			return existing;
		JsonObject created = [];
		parent[ key ] = created;
		return created;
	}

	private static void CopyPrimitiveIfPresent( JsonElement src, JsonObject dst, string srcProp, string dstProp )
	{
		if( src.TryGetProperty( srcProp, out JsonElement el ) && el.ValueKind != JsonValueKind.Undefined )
			dst[ dstProp ] = JsonNode.Parse( el.GetRawText() );
	}

	private static void ApplySectionResetToJson( JsonObject root, string section )
	{
		switch( section.Trim().ToLowerInvariant() )
		{
			case "theme":
				root.Remove( "Theme" );
				break;
			case "entrycolors" or "odooentrycolors":
				root.Remove( "OdooEntryColors" );
				break;
			case "columns" or "columnsettings":
				root.Remove( "ColumnSettings" );
				break;
			case "workspacelayout" or "layout":
				root.Remove( "WorkspaceLayout" );
				break;
			case "windowsettings" or "windows":
				root.Remove( "WindowSettings" );
				break;
			case "all":
				root.Remove( "Theme" );
				root.Remove( "OdooEntryColors" );
				root.Remove( "ColumnSettings" );
				root.Remove( "WorkspaceLayout" );
				root.Remove( "WindowSettings" );
				break;
			default:
				throw new ArgumentException( $"Unknown resetSection '{section}'. Valid: theme, entryColors, columns, workspaceLayout, windowSettings, all." );
		}
	}

	private static HeadlessThemeDoc DeserializeThemeSection( JsonObject root )
	{
		if( root[ "Theme" ] is JsonObject themeObj )
		{
			HeadlessThemeDoc? parsed = themeObj.Deserialize<HeadlessThemeDoc>( ThemeConfigJsonOptions );
			if( parsed is not null )
			{
				parsed.Dark ??= [];
				parsed.Light ??= [];
				parsed.Style ??= new();
				parsed.Graph ??= new();
				parsed.Graph.NodeHeaders ??= new( StringComparer.OrdinalIgnoreCase );
				parsed.CustomThemes = new Dictionary<string, SavedThemePreset>( parsed.CustomThemes ?? [], StringComparer.OrdinalIgnoreCase );
				return parsed;
			}
		}
		return new HeadlessThemeDoc();
	}

	private static void SetColorKey( HeadlessThemeDoc theme, string key, string hex, string targetTheme )
	{
		string t = ( targetTheme ?? "active" ).Trim().ToLowerInvariant();
		bool dark = t is "dark" or "both" || ( t is "active" or "" && !string.Equals( theme.Mode, "Light", StringComparison.OrdinalIgnoreCase ) );
		bool light = t is "light" or "both" || ( t is "active" or "" && string.Equals( theme.Mode, "Light", StringComparison.OrdinalIgnoreCase ) );
		if( dark ) theme.Dark[ key ] = hex;
		if( light ) theme.Light[ key ] = hex;
		theme.ActiveCustomTheme = null;
	}

	private static void RemoveColorKey( HeadlessThemeDoc theme, string key, string targetTheme )
	{
		string t = ( targetTheme ?? "active" ).Trim().ToLowerInvariant();
		bool dark = t is "dark" or "both" || ( t is "active" or "" && !string.Equals( theme.Mode, "Light", StringComparison.OrdinalIgnoreCase ) );
		bool light = t is "light" or "both" || ( t is "active" or "" && string.Equals( theme.Mode, "Light", StringComparison.OrdinalIgnoreCase ) );
		if( dark ) theme.Dark.Remove( key );
		if( light ) theme.Light.Remove( key );
		theme.ActiveCustomTheme = null;
	}

	private static void ClearColorTarget( HeadlessThemeDoc theme, string targetTheme )
	{
		string t = ( targetTheme ?? "active" ).Trim().ToLowerInvariant();
		bool dark = t is "dark" or "both" || ( t is "active" or "" && !string.Equals( theme.Mode, "Light", StringComparison.OrdinalIgnoreCase ) );
		bool light = t is "light" or "both" || ( t is "active" or "" && string.Equals( theme.Mode, "Light", StringComparison.OrdinalIgnoreCase ) );
		if( dark ) theme.Dark.Clear();
		if( light ) theme.Light.Clear();
		theme.ActiveCustomTheme = null;
	}

	private sealed class HeadlessThemeDoc
	{
		public string Mode { get; set; } = "Dark";

		[System.Text.Json.Serialization.JsonConverter( typeof( System.Text.Json.Serialization.JsonStringEnumConverter<PaletteName> ) )]
		public PaletteName? Palette { get; set; } = PaletteName.Dark;

		[System.Text.Json.Serialization.JsonConverter( typeof( System.Text.Json.Serialization.JsonStringEnumConverter<FormName> ) )]
		public FormName Form { get; set; } = FormName.Default;

		public bool ShowTreeBranchLines { get; set; } = true;
		public Dictionary<string, string> Light { get; set; } = [];
		public Dictionary<string, string> Dark { get; set; } = [];
		public ThemeStyleOverrides Style { get; set; } = new();
		public GraphThemeOverrides Graph { get; set; } = new();
		public Dictionary<string, SavedThemePreset> CustomThemes { get; set; } = new( StringComparer.OrdinalIgnoreCase );
		public string? ActiveCustomTheme { get; set; }

		[System.Text.Json.Serialization.JsonIgnore]
		public PaletteName EffectivePalette => Palette ?? ( string.Equals( Mode, "Light", StringComparison.OrdinalIgnoreCase ) ? PaletteName.Light : PaletteName.Dark );
	}
}

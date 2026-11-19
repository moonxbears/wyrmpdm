using System.Globalization;
using System.Text.Json.Serialization;

using HackPDM.Core.Graph;

namespace HackPDM.Core.Theming;

/// <summary>Shared hex color parsing/formatting helpers (<c>#AARRGGBB</c> and <c>#RRGGBB</c>).</summary>
public static class ThemeColorParse
{
	public static bool TryParseHex( string? text, out uint argb )
	{
		argb = 0;
		if( string.IsNullOrWhiteSpace( text ) )
			return false;
		ReadOnlySpan<char> hex = text.AsSpan().Trim().TrimStart( '#' );
		if( hex.Length is not ( 6 or 8 ) || !uint.TryParse( hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint value ) )
			return false;
		if( hex.Length == 6 )
			value |= 0xFF000000;
		argb = value;
		return true;
	}

	public static string FormatHex( uint argb ) => $"#{argb:X8}";
}

/// <summary>
/// Independent shape, corner, border, density, typography, and spacing overrides stored in
/// <c>AppConfig.json</c> under <c>Theme.Style</c>. Any <see langword="null"/> property inherits
/// from the active <see cref="FormName"/> / <see cref="PaletteName"/> preset.
/// </summary>
public sealed class ThemeStyleOverrides
{
	public bool? CompactDensity { get; set; }
	public double? ControlCornerRadius { get; set; }
	public double? OverlayCornerRadius { get; set; }
	public double? PanelCornerRadius { get; set; }
	public double? PanelBorderThickness { get; set; }
	public bool? AccentPanels { get; set; }
	public string? PanelStrokeColor { get; set; }
	public string? FontFamily { get; set; }
	public double? BaseFontSize { get; set; }
	public double? ContentPadding { get; set; }

	[JsonIgnore]
	public bool IsEmpty =>
		CompactDensity is null
		&& ControlCornerRadius is null
		&& OverlayCornerRadius is null
		&& PanelCornerRadius is null
		&& PanelBorderThickness is null
		&& AccentPanels is null
		&& string.IsNullOrWhiteSpace( PanelStrokeColor )
		&& string.IsNullOrWhiteSpace( FontFamily )
		&& BaseFontSize is null
		&& ContentPadding is null;

	/// <summary>Returns an <see cref="AppForm"/> with every non-null override applied on top of <paramref name="baseForm"/>.</summary>
	public AppForm ApplyTo( AppForm baseForm ) => baseForm with
	{
		CompactDensity = CompactDensity ?? baseForm.CompactDensity,
		ControlCornerRadius = Clamp( ControlCornerRadius, 0, 32 ) ?? baseForm.ControlCornerRadius,
		OverlayCornerRadius = Clamp( OverlayCornerRadius, 0, 32 ) ?? baseForm.OverlayCornerRadius,
		PanelCornerRadius = Clamp( PanelCornerRadius, 0, 32 ) ?? baseForm.PanelCornerRadius,
		PanelBorderThickness = Clamp( PanelBorderThickness, 0, 12 ) ?? baseForm.PanelBorderThickness,
		AccentPanels = AccentPanels ?? baseForm.AccentPanels,
	};

	/// <summary>Resolves the effective panel border color (<c>0xAARRGGBB</c>), honoring <see cref="PanelStrokeColor"/> first.</summary>
	public uint ResolvePanelStroke( AppPalette palette, AppForm effectiveForm )
	{
		if( ThemeColorParse.TryParseHex( PanelStrokeColor, out uint custom ) )
			return custom;
		return ThemePresets.PanelStroke( palette, effectiveForm );
	}

	public void Clear()
	{
		CompactDensity = null;
		ControlCornerRadius = null;
		OverlayCornerRadius = null;
		PanelCornerRadius = null;
		PanelBorderThickness = null;
		AccentPanels = null;
		PanelStrokeColor = null;
		FontFamily = null;
		BaseFontSize = null;
		ContentPadding = null;
	}

	public ThemeStyleOverrides DeepClone() => new()
	{
		CompactDensity = CompactDensity,
		ControlCornerRadius = ControlCornerRadius,
		OverlayCornerRadius = OverlayCornerRadius,
		PanelCornerRadius = PanelCornerRadius,
		PanelBorderThickness = PanelBorderThickness,
		AccentPanels = AccentPanels,
		PanelStrokeColor = PanelStrokeColor,
		FontFamily = FontFamily,
		BaseFontSize = BaseFontSize,
		ContentPadding = ContentPadding,
	};

	public bool ValueEquals( ThemeStyleOverrides? other )
	{
		if( other is null )
			return IsEmpty;
		return CompactDensity == other.CompactDensity
			&& ControlCornerRadius == other.ControlCornerRadius
			&& OverlayCornerRadius == other.OverlayCornerRadius
			&& PanelCornerRadius == other.PanelCornerRadius
			&& PanelBorderThickness == other.PanelBorderThickness
			&& AccentPanels == other.AccentPanels
			&& string.Equals( PanelStrokeColor, other.PanelStrokeColor, StringComparison.OrdinalIgnoreCase )
			&& string.Equals( FontFamily, other.FontFamily, StringComparison.OrdinalIgnoreCase )
			&& BaseFontSize == other.BaseFontSize
			&& ContentPadding == other.ContentPadding;
	}

	private static double? Clamp( double? value, double min, double max )
		=> value.HasValue && !double.IsNaN( value.Value ) && !double.IsInfinity( value.Value )
			? Math.Clamp( value.Value, min, max )
			: null;
}

/// <summary>
/// Independent dependency graph color, shape, and layout overrides stored in <c>AppConfig.json</c>
/// under <c>Theme.Graph</c>. Every property applies live without requiring a restart.
/// </summary>
public sealed class GraphThemeOverrides
{
	// ---- Palette color overrides ("#AARRGGBB" or "#RRGGBB") ----
	public string? Backdrop { get; set; }
	public string? GridLine { get; set; }
	public string? Body { get; set; }
	public string? BodyBorder { get; set; }
	public string? Text { get; set; }
	public string? HeaderText { get; set; }
	public string? SubText { get; set; }
	public string? Link { get; set; }
	public string? LinkHighlight { get; set; }
	public string? Port { get; set; }
	public string? RootBorder { get; set; }
	public string? SelectedBorder { get; set; }
	public string? Shadow { get; set; }

	/// <summary>Per-<see cref="NodeKind"/> header colors (keys: Assembly, Part, Drawing, Pdf, Other, Deleted, Folder, Property).</summary>
	public Dictionary<string, string> NodeHeaders { get; set; } = new( StringComparer.OrdinalIgnoreCase );

	// ---- Form shape overrides ----
	[JsonConverter( typeof( JsonStringEnumConverter<HeaderStyle> ) )]
	public HeaderStyle? Header { get; set; }

	[JsonConverter( typeof( JsonStringEnumConverter<LinkStyle> ) )]
	public LinkStyle? Links { get; set; }

	public double? HeaderHeight { get; set; }
	public double? CornerRadius { get; set; }
	public double? BorderThickness { get; set; }
	public double? RootBorderThickness { get; set; }
	public double? TitleSize { get; set; }
	public bool? ShowFolder { get; set; }
	public bool? ShowSizeLine { get; set; }
	public double? PortSize { get; set; }
	public bool? HollowPorts { get; set; }
	public double? LinkThickness { get; set; }
	public double? ShadowDepth { get; set; }
	public bool? ShowGrid { get; set; }

	// ---- Layout spacing & sizing overrides ----
	public double? ColumnGap { get; set; }
	public double? RowGap { get; set; }
	public double? Margin { get; set; }
	public double? MinWidth { get; set; }
	public double? MinHeight { get; set; }
	public double? MaxWidth { get; set; }
	public double? MaxHeight { get; set; }

	[JsonIgnore]
	public bool IsEmpty =>
		Backdrop is null && GridLine is null && Body is null && BodyBorder is null
		&& Text is null && HeaderText is null && SubText is null && Link is null
		&& LinkHighlight is null && Port is null && RootBorder is null && SelectedBorder is null
		&& Shadow is null && ( NodeHeaders is null || NodeHeaders.Count == 0 )
		&& Header is null && Links is null && HeaderHeight is null && CornerRadius is null
		&& BorderThickness is null && RootBorderThickness is null && TitleSize is null
		&& ShowFolder is null && ShowSizeLine is null && PortSize is null && HollowPorts is null
		&& LinkThickness is null && ShadowDepth is null && ShowGrid is null
		&& ColumnGap is null && RowGap is null && Margin is null
		&& MinWidth is null && MinHeight is null && MaxWidth is null && MaxHeight is null;

	/// <summary>Produces a new <see cref="GraphTheme"/> overlaying all non-null overrides on <paramref name="baseTheme"/>.</summary>
	public GraphTheme ApplyTo( GraphTheme baseTheme )
	{
		if( IsEmpty )
			return baseTheme;

		GraphPalette bp = baseTheme.Palette;
		Dictionary<NodeKind, uint> headers = new( bp.Headers );
		if( NodeHeaders is not null )
		{
			foreach( var (k, hex) in NodeHeaders )
			{
				if( Enum.TryParse<NodeKind>( k, ignoreCase: true, out NodeKind kind )
					&& ThemeColorParse.TryParseHex( hex, out uint parsed ) )
				{
					headers[ kind ] = parsed;
				}
			}
		}

		GraphPalette palette = bp with
		{
			Backdrop = ColorOr( Backdrop, bp.Backdrop ),
			GridLine = ColorOr( GridLine, bp.GridLine ),
			Body = ColorOr( Body, bp.Body ),
			BodyBorder = ColorOr( BodyBorder, bp.BodyBorder ),
			Text = ColorOr( Text, bp.Text ),
			HeaderText = ColorOr( HeaderText, bp.HeaderText ),
			SubText = ColorOr( SubText, bp.SubText ),
			Link = ColorOr( Link, bp.Link ),
			LinkHighlight = ColorOr( LinkHighlight, bp.LinkHighlight ),
			Port = ColorOr( Port, bp.Port ),
			RootBorder = ColorOr( RootBorder, bp.RootBorder ),
			SelectedBorder = ColorOr( SelectedBorder, bp.SelectedBorder ),
			Shadow = ColorOr( Shadow, bp.Shadow ),
			Headers = headers,
		};

		GraphForm bf = baseTheme.Form;
		LayoutOptions bl = bf.Layout;
		double minW = Clamp( MinWidth, 60, 600 ) ?? bl.MinWidth;
		double minH = Clamp( MinHeight, 20, 400 ) ?? bl.MinHeight;
		double maxW = Math.Max( minW, Clamp( MaxWidth, 80, 800 ) ?? bl.MaxWidth );
		double maxH = Math.Max( minH, Clamp( MaxHeight, 24, 600 ) ?? bl.MaxHeight );
		LayoutOptions layout = new()
		{
			ColumnGap = Clamp( ColumnGap, 16, 400 ) ?? bl.ColumnGap,
			RowGap = Clamp( RowGap, 4, 200 ) ?? bl.RowGap,
			Margin = Clamp( Margin, 0, 200 ) ?? bl.Margin,
			MinWidth = minW,
			MinHeight = minH,
			MaxWidth = maxW,
			MaxHeight = maxH,
		};

		GraphForm form = bf with
		{
			Layout = layout,
			Header = Header ?? bf.Header,
			Links = Links ?? bf.Links,
			HeaderHeight = Clamp( HeaderHeight, 2, 80 ) ?? bf.HeaderHeight,
			CornerRadius = Clamp( CornerRadius, 0, 32 ) ?? bf.CornerRadius,
			BorderThickness = Clamp( BorderThickness, 0, 10 ) ?? bf.BorderThickness,
			RootBorderThickness = Clamp( RootBorderThickness, 0, 12 ) ?? bf.RootBorderThickness,
			TitleSize = Clamp( TitleSize, 8, 28 ) ?? bf.TitleSize,
			ShowFolder = ShowFolder ?? bf.ShowFolder,
			ShowSizeLine = ShowSizeLine ?? bf.ShowSizeLine,
			PortSize = Clamp( PortSize, 3, 28 ) ?? bf.PortSize,
			HollowPorts = HollowPorts ?? bf.HollowPorts,
			LinkThickness = Clamp( LinkThickness, 0.5, 10 ) ?? bf.LinkThickness,
			ShadowDepth = Clamp( ShadowDepth, 0, 24 ) ?? bf.ShadowDepth,
			ShowGrid = ShowGrid ?? bf.ShowGrid,
		};

		return new GraphTheme( palette, form );
	}

	public void Clear()
	{
		Backdrop = GridLine = Body = BodyBorder = Text = HeaderText = SubText = null;
		Link = LinkHighlight = Port = RootBorder = SelectedBorder = Shadow = null;
		NodeHeaders?.Clear();
		Header = null;
		Links = null;
		HeaderHeight = CornerRadius = BorderThickness = RootBorderThickness = TitleSize = null;
		ShowFolder = ShowSizeLine = HollowPorts = ShowGrid = null;
		PortSize = LinkThickness = ShadowDepth = null;
		ColumnGap = RowGap = Margin = MinWidth = MinHeight = MaxWidth = MaxHeight = null;
	}

	public GraphThemeOverrides DeepClone() => new()
	{
		Backdrop = Backdrop,
		GridLine = GridLine,
		Body = Body,
		BodyBorder = BodyBorder,
		Text = Text,
		HeaderText = HeaderText,
		SubText = SubText,
		Link = Link,
		LinkHighlight = LinkHighlight,
		Port = Port,
		RootBorder = RootBorder,
		SelectedBorder = SelectedBorder,
		Shadow = Shadow,
		NodeHeaders = new Dictionary<string, string>( NodeHeaders ?? [], StringComparer.OrdinalIgnoreCase ),
		Header = Header,
		Links = Links,
		HeaderHeight = HeaderHeight,
		CornerRadius = CornerRadius,
		BorderThickness = BorderThickness,
		RootBorderThickness = RootBorderThickness,
		TitleSize = TitleSize,
		ShowFolder = ShowFolder,
		ShowSizeLine = ShowSizeLine,
		PortSize = PortSize,
		HollowPorts = HollowPorts,
		LinkThickness = LinkThickness,
		ShadowDepth = ShadowDepth,
		ShowGrid = ShowGrid,
		ColumnGap = ColumnGap,
		RowGap = RowGap,
		Margin = Margin,
		MinWidth = MinWidth,
		MinHeight = MinHeight,
		MaxWidth = MaxWidth,
		MaxHeight = MaxHeight,
	};

	private static uint ColorOr( string? hex, uint fallback )
		=> ThemeColorParse.TryParseHex( hex, out uint parsed ) ? parsed : fallback;

	private static double? Clamp( double? value, double min, double max )
		=> value.HasValue && !double.IsNaN( value.Value ) && !double.IsInfinity( value.Value )
			? Math.Clamp( value.Value, min, max )
			: null;
}

/// <summary>A named, reusable custom theme preset saved inside <c>AppConfig.json</c> under <c>Theme.CustomThemes</c>.</summary>
public sealed class SavedThemePreset
{
	public string Name { get; set; } = string.Empty;
	public string Description { get; set; } = string.Empty;
	public string Mode { get; set; } = "Dark";

	[JsonConverter( typeof( JsonStringEnumConverter<PaletteName> ) )]
	public PaletteName Palette { get; set; } = PaletteName.Dark;

	[JsonConverter( typeof( JsonStringEnumConverter<FormName> ) )]
	public FormName Form { get; set; } = FormName.Default;

	public bool ShowTreeBranchLines { get; set; } = true;
	public Dictionary<string, string> Light { get; set; } = [];
	public Dictionary<string, string> Dark { get; set; } = [];
	public ThemeStyleOverrides Style { get; set; } = new();
	public GraphThemeOverrides Graph { get; set; } = new();
}

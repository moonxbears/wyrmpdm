using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;

using HackPDM.Core.Graph;
using HackPDM.Core.Theming;
using HackPDM.Mvvm.Models.Settings;
using HackPDM.Mvvm.ViewModels.Common;
using HackPDM.Mvvm.Views.Common.Settings;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

using Color = Windows.UI.Color;

namespace HackPDM.Mvvm.Views.Common.Theming;

/// <summary>
/// Applies the saved <see cref="ThemeSettings"/>: the color preset (<see cref="PaletteName"/>) sets the light/dark
/// mode on every window and recolors the shared Fluent theme brushes in place, so controls update live; the
/// user's own color changes go on top. The shape preset (<see cref="FormName"/>) and independent
/// <see cref="ThemeStyleOverrides"/> / <see cref="GraphThemeOverrides"/> control corners, density, panel borders,
/// typography, and the dependency graph.
/// </summary>
public sealed class ThemeService : IThemeService
{
	private const string PageBackgroundKey = "ApplicationPageBackgroundThemeBrush";
	private const string CompactDensityUri = "ms-appx:///Microsoft.UI.Xaml/DensityStyles/Compact.xaml";

	/// <summary>The app's own panel resources (folder tree, lists, graph frame); defaults in App.xaml, set from the presets.</summary>
	public const string PanelStrokeKey = "HackPanelStrokeBrush", PanelCornerKey = "HackPanelCornerRadius", PanelThicknessKey = "HackPanelBorderThickness";

	/// <summary>Picks the theme the app starts with instead of the saved one (smoke runs only): "Palette:Form".</summary>
	public const string SmokeThemeVariable = "HACKPDM_SMOKE_THEME";

	// window roots the theme is applied to (weak: windows close without telling us)
	private readonly List<WeakReference<FrameworkElement>> _roots = [];
	// roots whose background we set (they had none), so it follows the theme
	private readonly ConditionalWeakTable<FrameworkElement, object> _ownedBackgrounds = [];
	// built-in colors, captured before the first override, so a reset can restore them
	private readonly Dictionary<string, Color> _originals = [];

	// the theme the application is running with: the only one whose colors can be changed live
	private readonly bool _runningDark = Application.Current.RequestedTheme == ApplicationTheme.Dark;
	private readonly AppThemeMode _startupMode;
	private FormName _appliedForm;
	private ThemeStyleOverrides _appliedStyle;

	private ThemeSettings _settings;

	/// <summary>Creates the service and applies the saved color overrides of the running theme.</summary>
	/// <param name="settings">The saved theme settings; the service edits this instance.</param>
	public ThemeService( ThemeSettings settings )
	{
		_settings = EnsureSubObjects( settings );
		var (palette, form) = StartupTheme( _settings );
		_settings.Palette = palette;
		_settings.Form = form;
		_settings.Mode = ThemePresets.Palette( palette ).IsDark ? AppThemeMode.Dark : AppThemeMode.Light;
		_startupMode = _settings.Mode;
		_appliedForm = form;
		_appliedStyle = _settings.Style.DeepClone();
		ApplyOverrides();
		UpdatePanelBrush();
	}

	/// <inheritdoc/>
	public ThemeSettings Settings => _settings;

	/// <inheritdoc/>
	public PaletteName Palette => _settings.EffectivePalette;

	/// <inheritdoc/>
	public FormName Form => _settings.Form;

	/// <inheritdoc/>
	public AppForm EffectiveForm => _settings.Style.ApplyTo( ThemePresets.Form( Form ) );

	/// <inheritdoc/>
	public bool StyleNeedsRestart => _settings.Form != _appliedForm || !_settings.Style.ValueEquals( _appliedStyle );

	/// <inheritdoc/>
	public GraphTheme GraphTheme => _settings.Graph.ApplyTo( GraphThemes.From( Palette, Form ) );

	/// <inheritdoc/>
	public bool ShowTreeBranchLines => _settings.ShowTreeBranchLines;

	/// <inheritdoc/>
	public AppThemeMode Mode => _settings.Mode;

	/// <inheritdoc/>
	public bool IsDarkActive => _runningDark;

	/// <inheritdoc/>
	public bool NeedsRestart => _settings.Mode != _startupMode;

	/// <inheritdoc/>
	public string SavePath => AppConfigStore.FilePath;

	/// <inheritdoc/>
	public event Action? ThemeChanged;

	#region Startup

	/// <summary>
	/// Reads the full <see cref="ThemeSettings"/> from <c>AppConfig.json</c> early in the <see cref="Application"/>
	/// constructor (before DI services exist). Missing or unreadable files fall back to factory defaults.
	/// </summary>
	public static ThemeSettings ReadSavedThemeSettings()
	{
		try
		{
			string path = AppConfigStore.FilePath;
			if( !File.Exists( path ) )
				return EnsureSubObjects( new ThemeSettings() );

			using JsonDocument doc = JsonDocument.Parse( File.ReadAllText( path ) );
			if( doc.RootElement.TryGetProperty( "Theme", out JsonElement themeEl ) && themeEl.ValueKind == JsonValueKind.Object )
			{
				ThemeSettings? parsed = JsonSerializer.Deserialize<ThemeSettings>( themeEl.GetRawText(), AppConfigStore.Options );
				if( parsed is not null )
					return EnsureSubObjects( parsed );
			}
			return EnsureSubObjects( new ThemeSettings() );
		}
		catch( Exception ex )
		{
			HackPDM.Core.Logging.HackLogger.Warn( "ThemeService", $"Could not read saved theme; falling back to defaults: {ex.Message}", ex );
			return EnsureSubObjects( new ThemeSettings() );
		}
	}

	/// <summary>
	/// The presets saved in AppConfig.json, read directly (the full settings can't load this early).
	/// Called from the App constructor, where the application theme can still be set.
	/// </summary>
	public static (PaletteName Palette, FormName Form) ReadSavedTheme()
		=> StartupTheme( ReadSavedThemeSettings() );

	/// <summary>Resolves the startup palette &amp; form for <paramref name="saved"/> (honoring smoke override).</summary>
	public static (PaletteName Palette, FormName Form) ResolveStartupPresets( ThemeSettings saved )
		=> StartupTheme( saved );

	/// <summary>The theme to start with: the saved one, or in a smoke run the one <see cref="SmokeThemeVariable"/> names.</summary>
	private static (PaletteName, FormName) StartupTheme( ThemeSettings saved )
	{
		if( HackPDM.Shell.Testing.SmokeTestRunner.IsEnabled
			&& Environment.GetEnvironmentVariable( SmokeThemeVariable )?.Split( ':' ) is [ var palette, var form ] )
			return ( ThemePresets.ParsePalette( palette ), ThemePresets.ParseForm( form ) );
		return ( saved.EffectivePalette, saved.Form );
	}

	/// <summary>
	/// Sets the shape preset's resources before any page is built: control and overlay corners, Fluent's compact or
	/// standard density, and the app's panel corners, borders and border color. Called from the App constructor.
	/// </summary>
	public static void ApplyStartupResources( ResourceDictionary resources, PaletteName palette, FormName form )
		=> ApplyStartupResources( resources, palette, form, styleOverrides: null );

	/// <summary>
	/// Applies preset + independent <see cref="ThemeStyleOverrides"/> (corners, panel borders, density,
	/// typography) to <paramref name="resources"/>. Safe to invoke both at startup and during a soft UI reload.
	/// </summary>
	public static void ApplyStartupResources(
		ResourceDictionary resources,
		PaletteName palette,
		FormName form,
		ThemeStyleOverrides? styleOverrides )
	{
		AppForm baseForm = ThemePresets.Form( form );
		AppForm eff = styleOverrides?.ApplyTo( baseForm ) ?? baseForm;

		if( form != FormName.Default || styleOverrides?.ControlCornerRadius.HasValue == true )
			resources[ "ControlCornerRadius" ] = new CornerRadius( eff.ControlCornerRadius );
		else
			resources[ "ControlCornerRadius" ] = new CornerRadius( baseForm.ControlCornerRadius );

		if( form != FormName.Default || styleOverrides?.OverlayCornerRadius.HasValue == true )
			resources[ "OverlayCornerRadius" ] = new CornerRadius( eff.OverlayCornerRadius );
		else
			resources[ "OverlayCornerRadius" ] = new CornerRadius( baseForm.OverlayCornerRadius );

		resources[ PanelCornerKey ] = new CornerRadius( eff.PanelCornerRadius );
		resources[ PanelThicknessKey ] = new Thickness( eff.PanelBorderThickness );

		if( resources.TryGetValue( PanelStrokeKey, out object? stroke ) && stroke is SolidColorBrush brush )
		{
			uint strokeArgb = styleOverrides is not null
				? styleOverrides.ResolvePanelStroke( ThemePresets.Palette( palette ), eff )
				: ThemePresets.PanelStroke( ThemePresets.Palette( palette ), eff );
			brush.Color = ToColor( strokeArgb );
		}

		// Synchronize Fluent Compact vs Standard density dictionary
		ResourceDictionary? existingCompact = resources.MergedDictionaries
			.FirstOrDefault( d => d.Source?.OriginalString.EndsWith( "Compact.xaml", StringComparison.OrdinalIgnoreCase ) == true );
		if( !eff.CompactDensity && existingCompact is not null )
		{
			resources.MergedDictionaries.Remove( existingCompact );
		}
		else if( eff.CompactDensity && existingCompact is null )
		{
			try
			{
				resources.MergedDictionaries.Add( new ResourceDictionary { Source = new Uri( CompactDensityUri ) } );
			}
			catch
			{
				// Platform may not expose Compact.xaml dynamically; keep graceful
			}
		}

		// Independent typography overrides
		if( !string.IsNullOrWhiteSpace( styleOverrides?.FontFamily ) )
			resources[ "ContentControlThemeFontFamily" ] = new FontFamily( styleOverrides.FontFamily.Trim() );
		else
			resources.Remove( "ContentControlThemeFontFamily" );

		if( styleOverrides?.BaseFontSize is double fontSize && fontSize is >= 8 and <= 32 )
			resources[ "ControlContentThemeFontSize" ] = fontSize;
		else
			resources.Remove( "ControlContentThemeFontSize" );
	}

	/// <summary>Whether Fluent's compact density is in use (the smoke tests).</summary>
	public static bool IsCompactDensity( ResourceDictionary resources )
		=> resources.MergedDictionaries.Any( d => d.Source?.OriginalString.EndsWith( "Compact.xaml", StringComparison.OrdinalIgnoreCase ) == true );

	/// <summary>Applies the theme to <paramref name="window"/>'s content. Call after setting <see cref="Window.Content"/>.</summary>
	public void Attach( Window window )
	{
		if( window.Content is not FrameworkElement root )
			return;
		_roots.RemoveAll( r => !r.TryGetTarget( out var existing ) || ReferenceEquals( existing, root ) );
		_roots.Add( new( root ) );

		// a window with a bare root shows the platform's (often black) surface behind the text
		if( GetBackground( root ) is null )
			_ownedBackgrounds.AddOrUpdate( root, null! );
		ApplyMode( root );
		ApplyLiveTypographyToRoot( root );
	}

	#endregion

	#region Mode, Presets, Style & Graph Overrides

	/// <inheritdoc/>
	public void SetMode( AppThemeMode mode )
	{
		if( _settings.Mode == mode )
			return;
		_settings.Mode = mode;
		_settings.ActiveCustomTheme = null;
		foreach( var root in LiveRoots() )
			ApplyMode( root );
		ThemeChanged?.Invoke();
	}

	/// <inheritdoc/>
	public void SetPalette( PaletteName palette )
	{
		if( _settings.Palette == palette )
			return;
		foreach( string key in _originals.Keys.ToList() )
			Restore( key, toOriginal: true );
		_settings.Palette = palette;
		_settings.Mode = ThemePresets.Palette( palette ).IsDark ? AppThemeMode.Dark : AppThemeMode.Light;
		_settings.ActiveCustomTheme = null;
		ApplyOverrides();
		UpdatePanelBrush();
		foreach( var root in LiveRoots() )
			ApplyMode( root );
		ThemeChanged?.Invoke();
	}

	/// <inheritdoc/>
	public void SetForm( FormName form )
	{
		if( _settings.Form == form )
			return;
		_settings.Form = form;
		_settings.ActiveCustomTheme = null;
		ApplyLiveStyleResources();
		ThemeChanged?.Invoke();
	}

	/// <inheritdoc/>
	public void SetShowTreeBranchLines( bool show )
	{
		if( _settings.ShowTreeBranchLines == show )
			return;
		_settings.ShowTreeBranchLines = show;
		ThemeChanged?.Invoke();
	}

	/// <inheritdoc/>
	public void ApplyStyleOverrides( Action<ThemeStyleOverrides> mutate )
	{
		_settings.Style ??= new();
		mutate( _settings.Style );
		_settings.ActiveCustomTheme = null;
		ApplyLiveStyleResources();
		ThemeChanged?.Invoke();
	}

	/// <inheritdoc/>
	public void ResetStyleOverrides()
	{
		_settings.Style ??= new();
		_settings.Style.Clear();
		_settings.ActiveCustomTheme = null;
		ApplyLiveStyleResources();
		ThemeChanged?.Invoke();
	}

	/// <inheritdoc/>
	public void ApplyGraphOverrides( Action<GraphThemeOverrides> mutate )
	{
		_settings.Graph ??= new();
		mutate( _settings.Graph );
		_settings.ActiveCustomTheme = null;
		ThemeChanged?.Invoke();
	}

	/// <inheritdoc/>
	public void ResetGraphOverrides()
	{
		_settings.Graph ??= new();
		_settings.Graph.Clear();
		_settings.ActiveCustomTheme = null;
		ThemeChanged?.Invoke();
	}

	/// <inheritdoc/>
	public SavedThemePreset SaveCustomTheme( string name, string? description = null )
	{
		if( string.IsNullOrWhiteSpace( name ) )
			throw new ArgumentException( "Custom theme name is required.", nameof( name ) );

		string trimmed = name.Trim();
		_settings.CustomThemes ??= new( StringComparer.OrdinalIgnoreCase );

		SavedThemePreset preset = new()
		{
			Name = trimmed,
			Description = description?.Trim() ?? string.Empty,
			Mode = _settings.Mode.ToString(),
			Palette = Palette,
			Form = Form,
			ShowTreeBranchLines = _settings.ShowTreeBranchLines,
			Light = new Dictionary<string, string>( _settings.Light ),
			Dark = new Dictionary<string, string>( _settings.Dark ),
			Style = ( _settings.Style ?? new() ).DeepClone(),
			Graph = ( _settings.Graph ?? new() ).DeepClone(),
		};

		_settings.CustomThemes[ trimmed ] = preset;
		_settings.ActiveCustomTheme = trimmed;
		ThemeChanged?.Invoke();
		return preset;
	}

	/// <inheritdoc/>
	public SavedThemePreset LoadCustomTheme( string name )
	{
		if( string.IsNullOrWhiteSpace( name ) )
			throw new ArgumentException( "Custom theme name is required.", nameof( name ) );

		_settings.CustomThemes ??= new( StringComparer.OrdinalIgnoreCase );
		if( !_settings.CustomThemes.TryGetValue( name.Trim(), out SavedThemePreset? found ) )
			throw new KeyNotFoundException( $"Custom theme '{name}' not found." );

		foreach( string key in _originals.Keys.ToList() )
			Restore( key, toOriginal: true );

		_settings.Palette = found.Palette;
		_settings.Form = found.Form;
		_settings.Mode = Enum.TryParse<AppThemeMode>( found.Mode, ignoreCase: true, out AppThemeMode parsedMode )
			? parsedMode
			: ( ThemePresets.Palette( found.Palette ).IsDark ? AppThemeMode.Dark : AppThemeMode.Light );
		_settings.ShowTreeBranchLines = found.ShowTreeBranchLines;
		_settings.Light = new Dictionary<string, string>( found.Light ?? [] );
		_settings.Dark = new Dictionary<string, string>( found.Dark ?? [] );
		_settings.Style = ( found.Style ?? new() ).DeepClone();
		_settings.Graph = ( found.Graph ?? new() ).DeepClone();
		_settings.ActiveCustomTheme = found.Name;

		ReapplyLiveResources();
		ThemeChanged?.Invoke();
		return found;
	}

	/// <inheritdoc/>
	public bool DeleteCustomTheme( string name )
	{
		if( string.IsNullOrWhiteSpace( name ) || _settings.CustomThemes is null )
			return false;
		bool removed = _settings.CustomThemes.Remove( name.Trim() );
		if( removed && string.Equals( _settings.ActiveCustomTheme, name.Trim(), StringComparison.OrdinalIgnoreCase ) )
			_settings.ActiveCustomTheme = null;
		if( removed )
			ThemeChanged?.Invoke();
		return removed;
	}

	/// <inheritdoc/>
	public IReadOnlyList<SavedThemePreset> ListCustomThemes()
		=> _settings.CustomThemes?.Values.OrderBy( t => t.Name, StringComparer.OrdinalIgnoreCase ).ToList() ?? [];

	/// <inheritdoc/>
	public void ResetToFactoryDefaults( bool clearCustomThemes = false )
	{
		foreach( string key in _originals.Keys.ToList() )
			Restore( key, toOriginal: true );

		_settings.ResetToFactoryDefaults( clearCustomThemes );
		ReapplyLiveResources();
		ThemeChanged?.Invoke();
	}

	/// <inheritdoc/>
	public void ReapplyLiveResources()
	{
		foreach( string key in _originals.Keys.ToList() )
			Restore( key, toOriginal: true );

		ApplyOverrides();
		ApplyLiveStyleResources();
		foreach( var root in LiveRoots() )
		{
			ApplyMode( root );
			ApplyLiveTypographyToRoot( root );
		}
		RefreshOwnedBackgrounds();
	}

	/// <inheritdoc/>
	public void MarkStyleSnapshotApplied()
	{
		_appliedForm = _settings.Form;
		_appliedStyle = ( _settings.Style ?? new() ).DeepClone();
		ThemeChanged?.Invoke();
	}

	/// <summary>Whether the palette's colors can be set now: it builds on the theme the app is running with.</summary>
	private bool PresetApplies => ThemePresets.Palette( Palette ).IsDark == _runningDark;

	private void ApplyLiveStyleResources()
	{
		ApplyStartupResources( Application.Current.Resources, Palette, Form, _settings.Style );
		UpdatePanelBrush();
		foreach( var root in LiveRoots() )
			ApplyLiveTypographyToRoot( root );
	}

	private void ApplyLiveTypographyToRoot( FrameworkElement root )
	{
		if( root is not Control control )
			return;

		if( !string.IsNullOrWhiteSpace( _settings.Style?.FontFamily ) )
			control.FontFamily = new FontFamily( _settings.Style.FontFamily.Trim() );
		else
			control.ClearValue( Control.FontFamilyProperty );

		if( _settings.Style?.BaseFontSize is double size && size is >= 8 and <= 32 )
			control.FontSize = size;
		else
			control.ClearValue( Control.FontSizeProperty );
	}

	/// <summary>Recolors the app's panel border brush (shared by every panel, so they change live).</summary>
	private void UpdatePanelBrush()
	{
		if( Application.Current.Resources.TryGetValue( PanelStrokeKey, out object? value ) && value is SolidColorBrush brush )
		{
			uint strokeArgb = ( _settings.Style ?? new() ).ResolvePanelStroke( ThemePresets.Palette( Palette ), EffectiveForm );
			brush.Color = ToColor( strokeArgb );
		}
	}

	private static Color ToColor( uint argb ) => Color.FromArgb( (byte)( argb >> 24 ), (byte)( argb >> 16 ), (byte)( argb >> 8 ), (byte)argb );

	private void ApplyMode( FrameworkElement root )
	{
		root.RequestedTheme = _settings.Mode switch
		{
			AppThemeMode.Dark => ElementTheme.Dark,
			AppThemeMode.Light => ElementTheme.Light,
			_ => ElementTheme.Default,
		};
		if( _ownedBackgrounds.TryGetValue( root, out _ ) )
			SetBackground( root, new SolidColorBrush( PageBackgroundFor( root.ActualTheme ) ) );
	}

	// the page brush resolves to the running theme, so a root previewing the other theme gets the built-in color
	private Color PageBackgroundFor( ElementTheme theme )
		=> ( theme == ElementTheme.Dark ) == _runningDark && FindBrush( PageBackgroundKey ) is { } brush
			? brush.Color
			: theme == ElementTheme.Dark ? Color.FromArgb( 0xFF, 0x20, 0x20, 0x20 ) : Color.FromArgb( 0xFF, 0xF3, 0xF3, 0xF3 );

	#endregion

	#region Colors

	/// <inheritdoc/>
	public ArgbColor? GetColor( string key )
		=> FindBrush( key ) is { } brush ? ToArgb( brush.Color ) : null;

	/// <inheritdoc/>
	public bool IsOverridden( string key ) => _settings.OverridesFor( _runningDark ).ContainsKey( key );

	/// <inheritdoc/>
	public void SetColor( string key, ArgbColor color )
	{
		if( NeedsRestart || !Recolor( key, color ) )
			return;
		_settings.OverridesFor( _runningDark )[ key ] = color.ToString();
		_settings.ActiveCustomTheme = null;
		RefreshOwnedBackgrounds();
		ThemeChanged?.Invoke();
	}

	/// <inheritdoc/>
	public bool SetColor( string key, ArgbColor color, string targetTheme )
	{
		if( string.IsNullOrWhiteSpace( key ) )
			return false;

		string target = ( targetTheme ?? "active" ).Trim().ToLowerInvariant();
		bool writeDark = target switch
		{
			"dark" => true,
			"light" => false,
			"both" => true,
			_ => _settings.Mode != AppThemeMode.Light,
		};
		bool writeLight = target switch
		{
			"dark" => false,
			"light" => true,
			"both" => true,
			_ => _settings.Mode == AppThemeMode.Light,
		};

		if( writeDark )
			_settings.Dark[ key ] = color.ToString();
		if( writeLight )
			_settings.Light[ key ] = color.ToString();

		bool liveApplied = false;
		bool affectsRunning = ( _runningDark && writeDark ) || ( !_runningDark && writeLight );
		if( affectsRunning && !NeedsRestart )
		{
			liveApplied = Recolor( key, color );
			RefreshOwnedBackgrounds();
		}

		_settings.ActiveCustomTheme = null;
		ThemeChanged?.Invoke();
		return liveApplied || writeDark || writeLight;
	}

	/// <inheritdoc/>
	public void ResetColor( string key )
	{
		if( NeedsRestart || !_settings.OverridesFor( _runningDark ).Remove( key ) )
			return;
		Restore( key );
		_settings.ActiveCustomTheme = null;
		RefreshOwnedBackgrounds();
		ThemeChanged?.Invoke();
	}

	/// <inheritdoc/>
	public void ResetColor( string key, string targetTheme )
	{
		string target = ( targetTheme ?? "active" ).Trim().ToLowerInvariant();
		bool clearDark = target is "dark" or "both" || ( target is "active" or "" && _settings.Mode != AppThemeMode.Light );
		bool clearLight = target is "light" or "both" || ( target is "active" or "" && _settings.Mode == AppThemeMode.Light );

		bool removed = false;
		if( clearDark )
			removed |= _settings.Dark.Remove( key );
		if( clearLight )
			removed |= _settings.Light.Remove( key );

		if( ( ( _runningDark && clearDark ) || ( !_runningDark && clearLight ) ) && !NeedsRestart )
		{
			Restore( key );
			RefreshOwnedBackgrounds();
		}

		if( removed )
		{
			_settings.ActiveCustomTheme = null;
			ThemeChanged?.Invoke();
		}
	}

	/// <inheritdoc/>
	public void ResetAll()
	{
		var overrides = _settings.OverridesFor( _runningDark );
		if( !NeedsRestart )
		{
			foreach( string key in overrides.Keys.ToList() )
				Restore( key );
		}
		overrides.Clear();
		_settings.ActiveCustomTheme = null;
		RefreshOwnedBackgrounds();
		ThemeChanged?.Invoke();
	}

	/// <inheritdoc/>
	public void Revert()
	{
		foreach( string key in _originals.Keys.ToList() )
			Restore( key, toOriginal: true );
		_settings = EnsureSubObjects( AppConfigStore.Load().Theme );
		UIStorage.GlobalData.Theme = _settings;
		ReapplyLiveResources();
		ThemeChanged?.Invoke();
	}

	/// <inheritdoc/>
	public void Save()
	{
		UIStorage.GlobalData.Theme = _settings;
		AppConfigStore.Save( UIStorage.GlobalData );
	}

	/// <summary>The preset's colors, then the user's own changes; nothing while the preset needs the other base theme (a restart).</summary>
	private void ApplyOverrides()
	{
		if( !PresetApplies )
			return;
		foreach( var (key, argb) in ThemePresets.Palette( Palette ).Brushes )
			Recolor( key, new ArgbColor( (byte)( argb >> 24 ), (byte)( argb >> 16 ), (byte)( argb >> 8 ), (byte)argb ) );
		foreach( var (key, hex) in _settings.OverridesFor( _runningDark ) )
		{
			if( ArgbColor.TryParse( hex, out var color ) )
				Recolor( key, color );
		}
	}

	private bool Recolor( string key, ArgbColor color )
	{
		if( FindBrush( key ) is not { } brush )
			return false;
		_originals.TryAdd( key, brush.Color );
		brush.Color = Color.FromArgb( color.A, color.R, color.G, color.B );
		return true;
	}

	/// <summary>Puts a color back to the preset's value (or, with <paramref name="toOriginal"/> or when the preset leaves it alone, the built-in one).</summary>
	private void Restore( string key, bool toOriginal = false )
	{
		if( FindBrush( key ) is not { } brush )
			return;
		if( !toOriginal && PresetApplies && ThemePresets.Palette( Palette ).Brushes.TryGetValue( key, out uint preset ) )
			brush.Color = ToColor( preset );
		else if( _originals.TryGetValue( key, out var original ) )
			brush.Color = original;
	}

	private void RefreshOwnedBackgrounds()
	{
		foreach( var root in LiveRoots() )
		{
			if( _ownedBackgrounds.TryGetValue( root, out _ ) )
				SetBackground( root, new SolidColorBrush( PageBackgroundFor( root.ActualTheme ) ) );
		}
	}

	/// <summary>The brush instance controls share for <paramref name="key"/> in the running theme.</summary>
	private SolidColorBrush? FindBrush( string key )
	{
		ResourceDictionary resources = Application.Current.Resources;
		if( resources.TryGetValue( key, out object? value ) && value is SolidColorBrush direct )
			return direct;

		string themeKey = _runningDark ? "Dark" : "Light";
		if( resources.ThemeDictionaries.TryGetValue( themeKey, out object? dictObj )
			&& dictObj is ResourceDictionary dict
			&& dict.TryGetValue( key, out object? themed )
			&& themed is SolidColorBrush themedBrush )
		{
			return themedBrush;
		}
		return null;
	}

	private static ArgbColor ToArgb( Color c ) => new( c.A, c.R, c.G, c.B );

	private static ThemeSettings EnsureSubObjects( ThemeSettings s )
	{
		s.Light ??= [];
		s.Dark ??= [];
		s.Style ??= new();
		s.Graph ??= new();
		s.Graph.NodeHeaders ??= new( StringComparer.OrdinalIgnoreCase );
		s.CustomThemes = new Dictionary<string, SavedThemePreset>( s.CustomThemes ?? [], StringComparer.OrdinalIgnoreCase );
		return s;
	}

	#endregion

	#region Window roots

	private IEnumerable<FrameworkElement> LiveRoots()
	{
		_roots.RemoveAll( r => !r.TryGetTarget( out _ ) );
		foreach( var reference in _roots.ToList() )
		{
			if( reference.TryGetTarget( out var root ) )
				yield return root;
		}
	}

	private static Brush? GetBackground( FrameworkElement element ) => element switch
	{
		Panel panel => panel.Background,
		Control control => control.Background,
		Border border => border.Background,
		_ => null,
	};

	private static void SetBackground( FrameworkElement element, Brush brush )
	{
		switch( element )
		{
			case Panel panel: panel.Background = brush; break;
			case Control control: control.Background = brush; break;
			case Border border: border.Background = brush; break;
		}
	}

	#endregion
}

using System;
using System.Collections.Generic;

using HackPDM.Core.Theming;
using HackPDM.Mvvm.Models.Settings;

namespace HackPDM.Mvvm.ViewModels.Common;

/// <summary>Applies, edits and saves the app theme. Every change shows immediately in all open windows.</summary>
public interface IThemeService
{
	/// <summary>The live <see cref="ThemeSettings"/> instance persisted under <c>AppConfig.json: Theme</c>.</summary>
	ThemeSettings Settings { get; }

	/// <summary>The color preset in use (it decides <see cref="Mode"/>).</summary>
	PaletteName Palette { get; }

	/// <summary>The shape preset in use.</summary>
	FormName Form { get; }

	/// <summary>Effective shape/style values combining <see cref="Form"/> and <see cref="ThemeSettings.Style"/> overrides.</summary>
	AppForm EffectiveForm { get; }

	/// <summary>The style (corners, density, fonts, borders) was changed since startup/last soft reload.</summary>
	bool StyleNeedsRestart { get; }

	/// <summary>The dependency graph's colors and shapes for the current presets plus independent <see cref="ThemeSettings.Graph"/> overrides.</summary>
	HackPDM.Core.Graph.GraphTheme GraphTheme { get; }

	/// <summary>Whether folder trees draw branching visual guide lines.</summary>
	bool ShowTreeBranchLines { get; }

	/// <summary>Shows or hides branching visual guide lines in folder trees and raises <see cref="ThemeChanged"/>.</summary>
	void SetShowTreeBranchLines( bool show );

	/// <summary>Switches the color preset: live when it builds on the running light/dark theme, else previewed until a restart (<see cref="NeedsRestart"/>). Not saved until <see cref="Save"/>.</summary>
	void SetPalette( PaletteName palette );

	/// <summary>Switches the shape preset; panels and the graph follow at once, controls after a soft or full restart. Not saved until <see cref="Save"/>.</summary>
	void SetForm( FormName form );

	AppThemeMode Mode { get; }

	/// <summary>Whether the app is running with the dark theme: the theme whose colors are being edited.</summary>
	bool IsDarkActive { get; }

	/// <summary>
	/// The base mode was changed since startup: it previews now, but the new theme's colors can only be
	/// edited (and menus and dialogs only follow it) after a full process restart.
	/// </summary>
	bool NeedsRestart { get; }

	/// <summary>Where <see cref="Save"/> writes (AppConfig.json, loaded automatically at startup).</summary>
	string SavePath { get; }

	/// <summary>Raised after the mode, style, graph theme, or any color changes.</summary>
	event Action? ThemeChanged;

	/// <summary>Switches the base mode on every open window and raises <see cref="ThemeChanged"/>.</summary>
	void SetMode( AppThemeMode mode );

	/// <summary>Reads the color a theme brush currently has.</summary>
	ArgbColor? GetColor( string key );

	/// <summary>Whether <paramref name="key"/> has an override in the active theme.</summary>
	bool IsOverridden( string key );

	/// <summary>Overrides <paramref name="key"/> in the running theme (ignored while <see cref="NeedsRestart"/>).</summary>
	void SetColor( string key, ArgbColor color );

	/// <summary>
	/// Overrides <paramref name="key"/> in <paramref name="targetTheme"/> (<c>"active"</c>, <c>"dark"</c>, <c>"light"</c>, or <c>"both"</c>).
	/// Always writes the saved dictionary and recolors the live brush whenever it applies to the currently running theme.
	/// </summary>
	bool SetColor( string key, ArgbColor color, string targetTheme );

	/// <summary>Restores <paramref name="key"/> in the active theme to the built-in color.</summary>
	void ResetColor( string key );

	/// <summary>Restores <paramref name="key"/> in <paramref name="targetTheme"/> (<c>"active"</c>, <c>"dark"</c>, <c>"light"</c>, or <c>"both"</c>).</summary>
	void ResetColor( string key, string targetTheme );

	/// <summary>Restores every color override of the active theme.</summary>
	void ResetAll();

	/// <summary>Applies independent style/density/corner/border/font overrides live and stores them in <see cref="Settings"/>.</summary>
	void ApplyStyleOverrides( Action<ThemeStyleOverrides> mutate );

	/// <summary>Clears all independent style overrides back to the active <see cref="Form"/> preset.</summary>
	void ResetStyleOverrides();

	/// <summary>Applies independent dependency graph color/shape/layout overrides live and stores them in <see cref="Settings"/>.</summary>
	void ApplyGraphOverrides( Action<GraphThemeOverrides> mutate );

	/// <summary>Clears all independent dependency graph overrides back to the active preset.</summary>
	void ResetGraphOverrides();

	/// <summary>Snapshots the current complete theme (mode, palette, form, colors, style, graph) as a named custom theme in <see cref="Settings"/>.</summary>
	SavedThemePreset SaveCustomTheme( string name, string? description = null );

	/// <summary>Applies a previously saved custom theme preset by name.</summary>
	SavedThemePreset LoadCustomTheme( string name );

	/// <summary>Deletes a saved custom theme preset by name.</summary>
	bool DeleteCustomTheme( string name );

	/// <summary>Lists all saved custom theme presets.</summary>
	IReadOnlyList<SavedThemePreset> ListCustomThemes();

	/// <summary>Restores every theme setting (palette, form, mode, colors, branch lines, style, graph) to HackPDM factory defaults.</summary>
	void ResetToFactoryDefaults( bool clearCustomThemes = false );

	/// <summary>Re-applies all live resource brushes, panel borders, fonts, and window backgrounds from <see cref="Settings"/>.</summary>
	void ReapplyLiveResources();

	/// <summary>Marks the current style/form settings as freshly applied after an in-process soft UI reload.</summary>
	void MarkStyleSnapshotApplied();

	/// <summary>Discards unsaved changes: re-applies the theme as last saved in AppConfig.json.</summary>
	void Revert();

	/// <summary>Writes the theme to <see cref="SavePath"/>.</summary>
	void Save();
}

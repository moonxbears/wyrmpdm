using System.Diagnostics;
using System.Text.Json;

using HackPDM.Core.Graph;
using HackPDM.Core.Theming;
using HackPDM.Mcp;
using HackPDM.Mcp.Bridge;
using HackPDM.Mcp.Protocol;
using HackPDM.Mcp.Security;
using HackPDM.Shared.GlobalData;
using HackPDM.Tests.Characterization;

namespace HackPDM.Tests.Mcp;

[Collection( SharedStateCollection.Name )]
public sealed class McpThemeAndUiCustomizationTests : IDisposable
{
	private readonly string _tempDir;
	private readonly string _configDir;
	private readonly string _dataDir;
	private readonly string? _prevConfigEnv;

	public McpThemeAndUiCustomizationTests()
	{
		_tempDir = Path.Combine( Path.GetTempPath(), "hackpdm-mcp-theme-tests-" + Guid.NewGuid().ToString( "N" )[ ..8 ] );
		_configDir = Path.Combine( _tempDir, "config" );
		_dataDir = Path.Combine( _tempDir, "data" );
		Directory.CreateDirectory( _configDir );
		Directory.CreateDirectory( _dataDir );

		_prevConfigEnv = Environment.GetEnvironmentVariable( "HACKPDM_CONFIG_DIR" );
		Environment.SetEnvironmentVariable( "HACKPDM_CONFIG_DIR", _configDir );
	}

	public void Dispose()
	{
		Environment.SetEnvironmentVariable( "HACKPDM_CONFIG_DIR", _prevConfigEnv );
		try { if( Directory.Exists( _tempDir ) ) Directory.Delete( _tempDir, recursive: true ); } catch { }
	}

	[Fact]
	public void ThemeStyleOverrides_AndGraphThemeOverrides_ApplyIndependently_AndClearCleanly()
	{
		AppForm baseForm = ThemePresets.Form( FormName.Default );
		AppPalette darkPalette = ThemePresets.Palette( PaletteName.Dark );

		ThemeStyleOverrides style = new()
		{
			CompactDensity = false,
			ControlCornerRadius = 14,
			OverlayCornerRadius = 20,
			PanelCornerRadius = 18,
			PanelBorderThickness = 2.5,
			AccentPanels = true,
			PanelStrokeColor = "#FF00E5FF",
			FontFamily = "Inter",
			BaseFontSize = 15,
			ContentPadding = 16,
		};

		Assert.False( style.IsEmpty );
		AppForm effForm = style.ApplyTo( baseForm );
		Assert.False( effForm.CompactDensity );
		Assert.Equal( 14, effForm.ControlCornerRadius );
		Assert.Equal( 20, effForm.OverlayCornerRadius );
		Assert.Equal( 18, effForm.PanelCornerRadius );
		Assert.Equal( 2.5, effForm.PanelBorderThickness );
		Assert.True( effForm.AccentPanels );
		Assert.Equal( 0xFF00E5FFu, style.ResolvePanelStroke( darkPalette, effForm ) );

		style.Clear();
		Assert.True( style.IsEmpty );
		Assert.Equal( baseForm, style.ApplyTo( baseForm ) );
		Assert.Equal( darkPalette.PanelStroke, style.ResolvePanelStroke( darkPalette, baseForm ) );

		GraphTheme baseGraph = GraphTheme.Default;
		GraphThemeOverrides gOv = new()
		{
			Backdrop = "#FF090D16",
			LinkHighlight = "#FF00FFAA",
			Header = HeaderStyle.Gradient,
			Links = LinkStyle.GlowCurve,
			CornerRadius = 15,
			ShowGrid = true,
			HollowPorts = true,
			ColumnGap = 190,
			RowGap = 44,
			MinWidth = 200,
			MaxWidth = 360,
			NodeHeaders = new( StringComparer.OrdinalIgnoreCase )
			{
				[ "Assembly" ] = "#FF11CC88",
				[ "Part" ] = "#FF2288FF",
			},
		};

		GraphTheme effGraph = gOv.ApplyTo( baseGraph );
		Assert.Equal( 0xFF090D16u, effGraph.Palette.Backdrop );
		Assert.Equal( 0xFF00FFAAu, effGraph.Palette.LinkHighlight );
		Assert.Equal( 0xFF11CC88u, effGraph.Palette.Header( NodeKind.Assembly ) );
		Assert.Equal( 0xFF2288FFu, effGraph.Palette.Header( NodeKind.Part ) );
		Assert.Equal( baseGraph.Palette.Header( NodeKind.Pdf ), effGraph.Palette.Header( NodeKind.Pdf ) );
		Assert.Equal( HeaderStyle.Gradient, effGraph.Form.Header );
		Assert.Equal( LinkStyle.GlowCurve, effGraph.Form.Links );
		Assert.Equal( 15, effGraph.Form.CornerRadius );
		Assert.True( effGraph.Form.ShowGrid );
		Assert.True( effGraph.Form.HollowPorts );
		Assert.Equal( 190, effGraph.Form.Layout.ColumnGap );
		Assert.Equal( 44, effGraph.Form.Layout.RowGap );
		Assert.Equal( 200, effGraph.Form.Layout.MinWidth );
		Assert.Equal( 360, effGraph.Form.Layout.MaxWidth );

		gOv.Clear();
		Assert.True( gOv.IsEmpty );
		Assert.Equal( baseGraph, gOv.ApplyTo( baseGraph ) );
	}

	[Fact]
	public async Task UiThemeControl_Headless_EditsIndependently_SavesLoadsCustomThemes_AndResetsToDefaults()
	{
		using var bundle = HackPdmMcpServerFactory.Create(
			McpServerMode.Headless,
			dataDirOverride: _dataDir );

		// 1. Customize colors (both Dark and Light), style, graph, and save as a named custom theme
		McpToolResult editRes = await bundle.Server.CallToolAsync(
			"ui_theme_control",
			JsonArgs( new
			{
				palette = "NavyBlue",
				form = "Modern",
				showTreeBranchLines = false,
				targetTheme = "both",
				colors = new Dictionary<string, string>
				{
					[ "AccentFillColorDefaultBrush" ] = "#FF00E5FF",
					[ "ApplicationPageBackgroundThemeBrush" ] = "#FF07111F",
				},
				style = new
				{
					compactDensity = false,
					controlCornerRadius = 12,
					panelCornerRadius = 18,
					panelBorderThickness = 2,
					panelStrokeColor = "#FF00E5FF",
					fontFamily = "Inter",
					baseFontSize = 14.5,
				},
				graph = new
				{
					backdrop = "#FF050B14",
					headerStyle = "Gradient",
					linkStyle = "GlowCurve",
					showGrid = true,
					columnGap = 175,
					headers = new Dictionary<string, string> { [ "Assembly" ] = "#FF00FFAA" },
				},
				saveCustomThemeName = "Neon Blueprint",
				customThemeDescription = "High-contrast neon blueprint styling",
				save = true,
			} ) );

		Assert.True( editRes.IsError != true, editRes.Content[ 0 ].Text );
		using JsonDocument doc1 = JsonDocument.Parse( editRes.Content[ 0 ].Text! );
		Assert.True( doc1.RootElement.GetProperty( "saved" ).GetBoolean() );
		Assert.Equal( "NavyBlue", doc1.RootElement.GetProperty( "palette" ).GetString() );
		Assert.Equal( "Modern", doc1.RootElement.GetProperty( "form" ).GetString() );
		Assert.False( doc1.RootElement.GetProperty( "showTreeBranchLines" ).GetBoolean() );
		Assert.Equal( "Neon Blueprint", doc1.RootElement.GetProperty( "activeCustomTheme" ).GetString() );
		Assert.Equal( "#FF00E5FF", doc1.RootElement.GetProperty( "darkOverrides" ).GetProperty( "AccentFillColorDefaultBrush" ).GetString() );
		Assert.Equal( "#FF00E5FF", doc1.RootElement.GetProperty( "lightOverrides" ).GetProperty( "AccentFillColorDefaultBrush" ).GetString() );
		Assert.Equal( "Inter", doc1.RootElement.GetProperty( "effectiveStyle" ).GetProperty( "fontFamily" ).GetString() );
		Assert.Equal( 14.5, doc1.RootElement.GetProperty( "effectiveStyle" ).GetProperty( "baseFontSize" ).GetDouble() );
		Assert.Equal( "GlowCurve", doc1.RootElement.GetProperty( "effectiveGraph" ).GetProperty( "form" ).GetProperty( "linkStyle" ).GetString() );
		Assert.Equal( "#FF00FFAA", doc1.RootElement.GetProperty( "effectiveGraph" ).GetProperty( "palette" ).GetProperty( "headers" ).GetProperty( "Assembly" ).GetString() );
		Assert.True( File.Exists( Path.Combine( _configDir, "AppConfig.json" ) ) );

		// 2. Static resource hackpdm://ui/theme reflects saved changes
		McpResourceContent themeResource = await bundle.Server.ReadResourceAsync( "hackpdm://ui/theme" );
		Assert.Contains( "Neon Blueprint", themeResource.Text );
		Assert.Contains( "#FF00E5FF", themeResource.Text );

		// 3. Reset to factory defaults (preserves saved custom theme library by default)
		McpToolResult resetRes = await bundle.Server.CallToolAsync(
			"ui_theme_control",
			JsonArgs( new { resetToDefaults = true, save = true } ) );
		Assert.True( resetRes.IsError != true, resetRes.Content[ 0 ].Text );
		using JsonDocument docReset = JsonDocument.Parse( resetRes.Content[ 0 ].Text! );
		Assert.Equal( "Dark", docReset.RootElement.GetProperty( "palette" ).GetString() );
		Assert.Equal( "Default", docReset.RootElement.GetProperty( "form" ).GetString() );
		Assert.True( docReset.RootElement.GetProperty( "showTreeBranchLines" ).GetBoolean() );
		Assert.Empty( docReset.RootElement.GetProperty( "darkOverrides" ).EnumerateObject() );
		Assert.Equal( 1, docReset.RootElement.GetProperty( "customThemes" ).GetArrayLength() );

		// 4. Reload the saved "Neon Blueprint" custom theme cleanly
		McpToolResult reloadRes = await bundle.Server.CallToolAsync(
			"ui_theme_control",
			JsonArgs( new { loadCustomThemeName = "Neon Blueprint", save = true } ) );
		Assert.True( reloadRes.IsError != true, reloadRes.Content[ 0 ].Text );
		using JsonDocument docReload = JsonDocument.Parse( reloadRes.Content[ 0 ].Text! );
		Assert.Equal( "NavyBlue", docReload.RootElement.GetProperty( "palette" ).GetString() );
		Assert.Equal( "Inter", docReload.RootElement.GetProperty( "effectiveStyle" ).GetProperty( "fontFamily" ).GetString() );
		Assert.Equal( "#FF00FFAA", docReload.RootElement.GetProperty( "effectiveGraph" ).GetProperty( "palette" ).GetProperty( "headers" ).GetProperty( "Assembly" ).GetString() );
	}

	[Fact]
	public async Task UiCustomizeConfig_Headless_EditsAndResetsAllAppConfigSections()
	{
		using var bundle = HackPdmMcpServerFactory.Create(
			McpServerMode.Headless,
			dataDirOverride: _dataDir );

		McpToolResult res = await bundle.Server.CallToolAsync(
			"ui_customize_config",
			JsonArgs( new
			{
				entryColors = new { OrangeEntry = "#FFFF6600", BlueEntry = "#FF00AAFF" },
				columns = new
				{
					rowHeight = 26,
					fontSize = 13,
					groupingEnabled = false,
					groupByField = "Type",
					columns = new { Name = new { width = 340, header = "CAD File" } },
				},
				workspaceLayout = new
				{
					foldersDock = "Right",
					detailsDock = "Right",
					foldersPaneWidth = 320,
					showPreviewPane = false,
				},
				windowSettings = new
				{
					HackFileManager = new { title = "Custom HackPDM Workspace", width = 1600, height = 960 },
				},
				save = true,
			} ) );

		Assert.True( res.IsError != true, res.Content[ 0 ].Text );
		using JsonDocument doc = JsonDocument.Parse( res.Content[ 0 ].Text! );
		JsonElement cfg = doc.RootElement.GetProperty( "config" );
		Assert.Equal( "#FFFF6600", cfg.GetProperty( "OdooEntryColors" ).GetProperty( "OrangeEntry" ).GetString() );
		Assert.Equal( 26, cfg.GetProperty( "ColumnSettings" ).GetProperty( "RowHeight" ).GetDouble() );
		Assert.Equal( "CAD File", cfg.GetProperty( "ColumnSettings" ).GetProperty( "Columns" ).GetProperty( "Name" ).GetProperty( "Column" ).GetProperty( "Header" ).GetString() );
		Assert.Equal( "Right", cfg.GetProperty( "WorkspaceLayout" ).GetProperty( "FoldersDock" ).GetString() );
		Assert.Equal( "Custom HackPDM Workspace", cfg.GetProperty( "WindowSettings" ).GetProperty( "Windows" ).GetProperty( "HackFileManager" ).GetProperty( "Title" ).GetString() );

		// Factory reset all sections
		McpToolResult reset = await bundle.Server.CallToolAsync(
			"ui_customize_config",
			JsonArgs( new { resetSection = "all", save = true } ) );
		Assert.True( reset.IsError != true, reset.Content[ 0 ].Text );
		using JsonDocument resetDoc = JsonDocument.Parse( reset.Content[ 0 ].Text! );
		Assert.Empty( resetDoc.RootElement.GetProperty( "config" ).EnumerateObject() );
	}

	[Fact]
	public void PermissionClassification_AllowsThemeAndConfigReadsInReadOnly_AndDifferentiatesSoftVsFullRestart()
	{
		string permFile = Path.Combine( _configDir, "mcp-permissions.json" );
		McpPermissionManager perms = new( permFile, defaultPresetWhenUnconfigured: "read-only", persistChanges: false );

		// Read-only calls to ui_theme_control & ui_customize_config are permitted in read-only mode
		Assert.True( perms.Evaluate( "ui_theme_control", null ).Allowed );
		Assert.True( perms.Evaluate( "ui_theme_control", JsonArgs( new { } ) ).Allowed );
		Assert.True( perms.Evaluate( "ui_customize_config", JsonArgs( new { } ) ).Allowed );

		// Mutating calls are blocked in read-only mode
		Assert.False( perms.Evaluate( "ui_theme_control", JsonArgs( new { palette = "NavyBlue" } ) ).Allowed );
		Assert.False( perms.Evaluate( "ui_customize_config", JsonArgs( new { resetSection = "all" } ) ).Allowed );
		Assert.False( perms.Evaluate( "ui_app_restart", JsonArgs( new { mode = "soft" } ) ).Allowed );

		// In review-critical mode: soft restart is StandardAction (auto); full restart is CriticalAction (needs approval)
		perms.SelectProfile( "review-critical" );
		McpPermissionEvaluation softEval = perms.Evaluate( "ui_app_restart", JsonArgs( new { mode = "soft" } ) );
		Assert.True( softEval.Allowed );
		Assert.False( softEval.RequiresReview );

		McpPermissionEvaluation fullEval = perms.Evaluate( "ui_app_restart", JsonArgs( new { mode = "full" } ) );
		Assert.True( fullEval.Allowed );
		Assert.True( fullEval.RequiresReview );

		// In local-only mode: all three are local (non-RPC) and allowed!
		perms.SelectProfile( "local-only" );
		Assert.True( perms.Evaluate( "ui_theme_control", JsonArgs( new { palette = "NavyBlue" } ) ).Allowed );
		Assert.True( perms.Evaluate( "ui_customize_config", JsonArgs( new { save = true } ) ).Allowed );
		Assert.True( perms.Evaluate( "ui_app_restart", JsonArgs( new { mode = "auto" } ) ).Allowed );
	}

	[Fact]
	public async Task CliResetUiDefaults_BacksUpAndResetsAppConfigJson()
	{
		string? mcpDll = LocateMcpServerDll();
		Assert.False( string.IsNullOrWhiteSpace( mcpDll ), "HackPDM.McpServer.dll not found." );

		string appConfigPath = Path.Combine( _configDir, "AppConfig.json" );
		File.WriteAllText( appConfigPath, """{"Theme":{"Palette":"NavyBlue","Form":"Fancy"},"WorkspaceLayout":{"ShowFoldersPane":false}}""" );

		ProcessStartInfo psi = new()
		{
			FileName = "dotnet",
			Arguments = $"\"{mcpDll}\" --data-dir \"{_dataDir}\" --reset-ui-defaults",
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true,
		};
		psi.Environment[ "HACKPDM_CONFIG_DIR" ] = _configDir;

		using Process proc = Process.Start( psi )!;
		string stdout = await proc.StandardOutput.ReadToEndAsync();
		string stderr = await proc.StandardError.ReadToEndAsync();
		await proc.WaitForExitAsync();

		Assert.True( proc.ExitCode == 0, $"--reset-ui-defaults failed ({proc.ExitCode}): {stderr}" );
		Assert.Contains( "\"ok\": true", stdout );
		Assert.Contains( "\"resetScope\": \"all\"", stdout );

		using JsonDocument after = JsonDocument.Parse( File.ReadAllText( appConfigPath ) );
		Assert.False( after.RootElement.TryGetProperty( "Theme", out _ ) );
		Assert.False( after.RootElement.TryGetProperty( "WorkspaceLayout", out _ ) );
	}

	[Fact]
	public void GuidedActionTools_AreClassified_RespectModes_AndBlockSelfClick()
	{
		string permFile = Path.Combine( _configDir, "mcp-permissions.json" );
		McpPermissionManager perms = new( permFile, defaultPresetWhenUnconfigured: "review-critical", persistChanges: false );

		// In review-critical mode, guided visual cues run automatically (non-destructive StandardAction)
		foreach( string tool in new[] { "ui_guide_highlight", "ui_guide_popup", "ui_guide_tour", "ui_guide_clear" } )
		{
			Assert.True( McpToolCatalog.IsClassified( tool ), $"Expected '{tool}' to be classified." );
			McpPermissionEvaluation eval = perms.Evaluate( tool );
			Assert.True( eval.Allowed, $"Expected '{tool}' allowed in review-critical." );
			Assert.False( eval.RequiresReview, $"Expected '{tool}' not to require review in review-critical." );
			Assert.False( eval.IsRemoteRpc );
		}

		// In read-only mode, read-only status/list calls work while mutating visual overlays are blocked
		perms.SelectProfile( "read-only" );
		Assert.True( perms.Evaluate( "ui_guide_clear", JsonArgs( new { listOnly = true } ) ).Allowed );
		Assert.True( perms.Evaluate( "ui_guide_tour", JsonArgs( new { action = "status" } ) ).Allowed );
		Assert.False( perms.Evaluate( "ui_guide_highlight", JsonArgs( new { name = "PalettePicker" } ) ).Allowed );
		Assert.False( perms.Evaluate( "ui_guide_popup", JsonArgs( new { title = "Hi", message = "Test" } ) ).Allowed );

		// Even in auto mode, AI cannot click its own guided popup or tour buttons via ui_click_control
		perms.SelectProfile( "auto" );
		Assert.False( perms.Evaluate( "ui_click_control", JsonArgs( new { name = "McpGuidedPopupButton_confirm" } ) ).Allowed );
		Assert.False( perms.Evaluate( "ui_click_control", JsonArgs( new { name = "McpGuidedTourButton_next" } ) ).Allowed );

		// Prompt 'guide-user' is registered
		using var bundle = HackPdmMcpServerFactory.Create( McpServerMode.Headless, dataDirOverride: _dataDir );
		Assert.Contains( bundle.Server.GetPromptDefinitions(), p => p.Name == "guide-user" );
	}

	private static JsonElement JsonArgs( object obj )
	{
		string json = JsonSerializer.Serialize( obj );
		using JsonDocument doc = JsonDocument.Parse( json );
		return doc.RootElement.Clone();
	}

	private static string? LocateMcpServerDll()
	{
		for( DirectoryInfo? dir = new( AppContext.BaseDirectory ); dir is not null; dir = dir.Parent )
		{
			string dbg = Path.Combine( dir.FullName, "Src", "HackPDM.McpServer", "bin", "Debug", "net10.0", "HackPDM.McpServer.dll" );
			if( File.Exists( dbg ) )
				return dbg;
			string rel = Path.Combine( dir.FullName, "Src", "HackPDM.McpServer", "bin", "Release", "net10.0", "HackPDM.McpServer.dll" );
			if( File.Exists( rel ) )
				return rel;
		}
		return null;
	}
}

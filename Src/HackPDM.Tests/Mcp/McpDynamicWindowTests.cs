using System.Text.Json;

using HackPDM.Mcp;
using HackPDM.Mcp.Bridge;
using HackPDM.Mcp.DynamicUi;
using HackPDM.Mcp.Protocol;
using HackPDM.Mcp.Security;
using HackPDM.Shared.GlobalData;
using HackPDM.Tests.Characterization;

using Xunit;

namespace HackPDM.Tests.Mcp;

[Collection( SharedStateCollection.Name )]
public sealed class McpDynamicWindowTests : IDisposable
{
	private readonly string _tempRoot;
	private readonly string _dataDir;

	public McpDynamicWindowTests()
	{
		_tempRoot = Path.Combine( Path.GetTempPath(), "hackpdm_mcp_dynwin_" + Guid.NewGuid().ToString( "N" )[ ..8 ] );
		_dataDir = Path.Combine( _tempRoot, "mcpdata" );
		Directory.CreateDirectory( _dataDir );
	}

	public void Dispose()
	{
		try
		{
			if( Directory.Exists( _tempRoot ) )
				Directory.Delete( _tempRoot, recursive: true );
		}
		catch
		{
		}
	}

	[Fact]
	public void ParseCreate_ParsesAll21ComponentTypes_Aliases_AndGeneratesMarkdownSummary()
	{
		JsonElement payload = JsonArgs( new
		{
			windowId = "bom-dashboard",
			title = "Drive Assembly BOM & Commit Readiness",
			subtitle = "Restructured from local SolidWorks + Odoo metadata",
			badge = "BOM AUDIT",
			severity = "action",
			width = 1120,
			height = 820,
			alwaysOnTop = true,
			components = new object[]
			{
				new
				{
					type = "metrics",
					items = new object[]
					{
						new { id = "kpi_total", label = "Total Parts", value = 14, subtitle = "2 assemblies, 12 parts", severity = "info" },
						new { id = "kpi_ready", label = "Commit Ready", value = "12 / 14", badge = "86%", severity = "success" },
						new { id = "kpi_broken", label = "Broken Refs", value = 1, badge = "ACTION", severity = "critical" },
					},
				},
				new
				{
					type = "alert",
					id = "readiness_alert",
					title = "External Reference Detected",
					message = "Bracket_04.SLDPRT points outside the PWA root.",
					severity = "warning",
				},
				new
				{
					type = "progress",
					id = "scan_progress",
					label = "Validation Progress",
					value = 85,
					max = 100,
					statusText = "12 of 14 verified",
				},
				new
				{
					type = "tabs",
					id = "main_tabs",
					tabs = new object[]
					{
						new
						{
							id = "tab_table",
							title = "BOM Table",
							badge = "3",
							children = new object[]
							{
								new
								{
									type = "data_table",
									id = "bom_table",
									title = "Component Rollup",
									searchable = true,
									rowSelectable = true,
									columns = new object[]
									{
										new { key = "part", header = "Part File", width = "2*", monospace = true },
										new { key = "qty", header = "Qty", width = "70", align = "right" },
										new { key = "status", header = "Status", width = "130", isBadge = true },
									},
									rows = new object[]
									{
										new { part = "Drive_Main.SLDASM", qty = 1, status = "Modified" },
										new object[] { "Shaft_01.SLDPRT", 2, "Synced" },
										new { part = "Bracket_04.SLDPRT", qty = 4, status = "Broken Ref" },
									},
								},
							},
						},
						new
						{
							id = "tab_tree",
							title = "Hierarchy",
							children = new object[]
							{
								new
								{
									type = "bom_tree",
									title = "Assembly Tree",
									nodes = new object[]
									{
										new
										{
											label = "Drive_Main.SLDASM",
											badge = "ASM",
											children = new object[]
											{
												new { label = "Shaft_01.SLDPRT", subtitle = "Rev B • 4140 Steel", badge = "OK" },
												new { label = "Bracket_04.SLDPRT", subtitle = "External path", badge = "Broken", severity = "critical" },
											},
										},
									},
								},
							},
						},
					},
				},
				new
				{
					type = "columns",
					columns = new[] { "1*", "1*" },
					children = new object[]
					{
						new
						{
							type = "card",
							title = "SolidWorks Properties",
							collapsible = true,
							children = new object[]
							{
								new
								{
									type = "properties",
									columns = 1,
									items = new object[]
									{
										new { key = "Material", value = "6061-T6 Aluminum", badge = "LOCAL" },
										new { key = "Revision", value = "Rev C", monospace = true },
									},
								},
								new { type = "tags", items = new object[] { "SolidWorks", new { label = "Needs Repath", severity = "warning" } } },
							},
						},
						new
						{
							type = "card",
							title = "Notes & Payload",
							children = new object[]
							{
								new { type = "markdown", content = "## Next Steps\n- Import external part into PWA\n> Automatic restore point will be created" },
								new { type = "json", title = "Repath Plan", content = new { file = "Bracket_04.SLDPRT", action = "import_to_pwa" } },
								new { type = "list", items = new object[] { new { title = "Auto-backup enabled", badge = "SAFE", severity = "success" } } },
							},
						},
					},
				},
				new { type = "divider", label = "Interactive Options" },
				new
				{
					type = "row",
					children = new object[]
					{
						new { type = "heading", text = "User Review Controls" },
						new { type = "image", base64 = "iVBORw0KGgo=", caption = "Preview Thumbnail" },
					},
				},
				new { type = "input", id = "commit_note", label = "Commit Comment", value = "Fixed bracket reference" },
				new { type = "checkbox", id = "create_rp", label = "Create Restore Point", @checked = true },
				new { type = "dropdown", id = "target_dir", label = "Import Destination", options = new[] { "root\\Parts", "root\\Hardware" }, selected = "root\\Parts" },
				new
				{
					type = "actions",
					buttons = new object[]
					{
						new { id = "import_fix", label = "Import & Fix Refs", accent = true, action = "emit" },
						new { id = "copy_md", label = "Copy JSON", action = "copy", copyText = "{}" },
						new { id = "open_fm", label = "Open File Manager", action = "navigate", navigatePage = "HackFileManager" },
						new { id = "close_win", label = "Dismiss", action = "close" },
					},
				},
			},
		} );

		DynamicWindowSpec spec = DynamicWindowSpecParser.ParseCreate( payload );
		Assert.Equal( "bom-dashboard", spec.WindowId );
		Assert.Equal( "Drive Assembly BOM & Commit Readiness", spec.Title );
		Assert.Equal( "BOM AUDIT", spec.Badge );
		Assert.Equal( 1120, spec.Width );
		Assert.Equal( 820, spec.Height );
		Assert.True( spec.AlwaysOnTop );
		Assert.Equal( 11, spec.Components.Count );

		// Verify alias normalization & structured parsing
		Assert.Equal( "kpi_row", spec.Components[ 0 ].Type );
		Assert.Equal( 3, spec.Components[ 0 ].KpiItems.Count );
		Assert.Equal( "callout", spec.Components[ 1 ].Type );
		Assert.Equal( "progress", spec.Components[ 2 ].Type );
		Assert.Equal( "tabs", spec.Components[ 3 ].Type );
		DynamicComponentSpec table = spec.Components[ 3 ].Tabs[ 0 ].Children[ 0 ];
		Assert.Equal( "table", table.Type );
		Assert.Equal( 3, table.TableRows.Count );
		Assert.Equal( "Shaft_01.SLDPRT", table.TableRows[ 1 ][ "part" ] );
		Assert.Equal( "Synced", table.TableRows[ 1 ][ "status" ] );

		Assert.Equal( "grid", spec.Components[ 4 ].Type );
		Assert.Equal( 2, spec.Components[ 4 ].ColumnCount );
		DynamicComponentSpec jsonCard = spec.Components[ 4 ].Children[ 1 ].Children[ 1 ];
		Assert.Equal( "code", jsonCard.Type );
		Assert.Equal( "json", jsonCard.Language );
		Assert.Contains( "\"import_to_pwa\"", jsonCard.Content );

		Assert.Equal( "divider", spec.Components[ 5 ].Type );
		Assert.Equal( "stack", spec.Components[ 6 ].Type );
		Assert.Equal( "horizontal", spec.Components[ 6 ].Orientation );
		Assert.Equal( "input", spec.Components[ 7 ].Type );
		Assert.Equal( "checkbox", spec.Components[ 8 ].Type );
		Assert.True( spec.Components[ 8 ].Checked );
		Assert.Equal( "select", spec.Components[ 9 ].Type );
		Assert.Equal( 2, spec.Components[ 9 ].Options.Count );
		Assert.Equal( "button_row", spec.Components[ 10 ].Type );
		Assert.Equal( 4, spec.Components[ 10 ].Buttons.Count );
		Assert.Equal( "navigate", spec.Components[ 10 ].Buttons[ 2 ].Action );

		// Verify Markdown summary builder covers key sections
		string md = DynamicWindowSpecParser.BuildTextSummary( spec );
		Assert.Contains( "# Drive Assembly BOM & Commit Readiness", md );
		Assert.Contains( "**Total Parts:** 14", md );
		Assert.Contains( "| Part File | Qty | Status |", md );
		Assert.Contains( "Drive_Main.SLDASM", md );
		Assert.Contains( "6061-T6 Aluminum", md );
	}

	[Fact]
	public void ParseCreate_AndParseUpdate_RejectInvalidSpecsWithClearErrors()
	{
		Assert.Throws<ArgumentException>( () => DynamicWindowSpecParser.ParseCreate( JsonArgs( new { components = Array.Empty<object>() } ) ) );
		Assert.Throws<ArgumentException>( () => DynamicWindowSpecParser.ParseCreate( JsonArgs( new { title = "Empty", components = Array.Empty<object>() } ) ) );
		Assert.Throws<ArgumentException>( () => DynamicWindowSpecParser.ParseCreate( JsonArgs( new { title = "Bad", components = new object[] { new { type = "nonexistent_widget" } } } ) ) );
		Assert.Throws<ArgumentException>( () => DynamicWindowSpecParser.ParseCreate( JsonArgs( new { title = "NoImg", components = new object[] { new { type = "image" } } } ) ) );
		Assert.Throws<ArgumentException>( () => DynamicWindowSpecParser.ParseCreate( JsonArgs( new { title = "NoTabs", components = new object[] { new { type = "tabs", tabs = Array.Empty<object>() } } } ) ) );
		Assert.Throws<ArgumentException>( () => DynamicWindowSpecParser.ParseUpdate( JsonArgs( new { title = "Missing Id" } ) ) );
		Assert.Throws<ArgumentException>( () => DynamicWindowSpecParser.ParseUpdate( JsonArgs( new { windowId = "w1", patches = new object[] { new { text = "no id" } } } ) ) );
	}

	[Fact]
	public void ParseUpdate_ParsesTitlePatchesAppendAndSizeClamping()
	{
		DynamicWindowUpdateSpec update = DynamicWindowSpecParser.ParseUpdate( JsonArgs( new
		{
			windowId = "bom-dashboard",
			title = "Updated BOM Dashboard",
			width = 99999,
			height = 10,
			focus = true,
			appendComponents = new object[] { new { type = "text", text = "All checks complete." } },
			patches = new object[]
			{
				new { id = "scan_progress", numericValue = 100, statusText = "14 of 14 verified", severity = "success" },
				new { id = "readiness_alert", title = "Ready to Commit", message = "0 broken references.", severity = "success" },
				new { id = "bom_table", rows = new object[] { new { part = "Drive_Main.SLDASM", qty = 1, status = "Ready" } } },
			},
		} ) );

		Assert.Equal( "bom-dashboard", update.WindowId );
		Assert.Equal( 2400, update.Width );
		Assert.Equal( 260, update.Height );
		Assert.True( update.Focus );
		Assert.Single( update.AppendComponents! );
		Assert.Equal( 3, update.Patches!.Count );
		Assert.Equal( 100, update.Patches[ 0 ].NumericValue );
		Assert.Equal( "success", update.Patches[ 1 ].Severity );
		Assert.Single( update.Patches[ 2 ].Rows! );
	}

	[Fact]
	public void DynamicWindowTools_RespectPermissionModes_BlockSelfClick_AndRegisterPrompt()
	{
		string permFile = Path.Combine( _tempRoot, "mcp-permissions.json" );
		McpPermissionManager perms = new( permFile, defaultPresetWhenUnconfigured: "review-critical", persistChanges: false );

		string[] allTools = [ "ui_window_create", "ui_window_update", "ui_window_list", "ui_window_events", "ui_window_close" ];
		foreach( string tool in allTools )
		{
			Assert.True( McpToolCatalog.IsClassified( tool ), $"Expected '{tool}' to be classified." );
			McpPermissionEvaluation eval = perms.Evaluate( tool );
			Assert.True( eval.Allowed, $"Expected '{tool}' allowed in review-critical." );
			Assert.False( eval.RequiresReview, $"Expected '{tool}' to run without review in review-critical." );
			Assert.False( eval.IsRemoteRpc );
		}

		// In local-only mode, all 5 dynamic window tools remain allowed (0 remote RPC)
		perms.SelectProfile( "local-only" );
		foreach( string tool in allTools )
			Assert.True( perms.Evaluate( tool ).Allowed, $"Expected '{tool}' allowed in local-only mode." );

		// In read-only mode, listing & passive event inspection work while create/update/close & waitForEvent are blocked
		perms.SelectProfile( "read-only" );
		Assert.True( perms.Evaluate( "ui_window_list" ).Allowed );
		Assert.True( perms.Evaluate( "ui_window_events", JsonArgs( new { windowId = "w1", waitForEvent = false } ) ).Allowed );
		Assert.False( perms.Evaluate( "ui_window_events", JsonArgs( new { windowId = "w1", waitForEvent = true } ) ).Allowed );
		Assert.False( perms.Evaluate( "ui_window_create", JsonArgs( new { title = "X", components = new object[] { "hi" } } ) ).Allowed );
		Assert.False( perms.Evaluate( "ui_window_update", JsonArgs( new { windowId = "w1" } ) ).Allowed );
		Assert.False( perms.Evaluate( "ui_window_close", JsonArgs( new { windowId = "w1" } ) ).Allowed );

		// Even in auto mode, AI cannot click its own dynamic window buttons via ui_click_control
		perms.SelectProfile( "auto" );
		Assert.False( perms.Evaluate( "ui_click_control", JsonArgs( new { name = "McpDynamicWindowButton_bom_import_fix" } ) ).Allowed );

		// Verify prompt 'present-report' is registered
		using var bundle = HackPdmMcpServerFactory.Create( McpServerMode.Headless, dataDirOverride: _dataDir );
		Assert.Contains( bundle.Server.GetPromptDefinitions(), p => p.Name == "present-report" );
	}

	[Fact]
	public async Task DynamicWindowTools_DispatchThroughMcpServerToUiBridge()
	{
		StubDynamicWindowBridge ui = new();
		using var bundle = HackPdmMcpServerFactory.Create(
			McpServerMode.EmbeddedGui,
			localUiBridge: ui,
			dataDirOverride: _dataDir,
			defaultPermissionPresetWhenUnconfigured: "auto" );

		var createRes = await bundle.Server.CallToolAsync( "ui_window_create", JsonArgs( new
		{
			windowId = "summary-1",
			title = "Test Window",
			components = new object[] { new { type = "text", text = "Hello" } },
		} ) );
		Assert.NotEqual( true, createRes.IsError );
		Assert.Equal( 1, ui.CreateCalls );

		var updateRes = await bundle.Server.CallToolAsync( "ui_window_update", JsonArgs( new { windowId = "summary-1", title = "Updated" } ) );
		Assert.NotEqual( true, updateRes.IsError );
		Assert.Equal( 1, ui.UpdateCalls );

		var listRes = await bundle.Server.CallToolAsync( "ui_window_list", JsonArgs( new { } ) );
		Assert.NotEqual( true, listRes.IsError );
		Assert.Equal( 1, ui.ListCalls );

		var evRes = await bundle.Server.CallToolAsync( "ui_window_events", JsonArgs( new { windowId = "summary-1", waitForEvent = false } ) );
		Assert.NotEqual( true, evRes.IsError );
		Assert.Equal( 1, ui.EventsCalls );

		var closeRes = await bundle.Server.CallToolAsync( "ui_window_close", JsonArgs( new { windowId = "summary-1" } ) );
		Assert.NotEqual( true, closeRes.IsError );
		Assert.Equal( 1, ui.CloseCalls );
	}

	private static JsonElement JsonArgs( object obj )
	{
		string json = JsonSerializer.Serialize( obj );
		using JsonDocument doc = JsonDocument.Parse( json );
		return doc.RootElement.Clone();
	}

	private sealed class StubDynamicWindowBridge : IHackPdmUiBridge
	{
		public int CreateCalls { get; private set; }
		public int UpdateCalls { get; private set; }
		public int ListCalls { get; private set; }
		public int EventsCalls { get; private set; }
		public int CloseCalls { get; private set; }

		public Task<object> DynamicWindowCreateAsync( JsonElement? args, CancellationToken ct = default )
		{
			CreateCalls++;
			DynamicWindowSpec spec = DynamicWindowSpecParser.ParseCreate( args );
			return Task.FromResult<object>( new { ok = true, windowId = spec.WindowId, title = spec.Title } );
		}

		public Task<object> DynamicWindowUpdateAsync( JsonElement? args, CancellationToken ct = default )
		{
			UpdateCalls++;
			DynamicWindowUpdateSpec upd = DynamicWindowSpecParser.ParseUpdate( args );
			return Task.FromResult<object>( new { ok = true, windowId = upd.WindowId } );
		}

		public Task<object> DynamicWindowListAsync( CancellationToken ct = default )
		{
			ListCalls++;
			return Task.FromResult<object>( new { count = 1 } );
		}

		public Task<object> DynamicWindowEventsAsync( string windowId, bool waitForEvent, bool clearEvents, int timeoutSeconds, CancellationToken ct = default )
		{
			EventsCalls++;
			return Task.FromResult<object>( new { ok = true, windowId, eventCount = 0 } );
		}

		public Task<object> DynamicWindowCloseAsync( string? windowId, bool closeAll, CancellationToken ct = default )
		{
			CloseCalls++;
			return Task.FromResult<object>( new { ok = true, windowId } );
		}

		public Task<object> GetUiStateAsync( CancellationToken ct = default ) => Task.FromResult<object>( new { } );
		public Task<object> ListWindowsAsync( CancellationToken ct = default ) => Task.FromResult<object>( new { } );
		public Task<object> GetVisualTreeAsync( string? w, string? r, int d, bool i, bool io, CancellationToken ct = default ) => Task.FromResult<object>( new { } );
		public Task<object> FindControlsAsync( string? w, string? n, string? t, string? tx, string? a, bool v, bool io, int m, CancellationToken ct = default ) => Task.FromResult<object>( new { } );
		public Task<object> GetControlAsync( string? w, string? n, string? a, string? p, CancellationToken ct = default ) => Task.FromResult<object>( new { } );
		public Task<object> ClickControlAsync( string? w, string? n, string? a, string? p, string? t, CancellationToken ct = default ) => Task.FromResult<object>( new { } );
		public Task<object> SetControlValueAsync( string? w, string? n, string? a, string? p, JsonElement? v, int? si, bool? ic, CancellationToken ct = default ) => Task.FromResult<object>( new { } );
		public Task<object> InvokeCommandAsync( string? t, string? c, JsonElement? p, bool l, CancellationToken ct = default ) => Task.FromResult<object>( new { } );
		public Task<object> NavigateAsync( string? p, bool gb, string? sa, string? sea, bool oft, string? ss, CancellationToken ct = default ) => Task.FromResult<object>( new { } );
		public Task<object> FileManagerSelectFolderAsync( string? tp, int? did, bool? e, bool w, int to, CancellationToken ct = default ) => Task.FromResult<object>( new { } );
		public Task<object> FileManagerSelectEntriesAsync( IReadOnlyList<string>? n, IReadOnlyList<int>? ids, bool c, bool w, int to, CancellationToken ct = default ) => Task.FromResult<object>( new { } );
		public Task<object> FileManagerControlAsync( JsonElement? args, CancellationToken ct = default ) => Task.FromResult<object>( new { } );
		public Task<object> DependencyGraphControlAsync( JsonElement? args, CancellationToken ct = default ) => Task.FromResult<object>( new { } );
		public Task<object> SearchControlAsync( JsonElement? args, CancellationToken ct = default ) => Task.FromResult<object>( new { } );
		public Task<object> StatusPanelControlAsync( JsonElement? args, CancellationToken ct = default ) => Task.FromResult<object>( new { } );
		public Task<object> ThemeControlAsync( JsonElement? args, CancellationToken ct = default ) => Task.FromResult<object>( new { } );
		public Task<object> DialogRespondAsync( string? d, string? r, bool l, CancellationToken ct = default ) => Task.FromResult<object>( new { } );
		public Task<McpToolResult> CaptureScreenshotAsync( string? w, string? e, string? s, bool i, CancellationToken ct = default ) => Task.FromResult( McpToolResult.Ok( new { } ) );
		public Task<string> GetAppConfigJsonAsync( CancellationToken ct = default ) => Task.FromResult( "{}" );
		public Task SaveAppConfigJsonAsync( string json, CancellationToken ct = default ) => Task.CompletedTask;
		public void NotifyLoginChanged( bool isLoggedIn, string? accountKey ) { }
		public void NotifyServerMutated( bool treeChanged, bool entriesChanged ) { }
		public void MirrorStatusLog( StatusMessage status, string message ) { }
	}
}

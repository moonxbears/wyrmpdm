using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;

using HackPDM.Core.Theming;
using HackPDM.Mcp.DynamicUi;
using HackPDM.Mvvm.Models.Settings;
using HackPDM.Mvvm.ViewModels.Common;
using HackPDM.Mvvm.Views.Common.Controls;
using HackPDM.Mvvm.Views.Settings;
using HackPDM.Shared.GlobalData;

using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;
using Windows.UI;

namespace HackPDM.Shell.Mcp;

/// <summary>
/// Creates, updates, themes, and manages native HackPDM supplemental dynamic windows constructed
/// declaratively by MCP AI clients. Every dynamic window is registered with <see cref="WindowHelper"/>
/// (making it visible to <c>ui_list_windows</c>, <c>ui_visual_tree</c>, and <c>ui_screenshot</c>),
/// automatically follows live <see cref="AppComposition.Theme"/> changes, supports in-place live
/// patching without flickering, and exposes user interactions (inputs, selections, button clicks)
/// back to the MCP AI via <c>ui_window_events</c>.
/// </summary>
internal static class DynamicWindowManager
{
	private static readonly Dictionary<string, DynamicWindowInstance> Windows = new( StringComparer.OrdinalIgnoreCase );

	// ------------------------------------------------------------------ Public API (called on UI thread)

	public static Task<object> CreateOrReplaceWindowAsync( DynamicWindowSpec spec )
	{
		if( Windows.TryGetValue( spec.WindowId, out DynamicWindowInstance? existing ) )
		{
			if( !spec.ReplaceExisting )
				throw new InvalidOperationException( $"Dynamic window '{spec.WindowId}' is already open (pass replaceExisting: true or use ui_window_update)." );

			existing.Spec = spec;
			existing.RenderAll( preserveInputs: false );
			ApplyWindowSizing( existing.Window, spec );
			try { existing.Window.Activate(); } catch { }
			existing.SetStatusFeedback( "Window refreshed by AI" );

			return Task.FromResult<object>( BuildWindowSummary( existing, replaced: true ) );
		}

		Window win = new();
		DynamicWindowInstance instance = new( spec, win );
		Windows[ spec.WindowId ] = instance;

		WindowHelper.RegisterWindow( win, spec.WindowId );
		win.Closed += ( _, _ ) => OnWindowClosed( instance );

		instance.RenderAll( preserveInputs: false );
		win.Content = instance.RootGrid;
		AppComposition.Theme.Attach( win );
		AppComposition.Theme.ThemeChanged += instance.ThemeChangedHandler;

		win.Activate();
		ApplyWindowSizing( win, spec );

		return Task.FromResult<object>( BuildWindowSummary( instance, replaced: false ) );
	}

	public static Task<object> UpdateWindowAsync( DynamicWindowUpdateSpec update )
	{
		if( !Windows.TryGetValue( update.WindowId, out DynamicWindowInstance? instance ) )
			throw new InvalidOperationException( $"Dynamic window '{update.WindowId}' is not open." );

		bool fullRerender = false;
		int patchedCount = 0;
		List<string> missingPatchIds = [];

		if( !string.IsNullOrWhiteSpace( update.Title ) )
		{
			instance.Spec.Title = update.Title.Trim();
			instance.Window.Title = instance.Spec.Title;
			if( instance.HeaderTitleBlock is not null )
				instance.HeaderTitleBlock.Text = instance.Spec.Title;
		}

		if( update.Subtitle is not null )
		{
			instance.Spec.Subtitle = string.IsNullOrWhiteSpace( update.Subtitle ) ? null : update.Subtitle.Trim();
			if( instance.HeaderSubtitleBlock is not null )
			{
				instance.HeaderSubtitleBlock.Text = instance.Spec.Subtitle ?? string.Empty;
				instance.HeaderSubtitleBlock.Visibility = string.IsNullOrWhiteSpace( instance.Spec.Subtitle )
					? Visibility.Collapsed
					: Visibility.Visible;
			}
		}

		if( !string.IsNullOrWhiteSpace( update.Badge ) )
		{
			instance.Spec.Badge = update.Badge.Trim();
			if( instance.HeaderBadgeBlock is not null )
				instance.HeaderBadgeBlock.Text = instance.Spec.Badge.ToUpperInvariant();
		}

		if( !string.IsNullOrWhiteSpace( update.Severity ) || !string.IsNullOrWhiteSpace( update.AccentColor ) )
		{
			if( !string.IsNullOrWhiteSpace( update.Severity ) )
				instance.Spec.Severity = update.Severity;
			if( !string.IsNullOrWhiteSpace( update.AccentColor ) )
				instance.Spec.AccentColor = update.AccentColor;
			fullRerender = true;
		}

		if( update.ReplaceComponents is not null )
		{
			instance.Spec.Components = update.ReplaceComponents;
			fullRerender = true;
		}

		if( update.AppendComponents is { Count: > 0 } )
		{
			instance.Spec.Components.AddRange( update.AppendComponents );
			if( !fullRerender && instance.BodyStack is not null )
			{
				ThemePalette pal = ResolvePalette();
				foreach( DynamicComponentSpec comp in update.AppendComponents )
					instance.BodyStack.Children.Add( RenderComponent( instance, comp, pal ) );
				instance.UpdateFooterMeta();
			}
		}

		if( fullRerender )
			instance.RenderAll( preserveInputs: true );

		if( update.Patches is { Count: > 0 } )
		{
			ThemePalette pal = ResolvePalette();
			foreach( DynamicWindowPatch patch in update.Patches )
			{
				if( instance.PatchTargets.TryGetValue( patch.Id, out Action<DynamicWindowPatch, ThemePalette>? apply ) )
				{
					apply( patch, pal );
					patchedCount++;
				}
				else
				{
					missingPatchIds.Add( patch.Id );
				}
			}
		}

		if( update.Width.HasValue || update.Height.HasValue )
		{
			if( update.Width.HasValue )
				instance.Spec.Width = update.Width.Value;
			if( update.Height.HasValue )
				instance.Spec.Height = update.Height.Value;
			ApplyWindowSizing( instance.Window, instance.Spec );
		}

		if( update.Focus )
		{
			try { instance.Window.Activate(); } catch { }
		}

		instance.LastUpdatedAtUtc = DateTimeOffset.UtcNow;
		if( patchedCount > 0 )
			instance.SetStatusFeedback( $"Updated {patchedCount} element(s) live" );
		else if( update.AppendComponents is { Count: > 0 } )
			instance.SetStatusFeedback( $"Appended {update.AppendComponents.Count} component(s)" );
		else if( fullRerender )
			instance.SetStatusFeedback( "Window content updated" );

		return Task.FromResult<object>( new
		{
			ok = true,
			windowId = instance.Spec.WindowId,
			title = instance.Spec.Title,
			fullRerender,
			appendedCount = update.AppendComponents?.Count ?? 0,
			patchedCount,
			missingPatchIds,
			componentCount = instance.Spec.Components.Count,
			patchableIds = instance.PatchTargets.Keys.OrderBy( k => k, StringComparer.OrdinalIgnoreCase ).ToList(),
			inputValues = instance.ReadInputValues(),
		} );
	}

	public static object ListWindows()
	{
		List<object> list = [];
		foreach( DynamicWindowInstance inst in Windows.Values.OrderBy( w => w.CreatedAtUtc ) )
		{
			var size = inst.Window.AppWindow?.Size;
			list.Add( new
			{
				windowId = inst.Spec.WindowId,
				title = inst.Spec.Title,
				subtitle = inst.Spec.Subtitle,
				badge = inst.Spec.Badge,
				width = size?.Width ?? inst.Spec.Width,
				height = size?.Height ?? inst.Spec.Height,
				alwaysOnTop = inst.Spec.AlwaysOnTop,
				componentCount = inst.Spec.Components.Count,
				patchableIds = inst.PatchTargets.Keys.OrderBy( k => k, StringComparer.OrdinalIgnoreCase ).ToList(),
				pendingEventCount = inst.Events.Count,
				inputValues = inst.ReadInputValues(),
				createdAtUtc = inst.CreatedAtUtc,
				lastUpdatedAtUtc = inst.LastUpdatedAtUtc,
			} );
		}
		return new { count = list.Count, windows = list };
	}

	public static async Task<object> GetOrWaitEventsAsync(
		string windowId,
		bool waitForEvent,
		bool clearEvents,
		int timeoutSeconds,
		CancellationToken cancellationToken )
	{
		if( !Windows.TryGetValue( windowId, out DynamicWindowInstance? instance ) )
			throw new InvalidOperationException( $"Dynamic window '{windowId}' is not open." );

		bool timedOut = false;
		if( waitForEvent && instance.Events.Count == 0 && !instance.IsClosed )
		{
			TaskCompletionSource<bool> waiter = new( TaskCreationOptions.RunContinuationsAsynchronously );
			instance.Waiters.Add( waiter );
			using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource( cancellationToken );
			timeoutCts.CancelAfter( TimeSpan.FromSeconds( Math.Clamp( timeoutSeconds, 1, 600 ) ) );
			using CancellationTokenRegistration reg = timeoutCts.Token.Register( () => waiter.TrySetResult( false ) );

			bool arrived = await waiter.Task;
			instance.Waiters.Remove( waiter );
			timedOut = !arrived && !instance.IsClosed;
		}

		List<DynamicWindowEvent> snapshot = [ .. instance.Events ];
		if( clearEvents )
			instance.Events.Clear();

		return new
		{
			ok = true,
			windowId = instance.Spec.WindowId,
			title = instance.Spec.Title,
			isOpen = !instance.IsClosed,
			timedOut,
			eventCount = snapshot.Count,
			events = snapshot,
			inputValues = instance.ReadInputValues(),
		};
	}

	public static object CloseWindow( string? windowId, bool closeAll )
	{
		List<string> closed = [];
		if( closeAll || string.IsNullOrWhiteSpace( windowId ) )
		{
			foreach( DynamicWindowInstance inst in Windows.Values.ToList() )
			{
				closed.Add( inst.Spec.WindowId );
				CloseInstanceSafe( inst );
			}
		}
		else if( Windows.TryGetValue( windowId, out DynamicWindowInstance? inst ) )
		{
			closed.Add( inst.Spec.WindowId );
			CloseInstanceSafe( inst );
		}
		else
		{
			return new
			{
				ok = false,
				message = $"Dynamic window '{windowId}' is not open.",
				remainingCount = Windows.Count,
			};
		}

		return new
		{
			ok = true,
			closedWindowIds = closed,
			remainingCount = Windows.Count,
		};
	}

	// ------------------------------------------------------------------ Window Lifecycle & Shell Rendering

	private static void CloseInstanceSafe( DynamicWindowInstance instance )
	{
		try
		{
			instance.Window.Close();
		}
		catch
		{
			OnWindowClosed( instance );
		}
	}

	private static void OnWindowClosed( DynamicWindowInstance instance )
	{
		if( instance.IsClosed )
			return;
		instance.IsClosed = true;
		AppComposition.Theme.ThemeChanged -= instance.ThemeChangedHandler;
		Windows.Remove( instance.Spec.WindowId );

		instance.RecordEvent( new DynamicWindowEvent(
			TimestampUtc: DateTimeOffset.UtcNow,
			EventType: "window_closed",
			ElementId: instance.Spec.WindowId,
			Label: instance.Spec.Title,
			Value: null,
			InputValues: instance.ReadInputValues() ) );
	}

	private static void ApplyWindowSizing( Window win, DynamicWindowSpec spec )
	{
		WindowHelper.SetWindowConfig( win, new WindowConfig
		{
			Title = spec.Title,
			WindowKind = spec.AlwaysOnTop ? AppWindowPresenterKind.CompactOverlay : AppWindowPresenterKind.Default,
			PositionAndSize = ( spec.X, spec.Y, spec.Width, spec.Height ),
		} );
	}

	private static object BuildWindowSummary( DynamicWindowInstance inst, bool replaced ) => new
	{
		ok = true,
		windowId = inst.Spec.WindowId,
		title = inst.Spec.Title,
		subtitle = inst.Spec.Subtitle,
		replaced,
		width = inst.Spec.Width,
		height = inst.Spec.Height,
		alwaysOnTop = inst.Spec.AlwaysOnTop,
		componentCount = inst.Spec.Components.Count,
		patchableIds = inst.PatchTargets.Keys.OrderBy( k => k, StringComparer.OrdinalIgnoreCase ).ToList(),
		inputIds = inst.InputReaders.Keys.OrderBy( k => k, StringComparer.OrdinalIgnoreCase ).ToList(),
		openDynamicWindowCount = Windows.Count,
		hint = $"You can inspect or screenshot this window using windowId='{inst.Spec.WindowId}', update it live with ui_window_update, or read user actions with ui_window_events.",
	};

	private static void RenderShell( DynamicWindowInstance inst, bool preserveInputs )
	{
		Dictionary<string, object?> savedInputs = preserveInputs ? inst.ReadInputValues() : [];

		inst.PatchTargets.Clear();
		inst.InputReaders.Clear();
		inst.RootGrid.Children.Clear();
		inst.RootGrid.RowDefinitions.Clear();

		ThemePalette pal = ResolvePalette();
		Color accent = ResolveAccentColor( inst.Spec.Severity, inst.Spec.AccentColor, pal );

		inst.RootGrid.Background = Brush( pal.WindowBg );
		inst.RootGrid.RowDefinitions.Add( new RowDefinition { Height = GridLength.Auto } );
		inst.RootGrid.RowDefinitions.Add( new RowDefinition { Height = new GridLength( 1, GridUnitType.Star ) } );
		inst.RootGrid.RowDefinitions.Add( new RowDefinition { Height = GridLength.Auto } );

		// ---- Row 0: Header Banner ----
		Border headerBorder = new()
		{
			Background = Brush( pal.HeaderBg ),
			BorderBrush = Brush( pal.Border ),
			BorderThickness = new Thickness( 0, 0, 0, 1 ),
		};

		StackPanel headerOuter = new() { Spacing = 0 };
		headerOuter.Children.Add( new Border
		{
			Height = 3,
			Background = Brush( accent ),
			HorizontalAlignment = HorizontalAlignment.Stretch,
		} );

		Grid headerGrid = new() { Padding = new Thickness( 18, 12, 18, 12 ), ColumnSpacing = 12 };
		headerGrid.ColumnDefinitions.Add( new ColumnDefinition { Width = new GridLength( 1, GridUnitType.Star ) } );
		headerGrid.ColumnDefinitions.Add( new ColumnDefinition { Width = GridLength.Auto } );

		StackPanel titleArea = new() { Spacing = 4 };
		StackPanel badgeRow = new() { Orientation = Orientation.Horizontal, Spacing = 8 };

		TextBlock badgeText = new()
		{
			Text = inst.Spec.Badge.ToUpperInvariant(),
			FontSize = 10,
			FontWeight = FontWeights.Bold,
			Foreground = Brush( ContrastTextFor( accent ) ),
			CharacterSpacing = 50,
		};
		inst.HeaderBadgeBlock = badgeText;

		badgeRow.Children.Add( new Border
		{
			Background = Brush( accent ),
			CornerRadius = new CornerRadius( 4 ),
			Padding = new Thickness( 7, 2, 7, 2 ),
			Child = badgeText,
		} );

		badgeRow.Children.Add( new TextBlock
		{
			Text = $"Window: {inst.Spec.WindowId}",
			FontSize = 11,
			Foreground = Brush( pal.MutedText ),
			VerticalAlignment = VerticalAlignment.Center,
		} );
		titleArea.Children.Add( badgeRow );

		TextBlock titleBlock = new()
		{
			Text = inst.Spec.Title,
			FontSize = 19,
			FontWeight = FontWeights.SemiBold,
			Foreground = Brush( pal.PrimaryText ),
			TextWrapping = TextWrapping.Wrap,
			IsTextSelectionEnabled = true,
		};
		inst.HeaderTitleBlock = titleBlock;
		titleArea.Children.Add( titleBlock );

		TextBlock subtitleBlock = new()
		{
			Text = inst.Spec.Subtitle ?? string.Empty,
			FontSize = 12.5,
			Foreground = Brush( pal.MutedText ),
			TextWrapping = TextWrapping.Wrap,
			IsTextSelectionEnabled = true,
			Visibility = string.IsNullOrWhiteSpace( inst.Spec.Subtitle ) ? Visibility.Collapsed : Visibility.Visible,
		};
		inst.HeaderSubtitleBlock = subtitleBlock;
		titleArea.Children.Add( subtitleBlock );

		Grid.SetColumn( titleArea, 0 );
		headerGrid.Children.Add( titleArea );

		StackPanel headerActions = new()
		{
			Orientation = Orientation.Horizontal,
			Spacing = 8,
			VerticalAlignment = VerticalAlignment.Center,
		};

		Button copySummaryBtn = MakeHeaderButton( "Copy Summary", pal, accent: false );
		copySummaryBtn.Name = $"McpDynamicWindowButton_{SanitizeControlName( inst.Spec.WindowId )}_copy_summary";
		copySummaryBtn.Click += ( _, _ ) =>
		{
			string summary = DynamicWindowSpecParser.BuildTextSummary( inst.Spec );
			if( TryCopyToClipboard( summary ) )
				inst.SetStatusFeedback( "Copied Markdown summary to clipboard" );
			else
				inst.SetStatusFeedback( "Clipboard unavailable on this session" );
		};
		headerActions.Children.Add( copySummaryBtn );

		Button closeBtn = MakeHeaderButton( "Close", pal, accent: false );
		closeBtn.Name = $"McpDynamicWindowButton_{SanitizeControlName( inst.Spec.WindowId )}_header_close";
		closeBtn.Click += ( _, _ ) => CloseInstanceSafe( inst );
		headerActions.Children.Add( closeBtn );

		Grid.SetColumn( headerActions, 1 );
		headerGrid.Children.Add( headerActions );

		headerOuter.Children.Add( headerGrid );
		headerBorder.Child = headerOuter;
		Grid.SetRow( headerBorder, 0 );
		inst.RootGrid.Children.Add( headerBorder );

		// ---- Row 1: Scrollable Component Tree Body ----
		StackPanel bodyStack = new()
		{
			Spacing = 14,
			Padding = new Thickness( 18, 16, 18, 20 ),
		};
		inst.BodyStack = bodyStack;

		foreach( DynamicComponentSpec comp in inst.Spec.Components )
		{
			if( preserveInputs )
				RestoreSavedInputDefaults( comp, savedInputs );
			bodyStack.Children.Add( RenderComponent( inst, comp, pal ) );
		}

		ScrollViewer scroll = new()
		{
			Content = bodyStack,
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
			HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
		};
		Grid.SetRow( scroll, 1 );
		inst.RootGrid.Children.Add( scroll );

		// ---- Row 2: Subtle Footer Status Strip ----
		Border footer = new()
		{
			Background = Brush( pal.HeaderBg ),
			BorderBrush = Brush( pal.Border ),
			BorderThickness = new Thickness( 0, 1, 0, 0 ),
			Padding = new Thickness( 18, 7, 18, 7 ),
		};

		Grid footerGrid = new();
		footerGrid.ColumnDefinitions.Add( new ColumnDefinition { Width = new GridLength( 1, GridUnitType.Star ) } );
		footerGrid.ColumnDefinitions.Add( new ColumnDefinition { Width = GridLength.Auto } );

		TextBlock statusText = new()
		{
			Text = "Ready • Supplemental view generated by HackPDM MCP AI",
			FontSize = 11.5,
			Foreground = Brush( pal.MutedText ),
			VerticalAlignment = VerticalAlignment.Center,
		};
		inst.FooterStatusBlock = statusText;
		Grid.SetColumn( statusText, 0 );
		footerGrid.Children.Add( statusText );

		TextBlock metaText = new()
		{
			FontSize = 11,
			Foreground = Brush( pal.MutedText ),
			VerticalAlignment = VerticalAlignment.Center,
		};
		inst.FooterMetaBlock = metaText;
		inst.UpdateFooterMeta();
		Grid.SetColumn( metaText, 1 );
		footerGrid.Children.Add( metaText );

		footer.Child = footerGrid;
		Grid.SetRow( footer, 2 );
		inst.RootGrid.Children.Add( footer );
	}

	private static void RestoreSavedInputDefaults( DynamicComponentSpec comp, Dictionary<string, object?> saved )
	{
		if( !string.IsNullOrWhiteSpace( comp.Id ) && saved.TryGetValue( comp.Id, out object? val ) )
		{
			if( comp.Type == "input" && val is string s )
				comp.Value = s;
			else if( comp.Type == "checkbox" && val is bool b )
				comp.Checked = b;
			else if( comp.Type == "select" && val is string sel )
				comp.Selected = sel;
		}
		foreach( DynamicComponentSpec child in comp.Children )
			RestoreSavedInputDefaults( child, saved );
		foreach( DynamicTabSpec tab in comp.Tabs )
			foreach( DynamicComponentSpec child in tab.Children )
				RestoreSavedInputDefaults( child, saved );
	}

	// ------------------------------------------------------------------ Component Renderers (All 21 Types)

	private static UIElement RenderComponent( DynamicWindowInstance inst, DynamicComponentSpec comp, ThemePalette pal )
	{
		FrameworkElement element = comp.Type switch
		{
			"section" => RenderSection( inst, comp, pal ),
			"stack" => RenderStack( inst, comp, pal ),
			"grid" => RenderGrid( inst, comp, pal ),
			"tabs" => RenderTabs( inst, comp, pal ),
			"text" => RenderText( inst, comp, pal ),
			"markdown" => RenderMarkdown( inst, comp, pal ),
			"kpi_row" => RenderKpiRow( inst, comp, pal ),
			"key_value" => RenderKeyValue( inst, comp, pal ),
			"table" => RenderTable( inst, comp, pal ),
			"tree" => RenderTree( inst, comp, pal ),
			"list" => RenderList( inst, comp, pal ),
			"badge_row" => RenderBadgeRow( comp, pal ),
			"callout" => RenderCallout( inst, comp, pal ),
			"progress" => RenderProgress( inst, comp, pal ),
			"code" => RenderCode( inst, comp, pal ),
			"image" => RenderImage( comp, pal ),
			"divider" => RenderDivider( comp, pal ),
			"input" => RenderInput( inst, comp, pal ),
			"checkbox" => RenderCheckbox( inst, comp, pal ),
			"select" => RenderSelect( inst, comp, pal ),
			"button_row" => RenderButtonRow( inst, comp, pal ),
			_ => new TextBlock { Text = $"[Unsupported component: {comp.Type}]", Foreground = Brush( pal.MutedText ) },
		};

		element.Visibility = comp.Visible ? Visibility.Visible : Visibility.Collapsed;
		if( !string.IsNullOrWhiteSpace( comp.Id ) && !inst.PatchTargets.ContainsKey( comp.Id ) )
		{
			// Fallback visibility patch handler for any component with an id
			inst.PatchTargets[ comp.Id ] = ( patch, _ ) =>
			{
				if( patch.Visible.HasValue )
					element.Visibility = patch.Visible.Value ? Visibility.Visible : Visibility.Collapsed;
			};
		}

		return element;
	}

	private static FrameworkElement RenderSection( DynamicWindowInstance inst, DynamicComponentSpec comp, ThemePalette pal )
	{
		bool hasAccent = !string.IsNullOrWhiteSpace( comp.Severity ) || !string.IsNullOrWhiteSpace( comp.AccentColor );
		Color accent = hasAccent ? ResolveAccentColor( comp.Severity, comp.AccentColor, pal ) : pal.Accent;

		Border card = new()
		{
			Background = Brush( pal.CardBg ),
			BorderBrush = Brush( hasAccent ? WithAlpha( accent, 0x70 ) : pal.Border ),
			BorderThickness = new Thickness( hasAccent ? 3 : 1, 1, 1, 1 ),
			CornerRadius = new CornerRadius( 10 ),
			Padding = new Thickness( 16, 14, 16, 14 ),
		};

		StackPanel outer = new() { Spacing = 10 };
		StackPanel contentPanel = new()
		{
			Spacing = 10,
			Visibility = comp.Expanded ? Visibility.Visible : Visibility.Collapsed,
		};

		if( !string.IsNullOrWhiteSpace( comp.Title ) || !string.IsNullOrWhiteSpace( comp.Subtitle ) || comp.Collapsible )
		{
			Grid headerRow = new() { ColumnSpacing = 8 };
			headerRow.ColumnDefinitions.Add( new ColumnDefinition { Width = new GridLength( 1, GridUnitType.Star ) } );
			headerRow.ColumnDefinitions.Add( new ColumnDefinition { Width = GridLength.Auto } );

			StackPanel textStack = new() { Spacing = 2 };
			StackPanel titleLine = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
			if( !string.IsNullOrWhiteSpace( comp.Title ) )
			{
				titleLine.Children.Add( new TextBlock
				{
					Text = comp.Title,
					FontSize = 14.5,
					FontWeight = FontWeights.SemiBold,
					Foreground = Brush( pal.PrimaryText ),
					VerticalAlignment = VerticalAlignment.Center,
				} );
			}
			if( !string.IsNullOrWhiteSpace( comp.Badge ) )
				titleLine.Children.Add( MakePill( comp.Badge, accent, pal ) );
			textStack.Children.Add( titleLine );

			if( !string.IsNullOrWhiteSpace( comp.Subtitle ) )
			{
				textStack.Children.Add( new TextBlock
				{
					Text = comp.Subtitle,
					FontSize = 12,
					Foreground = Brush( pal.MutedText ),
					TextWrapping = TextWrapping.Wrap,
				} );
			}

			Grid.SetColumn( textStack, 0 );
			headerRow.Children.Add( textStack );

			if( comp.Collapsible )
			{
				Button toggleBtn = MakeHeaderButton( comp.Expanded ? "Hide ▴" : "Show ▾", pal, accent: false );
				toggleBtn.Padding = new Thickness( 9, 3, 9, 3 );
				toggleBtn.FontSize = 11.5;
				toggleBtn.Click += ( _, _ ) =>
				{
					bool nowOpen = contentPanel.Visibility != Visibility.Visible;
					contentPanel.Visibility = nowOpen ? Visibility.Visible : Visibility.Collapsed;
					toggleBtn.Content = nowOpen ? "Hide ▴" : "Show ▾";
				};
				Grid.SetColumn( toggleBtn, 1 );
				headerRow.Children.Add( toggleBtn );
			}

			outer.Children.Add( headerRow );
		}

		foreach( DynamicComponentSpec child in comp.Children )
			contentPanel.Children.Add( RenderComponent( inst, child, pal ) );

		outer.Children.Add( contentPanel );
		card.Child = outer;
		return card;
	}

	private static FrameworkElement RenderStack( DynamicWindowInstance inst, DynamicComponentSpec comp, ThemePalette pal )
	{
		StackPanel panel = new()
		{
			Orientation = comp.Orientation == "horizontal" ? Orientation.Horizontal : Orientation.Vertical,
			Spacing = comp.Spacing,
		};
		foreach( DynamicComponentSpec child in comp.Children )
			panel.Children.Add( RenderComponent( inst, child, pal ) );
		return panel;
	}

	private static FrameworkElement RenderGrid( DynamicWindowInstance inst, DynamicComponentSpec comp, ThemePalette pal )
	{
		Grid grid = new()
		{
			ColumnSpacing = comp.ColumnSpacing,
			RowSpacing = comp.RowSpacing,
		};

		int cols = Math.Max( 1, comp.ColumnCount );
		for( int c = 0; c < cols; c++ )
		{
			GridLength gl = c < comp.GridColumnDefs.Count
				? ParseGridLength( comp.GridColumnDefs[ c ] )
				: new GridLength( 1, GridUnitType.Star );
			grid.ColumnDefinitions.Add( new ColumnDefinition { Width = gl } );
		}

		int rows = ( comp.Children.Count + cols - 1 ) / cols;
		for( int r = 0; r < Math.Max( 1, rows ); r++ )
			grid.RowDefinitions.Add( new RowDefinition { Height = GridLength.Auto } );

		for( int i = 0; i < comp.Children.Count; i++ )
		{
			UIElement child = RenderComponent( inst, comp.Children[ i ], pal );
			Grid.SetRow( (FrameworkElement)child, i / cols );
			Grid.SetColumn( (FrameworkElement)child, i % cols );
			grid.Children.Add( child );
		}

		return grid;
	}

	private static FrameworkElement RenderTabs( DynamicWindowInstance inst, DynamicComponentSpec comp, ThemePalette pal )
	{
		StackPanel root = new() { Spacing = 10 };

		// Segmented tab bar
		Border tabStripBorder = new()
		{
			Background = Brush( pal.SubtleBg ),
			BorderBrush = Brush( pal.Border ),
			BorderThickness = new Thickness( 1 ),
			CornerRadius = new CornerRadius( 8 ),
			Padding = new Thickness( 4 ),
			HorizontalAlignment = HorizontalAlignment.Left,
		};

		StackPanel tabStrip = new() { Orientation = Orientation.Horizontal, Spacing = 4 };
		Grid contentHost = new();
		List<(Button Btn, TextBlock Label, Border? BadgeBorder, TextBlock? BadgeText, StackPanel Pane)> entries = [];

		int activeIndex = Math.Clamp( comp.SelectedIndex, 0, Math.Max( 0, comp.Tabs.Count - 1 ) );

		void ActivateTab( int index )
		{
			Color activeInk = ContrastTextFor( pal.Accent );
			for( int i = 0; i < entries.Count; i++ )
			{
				bool isActive = i == index;
				entries[ i ].Pane.Visibility = isActive ? Visibility.Visible : Visibility.Collapsed;
				entries[ i ].Btn.Background = Brush( isActive ? pal.Accent : Colors.Transparent );
				entries[ i ].Label.Foreground = Brush( isActive ? activeInk : pal.PrimaryText );
				entries[ i ].Label.FontWeight = isActive ? FontWeights.SemiBold : FontWeights.Normal;
				if( entries[ i ].BadgeBorder is { } bb && entries[ i ].BadgeText is { } bt )
				{
					bb.Background = Brush( WithAlpha( isActive ? activeInk : pal.PrimaryText, 0x24 ) );
					bt.Foreground = Brush( isActive ? activeInk : pal.PrimaryText );
				}
			}
		}

		for( int i = 0; i < comp.Tabs.Count; i++ )
		{
			int tabIndex = i;
			DynamicTabSpec tab = comp.Tabs[ i ];

			StackPanel btnInner = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
			TextBlock tabLabel = new()
			{
				Text = tab.Title,
				FontSize = 12.5,
				VerticalAlignment = VerticalAlignment.Center,
			};
			btnInner.Children.Add( tabLabel );

			Border? badgeBorder = null;
			TextBlock? badgeText = null;
			if( !string.IsNullOrWhiteSpace( tab.Badge ) )
			{
				badgeText = new TextBlock
				{
					Text = tab.Badge,
					FontSize = 10.5,
					FontWeight = FontWeights.SemiBold,
					Foreground = Brush( pal.PrimaryText ),
				};
				badgeBorder = new Border
				{
					Background = Brush( WithAlpha( pal.PrimaryText, 0x22 ) ),
					CornerRadius = new CornerRadius( 8 ),
					Padding = new Thickness( 6, 1, 6, 1 ),
					Child = badgeText,
				};
				btnInner.Children.Add( badgeBorder );
			}

			Button tabBtn = new()
			{
				Content = btnInner,
				CornerRadius = new CornerRadius( 6 ),
				BorderThickness = new Thickness( 0 ),
				Padding = new Thickness( 12, 6, 12, 6 ),
				Background = Brush( Colors.Transparent ),
			};
			tabBtn.Click += ( _, _ ) => ActivateTab( tabIndex );
			tabStrip.Children.Add( tabBtn );

			StackPanel pane = new() { Spacing = 10 };
			foreach( DynamicComponentSpec child in tab.Children )
				pane.Children.Add( RenderComponent( inst, child, pal ) );
			contentHost.Children.Add( pane );

			entries.Add( ( tabBtn, tabLabel, badgeBorder, badgeText, pane ) );
		}

		ActivateTab( activeIndex );
		tabStripBorder.Child = tabStrip;
		root.Children.Add( tabStripBorder );
		root.Children.Add( contentHost );

		if( !string.IsNullOrWhiteSpace( comp.Id ) )
		{
			inst.PatchTargets[ comp.Id ] = ( patch, _ ) =>
			{
				if( patch.Visible.HasValue )
					root.Visibility = patch.Visible.Value ? Visibility.Visible : Visibility.Collapsed;
				if( patch.NumericValue.HasValue )
					ActivateTab( Math.Clamp( (int)patch.NumericValue.Value, 0, Math.Max( 0, entries.Count - 1 ) ) );
				else if( !string.IsNullOrWhiteSpace( patch.Selected ) )
				{
					int match = comp.Tabs.FindIndex( t =>
						string.Equals( t.Id, patch.Selected, StringComparison.OrdinalIgnoreCase )
						|| string.Equals( t.Title, patch.Selected, StringComparison.OrdinalIgnoreCase ) );
					if( match >= 0 )
						ActivateTab( match );
				}
			};
		}

		return root;
	}

	private static FrameworkElement RenderText( DynamicWindowInstance inst, DynamicComponentSpec comp, ThemePalette pal )
	{
		string variant = ( comp.Variant ?? "body" ).Trim().ToLowerInvariant();
		double fontSize = variant switch
		{
			"title" => 18,
			"subtitle" => 14.5,
			"heading" => 15,
			"caption" => 11.5,
			"muted" => 12.5,
			"code" => 12.5,
			_ => 13,
		};

		var weight = ( comp.Bold == true || variant is "title" or "heading" or "accent" )
			? FontWeights.SemiBold
			: FontWeights.Normal;

		Color fg = pal.PrimaryText;
		if( !string.IsNullOrWhiteSpace( comp.Color ) && ThemeColorParse.TryParseHex( comp.Color, out uint customArgb ) )
			fg = ToColor( customArgb );
		else if( !string.IsNullOrWhiteSpace( comp.Severity ) || !string.IsNullOrWhiteSpace( comp.AccentColor ) )
			fg = ResolveAccentColor( comp.Severity, comp.AccentColor, pal );
		else if( variant is "caption" or "muted" )
			fg = pal.MutedText;
		else if( variant == "accent" )
			fg = pal.Accent;

		TextBlock tb = new()
		{
			Text = comp.Text ?? comp.Content ?? comp.Value ?? string.Empty,
			FontSize = fontSize,
			FontWeight = weight,
			Foreground = Brush( fg ),
			TextWrapping = comp.Wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
			IsTextSelectionEnabled = comp.Selectable,
		};

		if( variant == "code" )
			tb.FontFamily = new FontFamily( "Cascadia Mono, Consolas, DejaVu Sans Mono, monospace" );

		if( !string.IsNullOrWhiteSpace( comp.Id ) )
		{
			inst.PatchTargets[ comp.Id ] = ( patch, p ) =>
			{
				if( patch.Visible.HasValue )
					tb.Visibility = patch.Visible.Value ? Visibility.Visible : Visibility.Collapsed;
				if( patch.Text is not null || patch.Value is not null || patch.Message is not null )
					tb.Text = patch.Text ?? patch.Value ?? patch.Message ?? string.Empty;
				if( !string.IsNullOrWhiteSpace( patch.Severity ) || !string.IsNullOrWhiteSpace( patch.AccentColor ) )
					tb.Foreground = Brush( ResolveAccentColor( patch.Severity, patch.AccentColor, p ) );
			};
		}

		return tb;
	}

	private static FrameworkElement RenderMarkdown( DynamicWindowInstance inst, DynamicComponentSpec comp, ThemePalette pal )
	{
		StackPanel panel = new() { Spacing = 6 };
		string content = comp.Content ?? comp.Text ?? string.Empty;
		string[] lines = content.Replace( "\r\n", "\n" ).Split( '\n' );

		bool inFence = false;
		string? fenceLang = null;
		List<string> fenceLines = [];

		void FlushFence()
		{
			if( fenceLines.Count == 0 )
				return;
			DynamicComponentSpec codeSpec = new()
			{
				Type = "code",
				Language = fenceLang,
				Content = string.Join( "\n", fenceLines ),
				Copyable = true,
				MaxHeight = 220,
			};
			panel.Children.Add( RenderCode( inst, codeSpec, pal ) );
			fenceLines.Clear();
			fenceLang = null;
		}

		foreach( string rawLine in lines )
		{
			string trimmed = rawLine.Trim();
			if( trimmed.StartsWith( "```", StringComparison.Ordinal ) )
			{
				if( inFence )
				{
					FlushFence();
					inFence = false;
				}
				else
				{
					inFence = true;
					fenceLang = trimmed.Length > 3 ? trimmed[ 3.. ].Trim() : null;
				}
				continue;
			}

			if( inFence )
			{
				fenceLines.Add( rawLine );
				continue;
			}

			if( string.IsNullOrWhiteSpace( trimmed ) )
				continue;

			if( trimmed is "---" or "***" )
			{
				panel.Children.Add( RenderDivider( new DynamicComponentSpec { Type = "divider" }, pal ) );
				continue;
			}

			if( trimmed.StartsWith( "### ", StringComparison.Ordinal ) )
			{
				panel.Children.Add( new TextBlock
				{
					Text = StripInlineMarkdown( trimmed[ 4.. ] ),
					FontSize = 14,
					FontWeight = FontWeights.SemiBold,
					Foreground = Brush( pal.PrimaryText ),
					Margin = new Thickness( 0, 4, 0, 0 ),
					TextWrapping = TextWrapping.Wrap,
					IsTextSelectionEnabled = true,
				} );
				continue;
			}

			if( trimmed.StartsWith( "## ", StringComparison.Ordinal ) )
			{
				panel.Children.Add( new TextBlock
				{
					Text = StripInlineMarkdown( trimmed[ 3.. ] ),
					FontSize = 15.5,
					FontWeight = FontWeights.SemiBold,
					Foreground = Brush( pal.PrimaryText ),
					Margin = new Thickness( 0, 6, 0, 0 ),
					TextWrapping = TextWrapping.Wrap,
					IsTextSelectionEnabled = true,
				} );
				continue;
			}

			if( trimmed.StartsWith( "# ", StringComparison.Ordinal ) )
			{
				panel.Children.Add( new TextBlock
				{
					Text = StripInlineMarkdown( trimmed[ 2.. ] ),
					FontSize = 17.5,
					FontWeight = FontWeights.Bold,
					Foreground = Brush( pal.PrimaryText ),
					Margin = new Thickness( 0, 6, 0, 2 ),
					TextWrapping = TextWrapping.Wrap,
					IsTextSelectionEnabled = true,
				} );
				continue;
			}

			if( trimmed.StartsWith( "> ", StringComparison.Ordinal ) )
			{
				panel.Children.Add( new Border
				{
					Background = Brush( pal.SubtleBg ),
					BorderBrush = Brush( pal.Accent ),
					BorderThickness = new Thickness( 3, 0, 0, 0 ),
					CornerRadius = new CornerRadius( 0, 6, 6, 0 ),
					Padding = new Thickness( 10, 6, 10, 6 ),
					Child = new TextBlock
					{
						Text = StripInlineMarkdown( trimmed[ 2.. ] ),
						FontSize = 12.5,
						Foreground = Brush( pal.MutedText ),
						TextWrapping = TextWrapping.Wrap,
						IsTextSelectionEnabled = true,
					},
				} );
				continue;
			}

			if( trimmed.StartsWith( "- ", StringComparison.Ordinal ) || trimmed.StartsWith( "* ", StringComparison.Ordinal ) || trimmed.StartsWith( "• ", StringComparison.Ordinal ) )
			{
				Grid bulletRow = new() { ColumnSpacing = 8, Margin = new Thickness( 6, 0, 0, 0 ) };
				bulletRow.ColumnDefinitions.Add( new ColumnDefinition { Width = GridLength.Auto } );
				bulletRow.ColumnDefinitions.Add( new ColumnDefinition { Width = new GridLength( 1, GridUnitType.Star ) } );
				TextBlock dot = new() { Text = "•", FontSize = 13, Foreground = Brush( pal.Accent ), FontWeight = FontWeights.Bold };
				TextBlock body = new()
				{
					Text = StripInlineMarkdown( trimmed[ 2.. ] ),
					FontSize = 13,
					Foreground = Brush( pal.PrimaryText ),
					TextWrapping = TextWrapping.Wrap,
					IsTextSelectionEnabled = true,
				};
				Grid.SetColumn( dot, 0 );
				Grid.SetColumn( body, 1 );
				bulletRow.Children.Add( dot );
				bulletRow.Children.Add( body );
				panel.Children.Add( bulletRow );
				continue;
			}

			panel.Children.Add( new TextBlock
			{
				Text = StripInlineMarkdown( trimmed ),
				FontSize = 13,
				Foreground = Brush( pal.PrimaryText ),
				TextWrapping = TextWrapping.Wrap,
				IsTextSelectionEnabled = true,
			} );
		}

		if( inFence )
			FlushFence();

		return panel;
	}

	private static FrameworkElement RenderKpiRow( DynamicWindowInstance inst, DynamicComponentSpec comp, ThemePalette pal )
	{
		if( comp.KpiItems.Count == 0 )
			return new Border();

		int cols = comp.ColumnCount > 0 ? comp.ColumnCount : Math.Min( 4, comp.KpiItems.Count );
		Grid grid = new() { ColumnSpacing = 12, RowSpacing = 12 };
		for( int c = 0; c < cols; c++ )
			grid.ColumnDefinitions.Add( new ColumnDefinition { Width = new GridLength( 1, GridUnitType.Star ) } );

		int rows = ( comp.KpiItems.Count + cols - 1 ) / cols;
		for( int r = 0; r < rows; r++ )
			grid.RowDefinitions.Add( new RowDefinition { Height = GridLength.Auto } );

		for( int i = 0; i < comp.KpiItems.Count; i++ )
		{
			DynamicKpiItem item = comp.KpiItems[ i ];
			Color accent = !string.IsNullOrWhiteSpace( item.Severity ) || !string.IsNullOrWhiteSpace( item.AccentColor )
				? ResolveAccentColor( item.Severity, item.AccentColor, pal )
				: pal.Accent;

			Border tile = new()
			{
				Background = Brush( pal.CardBg ),
				BorderBrush = Brush( pal.Border ),
				BorderThickness = new Thickness( 1 ),
				CornerRadius = new CornerRadius( 10 ),
			};

			StackPanel outer = new() { Spacing = 0 };
			Border topBar = new()
			{
				Height = 3,
				Background = Brush( accent ),
				CornerRadius = new CornerRadius( 10, 10, 0, 0 ),
			};
			outer.Children.Add( topBar );

			StackPanel inner = new() { Padding = new Thickness( 14, 10, 14, 12 ), Spacing = 4 };
			Grid labelLine = new() { ColumnSpacing = 6 };
			labelLine.ColumnDefinitions.Add( new ColumnDefinition { Width = new GridLength( 1, GridUnitType.Star ) } );
			labelLine.ColumnDefinitions.Add( new ColumnDefinition { Width = GridLength.Auto } );

			TextBlock lbl = new()
			{
				Text = item.Label.ToUpperInvariant(),
				FontSize = 10.5,
				FontWeight = FontWeights.SemiBold,
				Foreground = Brush( pal.MutedText ),
				CharacterSpacing = 40,
				TextTrimming = TextTrimming.CharacterEllipsis,
				VerticalAlignment = VerticalAlignment.Center,
			};
			Grid.SetColumn( lbl, 0 );
			labelLine.Children.Add( lbl );

			Border? pill = null;
			TextBlock? pillText = null;
			if( !string.IsNullOrWhiteSpace( item.Badge ) )
			{
				pillText = new TextBlock
				{
					Text = item.Badge,
					FontSize = 10,
					FontWeight = FontWeights.SemiBold,
					Foreground = Brush( ContrastTextFor( accent ) ),
				};
				pill = new Border
				{
					Background = Brush( accent ),
					CornerRadius = new CornerRadius( 4 ),
					Padding = new Thickness( 6, 1, 6, 1 ),
					Child = pillText,
				};
				Grid.SetColumn( pill, 1 );
				labelLine.Children.Add( pill );
			}
			inner.Children.Add( labelLine );

			TextBlock valBlock = new()
			{
				Text = item.Value,
				FontSize = 23,
				FontWeight = FontWeights.Bold,
				Foreground = Brush( pal.PrimaryText ),
				IsTextSelectionEnabled = true,
			};
			inner.Children.Add( valBlock );

			TextBlock subBlock = new()
			{
				Text = item.Subtitle ?? string.Empty,
				FontSize = 11.5,
				Foreground = Brush( pal.MutedText ),
				TextWrapping = TextWrapping.Wrap,
				Visibility = string.IsNullOrWhiteSpace( item.Subtitle ) ? Visibility.Collapsed : Visibility.Visible,
			};
			inner.Children.Add( subBlock );

			outer.Children.Add( inner );
			tile.Child = outer;

			if( !string.IsNullOrWhiteSpace( item.Id ) )
			{
				inst.PatchTargets[ item.Id ] = ( patch, p ) =>
				{
					if( patch.Visible.HasValue )
						tile.Visibility = patch.Visible.Value ? Visibility.Visible : Visibility.Collapsed;
					if( patch.Value is not null || patch.Text is not null )
						valBlock.Text = patch.Value ?? patch.Text ?? string.Empty;
					if( patch.Title is not null )
						lbl.Text = patch.Title.ToUpperInvariant();
					if( patch.Subtitle is not null )
					{
						subBlock.Text = patch.Subtitle;
						subBlock.Visibility = string.IsNullOrWhiteSpace( patch.Subtitle ) ? Visibility.Collapsed : Visibility.Visible;
					}
					if( !string.IsNullOrWhiteSpace( patch.Severity ) || !string.IsNullOrWhiteSpace( patch.AccentColor ) )
					{
						Color nextAccent = ResolveAccentColor( patch.Severity, patch.AccentColor, p );
						topBar.Background = Brush( nextAccent );
						if( pill is not null && pillText is not null )
						{
							pill.Background = Brush( nextAccent );
							pillText.Foreground = Brush( ContrastTextFor( nextAccent ) );
						}
					}
					if( patch.Badge is not null && pillText is not null )
						pillText.Text = patch.Badge;
				};
			}

			Grid.SetRow( tile, i / cols );
			Grid.SetColumn( tile, i % cols );
			grid.Children.Add( tile );
		}

		return grid;
	}

	private static FrameworkElement RenderKeyValue( DynamicWindowInstance inst, DynamicComponentSpec comp, ThemePalette pal )
	{
		StackPanel outer = new() { Spacing = 8 };
		if( !string.IsNullOrWhiteSpace( comp.Title ) )
		{
			outer.Children.Add( new TextBlock
			{
				Text = comp.Title,
				FontSize = 13.5,
				FontWeight = FontWeights.SemiBold,
				Foreground = Brush( pal.PrimaryText ),
			} );
		}

		Border frame = new()
		{
			Background = Brush( pal.SubtleBg ),
			BorderBrush = Brush( pal.Border ),
			BorderThickness = new Thickness( 1 ),
			CornerRadius = new CornerRadius( 8 ),
			Padding = new Thickness( 12, 6, 12, 6 ),
		};

		int cols = Math.Clamp( comp.ColumnCount <= 0 ? 1 : comp.ColumnCount, 1, 3 );
		Grid grid = new() { ColumnSpacing = 20, RowSpacing = 0 };
		for( int c = 0; c < cols; c++ )
			grid.ColumnDefinitions.Add( new ColumnDefinition { Width = new GridLength( 1, GridUnitType.Star ) } );

		int rows = ( comp.KeyValueItems.Count + cols - 1 ) / cols;
		for( int r = 0; r < Math.Max( 1, rows ); r++ )
			grid.RowDefinitions.Add( new RowDefinition { Height = GridLength.Auto } );

		for( int i = 0; i < comp.KeyValueItems.Count; i++ )
		{
			DynamicKeyValueItem item = comp.KeyValueItems[ i ];
			int r = i / cols;
			int c = i % cols;

			Border rowBorder = new()
			{
				BorderBrush = Brush( pal.Hairline ),
				BorderThickness = new Thickness( 0, r == 0 ? 0 : 1, 0, 0 ),
				Padding = new Thickness( 0, 7, 0, 7 ),
			};

			Grid pair = new() { ColumnSpacing = 12 };
			pair.ColumnDefinitions.Add( new ColumnDefinition { Width = new GridLength( 150, GridUnitType.Pixel ) } );
			pair.ColumnDefinitions.Add( new ColumnDefinition { Width = new GridLength( 1, GridUnitType.Star ) } );
			pair.ColumnDefinitions.Add( new ColumnDefinition { Width = GridLength.Auto } );

			TextBlock kBlock = new()
			{
				Text = item.Key,
				FontSize = 12,
				FontWeight = FontWeights.Medium,
				Foreground = Brush( pal.MutedText ),
				TextWrapping = TextWrapping.Wrap,
				VerticalAlignment = VerticalAlignment.Center,
			};
			Grid.SetColumn( kBlock, 0 );
			pair.Children.Add( kBlock );

			TextBlock vBlock = new()
			{
				Text = item.Value,
				FontSize = 12.5,
				Foreground = Brush( pal.PrimaryText ),
				TextWrapping = TextWrapping.Wrap,
				IsTextSelectionEnabled = true,
				VerticalAlignment = VerticalAlignment.Center,
			};
			if( item.Monospace )
				vBlock.FontFamily = new FontFamily( "Cascadia Mono, Consolas, DejaVu Sans Mono, monospace" );
			Grid.SetColumn( vBlock, 1 );
			pair.Children.Add( vBlock );

			if( !string.IsNullOrWhiteSpace( item.Badge ) )
			{
				Color pillColor = ResolveAccentColor( item.Severity, item.AccentColor, pal );
				Border badge = MakePill( item.Badge, pillColor, pal );
				Grid.SetColumn( badge, 2 );
				pair.Children.Add( badge );
			}

			rowBorder.Child = pair;
			Grid.SetRow( rowBorder, r );
			Grid.SetColumn( rowBorder, c );
			grid.Children.Add( rowBorder );
		}

		frame.Child = grid;
		outer.Children.Add( frame );
		_ = inst;
		return outer;
	}

	private static FrameworkElement RenderTable( DynamicWindowInstance inst, DynamicComponentSpec comp, ThemePalette pal )
	{
		StackPanel outer = new() { Spacing = 8 };
		List<Dictionary<string, string>> liveRows = [ .. comp.TableRows ];
		bool showSearch = comp.Searchable ?? ( liveRows.Count >= 4 );

		TextBlock countBadgeText = new()
		{
			FontSize = 11,
			FontWeight = FontWeights.SemiBold,
			Foreground = Brush( pal.MutedText ),
			VerticalAlignment = VerticalAlignment.Center,
		};

		TextBox? searchBox = null;

		if( !string.IsNullOrWhiteSpace( comp.Title ) || showSearch )
		{
			Grid topBar = new() { ColumnSpacing = 10 };
			topBar.ColumnDefinitions.Add( new ColumnDefinition { Width = new GridLength( 1, GridUnitType.Star ) } );
			topBar.ColumnDefinitions.Add( new ColumnDefinition { Width = GridLength.Auto } );
			topBar.ColumnDefinitions.Add( new ColumnDefinition { Width = GridLength.Auto } );

			if( !string.IsNullOrWhiteSpace( comp.Title ) )
			{
				TextBlock t = new()
				{
					Text = comp.Title,
					FontSize = 13.5,
					FontWeight = FontWeights.SemiBold,
					Foreground = Brush( pal.PrimaryText ),
					VerticalAlignment = VerticalAlignment.Center,
				};
				Grid.SetColumn( t, 0 );
				topBar.Children.Add( t );
			}

			Grid.SetColumn( countBadgeText, 1 );
			topBar.Children.Add( countBadgeText );

			if( showSearch )
			{
				searchBox = new TextBox
				{
					PlaceholderText = comp.SearchPlaceholder ?? "Filter rows...",
					Width = 220,
					FontSize = 12,
					Padding = new Thickness( 8, 4, 8, 4 ),
				};
				Grid.SetColumn( searchBox, 2 );
				topBar.Children.Add( searchBox );
			}

			outer.Children.Add( topBar );
		}

		Border tableFrame = new()
		{
			Background = Brush( pal.CardBg ),
			BorderBrush = Brush( pal.Border ),
			BorderThickness = new Thickness( 1 ),
			CornerRadius = new CornerRadius( 8 ),
		};

		StackPanel frameInner = new() { Spacing = 0 };

		// Header row
		Border headerBg = new()
		{
			Background = Brush( pal.SubtleBg ),
			BorderBrush = Brush( pal.Border ),
			BorderThickness = new Thickness( 0, 0, 0, 1 ),
			Padding = new Thickness( 12, 8, 12, 8 ),
		};

		Grid headerGrid = CreateTableRowGrid( comp.TableColumns );
		for( int c = 0; c < comp.TableColumns.Count; c++ )
		{
			DynamicTableColumn col = comp.TableColumns[ c ];
			TextBlock h = new()
			{
				Text = col.Header,
				FontSize = 11.5,
				FontWeight = FontWeights.SemiBold,
				Foreground = Brush( pal.MutedText ),
				HorizontalAlignment = ToHorizontalAlignment( col.Align ),
				TextTrimming = TextTrimming.CharacterEllipsis,
			};
			Grid.SetColumn( h, c );
			headerGrid.Children.Add( h );
		}
		headerBg.Child = headerGrid;
		frameInner.Children.Add( headerBg );

		StackPanel rowsStack = new() { Spacing = 0 };
		ScrollViewer bodyScroll = new()
		{
			Content = rowsStack,
			MaxHeight = comp.MaxHeight > 0 ? comp.MaxHeight : 360,
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
			HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
		};
		frameInner.Children.Add( bodyScroll );
		tableFrame.Child = frameInner;
		outer.Children.Add( tableFrame );

		Border? selectedRowBorder = null;
		Color selectedRowOriginalBg = Colors.Transparent;

		void RebuildRows( string? filter )
		{
			rowsStack.Children.Clear();
			selectedRowBorder = null;
			selectedRowOriginalBg = Colors.Transparent;

			string needle = ( filter ?? string.Empty ).Trim();
			List<Dictionary<string, string>> visible = string.IsNullOrEmpty( needle )
				? liveRows
				: liveRows.Where( r => r.Values.Any( v => v.Contains( needle, StringComparison.OrdinalIgnoreCase ) ) ).ToList();

			countBadgeText.Text = string.IsNullOrEmpty( needle )
				? $"{liveRows.Count} row(s)"
				: $"{visible.Count} of {liveRows.Count} row(s)";

			if( visible.Count == 0 )
			{
				rowsStack.Children.Add( new TextBlock
				{
					Text = string.IsNullOrEmpty( needle ) ? ( comp.EmptyText ?? "No rows to display." ) : $"No rows match '{needle}'.",
					FontSize = 12,
					Foreground = Brush( pal.MutedText ),
					Padding = new Thickness( 14, 14, 14, 14 ),
				} );
				return;
			}

			for( int r = 0; r < visible.Count; r++ )
			{
				Dictionary<string, string> row = visible[ r ];
				Color baseBg = ( comp.Striped && r % 2 == 1 ) ? pal.StripeBg : Colors.Transparent;

				Border rowBorder = new()
				{
					Background = Brush( baseBg ),
					BorderBrush = Brush( pal.Hairline ),
					BorderThickness = new Thickness( 0, r == 0 ? 0 : 1, 0, 0 ),
					Padding = new Thickness( 12, 7, 12, 7 ),
				};

				Grid rowGrid = CreateTableRowGrid( comp.TableColumns );
				for( int c = 0; c < comp.TableColumns.Count; c++ )
				{
					DynamicTableColumn col = comp.TableColumns[ c ];
					string cellVal = row.TryGetValue( col.Key, out string? v ) ? v : string.Empty;

					FrameworkElement cellEl;
					if( col.IsBadge && !string.IsNullOrWhiteSpace( cellVal ) )
					{
						Color badgeColor = InferBadgeColor( cellVal, pal );
						cellEl = MakePill( cellVal, badgeColor, pal );
						cellEl.HorizontalAlignment = ToHorizontalAlignment( col.Align );
					}
					else
					{
						TextBlock tb = new()
						{
							Text = cellVal,
							FontSize = 12.5,
							Foreground = Brush( pal.PrimaryText ),
							HorizontalAlignment = ToHorizontalAlignment( col.Align ),
							TextWrapping = TextWrapping.Wrap,
							IsTextSelectionEnabled = !comp.RowSelectable,
							VerticalAlignment = VerticalAlignment.Center,
						};
						if( col.Monospace )
							tb.FontFamily = new FontFamily( "Cascadia Mono, Consolas, DejaVu Sans Mono, monospace" );
						cellEl = tb;
					}

					Grid.SetColumn( cellEl, c );
					rowGrid.Children.Add( cellEl );
				}

				rowBorder.Child = rowGrid;

				if( comp.RowSelectable )
				{
					int capturedRowIndex = r;
					Color capturedBaseBg = baseBg;
					rowBorder.PointerPressed += ( _, _ ) =>
					{
						if( selectedRowBorder is not null )
							selectedRowBorder.Background = Brush( selectedRowOriginalBg );
						selectedRowBorder = rowBorder;
						selectedRowOriginalBg = capturedBaseBg;
						rowBorder.Background = Brush( WithAlpha( pal.Accent, 0x33 ) );
						string summary = string.Join( " | ", comp.TableColumns.Select( c => row.GetValueOrDefault( c.Key, "" ) ) );
						inst.RecordEvent( new DynamicWindowEvent(
							TimestampUtc: DateTimeOffset.UtcNow,
							EventType: "row_selected",
							ElementId: comp.Id ?? "table",
							Label: $"Row {capturedRowIndex + 1}",
							Value: summary,
							InputValues: inst.ReadInputValues() ) );
						inst.SetStatusFeedback( $"Selected row {capturedRowIndex + 1}" );
					};
				}

				rowsStack.Children.Add( rowBorder );
			}
		}

		if( searchBox is not null )
			searchBox.TextChanged += ( _, _ ) => RebuildRows( searchBox.Text );

		RebuildRows( null );

		if( !string.IsNullOrWhiteSpace( comp.Id ) )
		{
			inst.PatchTargets[ comp.Id ] = ( patch, _ ) =>
			{
				if( patch.Visible.HasValue )
					outer.Visibility = patch.Visible.Value ? Visibility.Visible : Visibility.Collapsed;
				if( patch.Rows is not null )
				{
					liveRows = [ .. patch.Rows ];
					comp.TableRows = liveRows;
					RebuildRows( searchBox?.Text );
				}
			};
		}

		return outer;
	}

	private static FrameworkElement RenderTree( DynamicWindowInstance inst, DynamicComponentSpec comp, ThemePalette pal )
	{
		StackPanel outer = new() { Spacing = 8 };
		if( !string.IsNullOrWhiteSpace( comp.Title ) )
		{
			outer.Children.Add( new TextBlock
			{
				Text = comp.Title,
				FontSize = 13.5,
				FontWeight = FontWeights.SemiBold,
				Foreground = Brush( pal.PrimaryText ),
			} );
		}

		Border frame = new()
		{
			Background = Brush( pal.SubtleBg ),
			BorderBrush = Brush( pal.Border ),
			BorderThickness = new Thickness( 1 ),
			CornerRadius = new CornerRadius( 8 ),
			Padding = new Thickness( 10, 8, 10, 8 ),
		};

		StackPanel nodesRoot = new() { Spacing = 4 };
		foreach( DynamicTreeNode node in comp.TreeNodes )
			nodesRoot.Children.Add( RenderTreeNode( inst, comp.Id ?? "tree", node, 0, pal ) );

		if( comp.TreeNodes.Count == 0 )
		{
			nodesRoot.Children.Add( new TextBlock
			{
				Text = comp.EmptyText ?? "No hierarchy nodes.",
				FontSize = 12,
				Foreground = Brush( pal.MutedText ),
			} );
		}

		ScrollViewer scroll = new()
		{
			Content = nodesRoot,
			MaxHeight = comp.MaxHeight > 0 ? comp.MaxHeight : 340,
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
			HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
		};

		frame.Child = scroll;
		outer.Children.Add( frame );
		return outer;
	}

	private static UIElement RenderTreeNode( DynamicWindowInstance inst, string treeId, DynamicTreeNode node, int depth, ThemePalette pal )
	{
		StackPanel wrapper = new() { Spacing = 3 };
		bool hasChildren = node.Children.Count > 0;
		bool expanded = node.Expanded;

		Border row = new()
		{
			Background = Brush( depth == 0 ? pal.CardBg : Colors.Transparent ),
			BorderBrush = Brush( depth == 0 ? pal.Border : Colors.Transparent ),
			BorderThickness = new Thickness( depth == 0 ? 1 : 0 ),
			CornerRadius = new CornerRadius( 6 ),
			Padding = new Thickness( 8 + depth * 18, 5, 8, 5 ),
		};

		Grid line = new() { ColumnSpacing = 8 };
		line.ColumnDefinitions.Add( new ColumnDefinition { Width = new GridLength( 20, GridUnitType.Pixel ) } );
		line.ColumnDefinitions.Add( new ColumnDefinition { Width = new GridLength( 1, GridUnitType.Star ) } );
		line.ColumnDefinitions.Add( new ColumnDefinition { Width = GridLength.Auto } );

		TextBlock glyph = new()
		{
			Text = hasChildren ? ( expanded ? "▾" : "▸" ) : "•",
			FontSize = hasChildren ? 13 : 12,
			FontWeight = FontWeights.Bold,
			Foreground = Brush( hasChildren ? pal.Accent : pal.MutedText ),
			HorizontalAlignment = HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Center,
		};
		Grid.SetColumn( glyph, 0 );
		line.Children.Add( glyph );

		StackPanel labelStack = new() { Spacing = 1 };
		labelStack.Children.Add( new TextBlock
		{
			Text = node.Label,
			FontSize = 12.5,
			FontWeight = depth == 0 ? FontWeights.SemiBold : FontWeights.Normal,
			Foreground = Brush( pal.PrimaryText ),
			TextWrapping = TextWrapping.Wrap,
			IsTextSelectionEnabled = true,
		} );
		if( !string.IsNullOrWhiteSpace( node.Subtitle ) )
		{
			labelStack.Children.Add( new TextBlock
			{
				Text = node.Subtitle,
				FontSize = 11,
				Foreground = Brush( pal.MutedText ),
				TextWrapping = TextWrapping.Wrap,
			} );
		}
		Grid.SetColumn( labelStack, 1 );
		line.Children.Add( labelStack );

		if( !string.IsNullOrWhiteSpace( node.Badge ) )
		{
			Color c = !string.IsNullOrWhiteSpace( node.Severity )
				? ResolveAccentColor( node.Severity, null, pal )
				: InferBadgeColor( node.Badge, pal );
			Border pill = MakePill( node.Badge, c, pal );
			Grid.SetColumn( pill, 2 );
			line.Children.Add( pill );
		}

		row.Child = line;
		wrapper.Children.Add( row );

		if( hasChildren )
		{
			StackPanel childHost = new()
			{
				Spacing = 3,
				Visibility = expanded ? Visibility.Visible : Visibility.Collapsed,
			};
			foreach( DynamicTreeNode child in node.Children )
				childHost.Children.Add( RenderTreeNode( inst, treeId, child, depth + 1, pal ) );
			wrapper.Children.Add( childHost );

			row.PointerPressed += ( _, _ ) =>
			{
				expanded = !expanded;
				childHost.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
				glyph.Text = expanded ? "▾" : "▸";
			};
		}

		return wrapper;
	}

	private static FrameworkElement RenderList( DynamicWindowInstance inst, DynamicComponentSpec comp, ThemePalette pal )
	{
		StackPanel outer = new() { Spacing = 6 };
		if( !string.IsNullOrWhiteSpace( comp.Title ) )
		{
			outer.Children.Add( new TextBlock
			{
				Text = comp.Title,
				FontSize = 13.5,
				FontWeight = FontWeights.SemiBold,
				Foreground = Brush( pal.PrimaryText ),
			} );
		}

		foreach( DynamicListItem item in comp.ListItems )
		{
			bool hasAccent = !string.IsNullOrWhiteSpace( item.Severity ) || !string.IsNullOrWhiteSpace( item.AccentColor );
			Color accent = hasAccent ? ResolveAccentColor( item.Severity, item.AccentColor, pal ) : pal.Accent;

			Border row = new()
			{
				Background = Brush( pal.SubtleBg ),
				BorderBrush = Brush( hasAccent ? WithAlpha( accent, 0x80 ) : pal.Border ),
				BorderThickness = new Thickness( hasAccent ? 3 : 1, 1, 1, 1 ),
				CornerRadius = new CornerRadius( 7 ),
				Padding = new Thickness( 12, 8, 12, 8 ),
			};

			Grid grid = new() { ColumnSpacing = 10 };
			grid.ColumnDefinitions.Add( new ColumnDefinition { Width = new GridLength( 1, GridUnitType.Star ) } );
			grid.ColumnDefinitions.Add( new ColumnDefinition { Width = GridLength.Auto } );
			grid.ColumnDefinitions.Add( new ColumnDefinition { Width = GridLength.Auto } );

			StackPanel textCol = new() { Spacing = 2 };
			textCol.Children.Add( new TextBlock
			{
				Text = item.Title,
				FontSize = 13,
				FontWeight = FontWeights.Medium,
				Foreground = Brush( pal.PrimaryText ),
				TextWrapping = TextWrapping.Wrap,
				IsTextSelectionEnabled = true,
			} );
			if( !string.IsNullOrWhiteSpace( item.Subtitle ) )
			{
				textCol.Children.Add( new TextBlock
				{
					Text = item.Subtitle,
					FontSize = 11.5,
					Foreground = Brush( pal.MutedText ),
					TextWrapping = TextWrapping.Wrap,
					IsTextSelectionEnabled = true,
				} );
			}
			Grid.SetColumn( textCol, 0 );
			grid.Children.Add( textCol );

			if( !string.IsNullOrWhiteSpace( item.Value ) )
			{
				TextBlock val = new()
				{
					Text = item.Value,
					FontSize = 12.5,
					FontWeight = FontWeights.SemiBold,
					Foreground = Brush( pal.PrimaryText ),
					VerticalAlignment = VerticalAlignment.Center,
					IsTextSelectionEnabled = true,
				};
				Grid.SetColumn( val, 1 );
				grid.Children.Add( val );
			}

			if( !string.IsNullOrWhiteSpace( item.Badge ) )
			{
				Border pill = MakePill( item.Badge, hasAccent ? accent : InferBadgeColor( item.Badge, pal ), pal );
				Grid.SetColumn( pill, 2 );
				grid.Children.Add( pill );
			}

			row.Child = grid;
			outer.Children.Add( row );
		}

		_ = inst;
		return outer;
	}

	private static FrameworkElement RenderBadgeRow( DynamicComponentSpec comp, ThemePalette pal )
	{
		StackPanel row = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
		foreach( DynamicBadgeItem b in comp.Badges )
		{
			Color c = !string.IsNullOrWhiteSpace( b.Severity ) || !string.IsNullOrWhiteSpace( b.AccentColor )
				? ResolveAccentColor( b.Severity, b.AccentColor, pal )
				: InferBadgeColor( b.Label, pal );
			row.Children.Add( MakePill( b.Label, c, pal ) );
		}
		return new ScrollViewer
		{
			Content = row,
			HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
			VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
		};
	}

	private static FrameworkElement RenderCallout( DynamicWindowInstance inst, DynamicComponentSpec comp, ThemePalette pal )
	{
		Color accent = ResolveAccentColor( comp.Severity ?? "info", comp.AccentColor, pal );

		Border banner = new()
		{
			Background = Brush( WithAlpha( accent, pal.IsDark ? (byte)0x22 : (byte)0x1A ) ),
			BorderBrush = Brush( accent ),
			BorderThickness = new Thickness( 4, 1, 1, 1 ),
			CornerRadius = new CornerRadius( 8 ),
			Padding = new Thickness( 14, 11, 14, 11 ),
		};

		StackPanel stack = new() { Spacing = 4 };
		StackPanel headerLine = new() { Orientation = Orientation.Horizontal, Spacing = 8 };

		TextBlock badgeText = new()
		{
			Text = ( comp.Badge ?? ( comp.Severity ?? "info" ) ).ToUpperInvariant(),
			FontSize = 10,
			FontWeight = FontWeights.Bold,
			Foreground = Brush( ContrastTextFor( accent ) ),
		};
		Border badgePill = new()
		{
			Background = Brush( accent ),
			CornerRadius = new CornerRadius( 4 ),
			Padding = new Thickness( 6, 1, 6, 1 ),
			VerticalAlignment = VerticalAlignment.Center,
			Child = badgeText,
		};
		headerLine.Children.Add( badgePill );

		TextBlock titleBlock = new()
		{
			Text = comp.Title ?? string.Empty,
			FontSize = 13.5,
			FontWeight = FontWeights.SemiBold,
			Foreground = Brush( pal.PrimaryText ),
			VerticalAlignment = VerticalAlignment.Center,
			Visibility = string.IsNullOrWhiteSpace( comp.Title ) ? Visibility.Collapsed : Visibility.Visible,
		};
		headerLine.Children.Add( titleBlock );
		stack.Children.Add( headerLine );

		TextBlock msgBlock = new()
		{
			Text = comp.Message ?? comp.Text ?? string.Empty,
			FontSize = 12.5,
			Foreground = Brush( pal.PrimaryText ),
			TextWrapping = TextWrapping.Wrap,
			IsTextSelectionEnabled = true,
		};
		stack.Children.Add( msgBlock );

		banner.Child = stack;

		if( !string.IsNullOrWhiteSpace( comp.Id ) )
		{
			inst.PatchTargets[ comp.Id ] = ( patch, p ) =>
			{
				if( patch.Visible.HasValue )
					banner.Visibility = patch.Visible.Value ? Visibility.Visible : Visibility.Collapsed;
				if( patch.Title is not null )
				{
					titleBlock.Text = patch.Title;
					titleBlock.Visibility = string.IsNullOrWhiteSpace( patch.Title ) ? Visibility.Collapsed : Visibility.Visible;
				}
				if( patch.Message is not null || patch.Text is not null )
					msgBlock.Text = patch.Message ?? patch.Text ?? string.Empty;
				if( !string.IsNullOrWhiteSpace( patch.Severity ) || !string.IsNullOrWhiteSpace( patch.AccentColor ) )
				{
					Color nextAccent = ResolveAccentColor( patch.Severity, patch.AccentColor, p );
					banner.Background = Brush( WithAlpha( nextAccent, p.IsDark ? (byte)0x22 : (byte)0x1A ) );
					banner.BorderBrush = Brush( nextAccent );
					badgePill.Background = Brush( nextAccent );
					badgeText.Foreground = Brush( ContrastTextFor( nextAccent ) );
					if( patch.Badge is null && !string.IsNullOrWhiteSpace( patch.Severity ) )
						badgeText.Text = patch.Severity.ToUpperInvariant();
				}
				if( patch.Badge is not null )
					badgeText.Text = patch.Badge.ToUpperInvariant();
			};
		}

		return banner;
	}

	private static FrameworkElement RenderProgress( DynamicWindowInstance inst, DynamicComponentSpec comp, ThemePalette pal )
	{
		Color accent = ResolveAccentColor( comp.Severity ?? "action", comp.AccentColor, pal );

		Border box = new()
		{
			Background = Brush( pal.CardBg ),
			BorderBrush = Brush( pal.Border ),
			BorderThickness = new Thickness( 1 ),
			CornerRadius = new CornerRadius( 8 ),
			Padding = new Thickness( 14, 10, 14, 12 ),
		};

		StackPanel stack = new() { Spacing = 7 };
		Grid top = new();
		top.ColumnDefinitions.Add( new ColumnDefinition { Width = new GridLength( 1, GridUnitType.Star ) } );
		top.ColumnDefinitions.Add( new ColumnDefinition { Width = GridLength.Auto } );

		TextBlock lbl = new()
		{
			Text = comp.Label ?? comp.Title ?? "Progress",
			FontSize = 12.5,
			FontWeight = FontWeights.SemiBold,
			Foreground = Brush( pal.PrimaryText ),
		};
		Grid.SetColumn( lbl, 0 );
		top.Children.Add( lbl );

		string FormatStatus( double val, double max, bool indet, string? custom )
			=> !string.IsNullOrWhiteSpace( custom )
				? custom
				: ( indet ? "Working..." : $"{Math.Round( ( val / Math.Max( 0.0001, max ) ) * 100.0 )}%" );

		TextBlock status = new()
		{
			Text = FormatStatus( comp.ProgressValue, comp.ProgressMax, comp.Indeterminate, comp.StatusText ),
			FontSize = 12,
			FontWeight = FontWeights.SemiBold,
			Foreground = Brush( accent ),
		};
		Grid.SetColumn( status, 1 );
		top.Children.Add( status );
		stack.Children.Add( top );

		ProgressBar bar = new()
		{
			Minimum = 0,
			Maximum = comp.ProgressMax,
			Value = Math.Clamp( comp.ProgressValue, 0, comp.ProgressMax ),
			IsIndeterminate = comp.Indeterminate,
			Foreground = Brush( accent ),
			MinHeight = 6,
		};
		stack.Children.Add( bar );
		box.Child = stack;

		if( !string.IsNullOrWhiteSpace( comp.Id ) )
		{
			inst.PatchTargets[ comp.Id ] = ( patch, p ) =>
			{
				if( patch.Visible.HasValue )
					box.Visibility = patch.Visible.Value ? Visibility.Visible : Visibility.Collapsed;
				if( patch.Title is not null )
					lbl.Text = patch.Title;
				if( patch.Max.HasValue && patch.Max.Value > 0 )
					bar.Maximum = patch.Max.Value;
				if( patch.Indeterminate.HasValue )
					bar.IsIndeterminate = patch.Indeterminate.Value;
				if( patch.NumericValue.HasValue )
					bar.Value = Math.Clamp( patch.NumericValue.Value, 0, bar.Maximum );
				if( patch.StatusText is not null || patch.NumericValue.HasValue || patch.Indeterminate.HasValue )
					status.Text = FormatStatus( bar.Value, bar.Maximum, bar.IsIndeterminate, patch.StatusText );
				if( !string.IsNullOrWhiteSpace( patch.Severity ) || !string.IsNullOrWhiteSpace( patch.AccentColor ) )
				{
					Color nextAccent = ResolveAccentColor( patch.Severity, patch.AccentColor, p );
					bar.Foreground = Brush( nextAccent );
					status.Foreground = Brush( nextAccent );
				}
			};
		}

		return box;
	}

	private static FrameworkElement RenderCode( DynamicWindowInstance inst, DynamicComponentSpec comp, ThemePalette pal )
	{
		Border frame = new()
		{
			Background = Brush( pal.CodeBg ),
			BorderBrush = Brush( pal.Border ),
			BorderThickness = new Thickness( 1 ),
			CornerRadius = new CornerRadius( 8 ),
		};

		StackPanel outer = new() { Spacing = 0 };
		string codeText = comp.Content ?? comp.Text ?? string.Empty;

		if( !string.IsNullOrWhiteSpace( comp.Title ) || !string.IsNullOrWhiteSpace( comp.Language ) || comp.Copyable )
		{
			Border header = new()
			{
				Background = Brush( pal.SubtleBg ),
				BorderBrush = Brush( pal.Border ),
				BorderThickness = new Thickness( 0, 0, 0, 1 ),
				Padding = new Thickness( 12, 6, 10, 6 ),
			};

			Grid hdrGrid = new() { ColumnSpacing = 8 };
			hdrGrid.ColumnDefinitions.Add( new ColumnDefinition { Width = new GridLength( 1, GridUnitType.Star ) } );
			hdrGrid.ColumnDefinitions.Add( new ColumnDefinition { Width = GridLength.Auto } );

			StackPanel left = new() { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
			if( !string.IsNullOrWhiteSpace( comp.Title ) )
			{
				left.Children.Add( new TextBlock
				{
					Text = comp.Title,
					FontSize = 12,
					FontWeight = FontWeights.SemiBold,
					Foreground = Brush( pal.PrimaryText ),
					VerticalAlignment = VerticalAlignment.Center,
				} );
			}
			if( !string.IsNullOrWhiteSpace( comp.Language ) )
				left.Children.Add( MakePill( comp.Language.ToUpperInvariant(), pal.Accent, pal ) );

			Grid.SetColumn( left, 0 );
			hdrGrid.Children.Add( left );

			if( comp.Copyable )
			{
				Button copyBtn = MakeHeaderButton( "Copy", pal, accent: false );
				copyBtn.Padding = new Thickness( 9, 3, 9, 3 );
				copyBtn.FontSize = 11.5;
				copyBtn.Click += ( _, _ ) =>
				{
					if( TryCopyToClipboard( codeText ) )
					{
						copyBtn.Content = "Copied!";
						inst.SetStatusFeedback( $"Copied {comp.Title ?? "code snippet"} to clipboard" );
					}
				};
				Grid.SetColumn( copyBtn, 1 );
				hdrGrid.Children.Add( copyBtn );
			}

			header.Child = hdrGrid;
			outer.Children.Add( header );
		}

		TextBlock codeBlock = new()
		{
			Text = codeText,
			FontFamily = new FontFamily( "Cascadia Mono, Consolas, DejaVu Sans Mono, monospace" ),
			FontSize = 12,
			Foreground = Brush( pal.CodeText ),
			IsTextSelectionEnabled = true,
			Padding = new Thickness( 12, 10, 12, 10 ),
		};

		ScrollViewer scroll = new()
		{
			Content = codeBlock,
			MaxHeight = comp.MaxHeight > 0 ? comp.MaxHeight : 260,
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
			HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
		};
		outer.Children.Add( scroll );
		frame.Child = outer;

		if( !string.IsNullOrWhiteSpace( comp.Id ) )
		{
			inst.PatchTargets[ comp.Id ] = ( patch, _ ) =>
			{
				if( patch.Visible.HasValue )
					frame.Visibility = patch.Visible.Value ? Visibility.Visible : Visibility.Collapsed;
				if( patch.Text is not null || patch.Value is not null )
				{
					codeText = patch.Text ?? patch.Value ?? string.Empty;
					codeBlock.Text = codeText;
				}
			};
		}

		return frame;
	}

	private static FrameworkElement RenderImage( DynamicComponentSpec comp, ThemePalette pal )
	{
		Border card = new()
		{
			Background = Brush( pal.CardBg ),
			BorderBrush = Brush( pal.Border ),
			BorderThickness = new Thickness( 1 ),
			CornerRadius = new CornerRadius( 8 ),
			Padding = new Thickness( 12 ),
		};

		StackPanel stack = new() { Spacing = 8 };
		if( !string.IsNullOrWhiteSpace( comp.Title ) )
		{
			stack.Children.Add( new TextBlock
			{
				Text = comp.Title,
				FontSize = 13,
				FontWeight = FontWeights.SemiBold,
				Foreground = Brush( pal.PrimaryText ),
			} );
		}

		Image img = new()
		{
			Stretch = Stretch.Uniform,
			MaxHeight = comp.Height ?? ( comp.MaxHeight > 0 ? comp.MaxHeight : 280 ),
			HorizontalAlignment = HorizontalAlignment.Center,
		};
		if( comp.Width.HasValue && comp.Width.Value > 0 )
			img.MaxWidth = comp.Width.Value;

		TextBlock statusFallback = new()
		{
			Text = "Loading image...",
			FontSize = 11.5,
			Foreground = Brush( pal.MutedText ),
			HorizontalAlignment = HorizontalAlignment.Center,
		};

		stack.Children.Add( img );
		stack.Children.Add( statusFallback );

		if( !string.IsNullOrWhiteSpace( comp.Caption ) )
		{
			stack.Children.Add( new TextBlock
			{
				Text = comp.Caption,
				FontSize = 11.5,
				Foreground = Brush( pal.MutedText ),
				HorizontalAlignment = HorizontalAlignment.Center,
				TextWrapping = TextWrapping.Wrap,
			} );
		}

		_ = LoadImageIntoControlAsync( img, statusFallback, comp.Base64, comp.FilePath );
		card.Child = stack;
		return card;
	}

	private static async Task LoadImageIntoControlAsync( Image img, TextBlock fallback, string? base64, string? filePath )
	{
		try
		{
			byte[]? bytes = null;
			if( !string.IsNullOrWhiteSpace( base64 ) )
			{
				string clean = base64.Trim();
				int comma = clean.IndexOf( ',' );
				if( clean.StartsWith( "data:", StringComparison.OrdinalIgnoreCase ) && comma > 0 )
					clean = clean[ ( comma + 1 ).. ];
				bytes = Convert.FromBase64String( clean );
			}
			else if( !string.IsNullOrWhiteSpace( filePath ) && File.Exists( filePath ) )
			{
				bytes = await File.ReadAllBytesAsync( filePath );
			}

			if( bytes is null || bytes.Length == 0 )
			{
				fallback.Text = "Image source could not be loaded.";
				return;
			}

			using InMemoryRandomAccessStream stream = new();
			await stream.WriteAsync( bytes.AsBuffer() );
			stream.Seek( 0 );

			BitmapImage bmp = new();
			await bmp.SetSourceAsync( stream );
			img.Source = bmp;
			fallback.Visibility = Visibility.Collapsed;
		}
		catch( Exception ex )
		{
			fallback.Text = $"Image load failed: {ex.Message}";
		}
	}

	private static FrameworkElement RenderDivider( DynamicComponentSpec comp, ThemePalette pal )
	{
		if( string.IsNullOrWhiteSpace( comp.Label ) )
		{
			return new Border
			{
				Height = 1,
				Background = Brush( pal.Border ),
				HorizontalAlignment = HorizontalAlignment.Stretch,
				Margin = new Thickness( 0, 2, 0, 2 ),
			};
		}

		Grid grid = new() { ColumnSpacing = 10, Margin = new Thickness( 0, 4, 0, 4 ) };
		grid.ColumnDefinitions.Add( new ColumnDefinition { Width = new GridLength( 1, GridUnitType.Star ) } );
		grid.ColumnDefinitions.Add( new ColumnDefinition { Width = GridLength.Auto } );
		grid.ColumnDefinitions.Add( new ColumnDefinition { Width = new GridLength( 1, GridUnitType.Star ) } );

		Border leftLine = new() { Height = 1, Background = Brush( pal.Border ), VerticalAlignment = VerticalAlignment.Center };
		TextBlock label = new() { Text = comp.Label, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Brush( pal.MutedText ), VerticalAlignment = VerticalAlignment.Center };
		Border rightLine = new() { Height = 1, Background = Brush( pal.Border ), VerticalAlignment = VerticalAlignment.Center };

		Grid.SetColumn( leftLine, 0 );
		Grid.SetColumn( label, 1 );
		Grid.SetColumn( rightLine, 2 );
		grid.Children.Add( leftLine );
		grid.Children.Add( label );
		grid.Children.Add( rightLine );
		return grid;
	}

	private static FrameworkElement RenderInput( DynamicWindowInstance inst, DynamicComponentSpec comp, ThemePalette pal )
	{
		string id = comp.Id ?? $"input_{inst.InputReaders.Count + 1}";
		StackPanel stack = new() { Spacing = 4 };
		if( !string.IsNullOrWhiteSpace( comp.Label ) )
		{
			stack.Children.Add( new TextBlock
			{
				Text = comp.Label,
				FontSize = 12,
				FontWeight = FontWeights.SemiBold,
				Foreground = Brush( pal.PrimaryText ),
			} );
		}

		TextBox box = new()
		{
			Text = comp.Value ?? string.Empty,
			PlaceholderText = comp.Placeholder ?? string.Empty,
			AcceptsReturn = comp.Multiline,
			TextWrapping = comp.Multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
			MinHeight = comp.Multiline ? 76 : 32,
		};
		stack.Children.Add( box );

		inst.InputReaders[ id ] = () => box.Text;
		inst.PatchTargets[ id ] = ( patch, _ ) =>
		{
			if( patch.Visible.HasValue )
				stack.Visibility = patch.Visible.Value ? Visibility.Visible : Visibility.Collapsed;
			if( patch.Value is not null || patch.Text is not null )
				box.Text = patch.Value ?? patch.Text ?? string.Empty;
		};

		return stack;
	}

	private static FrameworkElement RenderCheckbox( DynamicWindowInstance inst, DynamicComponentSpec comp, ThemePalette pal )
	{
		string id = comp.Id ?? $"checkbox_{inst.InputReaders.Count + 1}";
		CheckBox cb = new()
		{
			Content = comp.Label ?? comp.Title ?? id,
			IsChecked = comp.Checked,
			Foreground = Brush( pal.PrimaryText ),
		};

		inst.InputReaders[ id ] = () => cb.IsChecked == true;
		inst.PatchTargets[ id ] = ( patch, _ ) =>
		{
			if( patch.Visible.HasValue )
				cb.Visibility = patch.Visible.Value ? Visibility.Visible : Visibility.Collapsed;
			if( patch.Checked.HasValue )
				cb.IsChecked = patch.Checked.Value;
			if( patch.Title is not null || patch.Text is not null )
				cb.Content = patch.Title ?? patch.Text;
		};

		return cb;
	}

	private static FrameworkElement RenderSelect( DynamicWindowInstance inst, DynamicComponentSpec comp, ThemePalette pal )
	{
		string id = comp.Id ?? $"select_{inst.InputReaders.Count + 1}";
		StackPanel stack = new() { Spacing = 4 };
		if( !string.IsNullOrWhiteSpace( comp.Label ) )
		{
			stack.Children.Add( new TextBlock
			{
				Text = comp.Label,
				FontSize = 12,
				FontWeight = FontWeights.SemiBold,
				Foreground = Brush( pal.PrimaryText ),
			} );
		}

		ComboBox combo = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
		foreach( string opt in comp.Options )
			combo.Items.Add( opt );

		if( !string.IsNullOrWhiteSpace( comp.Selected ) )
		{
			int idx = comp.Options.FindIndex( o => string.Equals( o, comp.Selected, StringComparison.OrdinalIgnoreCase ) );
			combo.SelectedIndex = idx >= 0 ? idx : ( comp.Options.Count > 0 ? 0 : -1 );
		}
		else if( comp.Options.Count > 0 )
		{
			combo.SelectedIndex = 0;
		}

		stack.Children.Add( combo );
		inst.InputReaders[ id ] = () => combo.SelectedItem?.ToString();
		inst.PatchTargets[ id ] = ( patch, _ ) =>
		{
			if( patch.Visible.HasValue )
				stack.Visibility = patch.Visible.Value ? Visibility.Visible : Visibility.Collapsed;
			if( !string.IsNullOrWhiteSpace( patch.Selected ) )
			{
				int idx = comp.Options.FindIndex( o => string.Equals( o, patch.Selected, StringComparison.OrdinalIgnoreCase ) );
				if( idx >= 0 )
					combo.SelectedIndex = idx;
			}
		};

		return stack;
	}

	private static FrameworkElement RenderButtonRow( DynamicWindowInstance inst, DynamicComponentSpec comp, ThemePalette pal )
	{
		StackPanel row = new()
		{
			Orientation = Orientation.Horizontal,
			Spacing = 10,
			HorizontalAlignment = ToHorizontalAlignment( comp.Align ),
		};

		foreach( DynamicButtonSpec btnSpec in comp.Buttons )
		{
			Color btnAccent = !string.IsNullOrWhiteSpace( btnSpec.Severity )
				? ResolveAccentColor( btnSpec.Severity, null, pal )
				: pal.Accent;

			Button btn = new()
			{
				Name = $"McpDynamicWindowButton_{SanitizeControlName( inst.Spec.WindowId )}_{SanitizeControlName( btnSpec.Id )}",
				Content = btnSpec.Label,
				Padding = new Thickness( 14, 7, 14, 7 ),
				CornerRadius = new CornerRadius( 6 ),
				FontSize = 12.5,
				FontWeight = btnSpec.IsAccent ? FontWeights.SemiBold : FontWeights.Normal,
				Background = Brush( btnSpec.IsAccent ? btnAccent : pal.SubtleBg ),
				Foreground = Brush( btnSpec.IsAccent ? ContrastTextFor( btnAccent ) : pal.PrimaryText ),
				BorderBrush = Brush( btnSpec.IsAccent ? btnAccent : pal.Border ),
				BorderThickness = new Thickness( 1 ),
			};

			btn.Click += ( _, _ ) => HandleDynamicButtonClick( inst, btnSpec );
			row.Children.Add( btn );
		}

		return row;
	}

	private static void HandleDynamicButtonClick( DynamicWindowInstance inst, DynamicButtonSpec btn )
	{
		inst.RecordEvent( new DynamicWindowEvent(
			TimestampUtc: DateTimeOffset.UtcNow,
			EventType: "button_clicked",
			ElementId: btn.Id,
			Label: btn.Label,
			Value: btn.Action,
			InputValues: inst.ReadInputValues() ) );

		switch( btn.Action )
		{
			case "copy":
				string payload = btn.CopyText ?? DynamicWindowSpecParser.BuildTextSummary( inst.Spec );
				inst.SetStatusFeedback( TryCopyToClipboard( payload )
					? $"Copied '{btn.Label}' content to clipboard"
					: "Clipboard unavailable" );
				break;

			case "close":
				CloseInstanceSafe( inst );
				break;

			case "navigate":
				NavigateMainWindow( btn.NavigatePage, btn.SettingsSection );
				inst.SetStatusFeedback( $"Navigated HackPDM to {btn.NavigatePage ?? "Main Window"}" );
				break;

			default:
				inst.SetStatusFeedback( $"Action '{btn.Label}' recorded for AI" );
				break;
		}
	}

	private static void NavigateMainWindow( string? pageName, string? settingsSection )
	{
		try
		{
			if( !string.IsNullOrWhiteSpace( pageName )
				&& Enum.TryParse<NavigatePageMenu>( pageName.Trim(), ignoreCase: true, out NavigatePageMenu target ) )
			{
				AppComposition.Navigation.NavigateTo( target );
			}

			if( !string.IsNullOrWhiteSpace( settingsSection )
				&& HackApp.HomePage?.ContentFrame.Content is ApplicationSettingsView settingsView )
			{
				settingsView.SelectSection( settingsSection );
			}

			HackApp.Window?.Activate();
		}
		catch
		{
		}
	}

	// ------------------------------------------------------------------ Helpers: Theme Palette, Pills, Grid, Clipboard

	private static Grid CreateTableRowGrid( IReadOnlyList<DynamicTableColumn> columns )
	{
		Grid grid = new() { ColumnSpacing = 12 };
		foreach( DynamicTableColumn col in columns )
		{
			GridLength gl = string.IsNullOrWhiteSpace( col.Width )
				? new GridLength( 1, GridUnitType.Star )
				: ParseGridLength( col.Width );
			grid.ColumnDefinitions.Add( new ColumnDefinition { Width = gl } );
		}
		return grid;
	}

	private static GridLength ParseGridLength( string raw )
	{
		string s = raw.Trim();
		if( string.Equals( s, "auto", StringComparison.OrdinalIgnoreCase ) )
			return GridLength.Auto;
		if( s.EndsWith( "*", StringComparison.Ordinal ) )
		{
			string num = s[ ..^1 ].Trim();
			double weight = string.IsNullOrEmpty( num ) ? 1 : ( double.TryParse( num, out double w ) && w > 0 ? w : 1 );
			return new GridLength( weight, GridUnitType.Star );
		}
		if( double.TryParse( s, out double px ) && px > 0 )
			return new GridLength( px, GridUnitType.Pixel );
		return new GridLength( 1, GridUnitType.Star );
	}

	private static HorizontalAlignment ToHorizontalAlignment( string align ) => align switch
	{
		"center" => HorizontalAlignment.Center,
		"right" => HorizontalAlignment.Right,
		_ => HorizontalAlignment.Left,
	};

	private static Border MakePill( string label, Color color, ThemePalette pal )
	{
		_ = pal;
		return new Border
		{
			Background = Brush( WithAlpha( color, 0x28 ) ),
			BorderBrush = Brush( WithAlpha( color, 0x99 ) ),
			BorderThickness = new Thickness( 1 ),
			CornerRadius = new CornerRadius( 10 ),
			Padding = new Thickness( 8, 2, 8, 2 ),
			VerticalAlignment = VerticalAlignment.Center,
			Child = new TextBlock
			{
				Text = label,
				FontSize = 11,
				FontWeight = FontWeights.SemiBold,
				Foreground = Brush( color ),
				VerticalAlignment = VerticalAlignment.Center,
			},
		};
	}

	private static Button MakeHeaderButton( string label, ThemePalette pal, bool accent ) => new()
	{
		Content = label,
		FontSize = 12,
		Padding = new Thickness( 11, 5, 11, 5 ),
		CornerRadius = new CornerRadius( 6 ),
		Background = Brush( accent ? pal.Accent : pal.SubtleBg ),
		Foreground = Brush( accent ? ContrastTextFor( pal.Accent ) : pal.PrimaryText ),
		BorderBrush = Brush( accent ? pal.Accent : pal.Border ),
		BorderThickness = new Thickness( 1 ),
	};

	private static Color InferBadgeColor( string text, ThemePalette pal )
	{
		string s = text.Trim().ToLowerInvariant();
		if( s.Contains( "error" ) || s.Contains( "broken" ) || s.Contains( "missing" ) || s.Contains( "critical" ) || s.Contains( "fail" ) || s.Contains( "deleted" ) || s == "dt" )
			return Color.FromArgb( 0xFF, 0xEF, 0x44, 0x44 );
		if( s.Contains( "warn" ) || s.Contains( "modif" ) || s.Contains( "out" ) || s.Contains( "pending" ) || s.Contains( "stale" ) || s.Contains( "review" ) || s is "lm" or "cm" or "co" )
			return Color.FromArgb( 0xFF, 0xF5, 0x9E, 0x0B );
		if( s.Contains( "ok" ) || s.Contains( "sync" ) || s.Contains( "valid" ) || s.Contains( "pass" ) || s.Contains( "ready" ) || s.Contains( "clean" ) || s.Contains( "released" ) )
			return Color.FromArgb( 0xFF, 0x10, 0xB9, 0x81 );
		if( s.Contains( "new" ) || s.Contains( "local" ) || s.Contains( "info" ) || s == "lo" )
			return Color.FromArgb( 0xFF, 0x38, 0xBD, 0xF8 );
		return pal.Accent;
	}

	private static Color ResolveAccentColor( string? severity, string? hexOverride, ThemePalette pal )
	{
		if( ThemeColorParse.TryParseHex( hexOverride, out uint argb ) )
			return ToColor( argb );

		return ( severity?.Trim().ToLowerInvariant() ) switch
		{
			"warning" => Color.FromArgb( 0xFF, 0xF5, 0x9E, 0x0B ),
			"critical" => Color.FromArgb( 0xFF, 0xEF, 0x44, 0x44 ),
			"success" => Color.FromArgb( 0xFF, 0x10, 0xB9, 0x81 ),
			"tip" => Color.FromArgb( 0xFF, 0x06, 0xB6, 0xD4 ),
			"action" => pal.Accent,
			"info" => Color.FromArgb( 0xFF, 0x60, 0xA5, 0xFA ),
			_ => pal.Accent,
		};
	}

	private static Color? GetThemeColor( IThemeService theme, string key )
		=> theme.GetColor( key ) is { } c ? Color.FromArgb( c.A, c.R, c.G, c.B ) : null;

	private static ThemePalette ResolvePalette()
	{
		var theme = AppComposition.Theme;
		bool dark = theme.IsDarkActive;
		AppPalette basePal = ThemePresets.Palette( theme.Palette );

		Color accent = GetThemeColor( theme, "AccentFillColorDefaultBrush" )
			?? GetThemeColor( theme, "SystemControlHighlightAccentBrush" )
			?? WithAlpha( ToColor( basePal.FancyPanelStroke ), 0xFF );

		if( dark )
		{
			Color bg = GetThemeColor( theme, "ApplicationPageBackgroundThemeBrush" ) ?? ToColor( basePal.PageBackground );
			Color card = GetThemeColor( theme, "LayerFillColorDefaultBrush" ) ?? BlendColor( bg, Color.FromArgb( 0xFF, 0xFF, 0xFF, 0xFF ), 0.06 );
			Color header = BlendColor( bg, Color.FromArgb( 0xFF, 0xFF, 0xFF, 0xFF ), 0.04 );
			Color subtle = BlendColor( card, Color.FromArgb( 0xFF, 0xFF, 0xFF, 0xFF ), 0.05 );
			return new ThemePalette(
				IsDark: true,
				WindowBg: bg,
				HeaderBg: header,
				CardBg: card,
				SubtleBg: subtle,
				StripeBg: Color.FromArgb( 0x16, 0xFF, 0xFF, 0xFF ),
				CodeBg: Color.FromArgb( 0xFF, 0x11, 0x15, 0x1D ),
				Border: Color.FromArgb( 0x33, 0xFF, 0xFF, 0xFF ),
				Hairline: Color.FromArgb( 0x1F, 0xFF, 0xFF, 0xFF ),
				PrimaryText: Color.FromArgb( 0xFF, 0xF3, 0xF5, 0xF9 ),
				MutedText: Color.FromArgb( 0xFF, 0x9C, 0xA7, 0xB8 ),
				CodeText: Color.FromArgb( 0xFF, 0xD7, 0xE6, 0xF8 ),
				Accent: accent );
		}
		else
		{
			Color bg = GetThemeColor( theme, "ApplicationPageBackgroundThemeBrush" ) ?? ToColor( basePal.PageBackground );
			Color card = Color.FromArgb( 0xFF, 0xFF, 0xFF, 0xFF );
			Color header = BlendColor( bg, Color.FromArgb( 0xFF, 0x00, 0x00, 0x00 ), 0.03 );
			Color subtle = Color.FromArgb( 0xFF, 0xF3, 0xF5, 0xF8 );
			return new ThemePalette(
				IsDark: false,
				WindowBg: bg,
				HeaderBg: header,
				CardBg: card,
				SubtleBg: subtle,
				StripeBg: Color.FromArgb( 0x0A, 0x00, 0x00, 0x00 ),
				CodeBg: Color.FromArgb( 0xFF, 0xF4, 0xF6, 0xFA ),
				Border: Color.FromArgb( 0x28, 0x00, 0x00, 0x00 ),
				Hairline: Color.FromArgb( 0x16, 0x00, 0x00, 0x00 ),
				PrimaryText: Color.FromArgb( 0xFF, 0x14, 0x19, 0x23 ),
				MutedText: Color.FromArgb( 0xFF, 0x52, 0x5E, 0x70 ),
				CodeText: Color.FromArgb( 0xFF, 0x1F, 0x29, 0x37 ),
				Accent: accent );
		}
	}

	private static Color BlendColor( Color baseColor, Color tint, double amount ) => Color.FromArgb(
		0xFF,
		(byte)Math.Clamp( baseColor.R + ( tint.R - baseColor.R ) * amount, 0, 255 ),
		(byte)Math.Clamp( baseColor.G + ( tint.G - baseColor.G ) * amount, 0, 255 ),
		(byte)Math.Clamp( baseColor.B + ( tint.B - baseColor.B ) * amount, 0, 255 ) );

	private static Color ToColor( uint argb ) => Color.FromArgb(
		(byte)( argb >> 24 ),
		(byte)( argb >> 16 ),
		(byte)( argb >> 8 ),
		(byte)argb );

	private static Color WithAlpha( Color c, byte a ) => Color.FromArgb( a, c.R, c.G, c.B );

	private static SolidColorBrush Brush( Color c ) => new( c );

	private static Color ContrastTextFor( Color bg )
	{
		uint packed = ( (uint)bg.A << 24 ) | ( (uint)bg.R << 16 ) | ( (uint)bg.G << 8 ) | bg.B;
		double withBlack = ThemePresets.Contrast( 0xFF000000, packed );
		double withWhite = ThemePresets.Contrast( 0xFFFFFFFF, packed );
		return withBlack >= withWhite
			? Color.FromArgb( 0xFF, 0x09, 0x0E, 0x18 )
			: Color.FromArgb( 0xFF, 0xFF, 0xFF, 0xFF );
	}

	private static string StripInlineMarkdown( string text )
		=> text.Replace( "**", "" ).Replace( "__", "" ).Replace( "`", "" );

	private static string SanitizeControlName( string raw )
		=> new( raw.Select( ch => char.IsLetterOrDigit( ch ) || ch == '_' ? ch : '_' ).ToArray() );

	private static bool TryCopyToClipboard( string text )
	{
		try
		{
			DataPackage pkg = new();
			pkg.SetText( text );
			Clipboard.SetContent( pkg );
			return true;
		}
		catch
		{
			return false;
		}
	}

	// ------------------------------------------------------------------ Internal Types

	private readonly record struct ThemePalette(
		bool IsDark,
		Color WindowBg,
		Color HeaderBg,
		Color CardBg,
		Color SubtleBg,
		Color StripeBg,
		Color CodeBg,
		Color Border,
		Color Hairline,
		Color PrimaryText,
		Color MutedText,
		Color CodeText,
		Color Accent );

	private sealed record DynamicWindowEvent(
		DateTimeOffset TimestampUtc,
		string EventType,
		string ElementId,
		string Label,
		string? Value,
		Dictionary<string, object?> InputValues );

	private sealed class DynamicWindowInstance
	{
		public DynamicWindowSpec Spec { get; set; }
		public Window Window { get; }
		public Grid RootGrid { get; } = new();
		public StackPanel? BodyStack { get; set; }
		public TextBlock? HeaderTitleBlock { get; set; }
		public TextBlock? HeaderSubtitleBlock { get; set; }
		public TextBlock? HeaderBadgeBlock { get; set; }
		public TextBlock? FooterStatusBlock { get; set; }
		public TextBlock? FooterMetaBlock { get; set; }
		public DateTimeOffset CreatedAtUtc { get; } = DateTimeOffset.UtcNow;
		public DateTimeOffset LastUpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
		public bool IsClosed { get; set; }
		public Action ThemeChangedHandler { get; }

		public Dictionary<string, Action<DynamicWindowPatch, ThemePalette>> PatchTargets { get; } = new( StringComparer.OrdinalIgnoreCase );
		public Dictionary<string, Func<object?>> InputReaders { get; } = new( StringComparer.OrdinalIgnoreCase );
		public List<DynamicWindowEvent> Events { get; } = [];
		public List<TaskCompletionSource<bool>> Waiters { get; } = [];

		public DynamicWindowInstance( DynamicWindowSpec spec, Window window )
		{
			Spec = spec;
			Window = window;
			ThemeChangedHandler = () =>
			{
				UiDispatcher.Post( () =>
				{
					if( !IsClosed )
						RenderAll( preserveInputs: true );
				} );
			};
		}

		public void RenderAll( bool preserveInputs ) => RenderShell( this, preserveInputs );

		public Dictionary<string, object?> ReadInputValues()
		{
			Dictionary<string, object?> values = new( StringComparer.OrdinalIgnoreCase );
			foreach( var (k, getter) in InputReaders )
				values[ k ] = getter();
			return values;
		}

		public void SetStatusFeedback( string text )
		{
			if( FooterStatusBlock is not null )
				FooterStatusBlock.Text = $"{DateTime.Now:HH:mm:ss} • {text}";
		}

		public void UpdateFooterMeta()
		{
			if( FooterMetaBlock is not null )
				FooterMetaBlock.Text = $"{Spec.Components.Count} top-level component(s) • {PatchTargets.Count} live-patchable ID(s)";
		}

		public void RecordEvent( DynamicWindowEvent ev )
		{
			Events.Add( ev );
			if( Events.Count > 200 )
				Events.RemoveAt( 0 );
			foreach( TaskCompletionSource<bool> waiter in Waiters.ToList() )
				waiter.TrySetResult( true );
		}
	}
}

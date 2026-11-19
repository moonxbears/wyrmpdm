using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using HackPDM.Core.Theming;
using HackPDM.Mvvm.ViewModels.Common;
using HackPDM.Mvvm.Views.Hack;
using HackPDM.Mvvm.Views.Settings;
using HackPDM.Shared.GlobalData;

using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

using Windows.Foundation;
using Windows.UI;

namespace HackPDM.Shell.Mcp;

internal sealed record GuidedButtonSpec( string Id, string Label, bool IsAccent );

internal sealed record GuidedTourStep(
	string Title,
	string Message,
	string? Page,
	string? SettingsSection,
	string? TargetName,
	string? TargetAutomationId,
	string? TargetText,
	string? TargetControlType,
	string Animation,
	string? BadgeText,
	string? Severity,
	string? AccentColorHex );

/// <summary>
/// Manages non-destructive MCP Guided Actions &amp; Attention Cues overlaid on HackPDM's live GUI:
/// temporary control highlights &amp; animations (<c>pulse</c>, <c>spotlight</c>, <c>bounce</c>,
/// <c>shake</c>, <c>flash</c>, <c>outline</c>), floating/anchored interactive guided popups
/// (with optional <c>waitForUser</c> button responses), and interactive multi-step guided tours.
/// Uses pure cross-platform XAML primitives + <see cref="DispatcherTimer"/> so rendering and
/// animations behave identically on WinUI 3 (Windows) and Uno Skia (Linux/macOS).
/// </summary>
internal static class GuidedActionOverlayManager
{
	private static readonly Dictionary<string, ActiveCue> ActiveCues = new( StringComparer.Ordinal );
	private static DispatcherTimer? _timer;
	private static Canvas? _boundCanvas;
	private static ActiveTour? _activeTour;

	private static Canvas GetCanvas()
	{
		HomeView home = HackApp.HomePage ?? throw new InvalidOperationException( "HackPDM HomeView is not initialized." );
		Canvas canvas = home.GuidedOverlayCanvas;
		if( !ReferenceEquals( _boundCanvas, canvas ) )
		{
			// Soft UI reload replaced HomeView — clear stale cue references cleanly
			ClearAllInternal( "ui_reloaded" );
			_boundCanvas = canvas;
		}
		return canvas;
	}

	private static UIElement GetSearchRoot()
	{
		if( HackApp.Window?.Content is UIElement root )
			return root;
		if( HackApp.HomePage is UIElement home )
			return home;
		throw new InvalidOperationException( "Main window content is not ready." );
	}

	// ------------------------------------------------------------------ Highlight & Temporary Animation

	public static async Task<object> ShowHighlightAsync(
		string? targetName,
		string? targetAutomationId,
		string? targetText,
		string? targetControlType,
		string? page,
		string? settingsSection,
		string animation,
		string? title,
		string? message,
		string? badgeText,
		string severity,
		string? accentColorHex,
		int durationMs,
		bool dismissOnTargetClick,
		bool replaceExisting,
		CancellationToken cancellationToken )
	{
		await NavigateIfRequestedAsync( page, settingsSection, cancellationToken );

		FrameworkElement target = ResolveTargetElement( targetName, targetAutomationId, targetText, targetControlType );
		Canvas canvas = GetCanvas();
		canvas.UpdateLayout();

		if( replaceExisting )
			ClearByKind( "highlight" );

		string cueId = "hl-" + Guid.NewGuid().ToString( "N" )[ ..8 ];
		Color accent = ResolveAccentColor( severity, accentColorHex );
		string anim = NormalizeAnimation( animation );

		ActiveCue cue = new( cueId, "highlight", anim, target, title, message, durationMs );
		BuildHighlightVisuals( cue, canvas, accent, anim, title, message, badgeText, severity, dismissOnTargetClick );

		RegisterCue( cue );
		PositionCue( cue, canvas, 0 );

		Rect bounds = GetElementBounds( target, canvas );
		return new
		{
			ok = true,
			cueId,
			kind = "highlight",
			animation = anim,
			targetName = target.Name,
			targetType = target.GetType().Name,
			bounds = new { x = Math.Round( bounds.X ), y = Math.Round( bounds.Y ), width = Math.Round( bounds.Width ), height = Math.Round( bounds.Height ) },
			durationMs,
			dismissOnTargetClick,
			activeCueCount = ActiveCues.Count,
		};
	}

	// ------------------------------------------------------------------ Interactive Guided Action Popup

	public static async Task<object> ShowPopupAsync(
		string title,
		string message,
		string? badge,
		string severity,
		string? accentColorHex,
		string placement,
		string? targetName,
		string? targetAutomationId,
		string? targetText,
		string? targetControlType,
		string? page,
		string? settingsSection,
		string highlightAnimation,
		IReadOnlyList<GuidedButtonSpec> buttons,
		int durationMs,
		bool waitForUser,
		int timeoutSeconds,
		bool replaceExisting,
		CancellationToken cancellationToken )
	{
		await NavigateIfRequestedAsync( page, settingsSection, cancellationToken );

		FrameworkElement? anchor = null;
		bool hasTarget = !string.IsNullOrWhiteSpace( targetName )
			|| !string.IsNullOrWhiteSpace( targetAutomationId )
			|| !string.IsNullOrWhiteSpace( targetText )
			|| !string.IsNullOrWhiteSpace( targetControlType );
		if( hasTarget )
			anchor = ResolveTargetElement( targetName, targetAutomationId, targetText, targetControlType );

		Canvas canvas = GetCanvas();
		canvas.UpdateLayout();

		if( replaceExisting )
			ClearByKind( "popup" );

		string cueId = "pop-" + Guid.NewGuid().ToString( "N" )[ ..8 ];
		Color accent = ResolveAccentColor( severity, accentColorHex );
		string anim = anchor is not null ? NormalizeAnimation( highlightAnimation ) : "outline";
		int effectiveDuration = waitForUser ? 0 : Math.Max( 0, durationMs );

		ActiveCue cue = new( cueId, "popup", anim, anchor, title, message, effectiveDuration )
		{
			Placement = string.IsNullOrWhiteSpace( placement )
				? ( anchor is not null ? "anchor" : "top" )
				: placement.Trim().ToLowerInvariant(),
		};

		TaskCompletionSource<PopupDecision>? waiter = waitForUser
			? new( TaskCreationOptions.RunContinuationsAsynchronously )
			: null;
		cue.PopupWaiter = waiter;

		// Optional target highlight halo / spotlight underneath the popup card
		if( anchor is not null )
			BuildTargetHaloElements( cue, canvas, accent, anim, badgeText: null, dismissOnTargetClick: false );

		// Build the rich interactive popup card
		IReadOnlyList<GuidedButtonSpec> effectiveButtons = buttons.Count > 0
			? buttons
			: [ new GuidedButtonSpec( "ok", "Got It", true ) ];

		Border card = BuildPopupCard(
			cueId: cueId,
			title: title,
			message: message,
			badge: string.IsNullOrWhiteSpace( badge ) ? DefaultBadgeForSeverity( severity ) : badge,
			accent: accent,
			buttons: effectiveButtons,
			stepProgress: null,
			onButtonClicked: ( btnId, btnLabel ) =>
			{
				cue.PopupWaiter?.TrySetResult( new PopupDecision( btnId, btnLabel, Dismissed: false, TimedOut: false ) );
				RemoveCue( cueId );
			},
			onDismiss: () =>
			{
				cue.PopupWaiter?.TrySetResult( new PopupDecision( null, null, Dismissed: true, TimedOut: false ) );
				RemoveCue( cueId );
			} );

		Canvas.SetZIndex( card, 40 );
		canvas.Children.Add( card );
		cue.CardElement = card;
		cue.OwnedElements.Add( card );

		RegisterCue( cue );
		card.Measure( new Size( 440, 500 ) );
		PositionCue( cue, canvas, 0 );

		if( waiter is null )
		{
			return new
			{
				ok = true,
				cueId,
				kind = "popup",
				displayed = true,
				placement = cue.Placement,
				anchoredTo = anchor?.Name,
				durationMs = effectiveDuration,
				buttons = effectiveButtons.Select( b => new { b.Id, b.Label, b.IsAccent } ).ToList(),
				activeCueCount = ActiveCues.Count,
			};
		}

		// Wait asynchronously (without blocking the UI thread!) for user click, dismiss, cancellation, or timeout
		int clampedTimeout = Math.Clamp( timeoutSeconds, 3, 600 );
		try
		{
			PopupDecision decision = await waiter.Task.WaitAsync( TimeSpan.FromSeconds( clampedTimeout ), cancellationToken );
			return new
			{
				ok = true,
				cueId,
				kind = "popup",
				waitedForUser = true,
				clickedButtonId = decision.ButtonId,
				clickedLabel = decision.ButtonLabel,
				dismissed = decision.Dismissed,
				timedOut = decision.TimedOut,
			};
		}
		catch( TimeoutException )
		{
			UiDispatcher.Post( () => RemoveCue( cueId ) );
			return new
			{
				ok = true,
				cueId,
				kind = "popup",
				waitedForUser = true,
				clickedButtonId = (string?)null,
				clickedLabel = (string?)null,
				dismissed = false,
				timedOut = true,
			};
		}
		catch( OperationCanceledException )
		{
			UiDispatcher.Post( () => RemoveCue( cueId ) );
			throw;
		}
	}

	// ------------------------------------------------------------------ Multi-Step Interactive Guided Tour

	public static async Task<object> ControlTourAsync(
		string action,
		string? tourTitle,
		IReadOnlyList<GuidedTourStep>? steps,
		int? stepIndex,
		CancellationToken cancellationToken )
	{
		string act = ( action ?? "start" ).Trim().ToLowerInvariant();
		switch( act )
		{
			case "status":
				return SnapshotTour();

			case "stop" or "close" or "cancel":
				StopTourInternal( completed: false );
				return new { ok = true, stopped = true, tour = SnapshotTour() };

			case "start":
				if( steps is null || steps.Count == 0 )
					throw new ArgumentException( "Provide at least one step in 'steps' when starting a guided tour." );
				StopTourInternal( completed: false );
				_activeTour = new ActiveTour(
					id: "tour-" + Guid.NewGuid().ToString( "N" )[ ..8 ],
					title: string.IsNullOrWhiteSpace( tourTitle ) ? "Guided Walkthrough" : tourTitle.Trim(),
					steps: [ .. steps ] );
				await RenderCurrentTourStepAsync( cancellationToken );
				return new { ok = true, started = true, tour = SnapshotTour() };

			case "next":
				if( _activeTour is null )
					throw new InvalidOperationException( "No guided tour is currently active." );
				if( _activeTour.CurrentIndex + 1 >= _activeTour.Steps.Count )
				{
					StopTourInternal( completed: true );
					return new { ok = true, finished = true, tour = SnapshotTour() };
				}
				_activeTour.CurrentIndex++;
				await RenderCurrentTourStepAsync( cancellationToken );
				return new { ok = true, tour = SnapshotTour() };

			case "previous" or "prev" or "back":
				if( _activeTour is null )
					throw new InvalidOperationException( "No guided tour is currently active." );
				if( _activeTour.CurrentIndex > 0 )
					_activeTour.CurrentIndex--;
				await RenderCurrentTourStepAsync( cancellationToken );
				return new { ok = true, tour = SnapshotTour() };

			case "goto":
				if( _activeTour is null )
					throw new InvalidOperationException( "No guided tour is currently active." );
				int targetIdx = Math.Clamp( stepIndex ?? 0, 0, _activeTour.Steps.Count - 1 );
				_activeTour.CurrentIndex = targetIdx;
				await RenderCurrentTourStepAsync( cancellationToken );
				return new { ok = true, tour = SnapshotTour() };

			default:
				throw new ArgumentException( $"Unknown tour action '{action}'. Valid: start, next, previous, goto, status, stop." );
		}
	}

	private static async Task RenderCurrentTourStepAsync( CancellationToken cancellationToken )
	{
		if( _activeTour is not { } tour )
			return;

		ClearByKind( "tour" );
		GuidedTourStep step = tour.Steps[ tour.CurrentIndex ];

		await NavigateIfRequestedAsync( step.Page, step.SettingsSection, cancellationToken );

		FrameworkElement? target = null;
		bool wantsTarget = !string.IsNullOrWhiteSpace( step.TargetName )
			|| !string.IsNullOrWhiteSpace( step.TargetAutomationId )
			|| !string.IsNullOrWhiteSpace( step.TargetText )
			|| !string.IsNullOrWhiteSpace( step.TargetControlType );
		if( wantsTarget )
		{
			try
			{
				target = ResolveTargetElement( step.TargetName, step.TargetAutomationId, step.TargetText, step.TargetControlType );
			}
			catch
			{
				// Fall back gracefully to centered/top floating card if control isn't mounted yet
				target = null;
			}
		}

		Canvas canvas = GetCanvas();
		canvas.UpdateLayout();

		string cueId = $"{tour.Id}-step-{tour.CurrentIndex + 1}";
		string sev = string.IsNullOrWhiteSpace( step.Severity ) ? "action" : step.Severity;
		Color accent = ResolveAccentColor( sev, step.AccentColorHex );
		string anim = target is not null
			? NormalizeAnimation( string.IsNullOrWhiteSpace( step.Animation ) ? "spotlight" : step.Animation )
			: "outline";

		ActiveCue cue = new( cueId, "tour", anim, target, step.Title, step.Message, durationMs: 0 )
		{
			Placement = target is not null ? "anchor" : "top",
		};

		if( target is not null )
			BuildTargetHaloElements( cue, canvas, accent, anim, step.BadgeText, dismissOnTargetClick: false );

		bool isFirst = tour.CurrentIndex == 0;
		bool isLast = tour.CurrentIndex == tour.Steps.Count - 1;
		List<GuidedButtonSpec> navButtons = [];
		if( !isFirst )
			navButtons.Add( new GuidedButtonSpec( "back", "Back", IsAccent: false ) );
		navButtons.Add( new GuidedButtonSpec( isLast ? "finish" : "next", isLast ? "Finish" : "Next", IsAccent: true ) );

		string progress = $"{tour.Title} • Step {tour.CurrentIndex + 1} of {tour.Steps.Count}";
		Border card = BuildPopupCard(
			cueId: cueId,
			title: step.Title,
			message: step.Message,
			badge: string.IsNullOrWhiteSpace( step.BadgeText ) ? $"STEP {tour.CurrentIndex + 1}/{tour.Steps.Count}" : step.BadgeText,
			accent: accent,
			buttons: navButtons,
			stepProgress: progress,
			isTourButton: true,
			onButtonClicked: ( btnId, _label ) =>
			{
				if( btnId == "back" )
					_ = AdvanceTourFromUiAsync( -1 );
				else if( btnId == "next" )
					_ = AdvanceTourFromUiAsync( +1 );
				else
					StopTourInternal( completed: true );
			},
			onDismiss: () => StopTourInternal( completed: false ) );

		Canvas.SetZIndex( card, 50 );
		canvas.Children.Add( card );
		cue.CardElement = card;
		cue.OwnedElements.Add( card );

		RegisterCue( cue );
		card.Measure( new Size( 440, 500 ) );
		PositionCue( cue, canvas, 0 );
	}

	private static async Task AdvanceTourFromUiAsync( int delta )
	{
		if( _activeTour is null )
			return;
		int nextIdx = _activeTour.CurrentIndex + delta;
		if( nextIdx < 0 )
			nextIdx = 0;
		if( nextIdx >= _activeTour.Steps.Count )
		{
			StopTourInternal( completed: true );
			return;
		}
		_activeTour.CurrentIndex = nextIdx;
		try
		{
			await RenderCurrentTourStepAsync( CancellationToken.None );
		}
		catch
		{
			// Never crash UI event handler
		}
	}

	private static void StopTourInternal( bool completed )
	{
		if( _activeTour is not null )
		{
			_activeTour.IsActive = false;
			_activeTour.Completed = completed;
		}
		ClearByKind( "tour" );
	}

	// ------------------------------------------------------------------ Clear & Inspect

	public static object ClearOrList( string? cueId, bool listOnly )
	{
		if( !listOnly )
		{
			if( !string.IsNullOrWhiteSpace( cueId ) )
			{
				bool removed = RemoveCue( cueId.Trim() );
				return new { ok = removed, removedCueId = cueId, removed, active = SnapshotActiveCues(), tour = SnapshotTour() };
			}
			int count = ActiveCues.Count;
			StopTourInternal( completed: false );
			ClearAllInternal( "cleared_by_mcp" );
			return new { ok = true, clearedCount = count, active = SnapshotActiveCues(), tour = SnapshotTour() };
		}

		return new
		{
			ok = true,
			activeCount = ActiveCues.Count,
			active = SnapshotActiveCues(),
			tour = SnapshotTour(),
		};
	}

	private static List<object> SnapshotActiveCues()
		=> ActiveCues.Values.Select( c => (object)new
		{
			c.Id,
			c.Kind,
			c.Animation,
			c.Title,
			c.Message,
			targetName = c.Target?.Name,
			targetType = c.Target?.GetType().Name,
			c.Placement,
			c.DurationMs,
			elapsedMs = (long)( DateTimeOffset.UtcNow - c.StartedAtUtc ).TotalMilliseconds,
			waitingForUser = c.PopupWaiter is not null && !c.PopupWaiter.Task.IsCompleted,
		} ).ToList();

	private static object SnapshotTour()
	{
		if( _activeTour is null )
			return new { isActive = false };
		GuidedTourStep? cur = _activeTour.IsActive && _activeTour.CurrentIndex < _activeTour.Steps.Count
			? _activeTour.Steps[ _activeTour.CurrentIndex ]
			: null;
		return new
		{
			_activeTour.Id,
			_activeTour.Title,
			_activeTour.IsActive,
			_activeTour.Completed,
			currentStepIndex = _activeTour.CurrentIndex,
			currentStepNumber = _activeTour.CurrentIndex + 1,
			totalSteps = _activeTour.Steps.Count,
			currentStep = cur is null ? null : new { cur.Title, cur.Message, cur.Page, cur.SettingsSection, cur.TargetName, cur.Animation },
		};
	}

	// ------------------------------------------------------------------ Visual Builders & Layout

	private static void BuildHighlightVisuals(
		ActiveCue cue,
		Canvas canvas,
		Color accent,
		string anim,
		string? title,
		string? message,
		string? badgeText,
		string severity,
		bool dismissOnTargetClick )
	{
		BuildTargetHaloElements( cue, canvas, accent, anim, badgeText, dismissOnTargetClick );

		if( !string.IsNullOrWhiteSpace( title ) || !string.IsNullOrWhiteSpace( message ) )
		{
			Border card = BuildPopupCard(
				cueId: cue.Id,
				title: string.IsNullOrWhiteSpace( title ) ? "Guided Attention" : title,
				message: message ?? string.Empty,
				badge: string.IsNullOrWhiteSpace( badgeText ) ? DefaultBadgeForSeverity( severity ) : badgeText,
				accent: accent,
				buttons: [],
				stepProgress: null,
				onButtonClicked: null,
				onDismiss: () => RemoveCue( cue.Id ) );
			Canvas.SetZIndex( card, 35 );
			canvas.Children.Add( card );
			cue.CardElement = card;
			cue.OwnedElements.Add( card );
			card.Measure( new Size( 400, 320 ) );
		}
	}

	private static void BuildTargetHaloElements(
		ActiveCue cue,
		Canvas canvas,
		Color accent,
		string anim,
		string? badgeText,
		bool dismissOnTargetClick )
	{
		if( cue.Target is null )
			return;

		// 1. Optional 4-panel spotlight scrim (leaves an open click-through cutout over target!)
		if( anim == "spotlight" )
		{
			SolidColorBrush scrimBrush = new( Color.FromArgb( 0x92, 0x05, 0x09, 0x14 ) );
			Rectangle MakeScrim()
			{
				Rectangle r = new() { Fill = scrimBrush, IsHitTestVisible = false };
				Canvas.SetZIndex( r, 10 );
				canvas.Children.Add( r );
				cue.OwnedElements.Add( r );
				return r;
			}
			cue.ScrimTop = MakeScrim();
			cue.ScrimBottom = MakeScrim();
			cue.ScrimLeft = MakeScrim();
			cue.ScrimRight = MakeScrim();
		}

		// 2. Outer animated glow / expansion ring
		Border outerRing = new()
		{
			BorderBrush = new SolidColorBrush( accent ),
			BorderThickness = new Thickness( 3 ),
			CornerRadius = new CornerRadius( 10 ),
			Background = new SolidColorBrush( Color.FromArgb( 0x1E, accent.R, accent.G, accent.B ) ),
			IsHitTestVisible = false,
			RenderTransform = new CompositeTransform(),
		};
		Canvas.SetZIndex( outerRing, 20 );
		canvas.Children.Add( outerRing );
		cue.OuterRing = outerRing;
		cue.OwnedElements.Add( outerRing );

		// 3. Crisp inner attention frame
		Border innerFrame = new()
		{
			BorderBrush = new SolidColorBrush( accent ),
			BorderThickness = new Thickness( 3 ),
			CornerRadius = new CornerRadius( 8 ),
			Background = anim == "flash"
				? new SolidColorBrush( Color.FromArgb( 0x30, accent.R, accent.G, accent.B ) )
				: new SolidColorBrush( Color.FromArgb( 0x00, 0, 0, 0 ) ),
			IsHitTestVisible = false,
			RenderTransform = new CompositeTransform(),
		};
		Canvas.SetZIndex( innerFrame, 25 );
		canvas.Children.Add( innerFrame );
		cue.InnerFrame = innerFrame;
		cue.OwnedElements.Add( innerFrame );

		// 4. Optional floating pill badge / bounce arrow directly above the control
		if( anim == "bounce" || !string.IsNullOrWhiteSpace( badgeText ) )
		{
			string pillLabel = anim == "bounce"
				? ( string.IsNullOrWhiteSpace( badgeText ) ? "▼ Look Here" : $"▼ {badgeText}" )
				: badgeText!;
			Border badgePill = new()
			{
				Background = new SolidColorBrush( accent ),
				CornerRadius = new CornerRadius( 12 ),
				Padding = new Thickness( 10, 3, 10, 3 ),
				IsHitTestVisible = false,
				RenderTransform = new CompositeTransform(),
				Child = new TextBlock
				{
					Text = pillLabel,
					FontSize = 11,
					FontWeight = FontWeights.Bold,
					Foreground = new SolidColorBrush( ContrastTextFor( accent ) ),
				},
			};
			Canvas.SetZIndex( badgePill, 30 );
			canvas.Children.Add( badgePill );
			cue.BadgePill = badgePill;
			cue.OwnedElements.Add( badgePill );
			badgePill.Measure( new Size( 260, 40 ) );
		}

		// 5. Optional auto-dismiss when user clicks the target control
		if( dismissOnTargetClick )
		{
			PointerEventHandler handler = ( _, _ ) => RemoveCue( cue.Id );
			cue.Target.AddHandler( UIElement.PointerPressedEvent, handler, handledEventsToo: true );
			cue.TargetClickHandler = handler;
		}
	}

	private static Border BuildPopupCard(
		string cueId,
		string title,
		string message,
		string badge,
		Color accent,
		IReadOnlyList<GuidedButtonSpec> buttons,
		string? stepProgress,
		Action<string, string>? onButtonClicked,
		Action onDismiss,
		bool isTourButton = false )
	{
		bool isDark = AppComposition.Theme.IsDarkActive;
		Color cardBg = isDark ? Color.FromArgb( 0xF5, 0x16, 0x1C, 0x29 ) : Color.FromArgb( 0xF7, 0xFF, 0xFF, 0xFF );
		Color fgPrimary = isDark ? Color.FromArgb( 0xFF, 0xF1, 0xF5, 0xF9 ) : Color.FromArgb( 0xFF, 0x11, 0x18, 0x27 );
		Color fgSecondary = isDark ? Color.FromArgb( 0xFF, 0xB8, 0xC7, 0xDC ) : Color.FromArgb( 0xFF, 0x4B, 0x55, 0x63 );

		StackPanel rootStack = new() { Spacing = 8 };

		// Header bar: severity badge + optional tour progress + dismiss X button
		Grid headerGrid = new() { ColumnSpacing = 8 };
		headerGrid.ColumnDefinitions.Add( new ColumnDefinition { Width = GridLength.Auto } );
		headerGrid.ColumnDefinitions.Add( new ColumnDefinition { Width = new GridLength( 1, GridUnitType.Star ) } );
		headerGrid.ColumnDefinitions.Add( new ColumnDefinition { Width = GridLength.Auto } );

		Border badgeBorder = new()
		{
			Background = new SolidColorBrush( accent ),
			CornerRadius = new CornerRadius( 4 ),
			Padding = new Thickness( 7, 2, 7, 2 ),
			VerticalAlignment = VerticalAlignment.Center,
			Child = new TextBlock
			{
				Text = badge.ToUpperInvariant(),
				FontSize = 10,
				FontWeight = FontWeights.Bold,
				Foreground = new SolidColorBrush( ContrastTextFor( accent ) ),
			},
		};
		Grid.SetColumn( badgeBorder, 0 );
		headerGrid.Children.Add( badgeBorder );

		if( !string.IsNullOrWhiteSpace( stepProgress ) )
		{
			TextBlock progressTb = new()
			{
				Text = stepProgress,
				FontSize = 11,
				VerticalAlignment = VerticalAlignment.Center,
				Foreground = new SolidColorBrush( fgSecondary ),
				TextTrimming = TextTrimming.CharacterEllipsis,
			};
			Grid.SetColumn( progressTb, 1 );
			headerGrid.Children.Add( progressTb );
		}

		Button dismissBtn = new()
		{
			Name = $"McpGuidedDismissButton_{cueId}",
			Content = "✕",
			FontSize = 11,
			Padding = new Thickness( 7, 2, 7, 2 ),
			MinWidth = 0,
			MinHeight = 0,
			VerticalAlignment = VerticalAlignment.Center,
		};
		dismissBtn.Click += ( _, _ ) => onDismiss();
		Grid.SetColumn( dismissBtn, 2 );
		headerGrid.Children.Add( dismissBtn );
		rootStack.Children.Add( headerGrid );

		if( !string.IsNullOrWhiteSpace( title ) )
		{
			rootStack.Children.Add( new TextBlock
			{
				Text = title,
				FontSize = 14,
				FontWeight = FontWeights.SemiBold,
				Foreground = new SolidColorBrush( fgPrimary ),
				TextWrapping = TextWrapping.Wrap,
			} );
		}

		if( !string.IsNullOrWhiteSpace( message ) )
		{
			rootStack.Children.Add( new TextBlock
			{
				Text = message,
				FontSize = 12,
				Foreground = new SolidColorBrush( fgSecondary ),
				TextWrapping = TextWrapping.Wrap,
			} );
		}

		if( buttons.Count > 0 && onButtonClicked is not null )
		{
			StackPanel btnRow = new()
			{
				Orientation = Orientation.Horizontal,
				HorizontalAlignment = HorizontalAlignment.Right,
				Spacing = 6,
				Margin = new Thickness( 0, 4, 0, 0 ),
			};

			string prefix = isTourButton ? "McpGuidedTourButton" : "McpGuidedPopupButton";
			foreach( GuidedButtonSpec b in buttons )
			{
				Button btn = new()
				{
					Name = $"{prefix}_{b.Id}",
					Content = b.Label,
					Padding = new Thickness( 12, 5, 12, 5 ),
				};
				if( b.IsAccent )
				{
					btn.Background = new SolidColorBrush( accent );
					btn.Foreground = new SolidColorBrush( ContrastTextFor( accent ) );
					btn.BorderBrush = new SolidColorBrush( accent );
				}
				string capturedId = b.Id;
				string capturedLabel = b.Label;
				btn.Click += ( _, _ ) => onButtonClicked( capturedId, capturedLabel );
				btnRow.Children.Add( btn );
			}
			rootStack.Children.Add( btnRow );
		}

		Border card = new()
		{
			Name = $"McpGuidedCard_{cueId}",
			Width = 370,
			MaxWidth = 420,
			Padding = new Thickness( 14, 12, 14, 12 ),
			Background = new SolidColorBrush( cardBg ),
			BorderBrush = new SolidColorBrush( accent ),
			BorderThickness = new Thickness( 2 ),
			CornerRadius = new CornerRadius( 10 ),
			Child = rootStack,
			RenderTransform = new CompositeTransform(),
		};

		return card;
	}

	// ------------------------------------------------------------------ Animation Tick & Positioning

	private static void RegisterCue( ActiveCue cue )
	{
		ActiveCues[ cue.Id ] = cue;
		EnsureTimerRunning();
	}

	private static void EnsureTimerRunning()
	{
		if( _timer is null )
		{
			_timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds( 25 ) };
			_timer.Tick += OnAnimationTick;
		}
		if( !_timer.IsEnabled && ActiveCues.Count > 0 )
			_timer.Start();
	}

	private static void OnAnimationTick( object? sender, object e )
	{
		if( ActiveCues.Count == 0 )
		{
			_timer?.Stop();
			return;
		}

		Canvas? canvas = HackApp.HomePage?.GuidedOverlayCanvas;
		if( canvas is null )
		{
			ClearAllInternal( "canvas_unloaded" );
			return;
		}

		DateTimeOffset now = DateTimeOffset.UtcNow;
		List<string>? expired = null;

		foreach( ActiveCue cue in ActiveCues.Values.ToList() )
		{
			double elapsedSec = ( now - cue.StartedAtUtc ).TotalSeconds;
			if( cue.DurationMs > 0 && elapsedSec * 1000.0 >= cue.DurationMs )
			{
				( expired ??= [] ).Add( cue.Id );
				continue;
			}
			PositionCue( cue, canvas, elapsedSec );
		}

		if( expired is not null )
		{
			foreach( string id in expired )
				RemoveCue( id );
		}
	}

	private static void PositionCue( ActiveCue cue, Canvas canvas, double t )
	{
		double canvasW = canvas.ActualWidth > 0 ? canvas.ActualWidth : ( HackApp.HomePage?.ActualWidth ?? 1280 );
		double canvasH = canvas.ActualHeight > 0 ? canvas.ActualHeight : ( HackApp.HomePage?.ActualHeight ?? 800 );

		Rect? targetRect = null;
		if( cue.Target is not null )
		{
			try
			{
				Rect r = GetElementBounds( cue.Target, canvas );
				if( r.Width > 0 && r.Height > 0 )
					targetRect = r;
			}
			catch
			{
				// Control may have been unloaded during navigation
			}
		}

		if( targetRect is Rect rect )
		{
			double pad = 5;
			double fx = Math.Max( 0, rect.X - pad );
			double fy = Math.Max( 0, rect.Y - pad );
			double fw = rect.Width + pad * 2;
			double fh = rect.Height + pad * 2;

			// Spotlight 4-panel scrim cutout
			if( cue.ScrimTop is not null && cue.ScrimBottom is not null && cue.ScrimLeft is not null && cue.ScrimRight is not null )
			{
				double rightX = Math.Min( canvasW, fx + fw );
				double bottomY = Math.Min( canvasH, fy + fh );

				Canvas.SetLeft( cue.ScrimTop, 0 );
				Canvas.SetTop( cue.ScrimTop, 0 );
				cue.ScrimTop.Width = canvasW;
				cue.ScrimTop.Height = Math.Max( 0, fy );

				Canvas.SetLeft( cue.ScrimBottom, 0 );
				Canvas.SetTop( cue.ScrimBottom, bottomY );
				cue.ScrimBottom.Width = canvasW;
				cue.ScrimBottom.Height = Math.Max( 0, canvasH - bottomY );

				Canvas.SetLeft( cue.ScrimLeft, 0 );
				Canvas.SetTop( cue.ScrimLeft, fy );
				cue.ScrimLeft.Width = Math.Max( 0, fx );
				cue.ScrimLeft.Height = Math.Max( 0, bottomY - fy );

				Canvas.SetLeft( cue.ScrimRight, rightX );
				Canvas.SetTop( cue.ScrimRight, fy );
				cue.ScrimRight.Width = Math.Max( 0, canvasW - rightX );
				cue.ScrimRight.Height = Math.Max( 0, bottomY - fy );
			}

			// Compute animated offsets & opacities per style
			double pulse = 0.5 + 0.5 * Math.Sin( t * 5.0 );
			double shakeX = cue.Animation == "shake"
				? Math.Sin( t * 34.0 ) * Math.Exp( -( t % 1.3 ) * 3.8 ) * 9.0
				: 0.0;
			double bounceY = cue.Animation == "bounce"
				? -Math.Abs( Math.Sin( t * 5.5 ) ) * 10.0
				: 0.0;

			if( cue.InnerFrame is { } inner )
			{
				Canvas.SetLeft( inner, fx );
				Canvas.SetTop( inner, fy );
				inner.Width = fw;
				inner.Height = fh;
				if( inner.RenderTransform is CompositeTransform it )
					it.TranslateX = shakeX;
				inner.Opacity = cue.Animation switch
				{
					"flash" => 0.35 + 0.65 * ( 0.5 + 0.5 * Math.Sin( t * 11.0 ) ),
					"outline" => 0.95,
					_ => 0.78 + 0.22 * pulse,
				};
			}

			if( cue.OuterRing is { } outer )
			{
				double expand = cue.Animation switch
				{
					"pulse" => 4.0 + pulse * 10.0,
					"spotlight" => 3.0 + pulse * 6.0,
					"bounce" => 4.0 + pulse * 6.0,
					"flash" => 2.0 + pulse * 5.0,
					_ => 3.0,
				};
				Canvas.SetLeft( outer, Math.Max( 0, fx - expand ) );
				Canvas.SetTop( outer, Math.Max( 0, fy - expand ) );
				outer.Width = fw + expand * 2;
				outer.Height = fh + expand * 2;
				if( outer.RenderTransform is CompositeTransform ot )
					ot.TranslateX = shakeX;
				outer.Opacity = cue.Animation switch
				{
					"outline" => 0.30,
					"flash" => 0.15 + 0.55 * ( 0.5 + 0.5 * Math.Sin( t * 11.0 ) ),
					_ => 0.16 + ( 1.0 - pulse ) * 0.56,
				};
			}

			if( cue.BadgePill is { } pill )
			{
				double pw = Math.Max( pill.ActualWidth, pill.DesiredSize.Width );
				double ph = Math.Max( pill.ActualHeight, pill.DesiredSize.Height );
				if( pw <= 0 ) pw = 96;
				if( ph <= 0 ) ph = 24;
				double px = Math.Clamp( fx + ( fw - pw ) * 0.5, 8, Math.Max( 8, canvasW - pw - 8 ) );
				double py = fy >= ph + 16 ? fy - ph - 8 : fy + fh + 8;
				Canvas.SetLeft( pill, px );
				Canvas.SetTop( pill, py );
				if( pill.RenderTransform is CompositeTransform pt )
				{
					pt.TranslateX = shakeX;
					pt.TranslateY = bounceY;
				}
			}
		}

		// Position popup/callout card cleanly inside viewport
		if( cue.CardElement is { } card )
		{
			double cw = card.ActualWidth > 0 ? card.ActualWidth : ( card.Width > 0 ? card.Width : 370 );
			double ch = card.ActualHeight > 0 ? card.ActualHeight : Math.Max( 120, card.DesiredSize.Height );
			double left;
			double top;

			if( cue.Placement == "anchor" && targetRect is Rect r )
			{
				double gap = 16;
				// Prefer below target, else above, else right, else left
				if( r.Y + r.Height + gap + ch + 12 <= canvasH )
				{
					left = Math.Clamp( r.X, 14, Math.Max( 14, canvasW - cw - 14 ) );
					top = r.Y + r.Height + gap;
				}
				else if( r.Y - gap - ch >= 12 )
				{
					left = Math.Clamp( r.X, 14, Math.Max( 14, canvasW - cw - 14 ) );
					top = r.Y - gap - ch;
				}
				else if( r.X + r.Width + gap + cw + 12 <= canvasW )
				{
					left = r.X + r.Width + gap;
					top = Math.Clamp( r.Y, 14, Math.Max( 14, canvasH - ch - 14 ) );
				}
				else
				{
					left = Math.Clamp( r.X - gap - cw, 14, Math.Max( 14, canvasW - cw - 14 ) );
					top = Math.Clamp( r.Y, 14, Math.Max( 14, canvasH - ch - 14 ) );
				}
			}
			else
			{
				switch( cue.Placement )
				{
					case "center":
						left = Math.Max( 14, ( canvasW - cw ) * 0.5 );
						top = Math.Max( 14, ( canvasH - ch ) * 0.45 );
						break;
					case "bottom-right":
						left = Math.Max( 14, canvasW - cw - 20 );
						top = Math.Max( 14, canvasH - ch - 20 );
						break;
					case "bottom-center":
						left = Math.Max( 14, ( canvasW - cw ) * 0.5 );
						top = Math.Max( 14, canvasH - ch - 20 );
						break;
					default: // "top"
						left = Math.Max( 14, ( canvasW - cw ) * 0.5 );
						top = 20;
						break;
				}
			}

			Canvas.SetLeft( card, left );
			Canvas.SetTop( card, top );
		}
	}

	private static bool RemoveCue( string cueId )
	{
		if( !ActiveCues.Remove( cueId, out ActiveCue? cue ) )
			return false;
		DetachCueVisuals( cue );
		if( ActiveCues.Count == 0 )
			_timer?.Stop();
		return true;
	}

	private static void ClearByKind( string kind )
	{
		foreach( string id in ActiveCues.Values.Where( c => c.Kind == kind ).Select( c => c.Id ).ToList() )
			RemoveCue( id );
	}

	private static void ClearAllInternal( string reason )
	{
		_ = reason;
		foreach( ActiveCue cue in ActiveCues.Values.ToList() )
		{
			cue.PopupWaiter?.TrySetResult( new PopupDecision( null, null, Dismissed: true, TimedOut: false ) );
			DetachCueVisuals( cue );
		}
		ActiveCues.Clear();
		_timer?.Stop();
	}

	private static void DetachCueVisuals( ActiveCue cue )
	{
		if( cue.Target is not null && cue.TargetClickHandler is not null )
		{
			try { cue.Target.RemoveHandler( UIElement.PointerPressedEvent, cue.TargetClickHandler ); } catch { }
		}

		Canvas? canvas = _boundCanvas ?? HackApp.HomePage?.GuidedOverlayCanvas;
		if( canvas is not null )
		{
			foreach( UIElement el in cue.OwnedElements )
				canvas.Children.Remove( el );
		}
		cue.OwnedElements.Clear();
	}

	// ------------------------------------------------------------------ Target & Style Helpers

	private static async Task NavigateIfRequestedAsync( string? page, string? settingsSection, CancellationToken ct )
	{
		if( string.IsNullOrWhiteSpace( page ) && string.IsNullOrWhiteSpace( settingsSection ) )
			return;

		HomeView home = HackApp.HomePage ?? throw new InvalidOperationException( "HomeView is not initialized." );
		if( !string.IsNullOrWhiteSpace( page ) )
		{
			if( !Enum.TryParse<NavigatePageMenu>( page, ignoreCase: true, out NavigatePageMenu target ) )
				throw new ArgumentException( $"Unknown page '{page}'. Valid: {string.Join( ", ", Enum.GetNames<NavigatePageMenu>() )}" );
			AppComposition.Navigation.NavigateTo( target );
			await Task.Delay( 100, ct );
		}

		if( !string.IsNullOrWhiteSpace( settingsSection ) )
		{
			if( home.ContentFrame.Content is not ApplicationSettingsView )
			{
				AppComposition.Navigation.NavigateTo( NavigatePageMenu.Settings );
				for( int i = 0; i < 40 && home.ContentFrame.Content is not ApplicationSettingsView; i++ )
					await Task.Delay( 50, ct );
			}
			( home.ContentFrame.Content as ApplicationSettingsView )?.SelectSection( settingsSection );
			await Task.Delay( 100, ct );
		}
	}

	private static FrameworkElement ResolveTargetElement( string? name, string? automationId, string? text, string? controlType )
	{
		UIElement root = GetSearchRoot();
		var located = VisualTreeInspector.ResolveControl( root, name, automationId, text, controlType )
			?? throw new InvalidOperationException(
				$"Could not find target UI control matching name='{name}', automationId='{automationId}', text='{text}', controlType='{controlType}'." );

		return located.Element as FrameworkElement
			?? throw new InvalidOperationException( $"Matched control '{located.Element.GetType().Name}' is not a FrameworkElement." );
	}

	private static Rect GetElementBounds( FrameworkElement element, UIElement relativeTo )
	{
		Point pt = element.TransformToVisual( relativeTo ).TransformPoint( new Point( 0, 0 ) );
		return new Rect( pt.X, pt.Y, element.ActualWidth, element.ActualHeight );
	}

	private static string NormalizeAnimation( string? anim ) => ( anim?.Trim().ToLowerInvariant() ) switch
	{
		"spotlight" or "focus" or "dim" => "spotlight",
		"bounce" or "arrow" or "point" => "bounce",
		"shake" or "wiggle" or "nudge" => "shake",
		"flash" or "blink" => "flash",
		"outline" or "static" or "steady" => "outline",
		_ => "pulse",
	};

	private static Color ResolveAccentColor( string? severity, string? hexOverride )
	{
		if( ThemeColorParse.TryParseHex( hexOverride, out uint argb ) )
			return Color.FromArgb( (byte)( argb >> 24 ), (byte)( argb >> 16 ), (byte)( argb >> 8 ), (byte)argb );

		return ( severity?.Trim().ToLowerInvariant() ) switch
		{
			"warning" or "caution" => Color.FromArgb( 0xFF, 0xF5, 0x9E, 0x0B ),
			"critical" or "error" or "danger" => Color.FromArgb( 0xFF, 0xEF, 0x44, 0x44 ),
			"success" or "done" => Color.FromArgb( 0xFF, 0x10, 0xB9, 0x81 ),
			"tip" or "hint" => Color.FromArgb( 0xFF, 0x06, 0xB6, 0xD4 ),
			"action" => Color.FromArgb( 0xFF, 0x38, 0xBD, 0xF8 ),
			_ => Color.FromArgb( 0xFF, 0x60, 0xA5, 0xFA ),
		};
	}

	private static string DefaultBadgeForSeverity( string? severity ) => ( severity?.Trim().ToLowerInvariant() ) switch
	{
		"warning" or "caution" => "ATTENTION",
		"critical" or "error" or "danger" => "ACTION REQUIRED",
		"success" => "COMPLETE",
		"tip" or "hint" => "AI TIP",
		"action" => "GUIDED ACTION",
		_ => "AI GUIDE",
	};

	private static Color ContrastTextFor( Color bg )
	{
		uint packed = ( (uint)bg.A << 24 ) | ( (uint)bg.R << 16 ) | ( (uint)bg.G << 8 ) | bg.B;
		double withBlack = ThemePresets.Contrast( 0xFF000000, packed );
		double withWhite = ThemePresets.Contrast( 0xFFFFFFFF, packed );
		return withBlack >= withWhite
			? Color.FromArgb( 0xFF, 0x08, 0x0D, 0x16 )
			: Color.FromArgb( 0xFF, 0xFF, 0xFF, 0xFF );
	}

	private sealed record PopupDecision( string? ButtonId, string? ButtonLabel, bool Dismissed, bool TimedOut );

	private sealed class ActiveCue(
		string id,
		string kind,
		string animation,
		FrameworkElement? target,
		string? title,
		string? message,
		int durationMs )
	{
		public string Id { get; } = id;
		public string Kind { get; } = kind;
		public string Animation { get; } = animation;
		public FrameworkElement? Target { get; } = target;
		public string? Title { get; } = title;
		public string? Message { get; } = message;
		public int DurationMs { get; } = durationMs;
		public DateTimeOffset StartedAtUtc { get; } = DateTimeOffset.UtcNow;
		public string Placement { get; set; } = "anchor";

		public Rectangle? ScrimTop { get; set; }
		public Rectangle? ScrimBottom { get; set; }
		public Rectangle? ScrimLeft { get; set; }
		public Rectangle? ScrimRight { get; set; }
		public Border? OuterRing { get; set; }
		public Border? InnerFrame { get; set; }
		public Border? BadgePill { get; set; }
		public Border? CardElement { get; set; }
		public PointerEventHandler? TargetClickHandler { get; set; }
		public TaskCompletionSource<PopupDecision>? PopupWaiter { get; set; }
		public List<UIElement> OwnedElements { get; } = [];
	}

	private sealed class ActiveTour( string id, string title, List<GuidedTourStep> steps )
	{
		public string Id { get; } = id;
		public string Title { get; } = title;
		public List<GuidedTourStep> Steps { get; } = steps;
		public int CurrentIndex { get; set; }
		public bool IsActive { get; set; } = true;
		public bool Completed { get; set; }
	}
}

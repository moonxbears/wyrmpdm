using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

using HackPDM.Core.Theming;
using HackPDM.Mcp.Ai;
using HackPDM.Mcp.Database;
using HackPDM.Mcp.DynamicUi;
using HackPDM.Mcp.Protocol;
using HackPDM.Mcp.Security;
using HackPDM.Mvvm.ViewModels.Common;
using HackPDM.Mvvm.Views.Hack;
using HackPDM.Mvvm.Views.Settings;
using HackPDM.Shared.GlobalData;

using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Windows.UI;

namespace HackPDM.Shell.Mcp;

/// <summary>
/// Spotlight-style GUI Command Palette &amp; AI Assistant overlay hosted inside <see cref="HomeView"/>
/// (toggled via <c>Ctrl+K</c>, <c>Ctrl+Shift+P</c>, or the sidebar icon).
/// Provides:
/// <list type="bullet">
/// <item><b>Commands</b>: fuzzy search &amp; instant execution of HackPDM navigation, vault/PDM API,
/// SolidWorks, theme, restore-point, and permission-mode commands with inline parameter prompts.</item>
/// <item><b>MCP Tools</b>: live search &amp; direct invocation of all 115 registered MCP tools.</item>
/// <item><b>Ask AI</b>: direct multi-turn chat with the user's connected AI provider running live
/// in-process MCP tool calls against HackPDM (including dynamic windows and guided cues).</item>
/// <item><b>Connect AI</b>: one-click browser PKCE OAuth login (OpenRouter), one-click local AI &amp;
/// env-key auto-detection (Ollama, LM Studio, OpenAI, Claude, Gemini, GitHub), and one-click
/// auto-configuration for external AI clients (Claude Desktop, Claude Code, Cursor, VS Code,
/// Windsurf, Gemini CLI) without manually editing JSON files.</item>
/// </list>
/// </summary>
internal static class CommandPaletteManager
{
	private static Grid? _boundHost;
	private static bool _isOpen;
	private static string _activeTab = "commands"; // commands | tools | ai | connect
	private static string _searchText = string.Empty;
	private static int _selectedCommandIndex;
	private static PaletteCommandItem? _pendingParamCommand;
	private static string? _selectedToolName;
	private static string _selectedToolArgsJson = "{}";
	private static string? _lastCommandTitle;
	private static string? _lastCommandResultJson;
	private static bool _lastCommandSucceeded = true;
	private static string _statusText = "Ready • Ctrl+K to toggle • Esc to close";

	// Ask AI state
	private static bool _isAiBusy;
	private static readonly List<AiToolExecutionRecord> _inFlightToolCalls = [];
	private static string _draftAiPrompt = string.Empty;

	// Connect AI state
	private static AiProviderKind _selectedProviderKind = AiProviderKind.OpenRouter;
	private static string _connectFeedback = string.Empty;
	private static bool _connectFeedbackIsError;
	private static bool _isAuthBusy;
	private static IReadOnlyList<DetectedAiProvider> _detectedProviders = [];
	private static List<string> _fetchedModels = [];
	private static CancellationTokenSource? _oauthCts;

	private static bool _themeSubscribed;

	public static bool IsOpen => _isOpen;

	public static void Toggle( string? initialTab = null, string? initialQuery = null )
	{
		if( _isOpen && string.IsNullOrWhiteSpace( initialTab ) && string.IsNullOrWhiteSpace( initialQuery ) )
		{
			Close();
			return;
		}
		Open( initialTab, initialQuery );
	}

	public static void Open( string? initialTab = null, string? initialQuery = null )
	{
		HomeView? home = HackApp.HomePage;
		if( home is null )
			return;

		Grid host = home.CommandPaletteHost;
		_boundHost = host;
		EnsureThemeSubscription();

		if( !string.IsNullOrWhiteSpace( initialTab ) )
			_activeTab = NormalizeTab( initialTab );
		if( initialQuery is not null )
			_searchText = initialQuery;

		_pendingParamCommand = null;
		_selectedCommandIndex = 0;

		if( McpBridgeHost.AiProviders.ActiveProvider is { } activeProv )
			_selectedProviderKind = activeProv;

		_isOpen = true;
		host.Visibility = Visibility.Visible;
		Render();
	}

	public static void Close()
	{
		_isOpen = false;
		_pendingParamCommand = null;
		CancelPendingOAuth();
		if( _boundHost is not null )
		{
			_boundHost.Visibility = Visibility.Collapsed;
			_boundHost.Children.Clear();
		}
	}

	private static void EnsureThemeSubscription()
	{
		if( _themeSubscribed )
			return;
		_themeSubscribed = true;
		AppComposition.Theme.ThemeChanged += () =>
		{
			UiDispatcher.Post( () =>
			{
				if( _isOpen )
					Render();
			} );
		};
		McpBridgeHost.AiProviders.Changed += () =>
		{
			UiDispatcher.Post( () =>
			{
				if( _isOpen )
					Render();
			} );
		};
	}

	private static void CancelPendingOAuth()
	{
		try { _oauthCts?.Cancel(); } catch { }
		_oauthCts?.Dispose();
		_oauthCts = null;
		_isAuthBusy = false;
	}

	// ------------------------------------------------------------------ Root Modal Rendering

	private static void Render()
	{
		if( !_isOpen || _boundHost is null )
			return;

		PaletteColors pal = ResolveColors();
		_boundHost.Children.Clear();

		// Full-screen translucent scrim; clicking backdrop dismisses palette
		Border scrim = new()
		{
			Background = Brush( Color.FromArgb( pal.IsDark ? (byte)0xA4 : (byte)0x66, 0x06, 0x0A, 0x12 ) ),
			HorizontalAlignment = HorizontalAlignment.Stretch,
			VerticalAlignment = VerticalAlignment.Stretch,
		};
		scrim.PointerPressed += ( _, _ ) => Close();
		_boundHost.Children.Add( scrim );

		// Centered modal card
		Border modal = new()
		{
			Name = "CommandPaletteModalCard",
			Background = Brush( pal.WindowBg ),
			BorderBrush = Brush( pal.ModalBorder ),
			BorderThickness = new Thickness( 1 ),
			CornerRadius = new CornerRadius( 14 ),
			MaxWidth = 880,
			MaxHeight = 670,
			Margin = new Thickness( 28, 30, 28, 30 ),
			HorizontalAlignment = HorizontalAlignment.Stretch,
			VerticalAlignment = VerticalAlignment.Top,
		};
		// Absorb pointer taps inside modal so scrim does not close it
		modal.PointerPressed += ( _, e ) => e.Handled = true;

		Grid layout = new();
		layout.RowDefinitions.Add( new RowDefinition { Height = GridLength.Auto } ); // accent strip
		layout.RowDefinitions.Add( new RowDefinition { Height = GridLength.Auto } ); // spotlight bar + mode tabs
		layout.RowDefinitions.Add( new RowDefinition { Height = new GridLength( 1, GridUnitType.Star ) } ); // body
		layout.RowDefinitions.Add( new RowDefinition { Height = GridLength.Auto } ); // footer

		// Row 0: top accent strip
		Border topStripe = new()
		{
			Height = 3,
			Background = Brush( pal.Accent ),
			CornerRadius = new CornerRadius( 14, 14, 0, 0 ),
		};
		Grid.SetRow( topStripe, 0 );
		layout.Children.Add( topStripe );

		// Row 1: Header + Spotlight Search + Mode Tabs
		Border headerContainer = new()
		{
			Background = Brush( pal.HeaderBg ),
			BorderBrush = Brush( pal.Border ),
			BorderThickness = new Thickness( 0, 0, 0, 1 ),
			Padding = new Thickness( 16, 12, 16, 10 ),
		};
		StackPanel headerStack = new() { Spacing = 10 };
		headerStack.Children.Add( BuildSpotlightHeaderRow( pal ) );
		headerStack.Children.Add( BuildModeTabsRow( pal ) );
		headerContainer.Child = headerStack;
		Grid.SetRow( headerContainer, 1 );
		layout.Children.Add( headerContainer );

		// Row 2: Active Tab Content
		UIElement body = _activeTab switch
		{
			"tools" => BuildMcpToolsTab( pal ),
			"ai" => BuildAskAiTab( pal ),
			"connect" => BuildConnectAiTab( pal ),
			_ => BuildCommandsTab( pal ),
		};
		Border bodyWrap = new() { Padding = new Thickness( 16, 12, 16, 12 ), Child = body };
		Grid.SetRow( bodyWrap, 2 );
		layout.Children.Add( bodyWrap );

		// Row 3: Footer hints + live status
		Border footer = new()
		{
			Background = Brush( pal.HeaderBg ),
			BorderBrush = Brush( pal.Border ),
			BorderThickness = new Thickness( 0, 1, 0, 0 ),
			CornerRadius = new CornerRadius( 0, 0, 14, 14 ),
			Padding = new Thickness( 16, 7, 16, 7 ),
		};
		Grid footerGrid = new() { ColumnSpacing = 12 };
		footerGrid.ColumnDefinitions.Add( new ColumnDefinition { Width = new GridLength( 1, GridUnitType.Star ) } );
		footerGrid.ColumnDefinitions.Add( new ColumnDefinition { Width = GridLength.Auto } );

		TextBlock statusBlock = new()
		{
			Text = _statusText,
			FontSize = 11.5,
			Foreground = Brush( pal.MutedText ),
			VerticalAlignment = VerticalAlignment.Center,
			TextTrimming = TextTrimming.CharacterEllipsis,
		};
		Grid.SetColumn( statusBlock, 0 );
		footerGrid.Children.Add( statusBlock );

		TextBlock hintsBlock = new()
		{
			Text = "↑↓ Navigate  •  ↵ Run  •  / MCP Tools  •  ? Ask AI  •  Esc Close",
			FontSize = 11,
			Foreground = Brush( pal.MutedText ),
			VerticalAlignment = VerticalAlignment.Center,
		};
		Grid.SetColumn( hintsBlock, 1 );
		footerGrid.Children.Add( hintsBlock );
		footer.Child = footerGrid;
		Grid.SetRow( footer, 3 );
		layout.Children.Add( footer );

		modal.Child = layout;
		_boundHost.Children.Add( modal );
	}

	private static UIElement BuildSpotlightHeaderRow( PaletteColors pal )
	{
		Grid row = new() { ColumnSpacing = 10 };
		row.ColumnDefinitions.Add( new ColumnDefinition { Width = GridLength.Auto } );
		row.ColumnDefinitions.Add( new ColumnDefinition { Width = new GridLength( 1, GridUnitType.Star ) } );
		row.ColumnDefinitions.Add( new ColumnDefinition { Width = GridLength.Auto } );
		row.ColumnDefinitions.Add( new ColumnDefinition { Width = GridLength.Auto } );

		Border iconPill = new()
		{
			Background = Brush( WithAlpha( pal.Accent, 0x28 ) ),
			BorderBrush = Brush( WithAlpha( pal.Accent, 0x88 ) ),
			BorderThickness = new Thickness( 1 ),
			CornerRadius = new CornerRadius( 7 ),
			Padding = new Thickness( 9, 5, 9, 5 ),
			VerticalAlignment = VerticalAlignment.Center,
			Child = new TextBlock
			{
				Text = ">_",
				FontFamily = new FontFamily( "Cascadia Mono, Consolas, DejaVu Sans Mono, monospace" ),
				FontSize = 13,
				FontWeight = FontWeights.Bold,
				Foreground = Brush( pal.Accent ),
			},
		};
		Grid.SetColumn( iconPill, 0 );
		row.Children.Add( iconPill );

		int totalTools = McpBridgeHost.GetOrCreateEmbeddedMcpServer().GetToolDefinitions().Count;
		string placeholder = _activeTab switch
		{
			"tools" => $"Filter all {totalTools} MCP tools by name, category, or description...",
			"ai" => "Type a prompt below, or type > for commands / / for MCP tools...",
			"connect" => "Sign in to your AI provider, connect external AI apps, or configure Odoo PostgreSQL...",
			_ => "Search HackPDM commands (>), MCP tools (/), or ask AI (?)...",
		};

		TextBox searchBox = new()
		{
			Name = "CommandPaletteSearchBox",
			Text = _searchText,
			PlaceholderText = placeholder,
			FontSize = 14,
			VerticalAlignment = VerticalAlignment.Center,
			IsEnabled = _activeTab is "commands" or "tools",
		};
		searchBox.TextChanged += ( _, _ ) =>
		{
			string next = searchBox.Text ?? string.Empty;
			if( string.Equals( next, _searchText, StringComparison.Ordinal ) )
				return;

			// Prefix auto-routing for fast keyboard power users
			if( _activeTab == "commands" && next.StartsWith( "/" ) )
			{
				_activeTab = "tools";
				_searchText = next[ 1.. ];
				Render();
				return;
			}
			if( _activeTab == "commands" && ( next.StartsWith( "?" ) || next.StartsWith( "@" ) ) )
			{
				_activeTab = "ai";
				_draftAiPrompt = next[ 1.. ].TrimStart();
				_searchText = string.Empty;
				Render();
				return;
			}

			_searchText = next;
			_selectedCommandIndex = 0;
			Render();
		};
		searchBox.KeyDown += ( _, e ) =>
		{
			if( e.Key == VirtualKey.Escape )
			{
				e.Handled = true;
				Close();
			}
			else if( _activeTab == "commands" && _pendingParamCommand is null )
			{
				IReadOnlyList<PaletteCommandItem> items = CommandPaletteCatalog.SearchCommands( _searchText );
				if( e.Key == VirtualKey.Down && items.Count > 0 )
				{
					e.Handled = true;
					_selectedCommandIndex = ( _selectedCommandIndex + 1 ) % items.Count;
					Render();
				}
				else if( e.Key == VirtualKey.Up && items.Count > 0 )
				{
					e.Handled = true;
					_selectedCommandIndex = ( _selectedCommandIndex - 1 + items.Count ) % items.Count;
					Render();
				}
				else if( e.Key == VirtualKey.Enter && items.Count > 0 )
				{
					e.Handled = true;
					int idx = Math.Clamp( _selectedCommandIndex, 0, items.Count - 1 );
					_ = ExecuteCuratedCommandAsync( items[ idx ] );
				}
			}
		};
		Grid.SetColumn( searchBox, 1 );
		row.Children.Add( searchBox );

		// Active AI Connection Status button
		AiProviderProfile? activeAi = McpBridgeHost.AiProviders.GetActiveProfile();
		string aiPillLabel = activeAi is not null
			? $"● {AiProviderCatalog.Get( activeAi.Kind ).ShortTag}: {ShortModel( activeAi.Model )}"
			: "Connect AI →";
		Color aiPillColor = activeAi is not null ? Color.FromArgb( 0xFF, 0x10, 0xB9, 0x81 ) : pal.Accent;

		Button aiStatusBtn = new()
		{
			Name = "McpCommandPaletteButton_AiStatus",
			Content = aiPillLabel,
			FontSize = 11.5,
			FontWeight = FontWeights.SemiBold,
			Padding = new Thickness( 10, 5, 10, 5 ),
			CornerRadius = new CornerRadius( 12 ),
			Background = Brush( WithAlpha( aiPillColor, 0x26 ) ),
			Foreground = Brush( aiPillColor ),
			BorderBrush = Brush( WithAlpha( aiPillColor, 0x88 ) ),
			BorderThickness = new Thickness( 1 ),
			VerticalAlignment = VerticalAlignment.Center,
		};
		aiStatusBtn.Click += ( _, _ ) =>
		{
			_activeTab = activeAi is not null ? "ai" : "connect";
			Render();
		};
		Grid.SetColumn( aiStatusBtn, 2 );
		row.Children.Add( aiStatusBtn );

		Button closeBtn = MakeButton( "Esc", pal, accent: false );
		closeBtn.Name = "McpCommandPaletteButton_Close";
		closeBtn.Padding = new Thickness( 10, 5, 10, 5 );
		closeBtn.Click += ( _, _ ) => Close();
		Grid.SetColumn( closeBtn, 3 );
		row.Children.Add( closeBtn );

		return row;
	}

	private static UIElement BuildModeTabsRow( PaletteColors pal )
	{
		Grid row = new() { ColumnSpacing = 10 };
		row.ColumnDefinitions.Add( new ColumnDefinition { Width = new GridLength( 1, GridUnitType.Star ) } );
		row.ColumnDefinitions.Add( new ColumnDefinition { Width = GridLength.Auto } );

		int toolCount = McpBridgeHost.GetOrCreateEmbeddedMcpServer().GetToolDefinitions().Count;
		Border stripFrame = new()
		{
			Background = Brush( pal.SubtleBg ),
			BorderBrush = Brush( pal.Border ),
			BorderThickness = new Thickness( 1 ),
			CornerRadius = new CornerRadius( 8 ),
			Padding = new Thickness( 3 ),
			HorizontalAlignment = HorizontalAlignment.Left,
		};

		StackPanel tabs = new() { Orientation = Orientation.Horizontal, Spacing = 4 };
		(string Id, string Title, string? Badge)[] defs =
		[
			( "commands", "Commands", null ),
			( "tools", "MCP Tools", toolCount.ToString() ),
			( "ai", "Ask AI", McpBridgeHost.AiChat.History.Count > 0 ? McpBridgeHost.AiChat.History.Count.ToString() : null ),
			( "connect", "Connect AI", McpBridgeHost.AiProviders.GetActiveProfile() is not null ? "ON" : "SETUP" ),
		];

		Color activeInk = ContrastTextFor( pal.Accent );
		foreach( var (id, title, badge) in defs )
		{
			bool isActive = string.Equals( _activeTab, id, StringComparison.OrdinalIgnoreCase );
			StackPanel inner = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
			inner.Children.Add( new TextBlock
			{
				Text = title,
				FontSize = 12,
				FontWeight = isActive ? FontWeights.SemiBold : FontWeights.Normal,
				Foreground = Brush( isActive ? activeInk : pal.PrimaryText ),
				VerticalAlignment = VerticalAlignment.Center,
			} );
			if( !string.IsNullOrWhiteSpace( badge ) )
			{
				inner.Children.Add( new Border
				{
					Background = Brush( WithAlpha( isActive ? activeInk : pal.PrimaryText, 0x22 ) ),
					CornerRadius = new CornerRadius( 8 ),
					Padding = new Thickness( 6, 1, 6, 1 ),
					Child = new TextBlock
					{
						Text = badge,
						FontSize = 10,
						FontWeight = FontWeights.SemiBold,
						Foreground = Brush( isActive ? activeInk : pal.PrimaryText ),
					},
				} );
			}

			string targetTab = id;
			Button tabBtn = new()
			{
				Name = $"CommandPaletteTab_{id}",
				Content = inner,
				CornerRadius = new CornerRadius( 6 ),
				BorderThickness = new Thickness( 0 ),
				Padding = new Thickness( 11, 5, 11, 5 ),
				Background = Brush( isActive ? pal.Accent : Colors.Transparent ),
			};
			tabBtn.Click += ( _, _ ) =>
			{
				_activeTab = targetTab;
				_pendingParamCommand = null;
				Render();
			};
			tabs.Children.Add( tabBtn );
		}
		stripFrame.Child = tabs;
		Grid.SetColumn( stripFrame, 0 );
		row.Children.Add( stripFrame );

		// Current MCP Permission Mode pill
		string mode = McpBridgeHost.Permissions.ActiveProfile.Mode;
		Border modePill = MakePill( $"Mode: {mode}", ColorForMode( mode, pal ), pal );
		Grid.SetColumn( modePill, 1 );
		row.Children.Add( modePill );

		return row;
	}

	// ------------------------------------------------------------------ Tab 1: Curated Commands

	private static UIElement BuildCommandsTab( PaletteColors pal )
	{
		if( _pendingParamCommand is { } paramCmd )
			return BuildParameterPromptPanel( paramCmd, pal );

		Grid grid = new() { RowSpacing = 10 };
		grid.RowDefinitions.Add( new RowDefinition { Height = new GridLength( 1, GridUnitType.Star ) } );
		grid.RowDefinitions.Add( new RowDefinition { Height = GridLength.Auto } );

		IReadOnlyList<PaletteCommandItem> matches = CommandPaletteCatalog.SearchCommands( _searchText );
		StackPanel listStack = new() { Spacing = 4 };

		if( matches.Count == 0 )
		{
			listStack.Children.Add( new TextBlock
			{
				Text = $"No commands match '{_searchText}'. Press '/' to search all 115 MCP tools or '?' to ask the AI.",
				FontSize = 13,
				Foreground = Brush( pal.MutedText ),
				Padding = new Thickness( 12, 18, 12, 18 ),
			} );
		}
		else
		{
			string? lastCat = null;
			for( int i = 0; i < matches.Count; i++ )
			{
				PaletteCommandItem item = matches[ i ];
				if( string.IsNullOrWhiteSpace( _searchText ) && !string.Equals( lastCat, item.Category, StringComparison.Ordinal ) )
				{
					lastCat = item.Category;
					listStack.Children.Add( new TextBlock
					{
						Text = item.Category.ToUpperInvariant(),
						FontSize = 10.5,
						FontWeight = FontWeights.Bold,
						Foreground = Brush( pal.MutedText ),
						CharacterSpacing = 50,
						Margin = new Thickness( 6, i == 0 ? 0 : 8, 6, 2 ),
					} );
				}

				bool isSelected = i == Math.Clamp( _selectedCommandIndex, 0, matches.Count - 1 );
				Color badgeColor = ResolveSeverityColor( item.Severity, pal );

				Border rowBorder = new()
				{
					Background = Brush( isSelected ? WithAlpha( pal.Accent, pal.IsDark ? (byte)0x26 : (byte)0x1E ) : pal.CardBg ),
					BorderBrush = Brush( isSelected ? WithAlpha( pal.Accent, 0x99 ) : pal.Border ),
					BorderThickness = new Thickness( 1 ),
					CornerRadius = new CornerRadius( 8 ),
					Padding = new Thickness( 12, 8, 12, 8 ),
				};

				Grid rowGrid = new() { ColumnSpacing = 10 };
				rowGrid.ColumnDefinitions.Add( new ColumnDefinition { Width = GridLength.Auto } );
				rowGrid.ColumnDefinitions.Add( new ColumnDefinition { Width = new GridLength( 1, GridUnitType.Star ) } );
				rowGrid.ColumnDefinitions.Add( new ColumnDefinition { Width = GridLength.Auto } );

				Border badge = MakePill( item.Badge, badgeColor, pal );
				Grid.SetColumn( badge, 0 );
				rowGrid.Children.Add( badge );

				StackPanel textStack = new() { Spacing = 1 };
				textStack.Children.Add( new TextBlock
				{
					Text = item.Title,
					FontSize = 13,
					FontWeight = FontWeights.SemiBold,
					Foreground = Brush( pal.PrimaryText ),
				} );
				textStack.Children.Add( new TextBlock
				{
					Text = item.Subtitle,
					FontSize = 11.5,
					Foreground = Brush( pal.MutedText ),
					TextTrimming = TextTrimming.CharacterEllipsis,
				} );
				Grid.SetColumn( textStack, 1 );
				rowGrid.Children.Add( textStack );

				if( !string.IsNullOrWhiteSpace( item.ShortcutHint ) )
				{
					Border kbd = new()
					{
						Background = Brush( pal.SubtleBg ),
						BorderBrush = Brush( pal.Border ),
						BorderThickness = new Thickness( 1 ),
						CornerRadius = new CornerRadius( 5 ),
						Padding = new Thickness( 7, 2, 7, 2 ),
						VerticalAlignment = VerticalAlignment.Center,
						Child = new TextBlock
						{
							Text = item.ShortcutHint,
							FontFamily = new FontFamily( "Cascadia Mono, Consolas, DejaVu Sans Mono, monospace" ),
							FontSize = 11,
							Foreground = Brush( pal.MutedText ),
						},
					};
					Grid.SetColumn( kbd, 2 );
					rowGrid.Children.Add( kbd );
				}

				rowBorder.Child = rowGrid;
				int capturedIndex = i;
				rowBorder.PointerPressed += ( _, e ) =>
				{
					e.Handled = true;
					_selectedCommandIndex = capturedIndex;
					_ = ExecuteCuratedCommandAsync( item );
				};
				listStack.Children.Add( rowBorder );
			}
		}

		ScrollViewer scroll = new()
		{
			Content = listStack,
			MaxHeight = _lastCommandResultJson is not null ? 290 : 470,
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
			HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
		};
		Grid.SetRow( scroll, 0 );
		grid.Children.Add( scroll );

		if( !string.IsNullOrWhiteSpace( _lastCommandResultJson ) )
		{
			UIElement resultCard = BuildLastResultPanel( pal );
			Grid.SetRow( resultCard, 1 );
			grid.Children.Add( resultCard );
		}

		return grid;
	}

	private static UIElement BuildParameterPromptPanel( PaletteCommandItem cmd, PaletteColors pal )
	{
		Border card = new()
		{
			Background = Brush( pal.CardBg ),
			BorderBrush = Brush( pal.Accent ),
			BorderThickness = new Thickness( 1 ),
			CornerRadius = new CornerRadius( 10 ),
			Padding = new Thickness( 16, 14, 16, 14 ),
		};

		StackPanel stack = new() { Spacing = 12 };
		stack.Children.Add( new TextBlock
		{
			Text = cmd.Title,
			FontSize = 15,
			FontWeight = FontWeights.SemiBold,
			Foreground = Brush( pal.PrimaryText ),
		} );
		stack.Children.Add( new TextBlock
		{
			Text = cmd.Subtitle,
			FontSize = 12,
			Foreground = Brush( pal.MutedText ),
			TextWrapping = TextWrapping.Wrap,
		} );

		Dictionary<string, TextBox> boxes = [];
		foreach( PaletteParamPrompt p in cmd.Prompts ?? [] )
		{
			StackPanel field = new() { Spacing = 4 };
			field.Children.Add( new TextBlock
			{
				Text = p.Required ? $"{p.Label} *" : p.Label,
				FontSize = 12,
				FontWeight = FontWeights.SemiBold,
				Foreground = Brush( pal.PrimaryText ),
			} );
			TextBox tb = new()
			{
				Text = p.DefaultValue ?? string.Empty,
				PlaceholderText = p.Placeholder,
			};
			boxes[ p.Key ] = tb;
			field.Children.Add( tb );
			stack.Children.Add( field );
		}

		StackPanel actions = new() { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
		Button backBtn = MakeButton( "Cancel", pal, accent: false );
		backBtn.Click += ( _, _ ) =>
		{
			_pendingParamCommand = null;
			Render();
		};
		Button runBtn = MakeButton( $"Run {cmd.Target}", pal, accent: true );
		runBtn.Click += async ( _, _ ) =>
		{
			Dictionary<string, object?> argsMap = [];
			foreach( PaletteParamPrompt p in cmd.Prompts ?? [] )
			{
				string val = boxes[ p.Key ].Text.Trim();
				if( p.Required && string.IsNullOrWhiteSpace( val ) )
				{
					_statusText = $"Please enter '{p.Label}'.";
					Render();
					return;
				}
				if( string.IsNullOrWhiteSpace( val ) )
					continue;
				if( p.IsJsonArrayOfStrings )
					argsMap[ p.Key ] = val.Split( ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries );
				else
					argsMap[ p.Key ] = val;
			}
			string argsJson = JsonSerializer.Serialize( argsMap );
			_pendingParamCommand = null;
			await RunMcpToolCommandAsync( cmd.Title, cmd.Target!, argsJson );
		};
		actions.Children.Add( backBtn );
		actions.Children.Add( runBtn );
		stack.Children.Add( actions );

		card.Child = stack;
		return card;
	}

	private static UIElement BuildLastResultPanel( PaletteColors pal )
	{
		Color accent = _lastCommandSucceeded ? Color.FromArgb( 0xFF, 0x10, 0xB9, 0x81 ) : Color.FromArgb( 0xFF, 0xEF, 0x44, 0x44 );
		Border frame = new()
		{
			Background = Brush( pal.CodeBg ),
			BorderBrush = Brush( WithAlpha( accent, 0x99 ) ),
			BorderThickness = new Thickness( 3, 1, 1, 1 ),
			CornerRadius = new CornerRadius( 8 ),
		};

		StackPanel stack = new() { Spacing = 0 };
		Grid hdr = new() { Padding = new Thickness( 10, 6, 10, 6 ), ColumnSpacing = 8 };
		hdr.ColumnDefinitions.Add( new ColumnDefinition { Width = new GridLength( 1, GridUnitType.Star ) } );
		hdr.ColumnDefinitions.Add( new ColumnDefinition { Width = GridLength.Auto } );

		StackPanel titleRow = new() { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
		titleRow.Children.Add( MakePill( _lastCommandSucceeded ? "OK" : "ERROR", accent, pal ) );
		titleRow.Children.Add( new TextBlock
		{
			Text = _lastCommandTitle ?? "Result",
			FontSize = 12,
			FontWeight = FontWeights.SemiBold,
			Foreground = Brush( pal.PrimaryText ),
			VerticalAlignment = VerticalAlignment.Center,
		} );
		Grid.SetColumn( titleRow, 0 );
		hdr.Children.Add( titleRow );

		StackPanel btns = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
		Button copyBtn = MakeSmallButton( "Copy", pal );
		copyBtn.Click += ( _, _ ) =>
		{
			if( TryCopy( _lastCommandResultJson ?? "" ) )
			{
				_statusText = "Copied result JSON to clipboard";
				Render();
			}
		};
		Button popBtn = MakeSmallButton( "Open in Window", pal );
		popBtn.Click += async ( _, _ ) =>
		{
			await PopResultIntoDynamicWindowAsync( _lastCommandTitle ?? "HackPDM Command Result", _lastCommandResultJson ?? "{}" );
			Close();
		};
		Button clearBtn = MakeSmallButton( "×", pal );
		clearBtn.Click += ( _, _ ) =>
		{
			_lastCommandResultJson = null;
			Render();
		};
		btns.Children.Add( copyBtn );
		btns.Children.Add( popBtn );
		btns.Children.Add( clearBtn );
		Grid.SetColumn( btns, 1 );
		hdr.Children.Add( btns );
		stack.Children.Add( hdr );

		ScrollViewer scroller = new()
		{
			MaxHeight = 140,
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
			HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
			Content = new TextBlock
			{
				Text = Truncate( _lastCommandResultJson ?? "", 6000 ),
				FontFamily = new FontFamily( "Cascadia Mono, Consolas, DejaVu Sans Mono, monospace" ),
				FontSize = 11.5,
				Foreground = Brush( pal.CodeText ),
				IsTextSelectionEnabled = true,
				Padding = new Thickness( 12, 6, 12, 10 ),
			},
		};
		stack.Children.Add( scroller );
		frame.Child = stack;
		return frame;
	}

	// ------------------------------------------------------------------ Tab 2: All 115 MCP Tools Runner

	private static UIElement BuildMcpToolsTab( PaletteColors pal )
	{
		McpServer server = McpBridgeHost.GetOrCreateEmbeddedMcpServer();
		IReadOnlyList<(McpToolDefinition Tool, McpToolClassification Class)> tools =
			CommandPaletteCatalog.SearchMcpTools( server.GetToolDefinitions(), _searchText );

		if( string.IsNullOrWhiteSpace( _selectedToolName ) && tools.Count > 0 )
		{
			_selectedToolName = tools[ 0 ].Tool.Name;
			_selectedToolArgsJson = BuildSchemaSkeleton( tools[ 0 ].Tool.InputSchema );
		}

		Grid split = new() { ColumnSpacing = 12 };
		split.ColumnDefinitions.Add( new ColumnDefinition { Width = new GridLength( 1.0, GridUnitType.Star ) } );
		split.ColumnDefinitions.Add( new ColumnDefinition { Width = new GridLength( 1.25, GridUnitType.Star ) } );

		// Left: Tool List
		StackPanel leftList = new() { Spacing = 4 };
		foreach( var (tool, cls) in tools.Take( 120 ) )
		{
			bool isSelected = string.Equals( tool.Name, _selectedToolName, StringComparison.Ordinal );
			Color riskColor = cls.RiskLevel switch
			{
				McpToolRiskLevel.CriticalAction => Color.FromArgb( 0xFF, 0xEF, 0x44, 0x44 ),
				McpToolRiskLevel.StandardAction => Color.FromArgb( 0xFF, 0xF5, 0x9E, 0x0B ),
				_ => Color.FromArgb( 0xFF, 0x10, 0xB9, 0x81 ),
			};

			Border row = new()
			{
				Background = Brush( isSelected ? WithAlpha( pal.Accent, 0x26 ) : pal.CardBg ),
				BorderBrush = Brush( isSelected ? pal.Accent : pal.Border ),
				BorderThickness = new Thickness( 1 ),
				CornerRadius = new CornerRadius( 7 ),
				Padding = new Thickness( 10, 7, 10, 7 ),
			};
			Grid rg = new() { ColumnSpacing = 6 };
			rg.ColumnDefinitions.Add( new ColumnDefinition { Width = new GridLength( 1, GridUnitType.Star ) } );
			rg.ColumnDefinitions.Add( new ColumnDefinition { Width = GridLength.Auto } );

			StackPanel txt = new() { Spacing = 1 };
			txt.Children.Add( new TextBlock
			{
				Text = tool.Name,
				FontFamily = new FontFamily( "Cascadia Mono, Consolas, DejaVu Sans Mono, monospace" ),
				FontSize = 12,
				FontWeight = FontWeights.SemiBold,
				Foreground = Brush( pal.PrimaryText ),
			} );
			txt.Children.Add( new TextBlock
			{
				Text = cls.Description,
				FontSize = 11,
				Foreground = Brush( pal.MutedText ),
				TextTrimming = TextTrimming.CharacterEllipsis,
			} );
			Grid.SetColumn( txt, 0 );
			rg.Children.Add( txt );

			string tag = cls.IsRemoteRpc ? "RPC" : ( cls.RiskLevel == McpToolRiskLevel.ReadOnly ? "READ" : ( cls.RiskLevel == McpToolRiskLevel.CriticalAction ? "CRIT" : "ACT" ) );
			Border pill = MakePill( tag, riskColor, pal );
			Grid.SetColumn( pill, 1 );
			rg.Children.Add( pill );

			row.Child = rg;
			McpToolDefinition captured = tool;
			row.PointerPressed += ( _, e ) =>
			{
				e.Handled = true;
				_selectedToolName = captured.Name;
				_selectedToolArgsJson = BuildSchemaSkeleton( captured.InputSchema );
				Render();
			};
			leftList.Children.Add( row );
		}

		ScrollViewer leftScroll = new()
		{
			Content = leftList,
			MaxHeight = 470,
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
			HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
		};
		Grid.SetColumn( leftScroll, 0 );
		split.Children.Add( leftScroll );

		// Right: Selected Tool Runner
		var selectedPair = tools.FirstOrDefault( x => string.Equals( x.Tool.Name, _selectedToolName, StringComparison.Ordinal ) );
		Border rightCard = new()
		{
			Background = Brush( pal.CardBg ),
			BorderBrush = Brush( pal.Border ),
			BorderThickness = new Thickness( 1 ),
			CornerRadius = new CornerRadius( 10 ),
			Padding = new Thickness( 14, 12, 14, 12 ),
		};

		if( selectedPair.Tool is null )
		{
			rightCard.Child = new TextBlock { Text = "Select an MCP tool on the left to inspect and invoke it.", Foreground = Brush( pal.MutedText ) };
		}
		else
		{
			McpToolDefinition t = selectedPair.Tool;
			McpToolClassification cls = selectedPair.Class;
			StackPanel rs = new() { Spacing = 10 };

			StackPanel headerLine = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
			headerLine.Children.Add( new TextBlock
			{
				Text = t.Name,
				FontFamily = new FontFamily( "Cascadia Mono, Consolas, DejaVu Sans Mono, monospace" ),
				FontSize = 14,
				FontWeight = FontWeights.Bold,
				Foreground = Brush( pal.PrimaryText ),
				VerticalAlignment = VerticalAlignment.Center,
			} );
			headerLine.Children.Add( MakePill( cls.Category.ToString(), pal.Accent, pal ) );
			if( cls.IsRemoteRpc )
				headerLine.Children.Add( MakePill( "REMOTE RPC", Color.FromArgb( 0xFF, 0xF5, 0x9E, 0x0B ), pal ) );
			rs.Children.Add( headerLine );

			rs.Children.Add( new TextBlock
			{
				Text = t.Description,
				FontSize = 12,
				Foreground = Brush( pal.MutedText ),
				TextWrapping = TextWrapping.Wrap,
			} );

			rs.Children.Add( new TextBlock
			{
				Text = "JSON Arguments",
				FontSize = 12,
				FontWeight = FontWeights.SemiBold,
				Foreground = Brush( pal.PrimaryText ),
			} );

			TextBox argsBox = new()
			{
				AcceptsReturn = true,
				TextWrapping = TextWrapping.Wrap,
				FontFamily = new FontFamily( "Cascadia Mono, Consolas, DejaVu Sans Mono, monospace" ),
				FontSize = 12,
				MinHeight = 110,
				MaxHeight = 160,
			};
			ScrollViewer.SetVerticalScrollBarVisibility( argsBox, ScrollBarVisibility.Auto );
			argsBox.Text = _selectedToolArgsJson;
			argsBox.TextChanged += ( _, _ ) => _selectedToolArgsJson = argsBox.Text ?? "{}";
			rs.Children.Add( argsBox );

			StackPanel actionBtns = new() { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
			Button resetArgsBtn = MakeButton( "Reset JSON", pal, accent: false );
			resetArgsBtn.Click += ( _, _ ) =>
			{
				_selectedToolArgsJson = BuildSchemaSkeleton( t.InputSchema );
				Render();
			};
			Button invokeBtn = MakeButton( $"Run {t.Name}", pal, accent: true );
			invokeBtn.Name = "McpCommandPaletteButton_RunTool";
			invokeBtn.Click += async ( _, _ ) => await RunMcpToolCommandAsync( t.Name, t.Name, _selectedToolArgsJson );
			actionBtns.Children.Add( resetArgsBtn );
			actionBtns.Children.Add( invokeBtn );
			rs.Children.Add( actionBtns );

			if( !string.IsNullOrWhiteSpace( _lastCommandResultJson ) )
				rs.Children.Add( BuildLastResultPanel( pal ) );

			rightCard.Child = new ScrollViewer
			{
				Content = rs,
				MaxHeight = 450,
				VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
				HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
			};
		}

		Grid.SetColumn( rightCard, 1 );
		split.Children.Add( rightCard );
		return split;
	}

	// ------------------------------------------------------------------ Tab 3: Direct AI Chat (MCP Tool-Calling Agent)

	private static UIElement BuildAskAiTab( PaletteColors pal )
	{
		Grid root = new() { RowSpacing = 10 };
		root.RowDefinitions.Add( new RowDefinition { Height = GridLength.Auto } );
		root.RowDefinitions.Add( new RowDefinition { Height = new GridLength( 1, GridUnitType.Star ) } );
		root.RowDefinitions.Add( new RowDefinition { Height = GridLength.Auto } );

		AiProviderProfile? active = McpBridgeHost.AiProviders.GetActiveProfile();

		// Top connection bar
		Border bar = new()
		{
			Background = Brush( active is not null ? pal.CardBg : WithAlpha( pal.Accent, 0x1E ) ),
			BorderBrush = Brush( active is not null ? pal.Border : pal.Accent ),
			BorderThickness = new Thickness( 1 ),
			CornerRadius = new CornerRadius( 8 ),
			Padding = new Thickness( 12, 8, 12, 8 ),
		};
		Grid bg = new() { ColumnSpacing = 10 };
		bg.ColumnDefinitions.Add( new ColumnDefinition { Width = new GridLength( 1, GridUnitType.Star ) } );
		bg.ColumnDefinitions.Add( new ColumnDefinition { Width = GridLength.Auto } );

		if( active is not null )
		{
			AiProviderDescriptor desc = AiProviderCatalog.Get( active.Kind );
			StackPanel info = new() { Spacing = 2 };
			info.Children.Add( new TextBlock
			{
				Text = $"Connected to {desc.DisplayName}  •  Model: {active.Model ?? desc.DefaultModel}",
				FontSize = 12.5,
				FontWeight = FontWeights.SemiBold,
				Foreground = Brush( pal.PrimaryText ),
			} );
			info.Children.Add( new TextBlock
			{
				Text = $"Direct MCP access enabled under '{McpBridgeHost.Permissions.ActiveProfile.Mode}' mode — AI can query HackPDM, guide you, customize themes, and spawn supplemental windows.",
				FontSize = 11,
				Foreground = Brush( pal.MutedText ),
				TextWrapping = TextWrapping.Wrap,
			} );
			Grid.SetColumn( info, 0 );
			bg.Children.Add( info );
		}
		else
		{
			StackPanel info = new() { Spacing = 2 };
			info.Children.Add( new TextBlock
			{
				Text = "No AI Provider Signed In Yet",
				FontSize = 13,
				FontWeight = FontWeights.SemiBold,
				Foreground = Brush( pal.PrimaryText ),
			} );
			info.Children.Add( new TextBlock
			{
				Text = "Sign in with your browser in 1 click (OpenRouter OAuth), auto-detect Ollama / LM Studio, or connect OpenAI, Claude, or Gemini.",
				FontSize = 11.5,
				Foreground = Brush( pal.MutedText ),
				TextWrapping = TextWrapping.Wrap,
			} );
			Grid.SetColumn( info, 0 );
			bg.Children.Add( info );
		}

		StackPanel barBtns = new() { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
		Button switchBtn = MakeSmallButton( active is not null ? "Switch / Login" : "Sign In / Connect AI →", pal, accent: active is null );
		switchBtn.Click += ( _, _ ) =>
		{
			_activeTab = "connect";
			Render();
		};
		barBtns.Children.Add( switchBtn );

		if( McpBridgeHost.AiChat.History.Count > 0 )
		{
			Button popChatBtn = MakeSmallButton( "Pop Out", pal );
			popChatBtn.Click += async ( _, _ ) =>
			{
				await PopConversationIntoDynamicWindowAsync();
				Close();
			};
			Button clearChatBtn = MakeSmallButton( "Clear", pal );
			clearChatBtn.Click += ( _, _ ) =>
			{
				McpBridgeHost.AiChat.ClearHistory();
				Render();
			};
			barBtns.Children.Add( popChatBtn );
			barBtns.Children.Add( clearChatBtn );
		}

		Grid.SetColumn( barBtns, 1 );
		bg.Children.Add( barBtns );
		bar.Child = bg;
		Grid.SetRow( bar, 0 );
		root.Children.Add( bar );

		// Middle: Conversation feed or Starter prompt chips
		StackPanel feed = new() { Spacing = 10 };
		if( McpBridgeHost.AiChat.History.Count == 0 && !_isAiBusy )
		{
			feed.Children.Add( new TextBlock
			{
				Text = "Try asking HackPDM AI:",
				FontSize = 12,
				FontWeight = FontWeights.SemiBold,
				Foreground = Brush( pal.MutedText ),
				Margin = new Thickness( 2, 4, 0, 0 ),
			} );

			string[] starters =
			[
				"Summarize HackPDM status, permissions, and diagnostics in a supplemental window",
				"List files in my local PWA folder and show them in a searchable table window",
				"Guide me visually to the Theme Editor and highlight the Palette selector",
				"Show my recent MCP activity log and restore points",
			];
			foreach( string s in starters )
			{
				Border chip = new()
				{
					Background = Brush( pal.CardBg ),
					BorderBrush = Brush( pal.Border ),
					BorderThickness = new Thickness( 1 ),
					CornerRadius = new CornerRadius( 8 ),
					Padding = new Thickness( 12, 8, 12, 8 ),
					Child = new TextBlock
					{
						Text = $"✦  {s}",
						FontSize = 12.5,
						Foreground = Brush( pal.PrimaryText ),
						TextWrapping = TextWrapping.Wrap,
					},
				};
				string captured = s;
				chip.PointerPressed += ( _, e ) =>
				{
					e.Handled = true;
					_draftAiPrompt = captured;
					if( active is not null )
						_ = SendAiMessageAsync( captured );
					else
						Render();
				};
				feed.Children.Add( chip );
			}
		}
		else
		{
			foreach( AiChatTurn turn in McpBridgeHost.AiChat.History )
			{
				bool isUser = turn.Role == "user";
				Border bubble = new()
				{
					Background = Brush( isUser ? WithAlpha( pal.Accent, pal.IsDark ? (byte)0x26 : (byte)0x1C ) : pal.CardBg ),
					BorderBrush = Brush( isUser ? WithAlpha( pal.Accent, 0x80 ) : pal.Border ),
					BorderThickness = new Thickness( 1 ),
					CornerRadius = new CornerRadius( 10 ),
					Padding = new Thickness( 12, 9, 12, 10 ),
					Margin = isUser ? new Thickness( 64, 0, 0, 0 ) : new Thickness( 0, 0, 28, 0 ),
				};

				StackPanel bs = new() { Spacing = 6 };
				StackPanel hdrLine = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
				hdrLine.Children.Add( new TextBlock
				{
					Text = isUser ? "You" : $"HackPDM AI ({turn.ProviderSummary ?? "MCP"})",
					FontSize = 11,
					FontWeight = FontWeights.Bold,
					Foreground = Brush( isUser ? pal.Accent : pal.MutedText ),
				} );
				bs.Children.Add( hdrLine );

				if( turn.ToolCalls.Count > 0 )
				{
					StackPanel toolPills = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
					foreach( AiToolExecutionRecord tc in turn.ToolCalls )
					{
						Color tcColor = tc.WasBlockedByPermissions
							? Color.FromArgb( 0xFF, 0xEF, 0x44, 0x44 )
							: ( tc.Succeeded ? Color.FromArgb( 0xFF, 0x10, 0xB9, 0x81 ) : Color.FromArgb( 0xFF, 0xF5, 0x9E, 0x0B ) );
						string sym = tc.WasBlockedByPermissions ? "⛔" : ( tc.Succeeded ? "✓" : "!" );
						toolPills.Children.Add( MakePill( $"{sym} {tc.ToolName} ({tc.DurationMs}ms)", tcColor, pal ) );
					}
					bs.Children.Add( new ScrollViewer
					{
						Content = toolPills,
						HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
						VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
					} );
				}

				bs.Children.Add( new TextBlock
				{
					Text = turn.Text,
					FontSize = 13,
					Foreground = Brush( pal.PrimaryText ),
					TextWrapping = TextWrapping.Wrap,
					IsTextSelectionEnabled = true,
				} );
				bubble.Child = bs;
				feed.Children.Add( bubble );
			}

			if( _isAiBusy )
			{
				Border busy = new()
				{
					Background = Brush( pal.SubtleBg ),
					BorderBrush = Brush( pal.Accent ),
					BorderThickness = new Thickness( 1 ),
					CornerRadius = new CornerRadius( 8 ),
					Padding = new Thickness( 12, 9, 12, 9 ),
				};
				StackPanel bs = new() { Spacing = 6 };
				bs.Children.Add( new TextBlock
				{
					Text = _inFlightToolCalls.Count == 0
						? "Thinking & deciding which HackPDM MCP tools to run..."
						: $"Running HackPDM MCP tools ({_inFlightToolCalls.Count} executed)...",
					FontSize = 12,
					FontWeight = FontWeights.SemiBold,
					Foreground = Brush( pal.Accent ),
				} );
				bs.Children.Add( new ProgressBar { IsIndeterminate = true, Foreground = Brush( pal.Accent ) } );
				busy.Child = bs;
				feed.Children.Add( busy );
			}
		}

		ScrollViewer feedScroll = new()
		{
			Content = feed,
			MaxHeight = 335,
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
			HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
		};
		Grid.SetRow( feedScroll, 1 );
		root.Children.Add( feedScroll );

		// Bottom composer
		Grid composer = new() { ColumnSpacing = 8 };
		composer.ColumnDefinitions.Add( new ColumnDefinition { Width = new GridLength( 1, GridUnitType.Star ) } );
		composer.ColumnDefinitions.Add( new ColumnDefinition { Width = GridLength.Auto } );

		TextBox promptBox = new()
		{
			Name = "CommandPaletteAiPromptBox",
			Text = _draftAiPrompt,
			PlaceholderText = active is not null
				? "Ask HackPDM AI to inspect files, run API commands, guide you, or build a supplemental window..."
				: "Connect an AI provider first (click 'Sign In / Connect AI' above)...",
			IsEnabled = !_isAiBusy,
		};
		promptBox.TextChanged += ( _, _ ) => _draftAiPrompt = promptBox.Text ?? string.Empty;
		promptBox.KeyDown += ( _, e ) =>
		{
			if( e.Key == VirtualKey.Enter && !_isAiBusy && !string.IsNullOrWhiteSpace( promptBox.Text ) )
			{
				e.Handled = true;
				if( active is null )
				{
					_activeTab = "connect";
					Render();
				}
				else
				{
					_ = SendAiMessageAsync( promptBox.Text );
				}
			}
			else if( e.Key == VirtualKey.Escape )
			{
				e.Handled = true;
				Close();
			}
		};
		Grid.SetColumn( promptBox, 0 );
		composer.Children.Add( promptBox );

		Button sendBtn = MakeButton( _isAiBusy ? "Running..." : "Send to AI", pal, accent: true );
		sendBtn.Name = "McpCommandPaletteButton_SendAi";
		sendBtn.IsEnabled = !_isAiBusy;
		sendBtn.Click += ( _, _ ) =>
		{
			if( active is null )
			{
				_activeTab = "connect";
				Render();
			}
			else if( !string.IsNullOrWhiteSpace( _draftAiPrompt ) )
			{
				_ = SendAiMessageAsync( _draftAiPrompt );
			}
		};
		Grid.SetColumn( sendBtn, 1 );
		composer.Children.Add( sendBtn );

		Grid.SetRow( composer, 2 );
		root.Children.Add( composer );
		return root;
	}

	// ------------------------------------------------------------------ Tab 4: One-Click AI Login & Client Auto-Connect

	private static UIElement BuildConnectAiTab( PaletteColors pal )
	{
		StackPanel content = new() { Spacing = 14 };

		// Feedback banner if present
		if( !string.IsNullOrWhiteSpace( _connectFeedback ) )
		{
			Color fbColor = _connectFeedbackIsError
				? Color.FromArgb( 0xFF, 0xEF, 0x44, 0x44 )
				: Color.FromArgb( 0xFF, 0x10, 0xB9, 0x81 );
			content.Children.Add( new Border
			{
				Background = Brush( WithAlpha( fbColor, 0x22 ) ),
				BorderBrush = Brush( fbColor ),
				BorderThickness = new Thickness( 3, 1, 1, 1 ),
				CornerRadius = new CornerRadius( 8 ),
				Padding = new Thickness( 12, 8, 12, 8 ),
				Child = new TextBlock
				{
					Text = _connectFeedback,
					FontSize = 12,
					Foreground = Brush( pal.PrimaryText ),
					TextWrapping = TextWrapping.Wrap,
				},
			} );
		}

		// ---- Section 1: In-App AI Provider Sign-In ----
		Border loginCard = new()
		{
			Background = Brush( pal.CardBg ),
			BorderBrush = Brush( pal.Border ),
			BorderThickness = new Thickness( 1 ),
			CornerRadius = new CornerRadius( 10 ),
			Padding = new Thickness( 14, 12, 14, 12 ),
		};
		StackPanel ls = new() { Spacing = 10 };

		Grid titleBar = new() { ColumnSpacing = 8 };
		titleBar.ColumnDefinitions.Add( new ColumnDefinition { Width = new GridLength( 1, GridUnitType.Star ) } );
		titleBar.ColumnDefinitions.Add( new ColumnDefinition { Width = GridLength.Auto } );

		StackPanel lTitle = new() { Spacing = 2 };
		lTitle.Children.Add( new TextBlock
		{
			Text = "1. Sign In to Your AI Provider (In-App Command Palette AI)",
			FontSize = 14,
			FontWeight = FontWeights.SemiBold,
			Foreground = Brush( pal.PrimaryText ),
		} );
		lTitle.Children.Add( new TextBlock
		{
			Text = "Sign in with your browser via OpenRouter OAuth (Claude, GPT-4o, Gemini, DeepSeek), auto-detect local Ollama/LM Studio, or connect directly with your account key.",
			FontSize = 11.5,
			Foreground = Brush( pal.MutedText ),
			TextWrapping = TextWrapping.Wrap,
		} );
		Grid.SetColumn( lTitle, 0 );
		titleBar.Children.Add( lTitle );

		StackPanel quickBtns = new() { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
		Button oauthBtn = MakeButton( _isAuthBusy ? "Waiting for Browser..." : "Browser Login (OAuth)", pal, accent: true );
		oauthBtn.Name = "McpAiConnect_BrowserOAuthButton";
		oauthBtn.IsEnabled = !_isAuthBusy;
		oauthBtn.Click += async ( _, _ ) => await RunOpenRouterBrowserOAuthAsync();

		Button detectBtn = MakeButton( "Auto-Detect AI", pal, accent: false );
		detectBtn.Name = "McpAiConnect_AutoDetectButton";
		detectBtn.IsEnabled = !_isAuthBusy;
		detectBtn.Click += async ( _, _ ) => await RunAutoDetectAsync();

		quickBtns.Children.Add( oauthBtn );
		quickBtns.Children.Add( detectBtn );
		Grid.SetColumn( quickBtns, 1 );
		titleBar.Children.Add( quickBtns );
		ls.Children.Add( titleBar );

		// Detected providers quick-connect strip (if Auto-Detect found local Ollama/LM Studio or env keys!)
		if( _detectedProviders.Count > 0 )
		{
			Border detBox = new()
			{
				Background = Brush( WithAlpha( Color.FromArgb( 0xFF, 0x10, 0xB9, 0x81 ), 0x18 ) ),
				BorderBrush = Brush( WithAlpha( Color.FromArgb( 0xFF, 0x10, 0xB9, 0x81 ), 0x88 ) ),
				BorderThickness = new Thickness( 1 ),
				CornerRadius = new CornerRadius( 8 ),
				Padding = new Thickness( 10, 8, 10, 8 ),
			};
			StackPanel ds = new() { Spacing = 6 };
			ds.Children.Add( new TextBlock
			{
				Text = $"Detected {_detectedProviders.Count} Ready AI Provider(s) on This Computer:",
				FontSize = 11.5,
				FontWeight = FontWeights.SemiBold,
				Foreground = Brush( Color.FromArgb( 0xFF, 0x10, 0xB9, 0x81 ) ),
			} );
			foreach( DetectedAiProvider det in _detectedProviders )
			{
				Grid dg = new() { ColumnSpacing = 8 };
				dg.ColumnDefinitions.Add( new ColumnDefinition { Width = new GridLength( 1, GridUnitType.Star ) } );
				dg.ColumnDefinitions.Add( new ColumnDefinition { Width = GridLength.Auto } );
				dg.Children.Add( new TextBlock
				{
					Text = $"{det.DisplayName} — {det.Source}" + ( det.SuggestedModel is { Length: > 0 } m ? $" (model: {m})" : "" ),
					FontSize = 12,
					Foreground = Brush( pal.PrimaryText ),
					VerticalAlignment = VerticalAlignment.Center,
				} );
				Button useBtn = MakeSmallButton( "Connect 1-Click", pal, accent: true );
				useBtn.Name = $"McpAiConnect_UseDetected_{det.Kind}";
				DetectedAiProvider captured = det;
				useBtn.Click += ( _, _ ) =>
				{
					McpBridgeHost.AiProviders.SaveAndActivate( new AiProviderProfile
					{
						Kind = captured.Kind,
						ApiKey = captured.DetectedApiKey,
						BaseUrl = captured.BaseUrl,
						Model = captured.SuggestedModel,
						AccountLabel = captured.DisplayName,
						AuthMethod = captured.DetectedApiKey is null ? "local_auto" : "env_auto",
					} );
					_selectedProviderKind = captured.Kind;
					_connectFeedbackIsError = false;
					_connectFeedback = $"Connected to {captured.DisplayName} ({captured.SuggestedModel})! Switch to 'Ask AI' to start chatting.";
					_statusText = $"Signed in: {captured.DisplayName}";
					Render();
				};
				Grid.SetColumn( useBtn, 1 );
				dg.Children.Add( useBtn );
				ds.Children.Add( dg );
			}
			detBox.Child = ds;
			ls.Children.Add( detBox );
		}

		// Provider selection pill row
		StackPanel provPills = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
		AiProviderKind? activeKind = McpBridgeHost.AiProviders.ActiveProvider;
		foreach( AiProviderDescriptor d in AiProviderCatalog.All )
		{
			bool isSel = d.Kind == _selectedProviderKind;
			bool isSignedIn = d.Kind == activeKind && McpBridgeHost.AiProviders.GetActiveProfile() is not null;
			Button pb = new()
			{
				Name = $"McpAiConnect_Provider_{d.Id}",
				Content = isSignedIn ? $"● {d.ShortTag}" : d.ShortTag,
				FontSize = 11.5,
				FontWeight = isSel ? FontWeights.SemiBold : FontWeights.Normal,
				Padding = new Thickness( 10, 4, 10, 4 ),
				CornerRadius = new CornerRadius( 6 ),
				Background = Brush( isSel ? pal.Accent : pal.SubtleBg ),
				Foreground = Brush( isSel ? ContrastTextFor( pal.Accent ) : pal.PrimaryText ),
				BorderBrush = Brush( isSel ? pal.Accent : pal.Border ),
				BorderThickness = new Thickness( 1 ),
			};
			AiProviderKind capturedKind = d.Kind;
			pb.Click += ( _, _ ) =>
			{
				_selectedProviderKind = capturedKind;
				_fetchedModels.Clear();
				Render();
			};
			provPills.Children.Add( pb );
		}
		ls.Children.Add( new ScrollViewer
		{
			Content = provPills,
			HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
			VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
		} );

		// Selected Provider Credentials Form
		AiProviderDescriptor selDesc = AiProviderCatalog.Get( _selectedProviderKind );
		AiProviderProfile selProfile = McpBridgeHost.AiProviders.GetOrCreateProfile( _selectedProviderKind );

		Border formBox = new()
		{
			Background = Brush( pal.SubtleBg ),
			BorderBrush = Brush( pal.Border ),
			BorderThickness = new Thickness( 1 ),
			CornerRadius = new CornerRadius( 8 ),
			Padding = new Thickness( 12, 10, 12, 10 ),
		};
		StackPanel fs = new() { Spacing = 8 };
		fs.Children.Add( new TextBlock
		{
			Text = $"{selDesc.DisplayName} — {selDesc.Description}",
			FontSize = 12,
			Foreground = Brush( pal.MutedText ),
			TextWrapping = TextWrapping.Wrap,
		} );

		Grid formGrid = new() { ColumnSpacing = 10, RowSpacing = 8 };
		formGrid.ColumnDefinitions.Add( new ColumnDefinition { Width = new GridLength( 1.15, GridUnitType.Star ) } );
		formGrid.ColumnDefinitions.Add( new ColumnDefinition { Width = new GridLength( 1, GridUnitType.Star ) } );
		formGrid.RowDefinitions.Add( new RowDefinition { Height = GridLength.Auto } );
		formGrid.RowDefinitions.Add( new RowDefinition { Height = GridLength.Auto } );

		PasswordBox? keyBox = null;
		TextBox? urlBox = null;

		if( selDesc.RequiresApiKey || selDesc.Kind == AiProviderKind.CustomOpenAi )
		{
			StackPanel keyCol = new() { Spacing = 3 };
			keyCol.Children.Add( new TextBlock
			{
				Text = selProfile.HasCredentials && !string.IsNullOrWhiteSpace( selProfile.ApiKey )
					? $"API Key / Token (Saved: {selProfile.MaskedApiKey})"
					: ( selDesc.RequiresApiKey ? "API Key / Account Token" : "API Key (Optional)" ),
				FontSize = 11.5,
				FontWeight = FontWeights.SemiBold,
				Foreground = Brush( pal.PrimaryText ),
			} );
			keyBox = new PasswordBox
			{
				Name = "McpAiConnect_ApiKeyBox",
				PlaceholderText = selProfile.HasCredentials ? "Leave blank to keep saved key, or paste new key..." : "Paste API key or click browser sign-in...",
			};
			keyCol.Children.Add( keyBox );
			Grid.SetRow( keyCol, 0 );
			Grid.SetColumn( keyCol, 0 );
			formGrid.Children.Add( keyCol );
		}
		else
		{
			StackPanel localCol = new() { Spacing = 3 };
			localCol.Children.Add( new TextBlock
			{
				Text = "Local Server URL (No Account Needed)",
				FontSize = 11.5,
				FontWeight = FontWeights.SemiBold,
				Foreground = Brush( pal.PrimaryText ),
			} );
			urlBox = new TextBox
			{
				Name = "McpAiConnect_BaseUrlBox",
				Text = selProfile.BaseUrl ?? selDesc.DefaultBaseUrl,
			};
			localCol.Children.Add( urlBox );
			Grid.SetRow( localCol, 0 );
			Grid.SetColumn( localCol, 0 );
			formGrid.Children.Add( localCol );
		}

		// Model selector (editable TextBox + suggested chips)
		StackPanel modelCol = new() { Spacing = 3 };
		modelCol.Children.Add( new TextBlock
		{
			Text = "Model ID",
			FontSize = 11.5,
			FontWeight = FontWeights.SemiBold,
			Foreground = Brush( pal.PrimaryText ),
		} );
		TextBox modelBox = new()
		{
			Name = "McpAiConnect_ModelBox",
			Text = selProfile.Model ?? selDesc.DefaultModel,
			PlaceholderText = selDesc.DefaultModel,
		};
		modelCol.Children.Add( modelBox );
		Grid.SetRow( modelCol, 0 );
		Grid.SetColumn( modelCol, 1 );
		formGrid.Children.Add( modelCol );

		if( selDesc.Kind == AiProviderKind.CustomOpenAi )
		{
			StackPanel customUrlCol = new() { Spacing = 3 };
			customUrlCol.Children.Add( new TextBlock
			{
				Text = "OpenAI-Compatible Base URL",
				FontSize = 11.5,
				FontWeight = FontWeights.SemiBold,
				Foreground = Brush( pal.PrimaryText ),
			} );
			urlBox = new TextBox
			{
				Name = "McpAiConnect_BaseUrlBox",
				Text = selProfile.BaseUrl ?? selDesc.DefaultBaseUrl,
				PlaceholderText = "https://your-gateway/v1",
			};
			customUrlCol.Children.Add( urlBox );
			Grid.SetRow( customUrlCol, 1 );
			Grid.SetColumn( customUrlCol, 0 );
			Grid.SetColumnSpan( customUrlCol, 2 );
			formGrid.Children.Add( customUrlCol );
		}

		fs.Children.Add( formGrid );

		// Suggested / live-fetched model quick-pick chips
		List<string> modelChoices = ( _fetchedModels.Count > 0 ? _fetchedModels : selDesc.SuggestedModels ).Take( 6 ).ToList();
		if( modelChoices.Count > 0 )
		{
			StackPanel chips = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
			foreach( string m in modelChoices )
			{
				Button mc = MakeSmallButton( m, pal );
				string capturedModel = m;
				mc.Click += ( _, _ ) => modelBox.Text = capturedModel;
				chips.Children.Add( mc );
			}
			fs.Children.Add( new ScrollViewer
			{
				Content = chips,
				HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
				VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
			} );
		}

		// Provider form buttons
		StackPanel formBtns = new() { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
		if( !string.IsNullOrWhiteSpace( selDesc.SignInUrl ) )
		{
			Button openPageBtn = MakeButton( selDesc.SignInButtonLabel ?? "Open Provider Sign-In", pal, accent: false );
			openPageBtn.Name = "McpAiConnect_OpenSignInUrlButton";
			openPageBtn.Click += async ( _, _ ) =>
			{
				if( selDesc.SupportsBrowserOAuth )
				{
					await RunOpenRouterBrowserOAuthAsync();
				}
				else
				{
					AiOAuthLoginFlow.TryOpenBrowser( selDesc.SignInUrl!, out string? err );
					_connectFeedbackIsError = err is not null;
					_connectFeedback = err is null
						? $"Opened {selDesc.DisplayName} sign-in page in your browser. Paste your key above and click 'Test & Sign In'."
						: $"Could not launch browser: {err}";
					Render();
				}
			};
			formBtns.Children.Add( openPageBtn );
		}

		if( selProfile.HasCredentials && McpBridgeHost.AiProviders.ListConfiguredProfiles().Any( p => p.Kind == _selectedProviderKind ) )
		{
			Button signOutBtn = MakeButton( "Sign Out", pal, accent: false );
			signOutBtn.Name = "McpAiConnect_SignOutButton";
			signOutBtn.Click += ( _, _ ) =>
			{
				McpBridgeHost.AiProviders.SignOut( _selectedProviderKind );
				_connectFeedbackIsError = false;
				_connectFeedback = $"Signed out of {selDesc.DisplayName}.";
				Render();
			};
			formBtns.Children.Add( signOutBtn );
		}

		Button testSaveBtn = MakeButton( _isAuthBusy ? "Connecting..." : "Test & Sign In", pal, accent: true );
		testSaveBtn.Name = "McpAiConnect_TestAndSignInButton";
		testSaveBtn.IsEnabled = !_isAuthBusy;
		testSaveBtn.Click += async ( _, _ ) =>
		{
			string? enteredKey = keyBox is not null && !string.IsNullOrWhiteSpace( keyBox.Password )
				? keyBox.Password.Trim()
				: selProfile.ApiKey;
			AiProviderProfile candidate = new()
			{
				Kind = _selectedProviderKind,
				ApiKey = enteredKey,
				BaseUrl = urlBox is not null ? urlBox.Text.Trim() : ( selProfile.BaseUrl ?? selDesc.DefaultBaseUrl ),
				Model = string.IsNullOrWhiteSpace( modelBox.Text ) ? selDesc.DefaultModel : modelBox.Text.Trim(),
			};

			_isAuthBusy = true;
			_connectFeedbackIsError = false;
			_connectFeedback = $"Testing connection to {selDesc.DisplayName}...";
			Render();

			AiConnectionTestResult res = await McpBridgeHost.AiChat.TestConnectionAndFetchModelsAsync( candidate );
			_isAuthBusy = false;
			if( res.Success )
			{
				_fetchedModels = [ .. res.Models ];
				if( string.IsNullOrWhiteSpace( candidate.Model ) && res.Models.Count > 0 )
					candidate.Model = res.Models[ 0 ];
				McpBridgeHost.AiProviders.SaveAndActivate( candidate );
				_connectFeedbackIsError = false;
				_connectFeedback = $"{res.Message} Saved & activated — click 'Ask AI' to chat directly with HackPDM!";
				_statusText = $"Connected to {selDesc.DisplayName}";
			}
			else
			{
				_connectFeedbackIsError = true;
				_connectFeedback = res.Message;
			}
			Render();
		};
		formBtns.Children.Add( testSaveBtn );
		fs.Children.Add( formBtns );

		formBox.Child = fs;
		ls.Children.Add( formBox );
		loginCard.Child = ls;
		content.Children.Add( loginCard );

		// ---- Section 2: One-Click External AI Client Auto-Connect ----
		Border clientsCard = new()
		{
			Background = Brush( pal.CardBg ),
			BorderBrush = Brush( pal.Border ),
			BorderThickness = new Thickness( 1 ),
			CornerRadius = new CornerRadius( 10 ),
			Padding = new Thickness( 14, 12, 14, 12 ),
		};
		StackPanel cs = new() { Spacing = 8 };
		cs.Children.Add( new TextBlock
		{
			Text = "2. One-Click Auto-Connect External AI Apps (Zero Manual mcp.json Editing)",
			FontSize = 14,
			FontWeight = FontWeights.SemiBold,
			Foreground = Brush( pal.PrimaryText ),
		} );
		cs.Children.Add( new TextBlock
		{
			Text = "Automatically register HackPDM's auto-attaching MCP server into your installed AI desktop apps & IDEs (backs up existing config to .hackpdm.bak and preserves all other servers).",
			FontSize = 11.5,
			Foreground = Brush( pal.MutedText ),
			TextWrapping = TextWrapping.Wrap,
		} );

		IReadOnlyList<ExternalAiClientStatus> clients = McpClientAutoConfigurator.DetectClients();
		var (launchCmd, launchArgs) = McpBridgeHost.ResolveClientStdioLaunch();

		foreach( ExternalAiClientStatus client in clients )
		{
			Border row = new()
			{
				Background = Brush( pal.SubtleBg ),
				BorderBrush = Brush( pal.Border ),
				BorderThickness = new Thickness( 1 ),
				CornerRadius = new CornerRadius( 7 ),
				Padding = new Thickness( 10, 7, 10, 7 ),
			};
			Grid rg = new() { ColumnSpacing = 10 };
			rg.ColumnDefinitions.Add( new ColumnDefinition { Width = new GridLength( 1, GridUnitType.Star ) } );
			rg.ColumnDefinitions.Add( new ColumnDefinition { Width = GridLength.Auto } );
			rg.ColumnDefinitions.Add( new ColumnDefinition { Width = GridLength.Auto } );

			StackPanel info = new() { Spacing = 1 };
			info.Children.Add( new TextBlock
			{
				Text = client.DisplayName,
				FontSize = 12.5,
				FontWeight = FontWeights.SemiBold,
				Foreground = Brush( pal.PrimaryText ),
			} );
			info.Children.Add( new TextBlock
			{
				Text = client.ConfigPath,
				FontFamily = new FontFamily( "Cascadia Mono, Consolas, DejaVu Sans Mono, monospace" ),
				FontSize = 10.5,
				Foreground = Brush( pal.MutedText ),
				TextTrimming = TextTrimming.CharacterEllipsis,
			} );
			Grid.SetColumn( info, 0 );
			rg.Children.Add( info );

			Color stColor = client.IsConnected
				? Color.FromArgb( 0xFF, 0x10, 0xB9, 0x81 )
				: ( client.IsDetected ? pal.Accent : pal.MutedText );
			Border stPill = MakePill( client.StatusLabel, stColor, pal );
			Grid.SetColumn( stPill, 1 );
			rg.Children.Add( stPill );

			Button actionBtn = MakeSmallButton( client.IsConnected ? "Disconnect" : "1-Click Connect", pal, accent: !client.IsConnected );
			actionBtn.Name = $"McpClientAutoConnect_{client.ClientId}";
			ExternalAiClientStatus captured = client;
			actionBtn.Click += ( _, _ ) =>
			{
				ExternalAiClientActionResult res = captured.IsConnected
					? McpClientAutoConfigurator.DisconnectClient( captured.ClientId )
					: McpClientAutoConfigurator.ConnectClient( captured.ClientId, launchCmd, launchArgs );
				_connectFeedbackIsError = !res.Success;
				_connectFeedback = res.Message;
				_statusText = res.Message;
				Render();
			};
			Grid.SetColumn( actionBtn, 2 );
			rg.Children.Add( actionBtn );

			row.Child = rg;
			cs.Children.Add( row );
		}

		clientsCard.Child = cs;
		content.Children.Add( clientsCard );

		// ---- Section 3: Direct Odoo PostgreSQL Connection (Optional) ----
		PostgresConnectionConfig pgCfg = McpBridgeHost.Postgres.GetSnapshot();
		Border pgCard = new()
		{
			Background = Brush( pal.CardBg ),
			BorderBrush = Brush( pal.Border ),
			BorderThickness = new Thickness( 1 ),
			CornerRadius = new CornerRadius( 10 ),
			Padding = new Thickness( 14, 12, 14, 12 ),
		};
		StackPanel pgs = new() { Spacing = 8 };
		pgs.Children.Add( new TextBlock
		{
			Text = "3. Direct Odoo PostgreSQL Connection (Optional — for postgres_query / SQL tools)",
			FontSize = 14,
			FontWeight = FontWeights.SemiBold,
			Foreground = Brush( pal.PrimaryText ),
		} );
		pgs.Children.Add( new TextBlock
		{
			Text = "Inherits Host & Database from HackPDM Odoo settings when blank. Stored owner-only in mcp-postgres.json. Read-only queries run inside PostgreSQL READ ONLY transactions.",
			FontSize = 11.5,
			Foreground = Brush( pal.MutedText ),
			TextWrapping = TextWrapping.Wrap,
		} );

		Grid pgGrid = new() { ColumnSpacing = 8, RowSpacing = 8 };
		pgGrid.ColumnDefinitions.Add( new ColumnDefinition { Width = new GridLength( 1.2, GridUnitType.Star ) } );
		pgGrid.ColumnDefinitions.Add( new ColumnDefinition { Width = new GridLength( 85, GridUnitType.Pixel ) } );
		pgGrid.ColumnDefinitions.Add( new ColumnDefinition { Width = new GridLength( 1, GridUnitType.Star ) } );
		pgGrid.RowDefinitions.Add( new RowDefinition { Height = GridLength.Auto } );
		pgGrid.RowDefinitions.Add( new RowDefinition { Height = GridLength.Auto } );

		TextBox pgHostBox = new() { Name = "McpPostgres_HostBox", Text = pgCfg.Host ?? string.Empty, PlaceholderText = "Host (default: Odoo host)" };
		TextBox pgPortBox = new() { Name = "McpPostgres_PortBox", Text = pgCfg.Port.ToString(), PlaceholderText = "5432" };
		TextBox pgDbBox = new() { Name = "McpPostgres_DatabaseBox", Text = pgCfg.Database ?? string.Empty, PlaceholderText = "Database (default: Odoo DB)" };
		TextBox pgUserBox = new() { Name = "McpPostgres_UserBox", Text = pgCfg.Username ?? string.Empty, PlaceholderText = "PostgreSQL role (e.g. odoo)" };
		PasswordBox pgPassBox = new()
		{
			Name = "McpPostgres_PasswordBox",
			PlaceholderText = string.IsNullOrWhiteSpace( pgCfg.Password ) ? "PostgreSQL password" : "Leave blank to keep saved password",
		};
		CheckBox pgWriteCheck = new()
		{
			Name = "McpPostgres_AllowWritesCheck",
			Content = "Allow modifying SQL (postgres_execute)",
			IsChecked = pgCfg.AllowWriteQueries,
			Foreground = Brush( pal.PrimaryText ),
			VerticalAlignment = VerticalAlignment.Center,
		};

		Grid.SetRow( pgHostBox, 0 ); Grid.SetColumn( pgHostBox, 0 ); pgGrid.Children.Add( pgHostBox );
		Grid.SetRow( pgPortBox, 0 ); Grid.SetColumn( pgPortBox, 1 ); pgGrid.Children.Add( pgPortBox );
		Grid.SetRow( pgDbBox, 0 ); Grid.SetColumn( pgDbBox, 2 ); pgGrid.Children.Add( pgDbBox );
		Grid.SetRow( pgUserBox, 1 ); Grid.SetColumn( pgUserBox, 0 ); pgGrid.Children.Add( pgUserBox );
		Grid.SetRow( pgPassBox, 1 ); Grid.SetColumn( pgPassBox, 1 ); Grid.SetColumnSpan( pgPassBox, 2 ); pgGrid.Children.Add( pgPassBox );
		pgs.Children.Add( pgGrid );

		Grid pgFooter = new() { ColumnSpacing = 8 };
		pgFooter.ColumnDefinitions.Add( new ColumnDefinition { Width = new GridLength( 1, GridUnitType.Star ) } );
		pgFooter.ColumnDefinitions.Add( new ColumnDefinition { Width = GridLength.Auto } );
		Grid.SetColumn( pgWriteCheck, 0 );
		pgFooter.Children.Add( pgWriteCheck );

		Button pgTestSaveBtn = MakeButton( "Save & Test PostgreSQL", pal, accent: true );
		pgTestSaveBtn.Name = "McpPostgres_SaveAndTestButton";
		pgTestSaveBtn.Click += async ( _, _ ) =>
		{
			int? parsedPort = int.TryParse( pgPortBox.Text.Trim(), out int p ) ? p : null;
			McpBridgeHost.Postgres.Configure(
				host: pgHostBox.Text,
				port: parsedPort,
				database: pgDbBox.Text,
				username: pgUserBox.Text,
				password: string.IsNullOrWhiteSpace( pgPassBox.Password ) ? null : pgPassBox.Password,
				clearPassword: false,
				sslMode: null,
				connectionString: null,
				clearConnectionString: false,
				allowWriteQueries: pgWriteCheck.IsChecked == true,
				statementTimeoutSeconds: null,
				maxRows: null );

			_connectFeedbackIsError = false;
			_connectFeedback = "Testing live PostgreSQL connection...";
			Render();

			object status = await McpBridgeHost.Postgres.GetStatusAsync( testConnection: true );
			string json = JsonSerializer.Serialize( status );
			using JsonDocument doc = JsonDocument.Parse( json );
			bool ok = doc.RootElement.TryGetProperty( "connected", out JsonElement cEl ) && cEl.ValueKind == JsonValueKind.True;
			string? err = doc.RootElement.TryGetProperty( "error", out JsonElement eEl ) && eEl.ValueKind == JsonValueKind.String ? eEl.GetString() : null;
			_connectFeedbackIsError = !ok;
			_connectFeedback = ok
				? "Connected to Odoo PostgreSQL database! You can now use postgres_list_tables, postgres_describe_table, and postgres_query."
				: $"Saved PostgreSQL settings, but live connection failed: {err ?? "check host/port/role/password"}";
			_statusText = ok ? "PostgreSQL connected" : "PostgreSQL connection test failed";
			Render();
		};
		Grid.SetColumn( pgTestSaveBtn, 1 );
		pgFooter.Children.Add( pgTestSaveBtn );
		pgs.Children.Add( pgFooter );

		pgCard.Child = pgs;
		content.Children.Add( pgCard );

		return new ScrollViewer
		{
			Content = content,
			MaxHeight = 475,
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
			HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
		};
	}

	// ------------------------------------------------------------------ Actions & Async Orchestration

	private static async Task ExecuteCuratedCommandAsync( PaletteCommandItem item )
	{
		switch( item.Kind )
		{
			case PaletteCommandKind.PaletteMode:
				_activeTab = NormalizeTab( item.Target );
				_searchText = string.Empty;
				Render();
				return;

			case PaletteCommandKind.AiPrompt:
				_activeTab = "ai";
				_draftAiPrompt = item.Target ?? string.Empty;
				if( McpBridgeHost.AiProviders.GetActiveProfile() is not null )
					await SendAiMessageAsync( _draftAiPrompt );
				else
					Render();
				return;

			case PaletteCommandKind.Navigate:
				ExecuteNavigationCommand( item.Target, item.SecondaryTarget );
				Close();
				return;

			case PaletteCommandKind.PermissionMode:
				if( !string.IsNullOrWhiteSpace( item.Target ) )
				{
					McpBridgeHost.Permissions.SelectProfile( item.Target );
					_statusText = $"Switched MCP permission mode to '{item.Target}'";
					_lastCommandTitle = $"Permission Mode: {item.Target}";
					_lastCommandSucceeded = true;
					_lastCommandResultJson = JsonSerializer.Serialize(
						McpBridgeHost.Permissions.GetStatusSnapshot(),
						new JsonSerializerOptions { WriteIndented = true } );
					Render();
				}
				return;

			case PaletteCommandKind.McpTool:
				if( item.Prompts is { Count: > 0 } )
				{
					_pendingParamCommand = item;
					Render();
					return;
				}
				await RunMcpToolCommandAsync( item.Title, item.Target!, item.DefaultArgsJson ?? "{}" );
				return;
		}
	}

	private static void ExecuteNavigationCommand( string? target, string? secondary )
	{
		if( string.Equals( target, "toggle_search", StringComparison.OrdinalIgnoreCase ) )
		{
			AppComposition.Dialogs.ToggleSearchWindow();
			return;
		}
		if( string.Equals( target, "toggle_status", StringComparison.OrdinalIgnoreCase ) )
		{
			AppComposition.Dialogs.ToggleStatusWindow();
			return;
		}

		if( !string.IsNullOrWhiteSpace( target ) && Enum.TryParse<NavigatePageMenu>( target, ignoreCase: true, out NavigatePageMenu page ) )
		{
			AppComposition.Navigation.NavigateTo( page );
			if( !string.IsNullOrWhiteSpace( secondary ) && HackApp.HomePage?.ContentFrame.Content is ApplicationSettingsView settingsView )
				settingsView.SelectSection( secondary );
		}
	}

	private static async Task RunMcpToolCommandAsync( string label, string toolName, string argsJson )
	{
		try
		{
			JsonElement? parsed = null;
			if( !string.IsNullOrWhiteSpace( argsJson ) )
			{
				using JsonDocument doc = JsonDocument.Parse( argsJson );
				parsed = doc.RootElement.Clone();
			}

			_statusText = $"Running {toolName}...";
			Render();

			Stopwatch sw = Stopwatch.StartNew();
			McpServer server = McpBridgeHost.GetOrCreateEmbeddedMcpServer();
			McpToolResult res = await server.CallToolAsync( toolName, parsed );
			sw.Stop();

			string text = string.Join( "\n", res.Content.Where( c => c.Type == "text" && c.Text is not null ).Select( c => c.Text ) );
			_lastCommandTitle = $"{label} ({sw.ElapsedMilliseconds} ms)";
			_lastCommandSucceeded = res.IsError != true;
			_lastCommandResultJson = PrettyJsonIfPossible( text );
			_statusText = _lastCommandSucceeded
				? $"Completed '{toolName}' in {sw.ElapsedMilliseconds} ms"
				: $"'{toolName}' returned an error or was blocked by permissions";
		}
		catch( Exception ex )
		{
			_lastCommandTitle = $"{label} (Error)";
			_lastCommandSucceeded = false;
			_lastCommandResultJson = ex.Message;
			_statusText = $"Error: {ex.Message}";
		}
		Render();
	}

	private static async Task SendAiMessageAsync( string text )
	{
		AiProviderProfile? profile = McpBridgeHost.AiProviders.GetActiveProfile();
		if( profile is null )
		{
			_activeTab = "connect";
			_connectFeedbackIsError = true;
			_connectFeedback = "Please sign in to an AI provider first.";
			Render();
			return;
		}

		_draftAiPrompt = string.Empty;
		_isAiBusy = true;
		_inFlightToolCalls.Clear();
		_statusText = $"Sending to {AiProviderCatalog.Get( profile.Kind ).DisplayName}...";
		Render();

		try
		{
			McpServer server = McpBridgeHost.GetOrCreateEmbeddedMcpServer();
			AiChatTurn reply = await McpBridgeHost.AiChat.SendMessageAsync(
				profile,
				text,
				server,
				McpBridgeHost.Permissions,
				onToolExecuted: rec =>
				{
					UiDispatcher.Post( () =>
					{
						_inFlightToolCalls.Add( rec );
						_statusText = $"AI ran {rec.ToolName} ({rec.DurationMs} ms)...";
						if( _isOpen && _activeTab == "ai" )
							Render();
					} );
				} );

			_statusText = $"AI replied ({reply.ToolCalls.Count} MCP tool call(s))";
		}
		catch( Exception ex )
		{
			_statusText = $"AI error: {ex.Message}";
		}
		finally
		{
			_isAiBusy = false;
			_inFlightToolCalls.Clear();
			Render();
		}
	}

	private static async Task RunOpenRouterBrowserOAuthAsync()
	{
		CancelPendingOAuth();
		_oauthCts = new CancellationTokenSource();
		_isAuthBusy = true;
		_selectedProviderKind = AiProviderKind.OpenRouter;
		_connectFeedbackIsError = false;
		_connectFeedback = "Opening your browser to sign in with OpenRouter (Google / GitHub / Email)... Complete login in your browser to connect automatically.";
		_statusText = "Waiting for browser OAuth sign-in...";
		Render();

		AiOAuthLoginResult res = await AiOAuthLoginFlow.SignInWithOpenRouterAsync(
			timeout: TimeSpan.FromMinutes( 3 ),
			cancellationToken: _oauthCts.Token );

		_isAuthBusy = false;
		if( res.Success && !string.IsNullOrWhiteSpace( res.ApiKey ) )
		{
			AiProviderDescriptor desc = AiProviderCatalog.Get( AiProviderKind.OpenRouter );
			McpBridgeHost.AiProviders.SaveAndActivate( new AiProviderProfile
			{
				Kind = AiProviderKind.OpenRouter,
				ApiKey = res.ApiKey,
				BaseUrl = desc.DefaultBaseUrl,
				Model = desc.DefaultModel,
				AccountLabel = res.AccountLabel ?? "OpenRouter (OAuth)",
				AuthMethod = "browser_oauth_pkce",
			} );
			_connectFeedbackIsError = false;
			_connectFeedback = "Signed in via browser OAuth! You now have direct access to Claude, GPT-4o, Gemini, and DeepSeek in 'Ask AI'.";
			_statusText = "Signed in via OpenRouter OAuth";
		}
		else
		{
			_connectFeedbackIsError = true;
			_connectFeedback = res.Message;
			_statusText = "Browser OAuth sign-in did not complete";
		}
		Render();
	}

	private static async Task RunAutoDetectAsync()
	{
		_isAuthBusy = true;
		_connectFeedbackIsError = false;
		_connectFeedback = "Scanning local AI servers (Ollama, LM Studio) and environment credentials...";
		Render();

		_detectedProviders = await AiProviderStore.AutoDetectAvailableAsync();
		_isAuthBusy = false;
		if( _detectedProviders.Count > 0 )
		{
			_connectFeedbackIsError = false;
			_connectFeedback = $"Found {_detectedProviders.Count} ready AI provider(s)! Click 'Connect 1-Click' below to activate.";
			_statusText = $"Auto-detected {_detectedProviders.Count} AI provider(s)";
		}
		else
		{
			_connectFeedbackIsError = false;
			_connectFeedback = "No local Ollama/LM Studio server or API-key env vars found. Use 'Browser Login (OAuth)' above or select a provider to sign in.";
			_statusText = "Auto-detect complete (0 found)";
		}
		Render();
	}

	private static async Task PopResultIntoDynamicWindowAsync( string title, string jsonText )
	{
		DynamicWindowSpec spec = new()
		{
			WindowId = "palette-result-" + Guid.NewGuid().ToString( "N" )[ ..6 ],
			Title = title,
			Subtitle = "Opened from HackPDM Command Palette",
			Badge = "COMMAND OUTPUT",
			Severity = _lastCommandSucceeded ? "info" : "critical",
			Width = 860,
			Height = 640,
			Components =
			[
				new DynamicComponentSpec
				{
					Type = "code",
					Title = "Command Response Payload",
					Language = "json",
					Content = jsonText,
					MaxHeight = 480,
					Copyable = true,
				},
			],
		};
		await DynamicWindowManager.CreateOrReplaceWindowAsync( spec );
	}

	private static async Task PopConversationIntoDynamicWindowAsync()
	{
		List<DynamicComponentSpec> comps = [];
		foreach( AiChatTurn turn in McpBridgeHost.AiChat.History )
		{
			comps.Add( new DynamicComponentSpec
			{
				Type = "section",
				Title = turn.Role == "user" ? "User Prompt" : $"HackPDM AI ({turn.ProviderSummary ?? "MCP"})",
				Severity = turn.Role == "user" ? "action" : "tip",
				Badge = turn.ToolCalls.Count > 0 ? $"{turn.ToolCalls.Count} TOOL(S)" : null,
				Children =
				[
					new DynamicComponentSpec { Type = "markdown", Content = turn.Text },
				],
			} );
		}
		if( comps.Count == 0 )
			return;

		await DynamicWindowManager.CreateOrReplaceWindowAsync( new DynamicWindowSpec
		{
			WindowId = "ai-chat-transcript",
			Title = "HackPDM AI Conversation",
			Subtitle = "Popped out from Command Palette",
			Badge = "AI CHAT",
			Severity = "tip",
			Width = 920,
			Height = 720,
			Components = comps,
		} );
	}

	// ------------------------------------------------------------------ Helpers & Theme Colors

	private static string NormalizeTab( string? raw ) => ( raw ?? "commands" ).Trim().ToLowerInvariant() switch
	{
		"tools" or "mcp" or "api" => "tools",
		"ai" or "chat" or "ask" => "ai",
		"connect" or "login" or "providers" or "auth" => "connect",
		_ => "commands",
	};

	private static string BuildSchemaSkeleton( JsonObject schema )
	{
		try
		{
			if( schema[ "properties" ] is not JsonObject props )
				return "{}";

			HashSet<string> req = new( StringComparer.Ordinal );
			if( schema[ "required" ] is JsonArray reqArr )
				foreach( JsonNode? r in reqArr )
					if( r?.GetValue<string>() is { Length: > 0 } rn )
						req.Add( rn );

			Dictionary<string, object?> sample = [];
			foreach( var kv in props )
			{
				if( req.Count > 0 && !req.Contains( kv.Key ) )
					continue;
				if( req.Count == 0 && sample.Count >= 3 )
					break;
				string type = ( kv.Value as JsonObject )?[ "type" ]?.GetValue<string>() ?? "string";
				sample[ kv.Key ] = type switch
				{
					"boolean" => false,
					"integer" or "number" => 0,
					"array" => Array.Empty<string>(),
					"object" => new { },
					_ => "",
				};
			}
			return JsonSerializer.Serialize( sample, new JsonSerializerOptions { WriteIndented = true } );
		}
		catch
		{
			return "{}";
		}
	}

	private static string PrettyJsonIfPossible( string raw )
	{
		try
		{
			using JsonDocument doc = JsonDocument.Parse( raw );
			return JsonSerializer.Serialize( doc.RootElement, new JsonSerializerOptions { WriteIndented = true } );
		}
		catch
		{
			return raw;
		}
	}

	private static string ShortModel( string? model )
	{
		if( string.IsNullOrWhiteSpace( model ) )
			return "default";
		int slash = model.LastIndexOf( '/' );
		string clean = slash >= 0 && slash < model.Length - 1 ? model[ ( slash + 1 ).. ] : model;
		return clean.Length > 22 ? clean[ ..22 ] + "…" : clean;
	}

	private static string Truncate( string s, int max ) => s.Length <= max ? s : s[ ..max ] + "\n… (truncated)";

	private static bool TryCopy( string text )
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

	private static Border MakePill( string label, Color color, PaletteColors pal )
	{
		_ = pal;
		return new Border
		{
			Background = Brush( WithAlpha( color, 0x26 ) ),
			BorderBrush = Brush( WithAlpha( color, 0x8C ) ),
			BorderThickness = new Thickness( 1 ),
			CornerRadius = new CornerRadius( 9 ),
			Padding = new Thickness( 7, 1.5, 7, 1.5 ),
			VerticalAlignment = VerticalAlignment.Center,
			Child = new TextBlock
			{
				Text = label,
				FontSize = 10.5,
				FontWeight = FontWeights.SemiBold,
				Foreground = Brush( color ),
			},
		};
	}

	private static Button MakeButton( string label, PaletteColors pal, bool accent ) => new()
	{
		Content = label,
		FontSize = 12,
		FontWeight = accent ? FontWeights.SemiBold : FontWeights.Normal,
		Padding = new Thickness( 12, 6, 12, 6 ),
		CornerRadius = new CornerRadius( 6 ),
		Background = Brush( accent ? pal.Accent : pal.SubtleBg ),
		Foreground = Brush( accent ? ContrastTextFor( pal.Accent ) : pal.PrimaryText ),
		BorderBrush = Brush( accent ? pal.Accent : pal.Border ),
		BorderThickness = new Thickness( 1 ),
	};

	private static Button MakeSmallButton( string label, PaletteColors pal, bool accent = false ) => new()
	{
		Content = label,
		FontSize = 11.5,
		FontWeight = accent ? FontWeights.SemiBold : FontWeights.Normal,
		Padding = new Thickness( 9, 3.5, 9, 3.5 ),
		CornerRadius = new CornerRadius( 6 ),
		Background = Brush( accent ? pal.Accent : pal.SubtleBg ),
		Foreground = Brush( accent ? ContrastTextFor( pal.Accent ) : pal.PrimaryText ),
		BorderBrush = Brush( accent ? pal.Accent : pal.Border ),
		BorderThickness = new Thickness( 1 ),
	};

	private static Color ResolveSeverityColor( string? severity, PaletteColors pal ) => ( severity ?? "info" ) switch
	{
		"warning" => Color.FromArgb( 0xFF, 0xF5, 0x9E, 0x0B ),
		"critical" => Color.FromArgb( 0xFF, 0xEF, 0x44, 0x44 ),
		"success" => Color.FromArgb( 0xFF, 0x10, 0xB9, 0x81 ),
		"tip" => Color.FromArgb( 0xFF, 0x06, 0xB6, 0xD4 ),
		"action" => pal.Accent,
		_ => Color.FromArgb( 0xFF, 0x60, 0xA5, 0xFA ),
	};

	private static Color ColorForMode( string mode, PaletteColors pal ) => mode switch
	{
		"read-only" => Color.FromArgb( 0xFF, 0x10, 0xB9, 0x81 ),
		"local-only" => Color.FromArgb( 0xFF, 0x06, 0xB6, 0xD4 ),
		"review" => Color.FromArgb( 0xFF, 0xF5, 0x9E, 0x0B ),
		"auto" => Color.FromArgb( 0xFF, 0xEF, 0x44, 0x44 ),
		_ => pal.Accent,
	};

	private static Color? GetThemeColor( IThemeService theme, string key )
		=> theme.GetColor( key ) is { } c ? Color.FromArgb( c.A, c.R, c.G, c.B ) : null;

	private static PaletteColors ResolveColors()
	{
		IThemeService theme = AppComposition.Theme;
		bool dark = theme.IsDarkActive;
		AppPalette basePal = ThemePresets.Palette( theme.Palette );
		Color accent = GetThemeColor( theme, "AccentFillColorDefaultBrush" )
			?? GetThemeColor( theme, "SystemControlHighlightAccentBrush" )
			?? WithAlpha( ToColor( basePal.FancyPanelStroke ), 0xFF );

		if( dark )
		{
			Color bg = GetThemeColor( theme, "ApplicationPageBackgroundThemeBrush" ) ?? ToColor( basePal.PageBackground );
			Color card = GetThemeColor( theme, "LayerFillColorDefaultBrush" ) ?? Blend( bg, Color.FromArgb( 0xFF, 0xFF, 0xFF, 0xFF ), 0.06 );
			return new PaletteColors(
				IsDark: true,
				WindowBg: Blend( bg, Color.FromArgb( 0xFF, 0xFF, 0xFF, 0xFF ), 0.02 ),
				HeaderBg: Blend( bg, Color.FromArgb( 0xFF, 0xFF, 0xFF, 0xFF ), 0.05 ),
				CardBg: card,
				SubtleBg: Blend( card, Color.FromArgb( 0xFF, 0xFF, 0xFF, 0xFF ), 0.05 ),
				CodeBg: Color.FromArgb( 0xFF, 0x10, 0x14, 0x1C ),
				Border: Color.FromArgb( 0x33, 0xFF, 0xFF, 0xFF ),
				ModalBorder: WithAlpha( accent, 0x77 ),
				PrimaryText: Color.FromArgb( 0xFF, 0xF3, 0xF5, 0xF9 ),
				MutedText: Color.FromArgb( 0xFF, 0x9C, 0xA7, 0xB8 ),
				CodeText: Color.FromArgb( 0xFF, 0xD7, 0xE6, 0xF8 ),
				Accent: accent );
		}

		Color lbg = GetThemeColor( theme, "ApplicationPageBackgroundThemeBrush" ) ?? ToColor( basePal.PageBackground );
		return new PaletteColors(
			IsDark: false,
			WindowBg: Color.FromArgb( 0xFF, 0xFC, 0xFD, 0xFF ),
			HeaderBg: Blend( lbg, Color.FromArgb( 0xFF, 0x00, 0x00, 0x00 ), 0.03 ),
			CardBg: Color.FromArgb( 0xFF, 0xFF, 0xFF, 0xFF ),
			SubtleBg: Color.FromArgb( 0xFF, 0xF2, 0xF4, 0xF8 ),
			CodeBg: Color.FromArgb( 0xFF, 0xF4, 0xF6, 0xFA ),
			Border: Color.FromArgb( 0x26, 0x00, 0x00, 0x00 ),
			ModalBorder: WithAlpha( accent, 0x88 ),
			PrimaryText: Color.FromArgb( 0xFF, 0x14, 0x19, 0x23 ),
			MutedText: Color.FromArgb( 0xFF, 0x52, 0x5E, 0x70 ),
			CodeText: Color.FromArgb( 0xFF, 0x1F, 0x29, 0x37 ),
			Accent: accent );
	}

	private static Color Blend( Color b, Color t, double amt ) => Color.FromArgb(
		0xFF,
		(byte)Math.Clamp( b.R + ( t.R - b.R ) * amt, 0, 255 ),
		(byte)Math.Clamp( b.G + ( t.G - b.G ) * amt, 0, 255 ),
		(byte)Math.Clamp( b.B + ( t.B - b.B ) * amt, 0, 255 ) );

	private static Color ToColor( uint argb ) => Color.FromArgb(
		(byte)( argb >> 24 ), (byte)( argb >> 16 ), (byte)( argb >> 8 ), (byte)argb );

	private static Color WithAlpha( Color c, byte a ) => Color.FromArgb( a, c.R, c.G, c.B );
	private static SolidColorBrush Brush( Color c ) => new( c );

	private static Color ContrastTextFor( Color bg )
	{
		uint packed = ( (uint)bg.A << 24 ) | ( (uint)bg.R << 16 ) | ( (uint)bg.G << 8 ) | bg.B;
		return ThemePresets.Contrast( 0xFF000000, packed ) >= ThemePresets.Contrast( 0xFFFFFFFF, packed )
			? Color.FromArgb( 0xFF, 0x09, 0x0E, 0x18 )
			: Color.FromArgb( 0xFF, 0xFF, 0xFF, 0xFF );
	}

	private readonly record struct PaletteColors(
		bool IsDark,
		Color WindowBg,
		Color HeaderBg,
		Color CardBg,
		Color SubtleBg,
		Color CodeBg,
		Color Border,
		Color ModalBorder,
		Color PrimaryText,
		Color MutedText,
		Color CodeText,
		Color Accent );
}

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Windows.Input;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

using WinUI.TableView;

using TableView = WinUI.TableView.TableView;
using TreeView = Microsoft.UI.Xaml.Controls.TreeView;

namespace HackPDM.Shell.Mcp;

/// <summary>
/// Inspects and interacts with live WinUI / Uno Platform XAML visual trees and controls.
/// All methods must be invoked on the UI thread.
/// </summary>
internal static class VisualTreeInspector
{
	public sealed record LocatedElement( DependencyObject Element, string Path );

	public static object BuildVisualTree(
		DependencyObject root,
		UIElement? coordSpace,
		int maxDepth,
		bool includeInvisible,
		bool interactiveOnly )
	{
		return WalkNode( root, "0", coordSpace, 0, Math.Clamp( maxDepth, 1, 64 ), includeInvisible, interactiveOnly )
			?? new { path = "0", type = root.GetType().Name, hidden = true };
	}

	private static object? WalkNode(
		DependencyObject current,
		string path,
		UIElement? coordSpace,
		int depth,
		int maxDepth,
		bool includeInvisible,
		bool interactiveOnly )
	{
		if( !includeInvisible && current is UIElement ui && ui.Visibility != Visibility.Visible )
			return null;

		int childCount = VisualTreeHelper.GetChildrenCount( current );
		List<object> children = [];
		bool truncated = false;

		if( depth < maxDepth )
		{
			for( int i = 0; i < childCount; i++ )
			{
				DependencyObject child = VisualTreeHelper.GetChild( current, i );
				string childPath = $"{path}/{i}";
				object? childNode = WalkNode( child, childPath, coordSpace, depth + 1, maxDepth, includeInvisible, interactiveOnly );
				if( childNode is List<object> promoted )
					children.AddRange( promoted );
				else if( childNode is not null )
					children.Add( childNode );
			}
		}
		else if( childCount > 0 )
		{
			truncated = true;
		}

		bool keepSelf = !interactiveOnly || depth == 0 || IsInterestingControl( current );
		if( !keepSelf )
			return children;

		return SummarizeElement( current, path, coordSpace, children, truncated );
	}

	public static List<object> FindControls(
		DependencyObject root,
		UIElement? coordSpace,
		string? name,
		string? typeName,
		string? text,
		string? automationId,
		bool visibleOnly,
		bool interactiveOnly,
		int maxResults )
	{
		List<object> matches = [];
		int limit = Math.Clamp( maxResults, 1, 500 );

		void Search( DependencyObject current, string path )
		{
			if( matches.Count >= limit )
				return;
			if( visibleOnly && current is UIElement ui && ui.Visibility != Visibility.Visible )
				return;

			if( MatchesFilter( current, name, typeName, text, automationId, interactiveOnly ) )
				matches.Add( SummarizeElement( current, path, coordSpace, children: null, truncated: false ) );

			int count = VisualTreeHelper.GetChildrenCount( current );
			for( int i = 0; i < count && matches.Count < limit; i++ )
				Search( VisualTreeHelper.GetChild( current, i ), $"{path}/{i}" );
		}

		Search( root, "0" );
		return matches;
	}

	public static LocatedElement? ResolveControl(
		DependencyObject root,
		string? name,
		string? automationId,
		string? path,
		string? text = null,
		bool preferInteractive = false )
	{
		if( !string.IsNullOrWhiteSpace( path ) )
		{
			DependencyObject? byPath = FollowPath( root, path.Trim() );
			return byPath is null ? null : new LocatedElement( byPath, path.Trim() );
		}

		LocatedElement? fallback = null;

		LocatedElement? Walk( DependencyObject current, string currentPath )
		{
			bool matched = false;
			if( !string.IsNullOrWhiteSpace( name ) )
				matched = current is FrameworkElement fe && string.Equals( fe.Name, name, StringComparison.OrdinalIgnoreCase );
			else if( !string.IsNullOrWhiteSpace( automationId ) )
				matched = string.Equals( AutomationProperties.GetAutomationId( current ), automationId, StringComparison.OrdinalIgnoreCase );
			else if( !string.IsNullOrWhiteSpace( text ) )
			{
				string? extracted = ExtractDisplayText( current );
				matched = extracted is not null && extracted.Contains( text, StringComparison.OrdinalIgnoreCase );
			}

			if( matched )
			{
				LocatedElement hit = new( current, currentPath );
				if( !preferInteractive || IsInteractive( current ) )
					return hit;
				fallback ??= hit;
			}

			int count = VisualTreeHelper.GetChildrenCount( current );
			for( int i = 0; i < count; i++ )
			{
				if( Walk( VisualTreeHelper.GetChild( current, i ), $"{currentPath}/{i}" ) is { } found )
					return found;
			}
			return null;
		}

		LocatedElement? result = Walk( root, "0" ) ?? fallback;
		if( result is not null && preferInteractive && !IsInteractive( result.Element ) )
		{
			// If a text search hit a TextBlock inside a Button/Toggle/TabItem, bubble up to its interactive ancestor
			DependencyObject? ancestor = result.Element;
			string ancestorPath = result.Path;
			while( ancestor is not null )
			{
				if( IsInteractive( ancestor ) )
					return new LocatedElement( ancestor, ancestorPath );
				ancestor = VisualTreeHelper.GetParent( ancestor );
				int slash = ancestorPath.LastIndexOf( '/' );
				ancestorPath = slash > 0 ? ancestorPath[ ..slash ] : "0";
			}
		}
		return result;
	}

	public static object DescribeControlDetailed( DependencyObject element, string path, UIElement? coordSpace )
	{
		List<string> parents = [];
		for( DependencyObject? p = VisualTreeHelper.GetParent( element ); p is not null && parents.Count < 12; p = VisualTreeHelper.GetParent( p ) )
		{
			string pName = p is FrameworkElement pfe && !string.IsNullOrEmpty( pfe.Name ) ? $"#{pfe.Name}" : "";
			parents.Add( $"{p.GetType().Name}{pName}" );
		}

		int childCount = VisualTreeHelper.GetChildrenCount( element );
		List<object> directChildren = [];
		for( int i = 0; i < Math.Min( childCount, 40 ); i++ )
		{
			DependencyObject c = VisualTreeHelper.GetChild( element, i );
			directChildren.Add( SummarizeElement( c, $"{path}/{i}", coordSpace, children: null, truncated: false ) );
		}

		object? itemsInfo = element switch
		{
			TreeView tv => new
			{
				rootNodeCount = tv.RootNodes.Count,
				selectedNode = tv.SelectedNode?.Content?.ToString(),
				roots = tv.RootNodes.Take( 40 ).Select( n => n.Content?.ToString() ).ToList(),
			},
			TabView tabView => new
			{
				selectedIndex = tabView.SelectedIndex,
				selectedName = ( tabView.SelectedItem as TabViewItem )?.Name,
				tabs = tabView.TabItems.OfType<TabViewItem>().Select( ( t, idx ) => new
				{
					index = idx,
					name = t.Name,
					header = t.Header?.ToString(),
				} ).ToList(),
			},
			SelectorBar bar => new
			{
				selectedName = bar.SelectedItem?.Name,
				items = bar.Items.Select( ( item, idx ) => new { index = idx, name = item.Name, text = item.Text, tag = item.Tag?.ToString() } ).ToList(),
			},
			NavigationView nav => new
			{
				selectedTag = ( nav.SelectedItem as NavigationViewItem )?.Tag?.ToString(),
				items = nav.MenuItems.OfType<NavigationViewItem>().Select( m => new
				{
					name = m.Name,
					content = m.Content?.ToString(),
					tag = m.Tag?.ToString(),
				} ).ToList(),
			},
			ComboBox combo => new
			{
				selectedIndex = combo.SelectedIndex,
				selectedItem = combo.SelectedItem?.ToString(),
				itemCount = combo.Items.Count,
				items = combo.Items.Take( 50 ).Select( ( item, idx ) => new { index = idx, text = item?.ToString() } ).ToList(),
			},
			TableView table => new
			{
				selectedIndex = table.SelectedIndex,
				selectedCount = table.SelectedItems.Count,
				rowCount = ( table.ItemsSource as ICollection )?.Count ?? table.Items.Count,
				columns = table.Columns.Select( c => new { header = c.Header?.ToString(), sortMemberPath = c.SortMemberPath, sortDirection = c.SortDirection?.ToString() } ).ToList(),
			},
			Selector selector => new
			{
				selectedIndex = selector.SelectedIndex,
				selectedItem = selector.SelectedItem?.ToString(),
				itemCount = selector.Items.Count,
				items = selector.Items.Take( 50 ).Select( ( item, idx ) => new { index = idx, text = item?.ToString() } ).ToList(),
			},
			_ => null,
		};

		object? commandInfo = element is ButtonBase btn && btn.Command is { } cmd
			? new { commandType = cmd.GetType().Name, canExecute = cmd.CanExecute( btn.CommandParameter ), parameter = btn.CommandParameter?.ToString() }
			: null;

		return new
		{
			summary = SummarizeElement( element, path, coordSpace, children: null, truncated: false ),
			command = commandInfo,
			items = itemsInfo,
			parentChain = parents,
			directChildCount = childCount,
			directChildren,
		};
	}

	public static object ClickControl( DependencyObject element )
	{
		if( element is Control ctrl && !ctrl.IsEnabled )
			throw new InvalidOperationException( $"Control '{DescribeId( element )}' is currently disabled." );

		switch( element )
		{
			case ToggleSwitch toggleSwitch:
				toggleSwitch.IsOn = !toggleSwitch.IsOn;
				return new { clicked = true, control = DescribeId( element ), action = "ToggleSwitch.IsOn", isOn = toggleSwitch.IsOn };

			case RadioButton radio:
				radio.IsChecked = true;
				return new { clicked = true, control = DescribeId( element ), action = "RadioButton.IsChecked", isChecked = radio.IsChecked };

			case CheckBox checkBox:
				checkBox.IsChecked = !( checkBox.IsChecked == true );
				if( checkBox.Command is { } cbCmd && cbCmd.CanExecute( checkBox.CommandParameter ) )
					cbCmd.Execute( checkBox.CommandParameter );
				return new { clicked = true, control = DescribeId( element ), action = "CheckBox.Toggle", isChecked = checkBox.IsChecked };

			case ToggleButton toggleBtn:
				if( TryInvokeAutomationPeer( toggleBtn, out string? toggleMethod ) )
					return new { clicked = true, control = DescribeId( element ), action = toggleMethod, isChecked = toggleBtn.IsChecked };
				toggleBtn.IsChecked = !( toggleBtn.IsChecked == true );
				if( toggleBtn.Command is { } tbCmd && tbCmd.CanExecute( toggleBtn.CommandParameter ) )
					tbCmd.Execute( toggleBtn.CommandParameter );
				return new { clicked = true, control = DescribeId( element ), action = "ToggleButton.Toggle", isChecked = toggleBtn.IsChecked };

			case TabViewItem tabItem:
				if( FindAncestor<TabView>( tabItem ) is { } parentTabView )
				{
					parentTabView.SelectedItem = tabItem;
					return new { clicked = true, control = DescribeId( element ), action = "TabView.SelectedItem", tab = tabItem.Name };
				}
				break;

			case SelectorBarItem barItem:
				if( FindAncestor<SelectorBar>( barItem ) is { } parentBar )
				{
					parentBar.SelectedItem = barItem;
					return new { clicked = true, control = DescribeId( element ), action = "SelectorBar.SelectedItem", item = barItem.Name };
				}
				break;

			case NavigationViewItem navItem:
				if( FindAncestor<NavigationView>( navItem ) is { } parentNav )
				{
					if( navItem.Tag is string tag && Enum.TryParse( tag, out HackPDM.Shared.GlobalData.NavigatePageMenu pageMenu ) )
					{
						AppComposition.Navigation.NavigateTo( pageMenu );
						return new { clicked = true, control = DescribeId( element ), action = "Navigation.NavigateTo", page = pageMenu.ToString() };
					}
					parentNav.SelectedItem = navItem;
					return new { clicked = true, control = DescribeId( element ), action = "NavigationView.SelectedItem", item = navItem.Name };
				}
				break;

			case MenuFlyoutItem mfi:
				if( mfi.Command is { } mfiCmd && mfiCmd.CanExecute( mfi.CommandParameter ) )
				{
					mfiCmd.Execute( mfi.CommandParameter );
					return new { clicked = true, control = DescribeId( element ), action = "MenuFlyoutItem.Command" };
				}
				if( TryInvokeAutomationPeer( mfi, out string? mfiMethod ) )
					return new { clicked = true, control = DescribeId( element ), action = mfiMethod };
				break;

			case ButtonBase buttonBase:
				if( TryInvokeAutomationPeer( buttonBase, out string? invokeMethod ) )
					return new { clicked = true, control = DescribeId( element ), action = invokeMethod };
				if( buttonBase.Command is { } cmd && cmd.CanExecute( buttonBase.CommandParameter ) )
				{
					cmd.Execute( buttonBase.CommandParameter );
					return new { clicked = true, control = DescribeId( element ), action = "ButtonBase.Command" };
				}
				throw new InvalidOperationException( $"Could not invoke button '{DescribeId( element )}': no AutomationPeer invoke pattern and no executable Command." );
		}

		if( element is UIElement uiEl && TryInvokeAutomationPeer( uiEl, out string? genericMethod ) )
			return new { clicked = true, control = DescribeId( element ), action = genericMethod };

		throw new InvalidOperationException( $"Control '{DescribeId( element )}' ({element.GetType().Name}) does not support click/invoke." );
	}

	public static object SetControlValue( DependencyObject element, JsonElement? value, int? selectedIndex, bool? isChecked )
	{
		string? textVal = value is { ValueKind: JsonValueKind.String } s ? s.GetString()
			: value is { ValueKind: not ( JsonValueKind.Null or JsonValueKind.Undefined ) } other ? other.GetRawText()
			: null;

		switch( element )
		{
			case PasswordBox pb:
				pb.Password = textVal ?? string.Empty;
				return new { ok = true, control = DescribeId( element ), passwordLength = pb.Password.Length };

			case TextBox tb:
				tb.Text = textVal ?? string.Empty;
				return new { ok = true, control = DescribeId( element ), text = tb.Text };

			case AutoSuggestBox asb:
				asb.Text = textVal ?? string.Empty;
				return new { ok = true, control = DescribeId( element ), text = asb.Text };

			case NumberBox nb:
				if( value is { ValueKind: JsonValueKind.Number } num && num.TryGetDouble( out double dv ) )
					nb.Value = dv;
				else if( double.TryParse( textVal, out double parsedNb ) )
					nb.Value = parsedNb;
				else
					throw new ArgumentException( "NumberBox requires a numeric 'value'." );
				return new { ok = true, control = DescribeId( element ), value = nb.Value };

			case Slider slider:
				if( value is { ValueKind: JsonValueKind.Number } sNum && sNum.TryGetDouble( out double sdv ) )
					slider.Value = sdv;
				else if( double.TryParse( textVal, out double parsedSlider ) )
					slider.Value = parsedSlider;
				else
					throw new ArgumentException( "Slider requires a numeric 'value'." );
				return new { ok = true, control = DescribeId( element ), value = slider.Value };

			case ToggleSwitch ts:
				bool targetOn = isChecked ?? ( value is { ValueKind: JsonValueKind.True } || bool.TryParse( textVal, out bool bOn ) && bOn );
				ts.IsOn = targetOn;
				return new { ok = true, control = DescribeId( element ), isOn = ts.IsOn };

			case ToggleButton tbBtn:
				bool targetChecked = isChecked ?? ( value is { ValueKind: JsonValueKind.True } || bool.TryParse( textVal, out bool bChk ) && bChk );
				tbBtn.IsChecked = targetChecked;
				return new { ok = true, control = DescribeId( element ), isChecked = tbBtn.IsChecked };

			case TabView tv:
				if( selectedIndex is int ti )
					tv.SelectedIndex = ti;
				else if( !string.IsNullOrWhiteSpace( textVal ) )
				{
					TabViewItem? match = tv.TabItems.OfType<TabViewItem>().FirstOrDefault( t =>
						string.Equals( t.Name, textVal, StringComparison.OrdinalIgnoreCase )
						|| string.Equals( t.Header?.ToString(), textVal, StringComparison.OrdinalIgnoreCase ) );
					if( match is null )
						throw new InvalidOperationException( $"Tab '{textVal}' not found in TabView '{DescribeId( element )}'." );
					tv.SelectedItem = match;
				}
				return new { ok = true, control = DescribeId( element ), selectedIndex = tv.SelectedIndex, selectedName = ( tv.SelectedItem as TabViewItem )?.Name };

			case SelectorBar sb:
				if( selectedIndex is int sbi && sbi >= 0 && sbi < sb.Items.Count )
					sb.SelectedItem = sb.Items[ sbi ];
				else if( !string.IsNullOrWhiteSpace( textVal ) )
				{
					SelectorBarItem? match = sb.Items.FirstOrDefault( i =>
						string.Equals( i.Name, textVal, StringComparison.OrdinalIgnoreCase )
						|| string.Equals( i.Text, textVal, StringComparison.OrdinalIgnoreCase )
						|| string.Equals( i.Tag?.ToString(), textVal, StringComparison.OrdinalIgnoreCase ) );
					if( match is null )
						throw new InvalidOperationException( $"Item '{textVal}' not found in SelectorBar '{DescribeId( element )}'." );
					sb.SelectedItem = match;
				}
				return new { ok = true, control = DescribeId( element ), selectedName = sb.SelectedItem?.Name, selectedText = sb.SelectedItem?.Text };

			case Selector selector:
				if( selectedIndex is int idx )
				{
					selector.SelectedIndex = idx;
				}
				else if( !string.IsNullOrWhiteSpace( textVal ) )
				{
					object? found = selector.Items.FirstOrDefault( item =>
						string.Equals( item?.ToString(), textVal, StringComparison.OrdinalIgnoreCase )
						|| ( item?.ToString()?.Contains( textVal, StringComparison.OrdinalIgnoreCase ) ?? false ) );
					if( found is null )
						throw new InvalidOperationException( $"Item matching '{textVal}' not found in '{DescribeId( element )}'." );
					selector.SelectedItem = found;
				}
				else
				{
					throw new ArgumentException( "Specify 'selectedIndex' or 'value' for selector controls." );
				}
				return new { ok = true, control = DescribeId( element ), selectedIndex = selector.SelectedIndex, selectedItem = selector.SelectedItem?.ToString() };

			default:
				throw new InvalidOperationException( $"Control '{DescribeId( element )}' ({element.GetType().Name}) does not support setting a value." );
		}
	}

	private static bool TryInvokeAutomationPeer( UIElement element, out string? method )
	{
		method = null;
		AutomationPeer? peer = FrameworkElementAutomationPeer.FromElement( element )
			?? FrameworkElementAutomationPeer.CreatePeerForElement( element );
		if( peer is null )
			return false;

		if( peer.GetPattern( PatternInterface.Invoke ) is IInvokeProvider invoker )
		{
			invoker.Invoke();
			method = "AutomationPeer.Invoke";
			return true;
		}
		if( peer.GetPattern( PatternInterface.Toggle ) is IToggleProvider toggler )
		{
			toggler.Toggle();
			method = "AutomationPeer.Toggle";
			return true;
		}
		if( peer.GetPattern( PatternInterface.SelectionItem ) is ISelectionItemProvider selector )
		{
			selector.Select();
			method = "AutomationPeer.Select";
			return true;
		}
		return false;
	}

	private static DependencyObject? FollowPath( DependencyObject root, string path )
	{
		string[] parts = path.Split( '/', StringSplitOptions.RemoveEmptyEntries );
		if( parts.Length == 0 || parts[ 0 ] != "0" )
			return null;
		DependencyObject current = root;
		for( int i = 1; i < parts.Length; i++ )
		{
			if( !int.TryParse( parts[ i ], out int childIdx ) || childIdx < 0 || childIdx >= VisualTreeHelper.GetChildrenCount( current ) )
				return null;
			current = VisualTreeHelper.GetChild( current, childIdx );
		}
		return current;
	}

	private static T? FindAncestor<T>( DependencyObject start ) where T : FrameworkElement
	{
		for( DependencyObject? cur = VisualTreeHelper.GetParent( start ); cur is not null; cur = VisualTreeHelper.GetParent( cur ) )
		{
			if( cur is T match )
				return match;
		}
		return null;
	}

	private static bool MatchesFilter(
		DependencyObject element,
		string? name,
		string? typeName,
		string? text,
		string? automationId,
		bool interactiveOnly )
	{
		if( interactiveOnly && !IsInteractive( element ) )
			return false;

		if( !string.IsNullOrWhiteSpace( name ) )
		{
			string? feName = ( element as FrameworkElement )?.Name;
			if( string.IsNullOrEmpty( feName ) || !feName.Contains( name, StringComparison.OrdinalIgnoreCase ) )
				return false;
		}

		if( !string.IsNullOrWhiteSpace( typeName ) )
		{
			if( !element.GetType().Name.Contains( typeName, StringComparison.OrdinalIgnoreCase ) )
				return false;
		}

		if( !string.IsNullOrWhiteSpace( automationId ) )
		{
			string? aid = AutomationProperties.GetAutomationId( element );
			if( string.IsNullOrEmpty( aid ) || !aid.Contains( automationId, StringComparison.OrdinalIgnoreCase ) )
				return false;
		}

		if( !string.IsNullOrWhiteSpace( text ) )
		{
			string? display = ExtractDisplayText( element );
			if( string.IsNullOrEmpty( display ) || !display.Contains( text, StringComparison.OrdinalIgnoreCase ) )
				return false;
		}

		return true;
	}

	private static object SummarizeElement(
		DependencyObject element,
		string path,
		UIElement? coordSpace,
		List<object>? children,
		bool truncated )
	{
		FrameworkElement? fe = element as FrameworkElement;
		Control? ctrl = element as Control;
		string? name = fe is { Name.Length: > 0 } ? fe.Name : null;
		string? automationId = AutomationProperties.GetAutomationId( element ) is { Length: > 0 } aid ? aid : null;
		string? automationName = AutomationProperties.GetName( element ) is { Length: > 0 } an ? an : null;

		object? bounds = null;
		if( fe is not null && coordSpace is not null && fe.ActualWidth > 0 && fe.ActualHeight > 0 )
		{
			try
			{
				Windows.Foundation.Point pt = fe.TransformToVisual( coordSpace ).TransformPoint( new Windows.Foundation.Point( 0, 0 ) );
				bounds = new { x = Math.Round( pt.X, 1 ), y = Math.Round( pt.Y, 1 ), width = Math.Round( fe.ActualWidth, 1 ), height = Math.Round( fe.ActualHeight, 1 ) };
			}
			catch
			{
				// Element may not yet be connected to coordSpace
			}
		}

		object? state = ExtractState( element );

		return new
		{
			path,
			type = element.GetType().Name,
			name,
			automationId,
			automationName,
			text = ExtractDisplayText( element ),
			isVisible = element is UIElement ui ? ui.Visibility == Visibility.Visible : (bool?)null,
			isEnabled = ctrl?.IsEnabled,
			interactive = IsInteractive( element ),
			state,
			bounds,
			dataContextType = fe?.DataContext?.GetType().Name,
			truncatedChildren = truncated ? true : (bool?)null,
			children = children is { Count: > 0 } ? children : null,
		};
	}

	private static object? ExtractState( DependencyObject element ) => element switch
	{
		ToggleSwitch ts => new { isOn = ts.IsOn },
		ToggleButton tb => new { isChecked = tb.IsChecked },
		PasswordBox pwBox => new { hasPassword = pwBox.Password.Length > 0, length = pwBox.Password.Length },
		TextBox textBox => new { text = textBox.Text, isReadOnly = textBox.IsReadOnly, placeholder = textBox.PlaceholderText },
		AutoSuggestBox asb => new { text = asb.Text, placeholder = asb.PlaceholderText },
		NumberBox nb => new { value = nb.Value },
		Slider sl => new { value = sl.Value, minimum = sl.Minimum, maximum = sl.Maximum },
		ComboBox cb => new { selectedIndex = cb.SelectedIndex, selectedText = cb.SelectedItem?.ToString(), itemCount = cb.Items.Count },
		TabView tv => new { selectedIndex = tv.SelectedIndex, selectedName = ( tv.SelectedItem as TabViewItem )?.Name, tabCount = tv.TabItems.Count },
		SelectorBar sb => new { selectedName = sb.SelectedItem?.Name, selectedText = sb.SelectedItem?.Text, itemCount = sb.Items.Count },
		TreeView tree => new { rootCount = tree.RootNodes.Count, selectedNode = tree.SelectedNode?.Content?.ToString() },
		TableView tbl => new { selectedIndex = tbl.SelectedIndex, selectedCount = tbl.SelectedItems.Count, rowCount = ( tbl.ItemsSource as ICollection )?.Count ?? tbl.Items.Count },
		Selector sel => new { selectedIndex = sel.SelectedIndex, itemCount = sel.Items.Count },
		ProgressBar pb => new { isIndeterminate = pb.IsIndeterminate, value = pb.Value, maximum = pb.Maximum },
		ProgressRing pr => new { isActive = pr.IsActive },
		InfoBar ib => new { isOpen = ib.IsOpen, severity = ib.Severity.ToString(), title = ib.Title, message = ib.Message },
		_ => null,
	};

	private static string? ExtractDisplayText( DependencyObject element )
	{
		string? raw = element switch
		{
			TextBlock tb => tb.Text,
			PasswordBox pw => pw.PlaceholderText,
			TextBox tbox => tbox.Text is { Length: > 0 } t ? t : tbox.PlaceholderText,
			AutoSuggestBox asb => asb.Text is { Length: > 0 } t ? t : asb.PlaceholderText,
			ToggleSwitch ts => ts.Header?.ToString(),
			TabViewItem tvi => tvi.Header?.ToString(),
			SelectorBarItem sbi => sbi.Text,
			NavigationViewItem nvi => nvi.Content?.ToString(),
			AppBarButton abb => abb.Label,
			AppBarToggleButton abtb => abtb.Label,
			MenuFlyoutItem mfi => mfi.Text,
			InfoBar ib => string.IsNullOrEmpty( ib.Title ) ? ib.Message : $"{ib.Title}: {ib.Message}",
			ContentControl cc => cc.Content switch
			{
				string s => s,
				TextBlock innerTb => innerTb.Text,
				_ => null,
			},
			_ => null,
		};

		if( string.IsNullOrWhiteSpace( raw ) )
			return null;
		string trimmed = raw.Trim();
		return trimmed.Length > 240 ? trimmed[ ..240 ] + "…" : trimmed;
	}

	private static bool IsInteractive( DependencyObject element ) => element is
		ButtonBase
		or TextBox
		or PasswordBox
		or AutoSuggestBox
		or NumberBox
		or Slider
		or ToggleSwitch
		or ComboBox
		or ListViewBase
		or TableView
		or TreeView
		or TreeViewItem
		or TabView
		or TabViewItem
		or SelectorBar
		or SelectorBarItem
		or NavigationViewItem
		or MenuFlyoutItem;

	private static bool IsInterestingControl( DependencyObject element )
		=> IsInteractive( element )
			|| element is TextBlock or InfoBar or ProgressBar or ProgressRing or Page or BreadcrumbBar
			|| ( element is FrameworkElement fe && !string.IsNullOrEmpty( fe.Name ) );

	private static string DescribeId( DependencyObject element )
	{
		if( element is FrameworkElement fe && !string.IsNullOrEmpty( fe.Name ) )
			return $"{fe.GetType().Name}#{fe.Name}";
		string aid = AutomationProperties.GetAutomationId( element );
		if( !string.IsNullOrEmpty( aid ) )
			return $"{element.GetType().Name}[AutomationId={aid}]";
		return element.GetType().Name;
	}
}

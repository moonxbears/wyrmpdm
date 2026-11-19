using System;
using System.Collections.Generic;
using System.Linq;

using HackPDM.Common;
using HackPDM.Core.Pdm;
using HackPDM.FileManager;
using HackPDM.Login;
using HackPDM.Settings;

using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;

using Windows.System;
using Windows.UI.Core;

namespace HackPDM.Shell;

public interface INavigationService
{
	void NavigateTo( NavigatePageMenu page );
}

public sealed class NavigationService : INavigationService
{
	private HomeView? _home;
	public void Attach( HomeView home ) => _home = home;
	public void NavigateTo( NavigatePageMenu page ) => UiDispatcher.Post( () => _home?.Select( page ) );
}

/// <summary>Main shell page hosting the left navigation bar and content frame.</summary>
public sealed partial class HomeView : Page
{
	private static readonly Dictionary<NavigatePageMenu, (Type Page, string Preset)> Pages = new()
	{
		[ NavigatePageMenu.HackFileManager ] = ( typeof( HackFileManagerView ), "HackFileManager" ),
		[ NavigatePageMenu.ProfileManager ] = ( typeof( ProfileManagerView ), "ProfileManager" ),
		[ NavigatePageMenu.Configuration ] = ( typeof( ConfigurationView ), "ConfigSettings" ),
	};

	private bool _syncingSelection;

	public HomeView()
	{
		InitializeComponent();
		AppComposition.Navigation.Attach( this );
		HomeNavigator.SelectedItem = ItemFor( NavigatePageMenu.ProfileManager );
		NavFrame.Navigated += ( _, _ ) => HomeNavigator.IsBackEnabled = NavFrame.CanGoBack;
	}

	internal void Select( NavigatePageMenu page )
	{
		NavigationViewItem? target = ItemFor( page ) ?? ItemFor( NavigatePageMenu.ProfileManager );
		if( ReferenceEquals( HomeNavigator.SelectedItem, target ) )
		{
			Show( page );
			return;
		}
		HomeNavigator.SelectedItem = target;
	}

	private NavigationViewItem? ItemFor( NavigatePageMenu page )
		=> HomeNavigator.MenuItems.OfType<NavigationViewItem>().FirstOrDefault( item => item.Tag as string == page.ToString() );

	private void HomeNavigator_ItemInvoked( NavigationView sender, NavigationViewItemInvokedEventArgs args )
	{
		if( args.InvokedItemContainer == StatusLogNav )
			AppComposition.Dialogs.ToggleStatusWindow();
		else if( args.InvokedItemContainer == SearchNav )
			AppComposition.Dialogs.ToggleSearchWindow();
	}

	private void HomeNavigator_SelectionChanged( NavigationView sender, NavigationViewSelectionChangedEventArgs args )
	{
		if( _syncingSelection )
			return;
		if( args.SelectedItem is NavigationViewItem { Tag: string tag } && Enum.TryParse( tag, out NavigatePageMenu page ) )
			Show( page );
	}

	private void Show( NavigatePageMenu page )
	{
		if( AppComposition.Session.IsVersionMismatch && page != NavigatePageMenu.ProfileManager )
		{
			Navigate( typeof( NotLoggedInView ), "NotLoggedIn" );
			return;
		}
		if( page == NavigatePageMenu.HackFileManager && !AppComposition.Session.IsLoggedIn )
		{
			Navigate( typeof( NotLoggedInView ), "NotLoggedIn" );
			return;
		}
		var (type, preset) = Pages[ page ];
		Navigate( type, preset );
	}

	private void Navigate( Type page, string preset )
	{
		if( HackApp.Window is { } window )
			WindowHelper.ApplyPreset( window, WindowHelper.Preset( preset ) );

		if( NavFrame.CurrentSourcePageType == page )
			return;

		NavFrame.Navigate( page, null, new SuppressNavigationTransitionInfo() );
		PruneStaleBackStack( AppComposition.Session.IsLoggedIn );
		HomeNavigator.IsBackEnabled = NavFrame.CanGoBack;
	}

	private void PruneStaleBackStack( bool isLoggedIn )
	{
		for( int i = NavFrame.BackStack.Count - 1; i >= 0; i-- )
		{
			Type pageType = NavFrame.BackStack[ i ].SourcePageType;
			if( isLoggedIn && pageType == typeof( NotLoggedInView ) )
				NavFrame.BackStack.RemoveAt( i );
			else if( !isLoggedIn && pageType == typeof( HackFileManagerView ) )
				NavFrame.BackStack.RemoveAt( i );
		}
	}

	private void HomeNavigator_BackRequested( NavigationView sender, NavigationViewBackRequestedEventArgs args ) => GoBack();

	private void GoBack()
	{
		PruneStaleBackStack( AppComposition.Session.IsLoggedIn );
		if( !NavFrame.CanGoBack )
		{
			HomeNavigator.IsBackEnabled = false;
			return;
		}

		NavFrame.GoBack();
		HomeNavigator.IsBackEnabled = NavFrame.CanGoBack;

		Type? current = NavFrame.CurrentSourcePageType;
		if( current == typeof( NotLoggedInView ) )
		{
			if( HackApp.Window is { } win )
				WindowHelper.ApplyPreset( win, WindowHelper.Preset( "NotLoggedIn" ) );
			return;
		}

		foreach( var (menu, entry) in Pages )
		{
			if( entry.Page != current )
				continue;
			_syncingSelection = true;
			try
			{
				if( ItemFor( menu ) is { } navItem )
					HomeNavigator.SelectedItem = navItem;
			}
			finally
			{
				_syncingSelection = false;
			}
			if( HackApp.Window is { } window )
				WindowHelper.ApplyPreset( window, WindowHelper.Preset( entry.Preset ) );
			break;
		}
	}

	private void HomeView_KeyDown( object sender, KeyRoutedEventArgs e )
	{
		bool ctrl = InputKeyboardSource.GetKeyStateForCurrentThread( VirtualKey.Control ).HasFlag( CoreVirtualKeyStates.Down );
		bool alt = InputKeyboardSource.GetKeyStateForCurrentThread( VirtualKey.Menu ).HasFlag( CoreVirtualKeyStates.Down );

		if( alt && e.Key == VirtualKey.Left )
		{
			e.Handled = true;
			GoBack();
			return;
		}
		if( alt && e.Key == VirtualKey.S )
		{
			e.Handled = true;
			AppComposition.Dialogs.ToggleStatusWindow();
			return;
		}
		if( ctrl && e.Key == VirtualKey.F )
		{
			e.Handled = true;
			AppComposition.Dialogs.ShowSearchWindow();
			return;
		}
		if( !ctrl )
			return;

		NavigatePageMenu? shortcut = e.Key switch
		{
			VirtualKey.Number1 or VirtualKey.NumberPad1 => NavigatePageMenu.HackFileManager,
			VirtualKey.Number2 or VirtualKey.NumberPad2 => NavigatePageMenu.ProfileManager,
			VirtualKey.Number3 or VirtualKey.NumberPad3 => NavigatePageMenu.Configuration,
			_ => null,
		};
		if( shortcut is { } target )
		{
			e.Handled = true;
			Select( target );
		}
	}
}

using System.Linq;

using HackPDM.Core.Services;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

using Windows.System;

namespace HackPDM.Search;

public sealed partial class SearchOdooView : Page
{
	public SearchOdooViewModel ViewModel => AppComposition.SearchOdoo;
	public Window? ParentWindow { get; set; }

	public SearchOdooView()
	{
		InitializeComponent();
		ViewModel.LoadLookups();

		OdooSearchResults.SelectionChanged += ( _, _ )
			=> ViewModel.SelectedResults = [ .. OdooSearchResults.SelectedItems.OfType<SearchRow>() ];
		OdooSearchPropList.SelectionChanged += ( _, _ )
			=> ViewModel.SelectedActiveProperties = [ .. OdooSearchPropList.SelectedItems.OfType<SearchPropertiesRow>() ];
		KeyDown += ( _, e ) =>
		{
			if( e.Key == VirtualKey.Escape )
			{
				e.Handled = true;
				ParentWindow?.Close();
			}
		};
	}

	private void Close_Click( object sender, RoutedEventArgs e ) => ParentWindow?.Close();

	private void Results_DoubleTapped( object sender, DoubleTappedRoutedEventArgs e )
		=> LocateAndFocusMainWindow( OdooSearchResults.SelectedItem as SearchRow );

	private void LocateSelected_Click( object sender, RoutedEventArgs e )
		=> LocateAndFocusMainWindow( OdooSearchResults.SelectedItem as SearchRow );

	private void LocateAndFocusMainWindow( SearchRow? row )
	{
		if( row is null )
			return;
		ViewModel.LocateCommand.Execute( row );
		HackApp.Window?.Activate();
	}

	private void FileNameTextbox_KeyDown( object sender, KeyRoutedEventArgs e )
	{
		if( e.Key == VirtualKey.Enter && ViewModel.SearchCommand.CanExecute( null ) )
		{
			e.Handled = true;
			ViewModel.SearchCommand.Execute( null );
		}
	}

	private void PropertyValue_KeyDown( object sender, KeyRoutedEventArgs e )
	{
		if( e.Key == VirtualKey.Enter && ViewModel.AddPropertyCommand.CanExecute( null ) )
		{
			e.Handled = true;
			ViewModel.AddPropertyCommand.Execute( null );
		}
	}
}

using HackPDM.Mvvm.ViewModels.Settings;
using HackPDM.Shell;
using HackPDM.Shell.Mcp;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace HackPDM.Mvvm.Views.Settings;

/// <summary>
/// Settings page for the embedded HackPDM MCP Server, zero-config connection details,
/// execution modes/presets/profiles, live interactive action review queue, persistent Activity Log,
/// and Restore Points.
/// </summary>
public sealed partial class McpSettingsView : Page
{
	public McpSettingsViewModel ViewModel { get; }

	public McpSettingsView()
	{
		ViewModel = new McpSettingsViewModel( AppComposition.Dialogs );
		InitializeComponent();
		Loaded += OnLoaded;
		Unloaded += OnUnloaded;
	}

	private void OnLoaded( object sender, RoutedEventArgs e )
	{
		McpBridgeHost.IsMcpViewActive = true;
		ViewModel.RefreshPendingReviews();
	}

	private void OnUnloaded( object sender, RoutedEventArgs e )
	{
		McpBridgeHost.IsMcpViewActive = false;
	}

	protected override void OnNavigatedTo( NavigationEventArgs e )
	{
		base.OnNavigatedTo( e );
		McpBridgeHost.IsMcpViewActive = true;
	}

	protected override void OnNavigatedFrom( NavigationEventArgs e )
	{
		base.OnNavigatedFrom( e );
		McpBridgeHost.IsMcpViewActive = false;
		ViewModel.Dispose();
	}

	private void ApproveReviewOnce_Click( object sender, RoutedEventArgs e )
	{
		if( sender is FrameworkElement { Tag: string id } )
			ViewModel.ApproveReviewOnce( id );
	}

	private void ApproveReviewSession_Click( object sender, RoutedEventArgs e )
	{
		if( sender is FrameworkElement { Tag: string id } )
			ViewModel.ApproveReviewSession( id );
	}

	private void DenyReview_Click( object sender, RoutedEventArgs e )
	{
		if( sender is FrameworkElement { Tag: string id } )
			ViewModel.DenyReview( id );
	}

	private void OpenCommandPalette_Click( object sender, RoutedEventArgs e )
		=> CommandPaletteManager.Open( initialTab: "connect" );
}

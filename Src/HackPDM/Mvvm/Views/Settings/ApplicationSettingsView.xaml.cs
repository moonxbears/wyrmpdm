using System;

using HackPDM.Mvvm.Views.Common.Controls;

using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

namespace HackPDM.Mvvm.Views.Settings;

/// <summary>Application settings hub (the NavigationView's settings gear): one section per settings page.</summary>
public sealed partial class ApplicationSettingsView : Page
{
	internal Page? ActiveSectionPage => SectionFrame.Content as Page;

	public ApplicationSettingsView()
	{
		InitializeComponent();
		Loaded += ( _, _ ) => ShowSection( SettingsSections.SelectedItem );
	}

	private void SettingsSections_SelectionChanged( SelectorBar sender, SelectorBarSelectionChangedEventArgs args )
		=> ShowSection( sender.SelectedItem );

	/// <summary>Switches the active tab (for example <c>"Status"</c>, <c>"Theme"</c>, or <c>"Mcp"</c>).</summary>
	public void SelectSection( string sectionName )
	{
		SelectorBarItem target = sectionName.Trim().ToLowerInvariant() switch
		{
			"status" => StatusSection,
			"mcp" or "mcpserver" or "mcp server" => McpSection,
			_ => ThemeSection,
		};
		SettingsSections.SelectedItem = target;
		ShowSection( target );
	}

	private void ShowSection( SelectorBarItem? item )
	{
		Type? page = item?.Name switch
		{
			nameof( ThemeSection ) => typeof( ThemeEditorView ),
			nameof( StatusSection ) => typeof( StatusSettingsView ),
			nameof( McpSection ) => typeof( McpSettingsView ),
			_ => null,
		};
		if( page is null || SectionFrame.CurrentSourcePageType == page )
			return;
		try
		{
			bool firstLoad = SectionFrame.CurrentSourcePageType is null;
			NavigationSlideAxis slide = firstLoad
				? NavigationSlideAxis.None
				: page == typeof( ThemeEditorView )
					? NavigationSlideAxis.Right
					: NavigationSlideAxis.Left;

			SectionFrame.Navigate( page, null, new SuppressNavigationTransitionInfo() );
			NavigationTransitions.Play( SectionFrame.Content, slide );
		}
		catch( Exception e )
		{
			HackPDM.Core.Logging.HackLogger.Error( "Settings", $"Settings page {page.Name} failed to open: {e.Message}", e );
			SectionFrame.Content = new TextBlock { Text = $"Could not open {item?.Text}: {e.Message}", Margin = new( 12 ) };
		}
	}
}

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using HackPDM.Common;
using HackPDM.Core.Configuration;
using HackPDM.Core.Pdm;
using HackPDM.Core.Services;

using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;

using Windows.System;
using Windows.UI;
using Windows.UI.Core;

using WinUI.TableView;

namespace HackPDM.FileManager;

public sealed partial class HackFileManagerView : Page
{
	private const string ChangeBadgeTag = "change-badge";

	private static readonly SolidColorBrush SyncedDotBrush = new( Color.FromArgb( 255, 0xC6, 0x97, 0x49 ) );
	private static readonly SolidColorBrush LocalDotBrush = new( Color.FromArgb( 255, 0x3F, 0xB9, 0x50 ) );
	private static readonly SolidColorBrush RemoteDotBrush = new( Color.FromArgb( 255, 0x58, 0xB4, 0xFF ) );

	private readonly IPreviewImageLoader _previewLoader = new PreviewImageLoader();
	private readonly ObservableCollection<TreeData> _treeItems = [];
	private readonly RangeObservableCollection<object> _groupedItems = [];
	private readonly HashSet<string> _collapsedGroupKeys = new( StringComparer.Ordinal );

	private List<EntryGroupHeader> _groups = [];
	private CancellationTokenSource _treeSelectionCancellation = new();
	private DirectoryNode? _fullTreeRoot;
	private TreeViewNode? _loadingNode;
	private TreeData? _selectedTreeData;
	private double _savedFoldersWidth = 260;
	private int _totalTreeFolderCount;
	private int _previewRequest;
	private int _copyFeedbackVersion;
	private bool _loaded;
	private bool _left;
	private bool _restorePending = true;
	private bool _isFilteringTree;
	private bool _syncingGroupedSelection;

	public HackFileManagerViewModel ViewModel { get; } = AppComposition.CreateFileManager();
	public ObservableCollection<TreeData> BreadcrumbPath { get; } = [];
	public TreeViewNode? LastSelectedNode { get; private set; }

	private bool IsUsingGroupedList => GroupedEntryContainer.Visibility == Visibility.Visible;

	public HackFileManagerView()
	{
		InitializeComponent();
		PopulateGroupByMenus();
		Loaded += ( _, _ ) => InitializePage();
	}

	protected override void OnNavigatedTo( NavigationEventArgs e )
	{
		base.OnNavigatedTo( e );
		InitializePage();
	}

	protected override void OnNavigatedFrom( NavigationEventArgs e )
	{
		base.OnNavigatedFrom( e );
		Leave();
	}

	private void Leave()
	{
		if( _left )
			return;
		_left = true;
		_treeSelectionCancellation.Cancel();
		ViewModel.RefreshRequested -= OnRefreshRequested;
		ViewModel.GroupingChanged -= OnGroupingChanged;
		ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
		ViewModel.TreeLoaded -= OnTreeLoadedAsync;
		ViewModel.LocateRequested -= OnLocateRequestedAsync;
		ViewModel.FolderChanged -= OnFolderChanged;
		ViewModel.RowReplaced -= OnRowReplaced;
		ViewModel.Dispose();
	}

	private void PopulateGroupByMenus()
	{
		foreach( string field in EntryRowKeys.Names )
		{
			EntryGroupBy.Items.Add( new MenuFlyoutItem { Text = field, Command = ViewModel.GroupByCommand, CommandParameter = field } );
			GroupByToolbarFlyout.Items.Add( new MenuFlyoutItem { Text = field, Command = ViewModel.GroupByCommand, CommandParameter = field } );
		}
	}

	private void InitializePage()
	{
		if( _loaded || _left )
			return;
		_loaded = true;
		ViewModel.Initialize();

		OdooDirectoryTree.ItemsSource = _treeItems;
		OnGroupingChanged();

		if( VersionTabs.TabItems.OfType<TabViewItem>().FirstOrDefault( t => t.Name == ViewModel.SelectedDetailTab ) is { } keptTab )
			VersionTabs.SelectedItem = keptTab;
		else
			ViewModel.SelectedDetailTab = ( VersionTabs.SelectedItem as TabViewItem )?.Name;

		ViewModel.RefreshRequested += OnRefreshRequested;
		ViewModel.GroupingChanged += OnGroupingChanged;
		ViewModel.PropertyChanged += OnViewModelPropertyChanged;
		ViewModel.TreeLoaded += OnTreeLoadedAsync;
		ViewModel.LocateRequested += OnLocateRequestedAsync;
		ViewModel.FolderChanged += OnFolderChanged;
		ViewModel.RowReplaced += OnRowReplaced;

		OdooDirectoryTree.SelectionChanged += OdooDirectoryTree_SelectionChanged;
		OdooDirectoryTree.Expanding += ( _, e ) => { if( !_isFilteringTree ) ViewModel.SetExpanded( e.Node?.LinkedData.FullPath, true ); };
		OdooDirectoryTree.Collapsed += ( _, e ) => { if( !_isFilteringTree ) ViewModel.SetExpanded( e.Node?.LinkedData.FullPath, false ); };
		OdooDirectoryTree.RightTapped += OdooDirectoryTree_RightTapped;
		OdooDirectoryTree.LostFocus += ( _, _ ) => { if( OdooDirectoryTree.SelectedNode is { } node ) SelectDirectory( node ); };
		OdooDirectoryBreadcrumb.ItemClicked += OdooDirectoryBreadcrumb_ItemClicked;

		OdooEntryList.SelectionChanged += OdooEntryList_SelectionChanged;
		OdooEntryList.Sorting += OdooEntryList_Sorting;
		OdooEntryList.DoubleTapped += ( _, _ ) => ViewModel.OpenSelectedCommand.Execute( null );
		OdooEntryList.ContainerContentChanging += EntryTable_ContainerContentChanging;
		HackChangesList.ContainerContentChanging += EntryTable_ContainerContentChanging;
		OdooChangesList.ContainerContentChanging += EntryTable_ContainerContentChanging;
		HackChangesList.DoubleTapped += ( _, _ ) => FocusEntryFromChanges( HackChangesList.SelectedItem as EntryRow );
		OdooChangesList.DoubleTapped += ( _, _ ) => FocusEntryFromChanges( OdooChangesList.SelectedItem as EntryRow );

		GroupedEntryList.ItemsSource = _groupedItems;
		GroupedEntryList.SelectionChanged += GroupedEntryList_SelectionChanged;
		GroupedEntryList.ItemClick += GroupedEntryList_ItemClick;
		GroupedEntryList.DoubleTapped += ( _, _ ) => { if( ViewModel.SelectedEntries.Count > 0 ) ViewModel.OpenSelectedCommand.Execute( null ); };
		GroupedEntryList.RightTapped += GroupedEntryList_RightTapped;

		OdooHistory.SelectionChanged += OdooHistory_SelectionChanged;
		OdooHistory.DoubleTapped += ( _, _ ) => ViewModel.DownloadHistoryWithDependenciesCommand.Execute( null );
		OdooParents.SelectionChanged += ( _, e ) => PreviewVersion( ( e.AddedItems.FirstOrDefault() as ParentRow )?.Version );
		OdooParents.DoubleTapped += OdooParents_DoubleTapped;
		OdooChildren.SelectionChanged += ( _, e ) => PreviewVersion( ( e.AddedItems.FirstOrDefault() as ChildrenRow )?.Version );
		OdooChildren.DoubleTapped += OdooChildren_DoubleTapped;
		KeyDown += HackFileManagerView_KeyDown;

		EntryOpen.DoubleTapped += ( _, _ ) => ViewModel.OpenSelectedCommand.Execute( null );
		EntryDelete.DoubleTapped += ( _, _ ) => ViewModel.LocalDeleteSelectedCommand.Execute( null );
		TreeDownload.DoubleTapped += ( _, _ ) => ViewModel.GetLatestDirectoryCommand.Execute( null );
		TreeUndelete.DoubleTapped += ( _, _ ) => ViewModel.RestoreDirectoryCommand.Execute( null );
		HistoryDownload.DoubleTapped += ( _, _ ) => ViewModel.DownloadHistoryCommand.Execute( null );
		HistoryMove.DoubleTapped += ( _, _ ) => ViewModel.MoveHistoryToTempCommand.Execute( null );

		ViewModel.IsTreeLoading = true;
		var pendingLocate = ViewModel.Activate();
		_ = LoadTreeAfterLayoutAsync( pendingLocate );
	}

	private async Task LoadTreeAfterLayoutAsync( (IReadOnlyList<string> Segments, string? FileName)? pendingLocate )
	{
		await Task.Delay( ViewModel.HasKeptTree ? 16 : 30 );
		if( _left )
			return;
		await ViewModel.LoadDirectoryTreeAsync();
		if( pendingLocate is var (segments, fileName) && !_left )
			await OnLocateRequestedAsync( segments, fileName );
	}

	private void OnRefreshRequested( FileManagerRefresh what )
	{
		if( what.HasFlag( FileManagerRefresh.Tree ) )
			_ = ViewModel.LoadDirectoryTreeAsync();
		if( what.HasFlag( FileManagerRefresh.Entries ) )
			_ = ReloadEntriesAsync();
	}

	private Task OnTreeLoadedAsync( DirectoryNode root )
	{
		_fullTreeRoot = root;
		_totalTreeFolderCount = root.CountRecursive();
		RebuildDisplayedTree( FolderFilterBox.Text );
		return Task.CompletedTask;
	}

	private void RebuildDisplayedTree( string? filterText )
	{
		if( _fullTreeRoot is null )
			return;

		string query = filterText?.Trim() ?? "";
		_isFilteringTree = query.Length > 0;
		DirectoryNode? displayRoot = _isFilteringTree ? _fullTreeRoot.Filter( query ) : _fullTreeRoot;
		if( displayRoot is null )
		{
			OdooDirectoryTree.RootNodes.Clear();
			EmptyTreeFilterPanel.Visibility = Visibility.Visible;
			TreeSummaryText.Text = $"0 / {_totalTreeFolderCount} folders matching \"{query}\"";
			return;
		}

		EmptyTreeFilterPanel.Visibility = Visibility.Collapsed;
		DirectoryTreeBuilder.Build( OdooDirectoryTree, displayRoot );

		if( _isFilteringTree )
		{
			OdooDirectoryTree.SetAllExpanded( true );
		}
		else if( ViewModel.ExpandedPaths.Count == 0 && OdooDirectoryTree.RootNodes.FirstOrDefault() is { } firstRoot )
		{
			firstRoot.LinkedData.IsExpanded = true;
			ViewModel.SetExpanded( firstRoot.LinkedData.FullPath, true );
		}
		else
		{
			foreach( string path in ViewModel.ExpandedPaths.ToList() )
				if( OdooDirectoryTree.FindTreeNode( path ) is { Node: { } open } )
					open.LinkedData.IsExpanded = true;
		}

		if( OdooDirectoryTree.FindTreeNode( ViewModel.LastSelectedNodePath ?? "" ) is { Node: { } node } data )
		{
			data.EnsureVisible( OdooDirectoryTree );
			OdooDirectoryTree.SelectedNode = node;
			SelectDirectory( node );
		}

		int shownCount = displayRoot.CountRecursive();
		TreeSummaryText.Text = _isFilteringTree
			? $"{shownCount} / {_totalTreeFolderCount} folders matching \"{query}\""
			: $"{_totalTreeFolderCount} folder{( _totalTreeFolderCount == 1 ? "" : "s" )}";
	}

	private async Task ReloadEntriesAsync()
	{
		if( OdooDirectoryTree.SelectedNode is null )
			return;
		await LoadSelectedEntriesAsync();
		if( IsUsingGroupedList )
		{
			if( GroupedEntryList.SelectedItems.OfType<EntryRow>().FirstOrDefault() is { } groupedEntry )
				GroupedEntryList.ScrollIntoView( groupedEntry );
		}
		else if( OdooEntryList.SelectedItems is { Count: > 0 } items && items[ 0 ] is EntryRow entry )
		{
			OdooEntryList.ScrollIntoView( entry );
		}
	}

	private void OnGroupingChanged()
	{
		EntryListGrouping.Apply( OdooEntryList, ViewModel.Entries, ViewModel.ActiveGroupKey );
#if !WINDOWS
		bool showGrouped = ViewModel.ActiveGroupKey is not null;
		OdooEntryList.Visibility = showGrouped ? Visibility.Collapsed : Visibility.Visible;
		GroupedEntryContainer.Visibility = showGrouped ? Visibility.Visible : Visibility.Collapsed;
		if( showGrouped )
		{
			GroupedByLabel.Text = $"Grouped by {ViewModel.ActiveGroupFieldName}";
			RebuildGroupedList();
		}
#endif
	}

	private void RebuildGroupedList()
	{
		if( ViewModel.ActiveGroupKey is not { } groupKey )
		{
			_groups.Clear();
			_groupedItems.Clear();
			return;
		}
		_groups = EntryListGrouping.BuildGroups( ViewModel.Entries, groupKey, _collapsedGroupKeys.Count > 0 ? _collapsedGroupKeys : null );
		RefreshGroupedFlatList();
	}

	private void RefreshGroupedFlatList()
	{
		HashSet<EntryRow> previous = [ .. ViewModel.SelectedEntries ];
		_syncingGroupedSelection = true;
		try
		{
			_groupedItems.ReplaceAll( EntryListGrouping.FlattenVisible( _groups ) );
			GroupedEntryList.SelectedItems.Clear();
			if( previous.Count > 0 )
			{
				foreach( object item in _groupedItems )
					if( item is EntryRow row && previous.Contains( row ) )
						GroupedEntryList.SelectedItems.Add( row );
			}
		}
		finally
		{
			_syncingGroupedSelection = false;
		}
	}

	private void GroupedEntryList_ItemClick( object sender, ItemClickEventArgs e )
	{
		if( e.ClickedItem is not EntryGroupHeader header )
			return;
		header.IsExpanded = !header.IsExpanded;
		if( header.IsExpanded )
			_collapsedGroupKeys.Remove( header.DisplayKey );
		else
			_collapsedGroupKeys.Add( header.DisplayKey );
		RefreshGroupedFlatList();
	}

	private void GroupedEntryList_SelectionChanged( object sender, SelectionChangedEventArgs e )
	{
		if( _syncingGroupedSelection )
			return;
		List<EntryGroupHeader> headers = [ .. GroupedEntryList.SelectedItems.OfType<EntryGroupHeader>() ];
		if( headers.Count > 0 )
		{
			_syncingGroupedSelection = true;
			try
			{
				foreach( EntryGroupHeader header in headers )
					GroupedEntryList.SelectedItems.Remove( header );
			}
			finally
			{
				_syncingGroupedSelection = false;
			}
		}
		ViewModel.SelectedEntries = [ .. GroupedEntryList.SelectedItems.OfType<EntryRow>() ];
		if( ViewModel.SelectedEntries is [ var entry ] )
			_ = ViewModel.ShowEntryDetailsAsync( entry );
	}

	private void GroupedEntryList_RightTapped( object sender, RightTappedRoutedEventArgs e )
	{
		if( ( e.OriginalSource as FrameworkElement )?.DataContext is EntryRow row && !GroupedEntryList.SelectedItems.Contains( row ) )
		{
			GroupedEntryList.SelectedItems.Clear();
			GroupedEntryList.SelectedItems.Add( row );
		}
		OdooEntryList.ContextFlyout?.ShowAt( GroupedEntryList, new FlyoutShowOptions { Position = e.GetPosition( GroupedEntryList ) } );
	}

	private void ExpandAllGroups_Click( object sender, RoutedEventArgs e )
	{
		_collapsedGroupKeys.Clear();
		foreach( EntryGroupHeader group in _groups )
			group.IsExpanded = true;
		RefreshGroupedFlatList();
	}

	private void CollapseAllGroups_Click( object sender, RoutedEventArgs e )
	{
		foreach( EntryGroupHeader group in _groups )
		{
			group.IsExpanded = false;
			_collapsedGroupKeys.Add( group.DisplayKey );
		}
		RefreshGroupedFlatList();
	}

	private void GroupedSortColumn_Click( object sender, RoutedEventArgs e )
	{
		if( sender is not FrameworkElement { Tag: string field } )
			return;
		bool descending = ViewModel.EntrySort is var (currentField, currentDesc)
			&& string.Equals( currentField, field, StringComparison.Ordinal )
			&& !currentDesc;
		ViewModel.SortEntries( field, descending );
	}

	private void SelectEntryRows( IReadOnlyList<EntryRow> rows, bool focus = false, bool scroll = true )
	{
		if( rows.Count == 0 )
			return;
		if( IsUsingGroupedList )
		{
			bool expandedAny = false;
			foreach( EntryGroupHeader group in _groups )
			{
				if( !group.IsExpanded && group.Items.Any( rows.Contains ) )
				{
					group.IsExpanded = true;
					_collapsedGroupKeys.Remove( group.DisplayKey );
					expandedAny = true;
				}
			}
			if( expandedAny )
				RefreshGroupedFlatList();

			GroupedEntryList.SelectedItems.Clear();
			foreach( EntryRow row in rows )
				if( _groupedItems.Contains( row ) )
					GroupedEntryList.SelectedItems.Add( row );
			if( focus )
				GroupedEntryList.Focus( FocusState.Programmatic );
			if( scroll )
				GroupedEntryList.ScrollIntoView( rows[ 0 ] );
			return;
		}

		OdooEntryList.SelectedItems.Clear();
		if( rows.Count == 1 )
			OdooEntryList.SelectedItem = rows[ 0 ];
		else
			foreach( EntryRow row in rows )
				OdooEntryList.SelectedItems.Add( row );
		if( focus )
			OdooEntryList.Focus( FocusState.Programmatic );
		if( scroll )
			OdooEntryList.ScrollIntoView( rows[ 0 ] );
	}

	private void EntryTable_ContainerContentChanging( ListViewBase sender, ContainerContentChangingEventArgs args )
	{
		if( args.ItemContainer is not Control container )
			return;
		if( args.InRecycleQueue )
		{
			container.ClearValue( Control.BackgroundProperty );
			return;
		}
		if( args.Item is EntryRow entry && StatusBrushes.ForTableRow( entry.Status ) is { } brush )
			container.Background = brush;
		else
			container.ClearValue( Control.BackgroundProperty );
	}

	private void OnViewModelPropertyChanged( object? sender, PropertyChangedEventArgs e )
	{
		if( e.PropertyName == nameof( HackFileManagerViewModel.Preview ) )
			_ = ShowPreviewAsync( ViewModel.Preview );
	}

	private async Task ShowPreviewAsync( EntryPreview? preview )
	{
		int request = ++_previewRequest;
		ImageSource? image = null;
		try
		{
			image = preview switch
			{
				{ ImageBytes: { Length: > 0 } bytes } => await _previewLoader.FromBytesAsync( bytes ),
				{ LocalFilePath: { } path } => await _previewLoader.FromFileIconAsync( path ),
				_ => null,
			};
		}
		catch( Exception ex )
		{
			HackLogger.Warn( "FileManager", $"Preview failed to load: {ex.Message}", ex );
		}
		if( request == _previewRequest )
		{
			OdooEntryImage.Source = image;
			NoPreviewPanel.Visibility = image is null ? Visibility.Visible : Visibility.Collapsed;
		}
	}

	private void OnRowReplaced( EntryRow? old, EntryRow? row )
	{
		if( IsUsingGroupedList )
			RebuildGroupedList();
		if( old is null )
			return;

		IReadOnlyList<EntryRow> selected = ViewModel.SelectedEntries;
		if( selected.Count == 0 || !selected.Contains( old ) )
			return;

		if( row is null )
		{
			List<EntryRow> remaining = [ .. selected.Where( r => !ReferenceEquals( r, old ) && ViewModel.Entries.Contains( r ) ) ];
			ViewModel.SelectedEntries = remaining;
			if( remaining.Count == 0 )
				_ = ViewModel.ShowEntryDetailsAsync( null );
			else
				SelectEntryRows( remaining, scroll: false );
			return;
		}

		List<EntryRow> updated = [ .. selected.Select( r => ReferenceEquals( r, old ) ? row : r ).Where( ViewModel.Entries.Contains ) ];
		SelectEntryRows( updated.Count > 0 ? updated : [ row ], scroll: false );
	}

	private void OnFolderChanged( string treePath, LocalChangeKind kind )
	{
		if( VisibleNodeFor( treePath ) is not { } node
			|| OdooDirectoryTree.ContainerFromNode( node ) is not TreeViewItem { Content: Panel panel } )
			return;

		RemoveBadges( panel );
		var (text, color) = kind switch
		{
			LocalChangeKind.Created => ( "( + ) created", Color.FromArgb( 255, 0x2E, 0x9E, 0x44 ) ),
			LocalChangeKind.Deleted => ( "( - ) deleted", Color.FromArgb( 255, 0xD1, 0x34, 0x38 ) ),
			_ => ( "( . ) updated", Color.FromArgb( 255, 0xC7, 0x84, 0x00 ) ),
		};
		TextBlock badge = new()
		{
			Text = text,
			Tag = ChangeBadgeTag,
			FontSize = 10,
			Margin = new Thickness( 8, 0, 0, 0 ),
			VerticalAlignment = VerticalAlignment.Center,
			Foreground = new SolidColorBrush( color ),
			Opacity = 0,
		};
		panel.Children.Add( badge );

		DoubleAnimationUsingKeyFrames fade = new();
		fade.KeyFrames.Add( new LinearDoubleKeyFrame { KeyTime = TimeSpan.FromMilliseconds( 200 ), Value = 1 } );
		fade.KeyFrames.Add( new LinearDoubleKeyFrame { KeyTime = TimeSpan.FromMilliseconds( 2400 ), Value = 1 } );
		fade.KeyFrames.Add( new LinearDoubleKeyFrame { KeyTime = TimeSpan.FromMilliseconds( 3200 ), Value = 0 } );
		Storyboard.SetTarget( fade, badge );
		Storyboard.SetTargetProperty( fade, nameof( Opacity ) );
		Storyboard story = new();
		story.Children.Add( fade );
		story.Completed += ( _, _ ) => panel.Children.Remove( badge );
		story.Begin();
	}

	private TreeViewNode? VisibleNodeFor( string treePath )
	{
		if( OdooDirectoryTree.RootNodes.FirstOrDefault() is not { } node )
			return null;
		foreach( string part in PwaPaths.Segments( treePath ) )
		{
			if( !node.IsExpanded )
				break;
			TreeViewNode? next = node.Children.FirstOrDefault( n => string.Equals( n.LinkedData.Name, part, StringComparison.OrdinalIgnoreCase ) );
			if( next is null )
				break;
			node = next;
		}
		return node;
	}

	private static void RemoveBadges( Panel panel )
	{
		foreach( TextBlock old in panel.Children.OfType<TextBlock>().Where( t => ChangeBadgeTag.Equals( t.Tag ) ).ToList() )
			panel.Children.Remove( old );
	}

	private void SelectDirectory( TreeViewNode? node )
	{
		LastSelectedNode = node;
		if( _selectedTreeData is not null && !ReferenceEquals( _selectedTreeData.Node, node ) )
			_selectedTreeData.IsSelectedInTree = false;
		_selectedTreeData = node?.LinkedData;
		if( _selectedTreeData is not null )
			_selectedTreeData.IsSelectedInTree = true;

		ViewModel.SelectedDirectory = node?.LinkedData is { } data
			? new DirectoryRef( data.Name ?? "", data.DirectoryId ?? 0, data.FullPath ?? data.Name ?? "", data.IsRemoteOnly is true )
			: null;
		node?.UpdateBreadCrumbCollection( BreadcrumbPath );
		UpdateBreadcrumbBarControls( node );
	}

	private void UpdateBreadcrumbBarControls( TreeViewNode? node )
	{
		NavigateUpButton.IsEnabled = node?.Parent is { Depth: >= 0 };
		CopyPathButton.IsEnabled = node is not null;

		int childCount = node?.Children.Count ?? 0;
		SubfolderMenuLabel.Text = $"{childCount} subfolder{( childCount == 1 ? "" : "s" )}";
		SubfolderMenuButton.Visibility = childCount > 0 ? Visibility.Visible : Visibility.Collapsed;

		if( node?.LinkedData is { } data )
		{
			CurrentFolderOriginText.Text = data.OriginLabel;
			CurrentFolderOriginDot.Fill = data.Origin switch
			{
				FolderOriginKind.LocalOnly => LocalDotBrush,
				FolderOriginKind.RemoteOnly => RemoteDotBrush,
				_ => SyncedDotBrush,
			};
			CurrentFolderOriginPill.Visibility = Visibility.Visible;
		}
		else
		{
			CurrentFolderOriginPill.Visibility = Visibility.Collapsed;
		}
	}

	private void ToggleFolderPane_Click( object sender, RoutedEventArgs e )
	{
		bool show = ToggleFolderPaneButton.IsChecked == true;
		if( !show && FoldersColumn.ActualWidth > 0 )
			_savedFoldersWidth = FoldersColumn.ActualWidth;
		FoldersPaneCard.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
		FoldersSplitter.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
		FoldersColumn.Width = show ? new GridLength( _savedFoldersWidth ) : new GridLength( 0 );
	}

	private void FoldersSplitter_DragDelta( double delta )
		=> FoldersColumn.Width = new GridLength( Math.Clamp( FoldersColumn.ActualWidth + delta, 180, 520 ) );

	private void InspectorSplitter_DragDelta( double delta )
		=> InspectorRow.Height = new GridLength( Math.Clamp( InspectorRow.ActualHeight - delta, 140, 520 ) );

	private void PreviewSplitter_DragDelta( double delta )
		=> PreviewColumn.Width = new GridLength( Math.Clamp( PreviewColumn.ActualWidth - delta, 180, 560 ) );

	private void ExpandAllFolders_Click( object sender, RoutedEventArgs e )
	{
		OdooDirectoryTree.SetAllExpanded( true );
		if( !_isFilteringTree )
			RecordExpandedState();
	}

	private void CollapseAllFolders_Click( object sender, RoutedEventArgs e )
	{
		OdooDirectoryTree.SetAllExpanded( false );
		if( !_isFilteringTree )
			RecordExpandedState();
	}

	private void RecordExpandedState()
	{
		foreach( TreeViewNode root in OdooDirectoryTree.RootNodes )
			RecordExpandedRecursive( root );
	}

	private void RecordExpandedRecursive( TreeViewNode node )
	{
		ViewModel.SetExpanded( node.LinkedData.FullPath, node.IsExpanded );
		foreach( TreeViewNode child in node.Children )
			RecordExpandedRecursive( child );
	}

	private void FolderFilterBox_TextChanged( object sender, TextChangedEventArgs e )
	{
		ClearFilterButton.Visibility = string.IsNullOrWhiteSpace( FolderFilterBox.Text ) ? Visibility.Collapsed : Visibility.Visible;
		RebuildDisplayedTree( FolderFilterBox.Text );
	}

	private void ClearFilterButton_Click( object sender, RoutedEventArgs e )
	{
		FolderFilterBox.Text = string.Empty;
		ClearFilterButton.Visibility = Visibility.Collapsed;
	}

	private void NavigateUp_Click( object sender, RoutedEventArgs e )
	{
		if( LastSelectedNode?.Parent is { Depth: >= 0 } parent )
			JumpToTreeNode( parent );
	}

	private void SubfolderMenuButton_Click( object sender, RoutedEventArgs e )
	{
		if( LastSelectedNode is not { Children.Count: > 0 } node )
			return;
		MenuFlyout flyout = new();
		foreach( TreeViewNode child in node.Children )
		{
			TreeData data = child.LinkedData;
			string suffix = data.Origin == FolderOriginKind.Synced ? "" : $"  [{data.OriginLabel}]";
			MenuFlyoutItem item = new() { Text = $"{data.Name}{suffix}", Icon = new FontIcon { Glyph = "\uE8B7" } };
			TreeViewNode captured = child;
			item.Click += ( _, _ ) => JumpToTreeNode( captured );
			flyout.Items.Add( item );
		}
		flyout.ShowAt( SubfolderMenuButton );
	}

	private async void CopyPath_Click( object sender, RoutedEventArgs e )
	{
		string? path = ViewModel.LastSelectedNodePath ?? LastSelectedNode?.LinkedData.FullPath;
		if( string.IsNullOrWhiteSpace( path ) )
			return;
		try
		{
			var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
			package.SetText( path );
			Windows.ApplicationModel.DataTransfer.Clipboard.SetContent( package );
		}
		catch { }

		int version = ++_copyFeedbackVersion;
		CopyPathIcon.Glyph = "\uE8FB";
		CopyPathLabel.Text = "Copied";
		await Task.Delay( 1400 );
		if( _left || version != _copyFeedbackVersion )
			return;
		CopyPathIcon.Glyph = "\uE8C8";
		CopyPathLabel.Text = "Copy path";
	}

	private void JumpToTreeNode( TreeViewNode node )
	{
		node.LinkedData.EnsureVisible( OdooDirectoryTree );
		OdooDirectoryTree.SelectedNode = node;
		SelectDirectory( node );
	}

	private async void OdooDirectoryTree_SelectionChanged( TreeView sender, TreeViewSelectionChangedEventArgs args )
	{
		if( args.AddedItems.FirstOrDefault() is not TreeData { Node: { } node } data || node == _loadingNode )
			return;
		SelectDirectory( node );
		data.EnsureVisible( sender );
		await LoadSelectedEntriesAsync();
	}

	private Task LoadSelectedEntriesAsync()
	{
		_loadingNode = LastSelectedNode;
		_treeSelectionCancellation.Cancel();
		_treeSelectionCancellation = new();
		ViewModel.IsListLoaded = false;
		return LoadAndRestoreAsync( _treeSelectionCancellation.Token );
	}

	private async Task LoadAndRestoreAsync( CancellationToken token )
	{
		await ViewModel.LoadEntriesAsync( token );
		if( !_restorePending || token.IsCancellationRequested || _left )
			return;
		_restorePending = false;
		RestoreSortArrow();
		IReadOnlyList<EntryRow> rows = ViewModel.TakeSelectionToRestore();
		if( rows.Count > 0 )
			SelectEntryRows( rows );
	}

	private void RestoreSortArrow()
	{
		if( ViewModel.EntrySort is not var (field, descending) )
			return;
		foreach( TableViewColumn column in OdooEntryList.Columns )
		{
			column.SortDirection = column.SortMemberPath == field
				? ( descending ? SortDirection.Descending : SortDirection.Ascending )
				: null;
		}
	}

	private void OdooDirectoryTree_RightTapped( object sender, RightTappedRoutedEventArgs e )
	{
		if( ( e.OriginalSource as FrameworkElement )?.DataContext is not TreeData { Node: { } node } )
			return;
		OdooDirectoryTree.SelectedNode = node;
		SelectDirectory( node );
	}

	private void OdooDirectoryBreadcrumb_ItemClicked( BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs args )
	{
		if( args.Item is TreeData { Node: { } node } )
			JumpToTreeNode( node );
	}

	private void TreeViewItem_Loaded( object sender, RoutedEventArgs e )
	{
		if( sender is TreeViewItem { Content: Panel panel } )
			RemoveBadges( panel );
	}

	private void OdooEntryList_SelectionChanged( object sender, SelectionChangedEventArgs e )
	{
		ViewModel.SelectedEntries = [ .. OdooEntryList.SelectedItems.OfType<EntryRow>() ];
		if( ViewModel.SelectedEntries is [ var entry ] )
			_ = ViewModel.ShowEntryDetailsAsync( entry );
	}

	private void VersionTabs_SelectionChanged( object sender, SelectionChangedEventArgs e )
	{
		ViewModel.SelectedDetailTab = ( VersionTabs.SelectedItem as TabViewItem )?.Name;
		EntryRow? selected = IsUsingGroupedList
			? GroupedEntryList.SelectedItems.OfType<EntryRow>().FirstOrDefault()
			: OdooEntryList.SelectedItem as EntryRow;
		if( e.AddedItems.Count > 0 && selected is not null )
			_ = ViewModel.ShowEntryDetailsAsync( selected );
	}

	private void OdooEntryList_Sorting( object? sender, TableViewSortingEventArgs e )
	{
		e.Handled = true;
		foreach( TableViewColumn column in OdooEntryList.Columns.Where( c => c != e.Column ) )
			column.SortDirection = null;

		bool descending = e.Column.SortDirection is SortDirection.Ascending;
		e.Column.SortDirection = descending ? SortDirection.Descending : SortDirection.Ascending;
		string? field = e.Column.SortMemberPath
			?? ( e.Column as TableViewBoundColumn )?.Binding?.Path?.Path
			?? e.Column.Header?.ToString();
		ViewModel.SortEntries( field, descending );
	}

	private void OdooHistory_SelectionChanged( object sender, SelectionChangedEventArgs e )
	{
		HistoryRow? row = e.AddedItems.FirstOrDefault() as HistoryRow;
		ViewModel.SelectedHistory = row;
		PreviewVersion( row?.Version );
	}

	private void PreviewVersion( int? versionId ) => _ = ViewModel.PreviewVersionAsync( versionId );

	private async void OdooParents_DoubleTapped( object sender, DoubleTappedRoutedEventArgs e )
	{
		if( OdooParents.SelectedItems is [ ParentRow row ] )
			await OnLocateRequestedAsync( PwaPaths.Segments( row.BasePath ?? "" ), row.Name ?? "" );
	}

	private async void OdooChildren_DoubleTapped( object sender, DoubleTappedRoutedEventArgs e )
	{
		if( OdooChildren.SelectedItems is [ ChildrenRow row ] )
			await OnLocateRequestedAsync( PwaPaths.Segments( row.BasePath ?? "" ), row.Name ?? "" );
	}

	private async Task OnLocateRequestedAsync( IReadOnlyList<string> segmentsBelowRoot, string? fileName )
	{
		try
		{
			await AsyncHelper.WaitUntil( () => ViewModel.IsTreeLoaded && OdooDirectoryTree.RootNodes.Count > 0, 100, 30_000 );
			TreeViewNode node = OdooDirectoryTree.RootNodes[ 0 ];
			foreach( string part in segmentsBelowRoot.Where( p => p.Length > 0 ) )
			{
				node = node.Children.FirstOrDefault( n => n.LinkedData.Name == part )
					?? throw new ArgumentException( $"directory '{part}' not found", nameof( segmentsBelowRoot ) );
			}

			foreach( TreeViewNode root in OdooDirectoryTree.RootNodes )
				root.IsExpanded = false;
			node.LinkedData.EnsureVisible( OdooDirectoryTree );
			OdooDirectoryTree.SelectedNode = node;
			SelectDirectory( node );

			await AsyncHelper.WaitUntil( () => ViewModel.IsListLoaded, 100, 60_000 );
			if( fileName is null )
				return;

			EntryRow entry = ViewModel.Entries.FirstOrDefault( e => e.Name == fileName )
				?? throw new ArgumentException( "entry doesn't exist", nameof( fileName ) );
			SelectEntryRows( [ entry ], focus: true, scroll: true );
		}
		catch( Exception ex )
		{
			HackLogger.Warn( "FileManager", $"Unable to find search selection '{fileName}': {ex.Message}", ex );
		}
	}

	private void Anchor_Checked( object sender, RoutedEventArgs e )
	{
		if( HackApp.Window?.AppWindow.Presenter is OverlappedPresenter presenter )
			presenter.IsAlwaysOnTop = Anchor.IsChecked ?? false;
	}

	private void Refresh_Click( object sender, RoutedEventArgs e ) => ViewModel.RefreshFromServer();
	private void Search_Click( object sender, RoutedEventArgs e ) => AppComposition.Dialogs.ShowSearchWindow();
	private void StatusWindow_Click( object sender, RoutedEventArgs e ) => AppComposition.Dialogs.ShowStatusWindow();

	private void FocusEntryFromChanges( EntryRow? entry )
	{
		if( entry is null )
			return;
		ListTabs.SelectedItem = RootEntryList;
		SelectEntryRows( [ entry ], focus: true, scroll: true );
	}

	private void HackFileManagerView_KeyDown( object sender, KeyRoutedEventArgs e )
	{
		bool ctrl = InputKeyboardSource.GetKeyStateForCurrentThread( VirtualKey.Control ).HasFlag( CoreVirtualKeyStates.Down );
		bool alt = InputKeyboardSource.GetKeyStateForCurrentThread( VirtualKey.Menu ).HasFlag( CoreVirtualKeyStates.Down );

		if( e.Key == VirtualKey.F5 )
		{
			e.Handled = true;
			ViewModel.RefreshFromServer();
		}
		else if( e.Key == VirtualKey.F && ctrl )
		{
			e.Handled = true;
			AppComposition.Dialogs.ShowSearchWindow();
		}
		else if( e.Key == VirtualKey.Up && alt && LastSelectedNode?.Parent is { Depth: >= 0 } parent )
		{
			e.Handled = true;
			JumpToTreeNode( parent );
		}
	}
}

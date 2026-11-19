using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using HackPDM.Core;
using HackPDM.Core.Graph;
using HackPDM.Core.Theming;
using HackPDM.Core.Hack;
using HackPDM.Infrastructure.Odoo;
using HackPDM.Infrastructure.Odoo.Models;
using HackPDM.Infrastructure.Operations;
using HackPDM.Domain.Representation;
using HackPDM.Mvvm.Models.Common;
using HackPDM.Mvvm.Models.Hack;
using HackPDM.Mvvm.Views.Common.Controls;
using HackPDM.Mvvm.Views.Common.Settings;
using HackPDM.Shared.GlobalData;
using HackPDM.Shell;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

using Windows.System;

using DetailRow = HackPDM.Mvvm.Models.Hack.EntryRow;
using TreeData = HackPDM.Mvvm.Views.Common.Types.TreeData;

namespace HackPDM.Mvvm.Views.Hack;

/// <summary>A file in the dependency page's list.</summary>
/// <param name="Id">The hp.entry id.</param>
/// <param name="Name">The file name with its extension.</param>
public sealed record GraphEntryItem( int Id, string Name )
{
	public override string ToString() => Name;
}

/// <summary>
/// Folders on the left, the files of the selected folder in the middle (several can be selected) and
/// the interactive node graph on the right: supports file dependencies (Children, Parents, Both, and
/// recursive Full Network showing each parent's and child's parents &amp; children), Folder Structure,
/// Folders &amp; Files, Group by Property, and Search / Filter views with independent depth, offspring/parent
/// branch count limits, and multi-criteria filtering.
/// </summary>
public sealed partial class DependencyGraphView : Page
{
	private readonly List<GraphEntryItem> _entries = [];
	private CancellationTokenSource? _entriesLoad;
	private CancellationTokenSource? _graphLoad;
	private bool _quietSelection;
	private bool _quietControls;

	/// <summary>Unfiltered graph loaded from Odoo for the current Dependencies query (enables instant local re-filtering).</summary>
	private DependencyGraph _rawGraph = DependencyGraph.Empty;
	private GraphDirection _rawGraphDirection = GraphDirection.Children;
	private int _rawGraphMaxDepth;
	private bool _rawGraphHasProperties;

	/// <summary>Currently displayed (filtered/shaped) graph on screen.</summary>
	private DependencyGraph _graph = DependencyGraph.Empty;
	private GraphFilterStats _stats = GraphFilterStats.Empty;
	private GraphFilterCriteria _criteria = GraphFilterCriteria.Default;

	/// <summary>The files the graph is drawn around: the list's selection, or a file double-clicked in the graph.</summary>
	private IReadOnlyList<int> _roots = [];
	private string? _overrideFolderPath;
	private int? _overrideDirectoryId;

	private readonly IEntryDetailsService _details = new EntryDetailsService();
	private readonly IPreviewImageLoader _previews = new PreviewImageLoader();
	private CancellationTokenSource? _detailLoad;
	private CancellationTokenSource? _previewLoad;
	/// <summary>The node whose details are open; null when the panel is closed.</summary>
	private GraphNode? _detailNode;
	private const double DetailPanelSpace = 360;

	// the file manager's detail tables, for the clicked file
	public ObservableCollection<TreeData> BreadcrumbPath { get; } = [];
	private TreeData? _selectedFolderData;
	public RangeObservableCollection<HistoryRow> HistoryRows { get; } = [];
	public RangeObservableCollection<ParentRow> ParentRows { get; } = [];
	public RangeObservableCollection<ChildrenRow> ChildRows { get; } = [];
	public RangeObservableCollection<PropertiesRow> PropertyRows { get; } = [];
	public RangeObservableCollection<VersionRow> VersionRows { get; } = [];

	public DependencyGraphView()
	{
		InitializeComponent();
		Graph.Theme = AppComposition.Theme.GraphTheme;
		GraphBranchLinesToggle.IsChecked = AppComposition.Theme.ShowTreeBranchLines;
		ApplyGraphPaneLayout();
		AppComposition.Theme.ThemeChanged += OnThemeChanged;
		Unloaded += ( _, _ ) => AppComposition.Theme.ThemeChanged -= OnThemeChanged;

		FolderTree.SelectionChanged += ( _, _ ) =>
		{
			_overrideFolderPath = null;
			_overrideDirectoryId = null;
			SyncFolderSelection();
			_ = OnFolderChangedAsync();
		};
		FolderTree.Expanding += ( _, e ) =>
		{
			e.Node?.LinkedData.NotifyBranchChanged();
			TreeBranchGuides.RefreshTree( FolderTree, selectedNode: FolderTree.SelectedNode, updateSelection: true );
		};
		FolderTree.Collapsed += ( _, e ) =>
		{
			e.Node?.LinkedData.NotifyBranchChanged();
			TreeBranchGuides.RefreshTree( FolderTree, selectedNode: FolderTree.SelectedNode, updateSelection: true );
		};
		FolderBreadcrumb.ItemClicked += FolderBreadcrumb_ItemClicked;
		EntryList.SelectionChanged += ( _, _ ) => OnEntrySelectionChanged();
		SelectAll.Checked += ( _, _ ) => SetAllSelected( true );
		SelectAll.Unchecked += ( _, _ ) => SetAllSelected( false );

		ViewModeCombo.SelectionChanged += ( _, _ ) => OnViewModeChanged();
		ShowChildren.Checked += ( _, _ ) => OnDirectionRadioChanged();
		ShowParents.Checked += ( _, _ ) => OnDirectionRadioChanged();
		ShowBoth.Checked += ( _, _ ) => OnDirectionRadioChanged();
		ShowFullNetwork.Checked += ( _, _ ) => OnDirectionRadioChanged();

		ToggleFiltersBtn.Click += ( _, _ ) =>
			FilterBarCard.Visibility = ToggleFiltersBtn.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

		DepthCombo.SelectionChanged += ( _, _ ) => OnFilterControlChanged();
		FileTypePresetCombo.SelectionChanged += ( _, _ ) => OnFileTypePresetChanged();
		BridgeLinksCheck.Click += ( _, _ ) => OnFilterControlChanged( localOnlyPreferred: true );
		KeepRootsCheck.Click += ( _, _ ) => OnFilterControlChanged( localOnlyPreferred: true );
		DepLinksOverlayCheck.Click += ( _, _ ) => OnFilterControlChanged();
		CheckedOutFilterCheck.Click += ( _, _ ) => OnFilterControlChanged( localOnlyPreferred: true );
		ApplyFilterBtn.Click += ( _, _ ) => PendingLoad = ApplyCurrentFiltersOrReloadAsync();
		ResetFilterBtn.Click += ( _, _ ) => PendingLoad = ResetFiltersAsync();

		foreach( TextBox box in new[] { ChildDepthBox, ParentDepthBox, ChildBranchesBox, ParentBranchesBox, ExtensionsFilterBox, NameFilterBox, PathFilterBox, PropertyNameBox, PropertyValueBox } )
		{
			box.KeyDown += ( _, e ) =>
			{
				if( e.Key == VirtualKey.Enter )
				{
					e.Handled = true;
					PendingLoad = ApplyCurrentFiltersOrReloadAsync();
				}
			};
		}

		ZoomIn.Click += ( _, _ ) => Graph.ZoomBy( 1.25 );
		ZoomOut.Click += ( _, _ ) => Graph.ZoomBy( 0.8 );
		FitButton.Click += ( _, _ ) => Graph.Fit();
		Graph.ZoomChanged += zoom => ZoomText.Text = $"{zoom * 100:0}%";
		Graph.NodeSelected += node =>
		{
			ShowStatus();
			_ = ShowDetailsAsync( node );
		};
		Graph.NodeInvoked += Focus;
		DetailClose.Click += ( _, _ ) => CloseDetails();
		DetailTabs.SelectionChanged += ( _, _ ) => _ = LoadDetailTabAsync();
		InfoFocus.Click += ( _, _ ) =>
		{
			if( _detailNode is { } node )
				Focus( node );
		};
		InfoExpandNeighbors.Click += ( _, _ ) =>
		{
			if( _detailNode is { Role: GraphNodeRole.File, Id: > 0 } node )
				PendingLoad = ExpandNodeAsync( node.Id, GraphDirection.Both );
		};
		InfoLocate.Click += ( _, _ ) => _ = LocateAsync();

		KeyboardAccelerator escape = new() { Key = VirtualKey.Escape };
		escape.Invoked += ( _, e ) =>
		{
			if( DetailPanel.Visibility == Visibility.Visible )
			{
				e.Handled = true;
				CloseDetails();
			}
		};
		KeyboardAccelerators.Add( escape );
		UpdateModeUiChrome();
	}

	/// <summary>Which dependencies the switch asks for.</summary>
	public GraphDirection Direction => ShowParents.IsChecked == true ? GraphDirection.Parents
		: ShowBoth.IsChecked == true ? GraphDirection.Both
		: ShowFullNetwork.IsChecked == true ? GraphDirection.FullNetwork
		: GraphDirection.Children;

	/// <summary>Active node graph view mode.</summary>
	public GraphViewMode ViewMode
	{
		get
		{
			string? tag = ( ViewModeCombo.SelectedItem as ComboBoxItem )?.Tag?.ToString();
			return Enum.TryParse<GraphViewMode>( tag, ignoreCase: true, out GraphViewMode mode )
				? mode
				: GraphViewMode.Dependencies;
		}
	}

	/// <summary>The graph on screen (the smoke tests read it).</summary>
	internal NodeGraphView GraphView => Graph;

	/// <summary>The files listed for the selected folder.</summary>
	internal IReadOnlyList<GraphEntryItem> Entries => _entries;

	/// <summary>The folder tree (the smoke tests pick folders in it).</summary>
	internal TreeView Folders => FolderTree;

	/// <summary>Opens <paramref name="node"/>'s folder (the smoke tests).</summary>
	internal Task OpenFolderAsync( TreeViewNode node )
	{
		_overrideFolderPath = null;
		_overrideDirectoryId = null;
		node.LinkedData.EnsureVisible( FolderTree );
		FolderTree.SelectedNode = node;
		SyncFolderSelection();
		return PendingLoad = OnFolderChangedAsync();
	}

	/// <summary>Selects the listed files named <paramref name="names"/> (the smoke tests).</summary>
	internal void SelectEntries( params string[] names )
	{
		EntryList.SelectedItems.Clear();
		foreach( object item in EntryList.Items )
			if( item is GraphEntryItem e && names.Contains( e.Name, StringComparer.OrdinalIgnoreCase ) )
				EntryList.SelectedItems.Add( item );
	}

	/// <summary>Ticks or clears "Select all" (the smoke tests).</summary>
	internal void SetSelectAll( bool selected ) => SelectAll.IsChecked = selected;

	/// <summary>Flips the Children / Parents / Both / Full Network switch.</summary>
	internal void SetDirection( GraphDirection direction )
		=> ( direction switch
		{
			GraphDirection.Parents => ShowParents,
			GraphDirection.Both => ShowBoth,
			GraphDirection.FullNetwork => ShowFullNetwork,
			_ => ShowChildren,
		} ).IsChecked = true;

	/// <summary>Switches the active graph view mode.</summary>
	internal void SetViewMode( GraphViewMode mode )
	{
		string tag = mode.ToString();
		for( int i = 0; i < ViewModeCombo.Items.Count; i++ )
		{
			if( ViewModeCombo.Items[ i ] is ComboBoxItem item
				&& string.Equals( item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase ) )
			{
				ViewModeCombo.SelectedIndex = i;
				break;
			}
		}
	}

	/// <summary>The files the graph was last drawn around.</summary>
	internal IReadOnlyList<int> Roots => _graph.Roots;

	/// <summary>The load in progress (the smoke tests wait on it).</summary>
	internal Task? PendingLoad { get; private set; }

	internal DependencyGraph CurrentGraph => _graph;
	internal DependencyGraph RawGraph => _rawGraph;
	internal GraphFilterCriteria CurrentCriteria => _criteria;
	internal GraphFilterStats CurrentStats => _stats;
	internal GraphNode? CurrentDetailNode => _detailNode;
	internal string CurrentDetailTabHeader => ( DetailTabs.SelectedItem as TabViewItem )?.Header?.ToString() ?? string.Empty;

	internal bool TryShowDetailTab( string nameOrHeader )
	{
		TabViewItem? tab = DetailTabs.TabItems.OfType<TabViewItem>().FirstOrDefault( t =>
			string.Equals( t.Header?.ToString(), nameOrHeader, StringComparison.OrdinalIgnoreCase )
			|| string.Equals( t.Name, nameOrHeader, StringComparison.OrdinalIgnoreCase ) );
		if( tab is null )
			return false;
		DetailTabs.SelectedItem = tab;
		return true;
	}

	internal Task LocateCurrentNodeForMcp() => LocateAsync();

	/// <summary>Programmatically applies a complete view mode, depth, branch-limit, and filter criteria from MCP.</summary>
	internal async Task ApplyQueryForMcpAsync(
		GraphFilterCriteria criteria,
		IReadOnlyList<int>? entryIds = null,
		string? folderPath = null,
		int? directoryId = null,
		bool resetMissingFilters = false,
		CancellationToken cancellationToken = default )
	{
		_quietControls = true;
		try
		{
			_criteria = criteria;
			SyncControlsFromCriteria( criteria );
			if( entryIds is not null )
				_roots = [ .. entryIds.Where( id => id > 0 ).Distinct() ];
			if( folderPath is not null )
				_overrideFolderPath = folderPath;
			if( directoryId is not null )
				_overrideDirectoryId = directoryId;
			if( resetMissingFilters )
				_rawGraph = DependencyGraph.Empty;
			UpdateModeUiChrome();
		}
		finally
		{
			_quietControls = false;
		}

		PendingLoad = LoadGraphAsync( debounce: false, externalToken: cancellationToken );
		await PendingLoad;
	}

	/// <summary>Expands a single node's parents and/or children live inside the current graph (GUI + MCP).</summary>
	internal Task ExpandNodeForMcpAsync( int nodeId, GraphDirection direction, CancellationToken cancellationToken = default )
	{
		PendingLoad = ExpandNodeAsync( nodeId, direction, cancellationToken );
		return PendingLoad;
	}

	/// <summary>Resets all graph filters, depth limits, and branch caps back to defaults (GUI + MCP).</summary>
	internal Task ResetFiltersForMcpAsync( CancellationToken cancellationToken = default )
	{
		PendingLoad = ResetFiltersAsync( cancellationToken );
		return PendingLoad;
	}

	protected override void OnNavigatedTo( NavigationEventArgs e )
	{
		base.OnNavigatedTo( e );
		Loaded += OnLoaded;
	}

	private void OnLoaded( object sender, RoutedEventArgs e )
	{
		Loaded -= OnLoaded;
		// as in the file manager: the items are nodes (DirectoryTreeBuilder), but Uno binds the item
		// template to them only when the tree has an (empty) items source, set once the page is loaded;
		// the nodes are added after the first layout or the folders opened below stay closed
		FolderTree.ItemsSource = new ObservableCollection<TreeData>();
		PendingLoad = LoadTreeAfterLayoutAsync();
	}

	private async Task LoadTreeAfterLayoutAsync()
	{
		TreeLoadingOverlay.Visibility = Visibility.Visible;
		TreeBusyRing.IsActive = true;
		await Task.Delay( AppComposition.FileManagerState.Tree is null ? 30 : 16 );
		await LoadTreeAsync();
	}

	protected override void OnNavigatedFrom( NavigationEventArgs e )
	{
		_detailLoad?.Cancel();
		_entriesLoad?.Cancel();
		_graphLoad?.Cancel();
		base.OnNavigatedFrom( e );
	}

	// ------------------------------------------------------------------ folders and files

	private async Task LoadTreeAsync()
	{
		try
		{
			TreeLoadingOverlay.Visibility = Visibility.Visible;
			TreeBusyRing.IsActive = true;
			DirectoryNode? root = AppComposition.FileManagerState.Tree ?? await new DirectoryTreeService().LoadAsync();
			if( root is null )
			{
				Status.Text = "The folder tree could not be read from the server.";
				return;
			}
			AppComposition.FileManagerState.Tree ??= root;
			DirectoryTreeBuilder.Build( FolderTree, root );
			TreeViewNode first = FolderTree.RootNodes[ 0 ];
			first.LinkedData.IsExpanded = true;
			// start in the folder the file manager has open
			string? path = AppComposition.FileManagerState.SelectedPath;
			FolderTree.SelectedNode = ( path is null ? null : FolderTree.FindTreeNode( path )?.Node ) ?? first;
			if( FolderTree.SelectedNode is { } node )
				node.LinkedData.EnsureVisible( FolderTree );
			SyncFolderSelection();
			await LoadEntriesAsync();
		}
		finally
		{
			TreeBusyRing.IsActive = false;
			TreeLoadingOverlay.Visibility = Visibility.Collapsed;
		}
	}

	private async Task OnFolderChangedAsync()
	{
		await LoadEntriesAsync();
		if( ViewMode != GraphViewMode.Dependencies )
			await LoadGraphAsync();
	}

	private async Task LoadEntriesAsync()
	{
		_entriesLoad?.Cancel();
		CancellationTokenSource load = _entriesLoad = new();
		int? directoryId = FolderTree.SelectedNode?.LinkedData.DirectoryId;

		_quietSelection = true;
		_entries.Clear();
		_roots = [];
		EntryList.ItemsSource = null;
		SelectAll.IsChecked = false;
		_quietSelection = false;
		EntryCount.Text = "";
		if( directoryId is not int id || id == 0 )
		{
			EntryCount.Text = "This folder is not on the server.";
			return;
		}

		Busy.IsActive = true;
		EntryBusyBar.Visibility = Visibility.Visible;
		EntryBusyOverlay.Visibility = Visibility.Visible;
		EntryBusyRing.IsActive = true;
		try
		{
			Result<IReadOnlyList<RemoteEntryDto>> rows = await HpDirectory.GetEntriesAsync( id );
			if( load.IsCancellationRequested )
				return;
			if( !rows.Ok )
			{
				EntryCount.Text = $"The files could not be read: {rows.Error}";
				return;
			}
			_entries.AddRange( rows.Value!.Where( r => !r.Deleted ).OrderBy( r => r.Name, StringComparer.OrdinalIgnoreCase )
				.Select( r => new GraphEntryItem( r.Id, r.Name ) ) );
			EntryList.ItemsSource = _entries.ToList();
			EntryCount.Text = _entries.Count == 1 ? "1 file" : $"{_entries.Count} files";
		}
		finally
		{
			if( _entriesLoad == load )
			{
				Busy.IsActive = false;
				EntryBusyRing.IsActive = false;
				EntryBusyBar.Visibility = Visibility.Collapsed;
				EntryBusyOverlay.Visibility = Visibility.Collapsed;
			}
		}
	}

	private void SetAllSelected( bool selected )
	{
		if( _quietSelection )
			return;
		// ListView.SelectAll is not implemented on Uno: add the rows one by one, then redraw once
		_quietSelection = true;
		EntryList.SelectedItems.Clear();
		if( selected )
			foreach( object item in EntryList.Items )
				EntryList.SelectedItems.Add( item );
		_quietSelection = false;
		OnEntrySelectionChanged();
	}

	private void OnEntrySelectionChanged()
	{
		if( _quietSelection )
			return;
		List<int> ids = [ .. EntryList.SelectedItems.OfType<GraphEntryItem>().Select( i => i.Id ) ];
		_quietSelection = true;
		SelectAll.IsChecked = ids.Count > 0 && ids.Count == _entries.Count;
		_quietSelection = false;
		_roots = ids;
		PendingLoad = LoadGraphAsync( debounce: true );
	}

	/// <summary>Draws the graph around one node (double-clicked in the graph).</summary>
	private void Focus( GraphNode node )
	{
		if( node.Role == GraphNodeRole.Folder )
		{
			if( FolderTree.FindTreeNode( node.Path ) is { Node: { } treeNode } )
			{
				_ = OpenFolderAsync( treeNode );
			}
			else
			{
				_overrideFolderPath = node.Path;
				_overrideDirectoryId = null;
				PendingLoad = LoadGraphAsync();
			}
			return;
		}
		if( node.Role == GraphNodeRole.PropertyGroup )
			return;

		if( ViewMode != GraphViewMode.Dependencies )
		{
			_quietControls = true;
			SetViewMode( GraphViewMode.Dependencies );
			UpdateModeUiChrome();
			_quietControls = false;
		}

		if( _entries.FirstOrDefault( i => i.Id == node.Id ) is { } listed )
		{
			// in this folder: make it the list's only selection, which redraws the graph
			EntryList.SelectedItems.Clear();
			EntryList.SelectedItem = EntryList.Items.OfType<GraphEntryItem>().FirstOrDefault( i => i.Id == listed.Id );
			EntryList.ScrollIntoView( EntryList.SelectedItem );
			return;
		}
		_quietSelection = true;
		EntryList.SelectedItems.Clear();
		SelectAll.IsChecked = false;
		_quietSelection = false;
		_roots = [ node.Id ];
		PendingLoad = LoadGraphAsync();
	}

	// ------------------------------------------------------------------ toolbar & filter controls

	private void OnViewModeChanged()
	{
		if( _quietControls )
			return;
		UpdateModeUiChrome();
		PendingLoad = LoadGraphAsync();
	}

	private void OnDirectionRadioChanged()
	{
		if( _quietControls )
			return;
		PendingLoad = LoadGraphAsync();
	}

	private void OnFileTypePresetChanged()
	{
		if( _quietControls )
			return;
		string? tag = ( FileTypePresetCombo.SelectedItem as ComboBoxItem )?.Tag?.ToString();
		if( tag == "__custom__" )
			return;
		_quietControls = true;
		ExtensionsFilterBox.Text = tag ?? "";
		_quietControls = false;
		PendingLoad = ApplyCurrentFiltersOrReloadAsync();
	}

	private void OnFilterControlChanged( bool localOnlyPreferred = false )
	{
		if( _quietControls )
			return;
		PendingLoad = ApplyCurrentFiltersOrReloadAsync( localOnlyPreferred );
	}

	private void UpdateModeUiChrome()
	{
		GraphViewMode mode = ViewMode;
		bool showDepControls = mode is GraphViewMode.Dependencies or GraphViewMode.SearchFilter || DepLinksOverlayCheck.IsChecked == true;
		DirectionPanel.Visibility = showDepControls ? Visibility.Visible : Visibility.Collapsed;
		DepLinksOverlayCheck.Visibility = mode != GraphViewMode.Dependencies ? Visibility.Visible : Visibility.Collapsed;
		PropertyNameBox.PlaceholderText = mode == GraphViewMode.ByProperty
			? "Group by (Material, Category...)"
			: "Property filter (e.g. Material)";
		Hint.Text = mode switch
		{
			GraphViewMode.FolderStructure => "Select a folder in the left tree to view its folder hierarchy graph",
			GraphViewMode.FoldersAndFiles => "Select a folder in the left tree to view folders & files as a node graph",
			GraphViewMode.ByProperty => "Choose a property (e.g. Material, Category, Extension) and click Apply",
			GraphViewMode.SearchFilter => "Enter file type, name, path, or property filters and click Apply",
			_ => "Pick one or more files to see their dependencies",
		};
	}

	private GraphFilterCriteria ReadCriteriaFromControls()
	{
		int maxDepth = int.TryParse( ( DepthCombo.SelectedItem as ComboBoxItem )?.Tag?.ToString(), out int d ) ? d : 0;
		IReadOnlyList<string> exts = GraphFilterEngine.ParseExtensionList( ExtensionsFilterBox.Text );
		string? propName = string.IsNullOrWhiteSpace( PropertyNameBox.Text ) ? null : PropertyNameBox.Text.Trim();
		string? propValue = string.IsNullOrWhiteSpace( PropertyValueBox.Text ) ? null : PropertyValueBox.Text.Trim();

		GraphViewMode mode = ViewMode;
		string? groupBy = mode == GraphViewMode.ByProperty ? ( propName ?? _criteria.GroupByProperty ?? "Category" ) : _criteria.GroupByProperty;
		string? filterProp = mode == GraphViewMode.ByProperty && string.IsNullOrWhiteSpace( propValue ) ? null : propName;

		return _criteria with
		{
			ViewMode = mode,
			Direction = Direction,
			MaxDepth = maxDepth,
			MaxChildDepth = ParseNullableNonNegInt( ChildDepthBox.Text ),
			MaxParentDepth = ParseNullableNonNegInt( ParentDepthBox.Text ),
			MaxChildBranches = ParseNullableNonNegInt( ChildBranchesBox.Text ),
			MaxParentBranches = ParseNullableNonNegInt( ParentBranchesBox.Text ),
			Extensions = exts.Count > 0 ? exts : null,
			NameFilter = string.IsNullOrWhiteSpace( NameFilterBox.Text ) ? null : NameFilterBox.Text.Trim(),
			PathFilter = string.IsNullOrWhiteSpace( PathFilterBox.Text ) ? null : PathFilterBox.Text.Trim(),
			CheckedOutOnly = CheckedOutFilterCheck.IsChecked == true ? true : null,
			PropertyName = filterProp,
			PropertyValue = propValue,
			GroupByProperty = groupBy,
			BridgeFilteredEdges = BridgeLinksCheck.IsChecked != false,
			KeepRoots = KeepRootsCheck.IsChecked != false,
			IncludeDependencyLinks = DepLinksOverlayCheck.IsChecked == true,
		};
	}

	private void SyncControlsFromCriteria( GraphFilterCriteria c )
	{
		SetViewMode( c.ViewMode );
		SetDirection( c.Direction );

		string depthTag = c.MaxDepth.ToString();
		int depthIdx = 0;
		for( int i = 0; i < DepthCombo.Items.Count; i++ )
		{
			if( DepthCombo.Items[ i ] is ComboBoxItem item && item.Tag?.ToString() == depthTag )
			{
				depthIdx = i;
				break;
			}
		}
		DepthCombo.SelectedIndex = depthIdx;

		ChildDepthBox.Text = c.MaxChildDepth?.ToString() ?? "";
		ParentDepthBox.Text = c.MaxParentDepth?.ToString() ?? "";
		ChildBranchesBox.Text = c.MaxChildBranches?.ToString() ?? "";
		ParentBranchesBox.Text = c.MaxParentBranches?.ToString() ?? "";
		BridgeLinksCheck.IsChecked = c.BridgeFilteredEdges;
		KeepRootsCheck.IsChecked = c.KeepRoots;
		DepLinksOverlayCheck.IsChecked = c.IncludeDependencyLinks;
		CheckedOutFilterCheck.IsChecked = c.CheckedOutOnly == true;

		string extJoined = c.Extensions is { Count: > 0 } ? string.Join( ",", GraphFilterEngine.NormalizeExtensions( c.Extensions ) ) : "";
		ExtensionsFilterBox.Text = extJoined;
		int presetIdx = -1;
		for( int i = 0; i < FileTypePresetCombo.Items.Count; i++ )
		{
			if( FileTypePresetCombo.Items[ i ] is ComboBoxItem item
				&& string.Equals( item.Tag?.ToString(), extJoined, StringComparison.OrdinalIgnoreCase ) )
			{
				presetIdx = i;
				break;
			}
		}
		FileTypePresetCombo.SelectedIndex = presetIdx >= 0 ? presetIdx : ( extJoined.Length > 0 ? FileTypePresetCombo.Items.Count - 1 : 0 );

		NameFilterBox.Text = c.NameFilter ?? "";
		PathFilterBox.Text = c.PathFilter ?? "";
		PropertyNameBox.Text = c.ViewMode == GraphViewMode.ByProperty
			? ( c.GroupByProperty ?? c.PropertyName ?? "" )
			: ( c.PropertyName ?? "" );
		PropertyValueBox.Text = c.PropertyValue ?? "";
	}

	private async Task ResetFiltersAsync( CancellationToken externalToken = default )
	{
		_quietControls = true;
		try
		{
			_criteria = GraphFilterCriteria.Default with
			{
				ViewMode = ViewMode,
				Direction = Direction,
			};
			SyncControlsFromCriteria( _criteria );
			UpdateModeUiChrome();
		}
		finally
		{
			_quietControls = false;
		}
		await ApplyCurrentFiltersOrReloadAsync( localOnlyPreferred: ViewMode == GraphViewMode.Dependencies, externalToken: externalToken );
	}

	private async Task ApplyCurrentFiltersOrReloadAsync( bool localOnlyPreferred = false, CancellationToken externalToken = default )
	{
		GraphFilterCriteria next = ReadCriteriaFromControls();
		_criteria = next;
		UpdateModeUiChrome();

		bool canFilterLocally = next.ViewMode == GraphViewMode.Dependencies
			&& _rawGraph.Nodes.Count > 0
			&& _rawGraphDirection == next.Direction
			&& ( !next.NeedsCustomProperties || _rawGraphHasProperties )
			&& ( next.Direction != GraphDirection.FullNetwork
				|| ( _rawGraphMaxDepth == 0 && next.MaxDepth <= 2 )
				|| ( next.MaxDepth > 0 && next.MaxDepth <= _rawGraphMaxDepth ) );

		if( canFilterLocally && ( localOnlyPreferred || next.ViewMode == GraphViewMode.Dependencies ) )
		{
			GraphFilterResult filtered = GraphFilterEngine.ApplyFilters( _rawGraph, next );
			await RenderGraphAsync( filtered, next.Direction, externalToken );
			return;
		}

		await LoadGraphAsync( debounce: false, externalToken: externalToken );
	}

	// ------------------------------------------------------------------ graph

	private async Task LoadGraphAsync( bool debounce = false, CancellationToken externalToken = default )
	{
		_graphLoad?.Cancel();
		CancellationTokenSource linked = externalToken.CanBeCanceled
			? CancellationTokenSource.CreateLinkedTokenSource( externalToken )
			: new CancellationTokenSource();
		CancellationTokenSource load = _graphLoad = linked;

		GraphFilterCriteria criteria = ReadCriteriaFromControls();
		_criteria = criteria;
		UpdateModeUiChrome();

		IReadOnlyList<int> roots = _roots;
		GraphDirection layoutDirection = ResolveLayoutDirection( criteria );

		try
		{
			if( debounce )
				await Task.Delay( 150, load.Token );

			bool willQuery = criteria.ViewMode != GraphViewMode.Dependencies || roots.Count > 0;
			Busy.IsActive = willQuery;
			GraphBusyOverlay.Visibility = willQuery ? Visibility.Visible : Visibility.Collapsed;
			GraphBusyRing.IsActive = willQuery;

			if( criteria.ViewMode == GraphViewMode.Dependencies )
			{
				if( roots.Count == 0 )
				{
					_rawGraph = DependencyGraph.Empty;
					await RenderGraphAsync( GraphFilterResult.Empty, layoutDirection, load.Token );
					return;
				}

				// Load raw unfiltered graph first so subsequent filter tweaks happen instantaneously in memory
				GraphFilterCriteria rawCriteria = GraphFilterCriteria.Default with
				{
					ViewMode = GraphViewMode.Dependencies,
					Direction = criteria.Direction,
					MaxDepth = criteria.Direction == GraphDirection.FullNetwork ? criteria.MaxDepth : 0,
					MaxNodes = Math.Max( criteria.MaxNodes * 2, 500 ),
					KeepRoots = true,
					BridgeFilteredEdges = false,
				};
				Result<GraphFilterResult> rawRes = await DependencyGraphLoader.LoadFilteredAsync(
					roots,
					rawCriteria,
					loadProperties: criteria.NeedsCustomProperties,
					token: load.Token );

				if( load.IsCancellationRequested )
					return;
				if( !rawRes.Ok || rawRes.Value is null )
				{
					Status.Text = $"The dependencies could not be read: {rawRes.Error}";
					return;
				}

				_rawGraph = rawRes.Value.Graph;
				_rawGraphDirection = criteria.Direction;
				_rawGraphMaxDepth = criteria.MaxDepth;
				_rawGraphHasProperties = criteria.NeedsCustomProperties;

				GraphFilterResult filtered = GraphFilterEngine.ApplyFilters( _rawGraph, criteria );
				await RenderGraphAsync( filtered, layoutDirection, load.Token );
				return;
			}

			int? dirId = _overrideDirectoryId ?? FolderTree.SelectedNode?.LinkedData.DirectoryId;
			if( dirId is 0 )
				dirId = null;
			string? folderPath = _overrideFolderPath ?? FolderTree.SelectedNode?.LinkedData.FullPath;

			GraphBuildRequest req = new()
			{
				EntryIds = criteria.ViewMode == GraphViewMode.ByProperty && roots.Count > 0 ? roots : null,
				DirectoryId = dirId,
				FolderPath = folderPath,
				RecursiveFolders = true,
				Criteria = criteria,
				LoadProperties = criteria.NeedsCustomProperties,
				CachedDirectoryTree = criteria.ViewMode == GraphViewMode.FolderStructure ? AppComposition.FileManagerState.Tree : null,
			};

			Result<GraphFilterResult> built = await DependencyGraphLoader.BuildGraphAsync( req, load.Token );
			if( load.IsCancellationRequested )
				return;
			if( !built.Ok || built.Value is null )
			{
				Status.Text = $"The node graph could not be built: {built.Error}";
				return;
			}

			_rawGraph = built.Value.Graph;
			_rawGraphDirection = layoutDirection;
			_rawGraphMaxDepth = criteria.MaxDepth;
			_rawGraphHasProperties = criteria.NeedsCustomProperties;

			await RenderGraphAsync( built.Value, layoutDirection, load.Token );
		}
		catch( OperationCanceledException )
		{
		}
		finally
		{
			if( _graphLoad == load )
			{
				Busy.IsActive = false;
				GraphBusyRing.IsActive = false;
				GraphBusyOverlay.Visibility = Visibility.Collapsed;
			}
		}
	}

	private async Task ExpandNodeAsync( int nodeId, GraphDirection expandDirection, CancellationToken externalToken = default )
	{
		if( nodeId <= 0 )
			return;
		_graphLoad?.Cancel();
		CancellationTokenSource linked = externalToken.CanBeCanceled
			? CancellationTokenSource.CreateLinkedTokenSource( externalToken )
			: new CancellationTokenSource();
		CancellationTokenSource load = _graphLoad = linked;

		try
		{
			Busy.IsActive = true;
			GraphBusyOverlay.Visibility = Visibility.Visible;
			GraphBusyRing.IsActive = true;

			DependencyGraph baseGraph = _rawGraph.Nodes.Count > 0 ? _rawGraph : _graph;
			Result<DependencyGraph> expanded = await DependencyGraphLoader.ExpandNodeNeighborsAsync( baseGraph, nodeId, expandDirection, load.Token );
			if( load.IsCancellationRequested )
				return;
			if( !expanded.Ok || expanded.Value is null )
			{
				Status.Text = $"Could not expand neighbors: {expanded.Error}";
				return;
			}

			DependencyGraph merged = expanded.Value;
			if( _criteria.NeedsCustomProperties )
				merged = await DependencyGraphLoader.AttachPropertiesAsync( merged, _criteria.PropertyName, load.Token );

			_rawGraph = merged;
			_rawGraphDirection = GraphDirection.FullNetwork;
			_rawGraphHasProperties = _rawGraphHasProperties || _criteria.NeedsCustomProperties;

			// Auto-switch direction to FullNetwork so newly expanded siblings & co-parents lay out cleanly left-to-right
			if( Direction != GraphDirection.FullNetwork )
			{
				_quietControls = true;
				ShowFullNetwork.IsChecked = true;
				_quietControls = false;
			}

			GraphFilterCriteria effectiveCriteria = ReadCriteriaFromControls() with
			{
				Direction = GraphDirection.FullNetwork,
				// Clear restrictive depth cap on manual interactive expand so the newly expanded wave stays visible
				MaxDepth = 0,
				MaxChildDepth = null,
				MaxParentDepth = null,
			};
			_criteria = effectiveCriteria;

			GraphFilterResult filtered = GraphFilterEngine.ApplyFilters( _rawGraph, effectiveCriteria );
			await RenderGraphAsync( filtered, GraphDirection.FullNetwork, load.Token );
		}
		catch( OperationCanceledException )
		{
		}
		finally
		{
			if( _graphLoad == load )
			{
				Busy.IsActive = false;
				GraphBusyRing.IsActive = false;
				GraphBusyOverlay.Visibility = Visibility.Collapsed;
			}
		}
	}

	private async Task RenderGraphAsync( GraphFilterResult result, GraphDirection layoutDirection, CancellationToken token )
	{
		_graph = result.Graph;
		_stats = result.Stats;
		GraphForm form = Graph.Theme.Form;
		GraphLayoutResult arranged = await Task.Run( () => GraphLayout.Arrange( _graph, layoutDirection, form.Layout ), token );
		if( token.IsCancellationRequested )
			return;
		Graph.Show( arranged );

		// the open details stay open when their node is still in the graph
		if( _detailNode is { } open && Graph.SelectNode( open.Id ) )
			Graph.EnsureVisible( open.Id, DetailPanelSpace );
		else
			CloseDetails();
		Hint.Visibility = _graph.Nodes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
		ZoomText.Text = $"{Graph.Zoom * 100:0}%";
		ShowStatus();
	}

	private static GraphDirection ResolveLayoutDirection( GraphFilterCriteria criteria ) => criteria.ViewMode switch
	{
		GraphViewMode.FolderStructure => GraphDirection.Children,
		GraphViewMode.FoldersAndFiles => criteria.IncludeDependencyLinks ? GraphDirection.FullNetwork : GraphDirection.Children,
		GraphViewMode.ByProperty => criteria.IncludeDependencyLinks ? GraphDirection.FullNetwork : GraphDirection.Children,
		GraphViewMode.SearchFilter => criteria.IncludeDependencyLinks ? criteria.Direction : GraphDirection.Children,
		_ => criteria.Direction,
	};

	private void ShowStatus()
	{
		if( _graph.Nodes.Count == 0 )
		{
			Status.Text = _criteria.HasAttributeFilters
				? $"{_criteria.ViewMode} · 0 matching nodes (adjust filters or click Reset)"
				: "";
			return;
		}

		List<string> parts = [];
		if( _criteria.ViewMode == GraphViewMode.Dependencies )
		{
			int others = Math.Max( 0, _graph.Nodes.Count - _graph.Roots.Count );
			string what = Direction switch
			{
				GraphDirection.Children => others == 1 ? "1 file used" : $"{others} files used",
				GraphDirection.Parents => others == 1 ? "used by 1 file" : $"used by {others} files",
				GraphDirection.FullNetwork => others == 1 ? "1 network neighbor" : $"{others} network neighbors",
				_ => others == 1 ? "1 related file" : $"{others} related files",
			};
			parts.Add( $"{_graph.Roots.Count} selected · {what}" );
		}
		else
		{
			string modeLabel = _criteria.ViewMode switch
			{
				GraphViewMode.FolderStructure => "Folder Structure",
				GraphViewMode.FoldersAndFiles => "Folders & Files",
				GraphViewMode.ByProperty => $"Grouped by {_criteria.GroupByProperty ?? "Category"}",
				GraphViewMode.SearchFilter => "Search / Filter",
				_ => _criteria.ViewMode.ToString(),
			};
			parts.Add( $"{modeLabel} · {_graph.Nodes.Count} nodes" );
		}

		string linkPart = $"{_graph.Edges.Count} links";
		if( _stats.BridgedEdgesAdded > 0 )
			linkPart += $" ({_stats.BridgedEdgesAdded} bridged)";
		parts.Add( linkPart );

		if( _criteria.MaxDepth > 0 )
			parts.Add( $"depth≤{_criteria.MaxDepth}" );
		if( _criteria.MaxChildDepth.HasValue )
			parts.Add( $"kids depth≤{_criteria.MaxChildDepth}" );
		if( _criteria.MaxParentDepth.HasValue )
			parts.Add( $"parents depth≤{_criteria.MaxParentDepth}" );
		if( _criteria.MaxChildBranches.HasValue )
			parts.Add( $"max {_criteria.MaxChildBranches} offspring/node" );
		if( _criteria.MaxParentBranches.HasValue )
			parts.Add( $"max {_criteria.MaxParentBranches} parents/node" );
		if( _criteria.Extensions is { Count: > 0 } exts )
			parts.Add( $"types: {string.Join( ",", exts )}" );
		if( _stats.TotalNodesBeforeFilter > _graph.Nodes.Count )
			parts.Add( $"showing {_graph.Nodes.Count} of {_stats.TotalNodesBeforeFilter}" );
		if( _graph.Truncated )
			parts.Add( "capped by limits" );

		string text = string.Join( " · ", parts );
		if( Graph.SelectedNode is { } node )
		{
			string info = node.Role == GraphNodeRole.File
				? $"{node.Path} ({ByteSize.Format( node.Size, " " )})"
				: $"{node.Path} ({node.Subtitle})";
			text += $"   |   {info}";
		}
		Status.Text = text;
	}

	// ------------------------------------------------------------------ theme

	/// <summary>The app's theme changed (Settings → Theme): redraw the graph in its colors and shapes, no server call.</summary>
	private void OnThemeChanged()
	{
		bool show = AppComposition.Theme.ShowTreeBranchLines;
		if( GraphBranchLinesToggle.IsChecked != show )
			GraphBranchLinesToggle.IsChecked = show;
		TreeBranchGuides.RefreshTree( FolderTree, showLines: show, selectedNode: FolderTree.SelectedNode, updateSelection: true );

		GraphTheme theme = AppComposition.Theme.GraphTheme;
		if( theme == Graph.Theme )
			return;
		Graph.Theme = theme;
		if( _detailNode is { } shown )
			InfoHeader.Background = new SolidColorBrush( Graph.HeaderColor( shown ) );
		if( _graph.Nodes.Count == 0 )
			return;
		Graph.Show( GraphLayout.Arrange( _graph, ResolveLayoutDirection( _criteria ), theme.Form.Layout ) );
		// the open details stay on their file
		if( _detailNode is { } open && Graph.SelectNode( open.Id ) )
			Graph.EnsureVisible( open.Id, DetailPanelSpace );
	}

	// ------------------------------------------------------------------ details of the clicked node

	/// <summary>Closes the details panel, as its close button does (the smoke tests).</summary>
	internal void CloseDetailsForTest() => CloseDetails();

	/// <summary>Whether the details panel is open (the smoke tests).</summary>
	internal bool DetailsOpen => DetailPanel.Visibility == Visibility.Visible;

	/// <summary>The details load in progress (the smoke tests wait on it).</summary>
	internal Task? DetailsLoad { get; private set; }

	/// <summary>The info card's lines (the smoke tests).</summary>
	internal string DetailsText => string.Join( " | ", new[] { InfoPreviewNone, InfoName, InfoType, InfoSize, InfoVersions, InfoState, InfoFolder, InfoModified, InfoUses, InfoUsedBy }
		.Select( t => t.Text ) );

	/// <summary>Shows a detail tab ("History", "Where Used", "Dependents", "Properties", "Info") (the smoke tests).</summary>
	internal void ShowDetailTab( string header )
		=> DetailTabs.SelectedItem = DetailTabs.TabItems.OfType<TabViewItem>().First( t => t.Header as string == header );

	private Task ShowDetailsAsync( GraphNode? node )
	{
		if( node is null )
		{
			CloseDetails();
			return Task.CompletedTask;
		}
		bool same = _detailNode?.Id == node.Id && DetailsOpen;
		_detailNode = node;
		FillInfoCard( node, keepPreview: same );
		DetailPanel.Visibility = Visibility.Visible;
		if( DetailTabs.SelectedItem is null )
			DetailTabs.SelectedIndex = 0;
		Graph.EnsureVisible( node.Id, DetailPanelSpace );
		if( same )
			return DetailsLoad ?? Task.CompletedTask;
		return DetailsLoad = LoadDetailsAsync( node );
	}

	private void CloseDetails()
	{
		_detailLoad?.Cancel();
		_previewLoad?.Cancel();
		_detailNode = null;
		DetailPanel.Visibility = Visibility.Collapsed;
	}

	private void FillInfoCard( GraphNode node, bool keepPreview = false )
	{
		InfoHeader.Background = new SolidColorBrush( Graph.HeaderColor( node ) );
		InfoName.Text = node.Name;

		bool isFile = node.Role == GraphNodeRole.File;
		if( !isFile )
		{
			InfoType.Text = node.TypeName.Length > 0 ? node.TypeName : node.Role.ToString();
			InfoSize.Text = node.Subtitle.Length > 0 ? node.Subtitle : $"{node.ItemCount} items";
			InfoVersions.Text = node.OmittedChildBranches > 0 ? $"+{node.OmittedChildBranches} hidden by branch limit" : "synthetic group node";
			InfoState.Text = node.Role == GraphNodeRole.Folder ? "Folder node" : "Property cluster";
			InfoFolder.Text = node.Path;
			ToolTipService.SetToolTip( InfoFolder, node.Path );
			InfoModified.Text = "—";
			InfoUses.Text = Related( _graph.Edges.Where( e => e.Parent == node.Id ).Select( e => e.Child ), null );
			InfoUsedBy.Text = Related( _graph.Edges.Where( e => e.Child == node.Id ).Select( e => e.Parent ), null );
			InfoFocus.IsEnabled = node.Role == GraphNodeRole.Folder;
			InfoLocate.IsEnabled = false;
			InfoExpandNeighbors.IsEnabled = false;
			InfoPreview.Source = null;
			InfoPreviewNone.Text = node.Role == GraphNodeRole.Folder ? "folder" : "group";
			InfoPreviewNone.Visibility = Visibility.Visible;
			return;
		}

		string type = node.TypeName.Length > 0 ? node.TypeName : node.Ext.ToUpperInvariant() + " file";
		InfoType.Text = node.Category.Length > 0 ? $"{type} · {node.Category}" : type;
		InfoSize.Text = ByteSize.Format( node.Size, " " );
		InfoVersions.Text = node.Versions switch
		{
			0 => "no versions",
			1 => $"1 version (id {node.VersionId})",
			_ => $"{node.Versions} versions, latest id {node.VersionId}",
		};
		InfoState.Text = node.Deleted ? "Deleted"
			: node.CheckedOut ? $"Checked out by {( node.CheckoutUser.Length > 0 ? node.CheckoutUser : "someone" )}"
			: "Not checked out";

		int cut = node.Path.LastIndexOf( '\\' );
		InfoFolder.Text = cut > 0 ? node.Path[ ..cut ] : node.Path;
		ToolTipService.SetToolTip( InfoFolder, node.Path );
		InfoModified.Text = DateTime.TryParse( node.Modified, System.Globalization.CultureInfo.InvariantCulture,
			System.Globalization.DateTimeStyles.AssumeUniversal, out DateTime changed ) ? changed.ToLocalTime().ToString( "g" ) : "unknown";

		InfoUses.Text = Related( _graph.Edges.Where( e => e.Parent == node.Id ).Select( e => e.Child ), GraphDirection.Parents, node.OmittedChildBranches );
		InfoUsedBy.Text = Related( _graph.Edges.Where( e => e.Child == node.Id ).Select( e => e.Parent ), GraphDirection.Children, node.OmittedParentBranches );
		InfoFocus.IsEnabled = true;
		InfoLocate.IsEnabled = !node.Deleted && node.Path.Contains( '\\' );
		InfoExpandNeighbors.IsEnabled = node.Id > 0;

		if( keepPreview )
			return;
		InfoPreview.Source = null;
		InfoPreviewNone.Text = "loading…";
		InfoPreviewNone.Visibility = Visibility.Visible;
	}

	/// <summary>"2 files: a, b" for the linked nodes in the graph; a hint when <paramref name="hiddenIn"/> does not show them.</summary>
	private string Related( IEnumerable<int> ids, GraphDirection? hiddenIn, int omittedCount = 0 )
	{
		List<string> names = [ .. ids.Distinct().Select( id => _graph.Nodes.FirstOrDefault( n => n.Id == id )?.Name ).OfType<string>().Order( StringComparer.OrdinalIgnoreCase ) ];
		string suffix = omittedCount > 0 ? $" (+{omittedCount} hidden by branch limit)" : "";
		if( names.Count == 0 )
		{
			if( omittedCount > 0 )
				return $"{omittedCount} hidden by branch limit";
			return hiddenIn.HasValue && _criteria.ViewMode == GraphViewMode.Dependencies && Direction == hiddenIn.Value
				? "not shown in this view"
				: "nothing";
		}
		string list = string.Join( ", ", names.Take( 4 ) ) + ( names.Count > 4 ? $", +{names.Count - 4} more" : "" );
		return ( names.Count == 1 ? list : $"{names.Count} items: {list}" ) + suffix;
	}

	private async Task LoadDetailsAsync( GraphNode node )
	{
		_previewLoad?.Cancel();
		CancellationTokenSource preview = _previewLoad = new();
		CancellationToken token = preview.Token;
		if( node.Role != GraphNodeRole.File || node.Id <= 0 )
		{
			HistoryRows.Clear();
			ParentRows.Clear();
			ChildRows.Clear();
			PropertyRows.Clear();
			VersionRows.Clear();
			return;
		}

		await LoadDetailTabAsync();
		try
		{
			byte[]? bytes = node.VersionId > 0 ? await _details.GetPreviewImageAsync( node.VersionId ) : null;
			if( token.IsCancellationRequested || _detailNode?.Id != node.Id )
				return;
			ImageSource? image = await _previews.FromBytesAsync( bytes );
			if( token.IsCancellationRequested || _detailNode?.Id != node.Id )
				return;
			InfoPreview.Source = image;
			InfoPreviewNone.Text = "no preview";
			InfoPreviewNone.Visibility = image is null ? Visibility.Visible : Visibility.Collapsed;
		}
		catch( Exception e )
		{
			HackPDM.Core.Logging.HackLogger.Warn( "DependencyGraph", $"Preview of '{node.Name}' failed: {e.Message}", e );
			if( _detailNode?.Id == node.Id )
				InfoPreviewNone.Text = "no preview";
		}
	}

	/// <summary>Fills the visible detail table for the open file (one server call or two, like the file manager).</summary>
	private Task LoadDetailTabAsync()
	{
		_detailLoad?.Cancel();
		CancellationTokenSource load = _detailLoad = new();
		HistoryRows.Clear();
		ParentRows.Clear();
		ChildRows.Clear();
		PropertyRows.Clear();
		VersionRows.Clear();
		if( _detailNode is not { Role: GraphNodeRole.File, Id: > 0 } node || DetailTabs.SelectedItem is not TabViewItem tab )
			return Task.CompletedTask;
		DetailRow row = new() { Id = node.Id, LatestId = node.VersionId > 0 ? node.VersionId : null };
		return DetailsLoad = FillAsync();

		async Task FillAsync()
		{
			try
			{
				switch( tab.Name )
				{
					case nameof( HistoryTab ):
						Apply( HistoryRows, await _details.GetHistoryAsync( row ) );
						break;
					case nameof( ParentTab ):
						Apply( ParentRows, await _details.GetParentsAsync( row ) );
						break;
					case nameof( ChildTab ):
						Apply( ChildRows, await _details.GetChildrenAsync( row ) );
						break;
					case nameof( PropertiesTab ):
						Apply( PropertyRows, await _details.GetPropertiesAsync( row ) );
						break;
					case nameof( InfoTab ):
						Apply( VersionRows, await _details.GetVersionInfoAsync( row ) );
						break;
				}
			}
			catch( Exception e )
			{
				HackPDM.Core.Logging.HackLogger.Warn( "DependencyGraph", $"{tab.Name} of '{node.Name}' failed: {e.Message}", e );
			}
		}

		void Apply<T>( RangeObservableCollection<T> target, List<T> rows )
		{
			if( !load.IsCancellationRequested )
				target.ReplaceAll( rows );
		}
	}

	/// <summary>Opens the file manager on the open file.</summary>
	private Task LocateAsync()
	{
		if( _detailNode is not { Role: GraphNodeRole.File } node )
			return Task.CompletedTask;
		string[] parts = node.Path.Split( '\\', StringSplitOptions.RemoveEmptyEntries );
		// the path starts with the root folder and ends with the file
		return parts.Length < 2 ? Task.CompletedTask : AppComposition.LocateInFileManagerAsync( parts[ 1..^1 ], node.Name );
	}

	private void SyncFolderSelection()
	{
		TreeViewNode? node = FolderTree.SelectedNode;
		if( _selectedFolderData is not null && !ReferenceEquals( _selectedFolderData.Node, node ) )
			_selectedFolderData.IsSelectedInTree = false;
		_selectedFolderData = node?.LinkedData;
		if( _selectedFolderData is not null )
			_selectedFolderData.IsSelectedInTree = true;
		node?.UpdateBreadCrumbCollection( BreadcrumbPath );
		TreeBranchGuides.RefreshTree( FolderTree, showLines: AppComposition.Theme.ShowTreeBranchLines, selectedNode: node, updateSelection: true );
	}

	private void FolderBreadcrumb_ItemClicked( BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs args )
	{
		if( args.Item is not TreeData { Node: { } node } data )
			return;
		FolderTree.SelectedNode = node;
		data.EnsureVisible( FolderTree );
		SyncFolderSelection();
	}

	private void BranchLinesToggle_Click( object sender, RoutedEventArgs e )
	{
		bool show = GraphBranchLinesToggle.IsChecked == true;
		AppComposition.Theme.SetShowTreeBranchLines( show );
		TreeBranchGuides.RefreshTree( FolderTree, showLines: show, selectedNode: FolderTree.SelectedNode, updateSelection: true );
	}

	private void ExpandAllFolders_Click( object sender, RoutedEventArgs e )
	{
		FolderTree.SetAllExpanded( true );
		TreeBranchGuides.RefreshTree( FolderTree, selectedNode: FolderTree.SelectedNode, updateSelection: true );
	}

	private void CollapseAllFolders_Click( object sender, RoutedEventArgs e )
	{
		FolderTree.SetAllExpanded( false );
		TreeBranchGuides.RefreshTree( FolderTree, selectedNode: FolderTree.SelectedNode, updateSelection: true );
	}

	private void ApplyGraphPaneLayout()
	{
		var layout = UIStorage.WorkspaceLayout;
		bool showFolders = ToggleFoldersPaneBtn.IsChecked == true;
		bool showFiles = ToggleFilesPaneBtn.IsChecked == true;

		GraphFoldersCard.Visibility = showFolders ? Visibility.Visible : Visibility.Collapsed;
		GraphFoldersSplitter.Visibility = showFolders ? Visibility.Visible : Visibility.Collapsed;
		GraphFoldersCol.Width = showFolders ? new GridLength( Math.Clamp( layout.GraphFoldersWidth, 180, 480 ) ) : new GridLength( 0 );

		GraphFilesCard.Visibility = showFiles ? Visibility.Visible : Visibility.Collapsed;
		GraphFilesSplitter.Visibility = showFiles ? Visibility.Visible : Visibility.Collapsed;
		GraphFilesCol.Width = showFiles ? new GridLength( Math.Clamp( layout.GraphFilesWidth, 180, 480 ) ) : new GridLength( 0 );
	}

	private void ToggleSidePanes_Click( object sender, RoutedEventArgs e )
		=> ApplyGraphPaneLayout();

	private void GraphFoldersSplitter_DragDelta( double delta )
	{
		double next = Math.Clamp( GraphFoldersCol.Width.Value + delta, 180, 480 );
		GraphFoldersCol.Width = new GridLength( next );
		UIStorage.WorkspaceLayout.GraphFoldersWidth = next;
	}

	private void GraphFilesSplitter_DragDelta( double delta )
	{
		double next = Math.Clamp( GraphFilesCol.Width.Value + delta, 180, 480 );
		GraphFilesCol.Width = new GridLength( next );
		UIStorage.WorkspaceLayout.GraphFilesWidth = next;
	}

	private void GraphSplitter_DragCompleted()
	{
		try
		{
			AppConfigStore.Save( UIStorage.GlobalData );
		}
		catch { }
	}

	private static int? ParseNullableNonNegInt( string? text )
	{
		if( string.IsNullOrWhiteSpace( text ) )
			return null;
		string trimmed = text.Trim();
		if( string.Equals( trimmed, "all", StringComparison.OrdinalIgnoreCase )
			|| string.Equals( trimmed, "unlimited", StringComparison.OrdinalIgnoreCase ) )
		{
			return null;
		}
		return int.TryParse( trimmed, out int v ) && v >= 0 ? v : null;
	}
}

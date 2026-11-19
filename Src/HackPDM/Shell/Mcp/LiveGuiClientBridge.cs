using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;

using CommunityToolkit.Mvvm.Input;

using HackPDM.Core.Graph;
using HackPDM.Core.Theming;
using HackPDM.Infrastructure.Odoo;
using HackPDM.Mcp.Bridge;
using HackPDM.Mcp.DynamicUi;
using HackPDM.Mcp.Protocol;
using HackPDM.Mcp.Services;
using HackPDM.Mcp.State;
using HackPDM.Mcp.Tools;
using HackPDM.Mvvm.Models.Hack;
using HackPDM.Mvvm.Models.Settings;
using HackPDM.Mvvm.Models.Tools;
using HackPDM.Mvvm.ViewModels.Common;
using HackPDM.Mvvm.ViewModels.Settings;
using HackPDM.Mvvm.Views.Common.Controls;
using HackPDM.Mvvm.Views.Common.Settings;
using HackPDM.Mvvm.Views.Common.Types;
using HackPDM.Mvvm.Views.Dialogs;
using HackPDM.Mvvm.Views.Hack;
using HackPDM.Mvvm.Views.Odoo;
using HackPDM.Mvvm.Views.Settings;
using HackPDM.Mvvm.Views.Tools;
using HackPDM.Shared.GlobalData;
using HackPDM.Shell.Testing;

using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

using WinUI.TableView;

using EntryRow = HackPDM.Mvvm.Models.Hack.EntryRow;
using MessageBox = HackPDM.Mvvm.Views.Dialogs.MessageBox;

namespace HackPDM.Shell.Mcp;

/// <summary>
/// Live in-process implementation of <see cref="IHackPdmUiBridge"/> running inside the HackPDM desktop client.
/// Marshals all WinUI / Uno visual tree reads, control interactions, navigation, and screenshots onto
/// <see cref="UiDispatcher.Current"/>.
/// </summary>
internal sealed class LiveGuiClientBridge : IHackPdmUiBridge
{
	private static async Task<T> OnUiAsync<T>( Func<T> action, CancellationToken cancellationToken = default )
	{
		T result = default!;
		await UiDispatcher.Current.RunAsync( () => result = action(), cancellationToken );
		return result;
	}

	private static async Task<T> OnUiAsync<T>( Func<Task<T>> action )
	{
		T result = default!;
		await UiDispatcher.Current.RunAsync( async () => result = await action() );
		return result;
	}

	private static (string WindowId, Window Window, UIElement Root) ResolveWindow( string? windowId )
	{
		IReadOnlyList<(string Id, Window Window)> open = WindowHelper.GetOpenWindows();
		Window? target = null;
		string resolvedId = "main";

		if( string.IsNullOrWhiteSpace( windowId ) || string.Equals( windowId, "main", StringComparison.OrdinalIgnoreCase ) )
		{
			target = HackApp.Window ?? open.FirstOrDefault().Window;
			resolvedId = open.FirstOrDefault( w => ReferenceEquals( w.Window, target ) ).Id ?? "main";
		}
		else
		{
			var match = open.FirstOrDefault( w =>
				string.Equals( w.Id, windowId, StringComparison.OrdinalIgnoreCase )
				|| string.Equals( w.Window.Title, windowId, StringComparison.OrdinalIgnoreCase ) );
			target = match.Window;
			resolvedId = match.Id ?? windowId;
		}

		if( target is null )
			throw new InvalidOperationException( $"Window '{windowId ?? "main"}' is not open." );
		if( target.Content is not UIElement root )
			throw new InvalidOperationException( $"Window '{resolvedId}' has no UIElement root content." );

		return ( resolvedId, target, root );
	}

	// ------------------------------------------------------------------ UI State & Windows

	public Task<object> ListWindowsAsync( CancellationToken cancellationToken = default )
		=> OnUiAsync<object>( () =>
		{
			var open = WindowHelper.GetOpenWindows();
			List<object> list = [];
			foreach( var (id, win) in open )
			{
				var size = win.AppWindow?.Size;
				object? rootContent = win.Content is Frame f ? f.Content : win.Content;
				list.Add( new
				{
					windowId = id,
					isMainWindow = ReferenceEquals( win, HackApp.Window ),
					title = win.Title,
					width = size?.Width,
					height = size?.Height,
					rootContentType = rootContent?.GetType().Name,
				} );
			}
			return new { count = list.Count, windows = list };
		}, cancellationToken );

	public Task<object> GetUiStateAsync( CancellationToken cancellationToken = default )
		=> OnUiAsync<object>( () =>
		{
			HomeView? home = HackApp.HomePage;
			Frame? frame = home?.ContentFrame;
			object? activePage = frame?.Content;
			DialogService? dialogs = AppComposition.Dialogs as DialogService;

			object? pageState = activePage switch
			{
				HackFileManagerView fm => SnapshotFileManager( fm ),
				DependencyGraphView dg => SnapshotDependencyGraph( dg ),
				ProfileManagerView pm => SnapshotProfileManager( pm ),
				ConfigurationView cfg => new
				{
					odooSettings = cfg.OdooSettingsPage?.ViewModel is { } ovm ? new
					{
						ovm.OdooAddress,
						ovm.OdooPort,
						ovm.OdooDb,
						ovm.OdooUser,
						ovm.OdooUseHttps,
						ovm.OdooAreaFactor,
					} : null,
					hackSettings = cfg.HackSettingsPage?.ViewModel is { } hvm ? new
					{
						hvm.PwaPath,
						hvm.TempFolderPath,
					} : null,
				},
				ApplicationSettingsView appSettings => new
				{
					activeSection = appSettings.ActiveSectionPage?.GetType().Name,
					theme = SnapshotTheme(),
					mcp = appSettings.ActiveSectionPage is McpSettingsView mv ? new
					{
						mv.ViewModel.IsBridgeRunning,
						mv.ViewModel.IsBridgeEnabled,
						mv.ViewModel.EndpointText,
						mv.ViewModel.AutoRestorePoints,
						restorePointCount = mv.ViewModel.RestorePoints.Count,
						activityEntryCount = mv.ViewModel.ActivityEntries.Count,
					} : null,
				},
				JsonEditorView je => new
				{
					rootNodeCount = je.ViewModel.RootNodes.Count,
					hasKeptText = je.ViewModel.KeptText is not null,
				},
				NotLoggedInView => new
				{
					isVersionMismatch = AppComposition.Session.IsVersionMismatch,
					versionMismatchMessage = AppComposition.Session.VersionMismatchMessage,
				},
				_ => null,
			};

			var statusVm = AppComposition.StatusLog;
			var searchVm = AppComposition.SearchOdoo;
			var openWindows = WindowHelper.GetOpenWindows().Select( w => new
			{
				windowId = w.Id,
				isMainWindow = ReferenceEquals( w.Window, HackApp.Window ),
				title = w.Window.Title,
				rootContentType = ( w.Window.Content is Frame wf ? wf.Content : w.Window.Content )?.GetType().Name,
			} ).ToList();

			return new
			{
				isLoggedIn = AppComposition.Session.IsLoggedIn,
				isVersionMismatch = AppComposition.Session.IsVersionMismatch,
				activeMenu = home?.CurrentMenu.ToString(),
				activePageType = activePage?.GetType().Name,
				canGoBack = frame?.CanGoBack ?? false,
				backStack = frame?.BackStack.Select( b => b.SourcePageType?.Name ).ToList(),
				windows = openWindows,
				activeDialogs = MessageBox.GetActiveDialogs(),
				drawers = new
				{
					status = new
					{
						isDockedOpen = home?.IsStatusDrawerOpen ?? false,
						isDetachedOpen = dialogs?.CurrentStatusPage?.ParentWindow is not null,
						isMinimal = UIStorage.WorkspaceLayout.StatusMinimalMode,
						isInProcess = statusVm.IsInProcess,
						progress = statusVm.CompactProgressText,
						processingCount = statusVm.ProcessingCount,
						successCount = statusVm.SuccessCount,
						infoCount = statusVm.InfoCount,
						warningCount = statusVm.WarningCount,
						errorCount = statusVm.ErrorCount,
					},
					search = new
					{
						isDockedOpen = home?.IsSearchDrawerOpen ?? false,
						isDetachedOpen = dialogs?.CurrentSearchPage is not null && !( home?.IsSearchDrawerOpen ?? false ),
						isMinimal = UIStorage.WorkspaceLayout.SearchMinimalMode,
						dockSide = UIStorage.WorkspaceLayout.SearchDock.ToString(),
						fileName = searchVm.FileName,
						isSearching = searchVm.IsSearching,
						resultCount = searchVm.SearchResultsList.Count,
						statusText = searchVm.SearchStatusText,
					},
				},
				theme = SnapshotTheme(),
				pageState,
			};
		}, cancellationToken );

	// ------------------------------------------------------------------ Visual Tree & Generic Controls

	public Task<object> GetVisualTreeAsync(
		string? windowId,
		string? rootName,
		int maxDepth,
		bool includeInvisible,
		bool interactiveOnly,
		CancellationToken cancellationToken = default )
		=> OnUiAsync<object>( () =>
		{
			var (resolvedId, win, root) = ResolveWindow( windowId );
			DependencyObject start = root;
			if( !string.IsNullOrWhiteSpace( rootName ) )
			{
				var located = VisualTreeInspector.ResolveControl( root, rootName, null, null );
				if( located is null )
					throw new InvalidOperationException( $"Control named '{rootName}' not found in window '{resolvedId}'." );
				start = located.Element;
			}
			return new
			{
				windowId = resolvedId,
				windowTitle = win.Title,
				rootName,
				maxDepth,
				tree = VisualTreeInspector.BuildVisualTree( start, root, maxDepth, includeInvisible, interactiveOnly ),
			};
		}, cancellationToken );

	public Task<object> FindControlsAsync(
		string? windowId,
		string? name,
		string? typeName,
		string? text,
		string? automationId,
		bool visibleOnly,
		bool interactiveOnly,
		int maxResults,
		CancellationToken cancellationToken = default )
		=> OnUiAsync<object>( () =>
		{
			var (resolvedId, _, root) = ResolveWindow( windowId );
			List<object> matches = VisualTreeInspector.FindControls( root, root, name, typeName, text, automationId, visibleOnly, interactiveOnly, maxResults );
			return new { windowId = resolvedId, count = matches.Count, controls = matches };
		}, cancellationToken );

	public Task<object> GetControlAsync(
		string? windowId,
		string? name,
		string? automationId,
		string? path,
		CancellationToken cancellationToken = default )
		=> OnUiAsync<object>( () =>
		{
			var (resolvedId, _, root) = ResolveWindow( windowId );
			var located = VisualTreeInspector.ResolveControl( root, name, automationId, path )
				?? throw new InvalidOperationException( "Control not found matching the provided name/automationId/path." );
			return new
			{
				windowId = resolvedId,
				control = VisualTreeInspector.DescribeControlDetailed( located.Element, located.Path, root ),
			};
		}, cancellationToken );

	public Task<object> ClickControlAsync(
		string? windowId,
		string? name,
		string? automationId,
		string? path,
		string? text,
		CancellationToken cancellationToken = default )
		=> OnUiAsync<object>( () =>
		{
			var (resolvedId, _, root) = ResolveWindow( windowId );
			var located = VisualTreeInspector.ResolveControl( root, name, automationId, path, text, preferInteractive: true )
				?? throw new InvalidOperationException( "Target control not found matching the provided selector." );
			return new
			{
				windowId = resolvedId,
				path = located.Path,
				result = VisualTreeInspector.ClickControl( located.Element ),
			};
		}, cancellationToken );

	public Task<object> SetControlValueAsync(
		string? windowId,
		string? name,
		string? automationId,
		string? path,
		JsonElement? value,
		int? selectedIndex,
		bool? isChecked,
		CancellationToken cancellationToken = default )
		=> OnUiAsync<object>( () =>
		{
			var (resolvedId, _, root) = ResolveWindow( windowId );
			var located = VisualTreeInspector.ResolveControl( root, name, automationId, path )
				?? throw new InvalidOperationException( "Target control not found matching the provided selector." );
			return new
			{
				windowId = resolvedId,
				path = located.Path,
				result = VisualTreeInspector.SetControlValue( located.Element, value, selectedIndex, isChecked ),
			};
		}, cancellationToken );

	public Task<object> InvokeCommandAsync(
		string? target,
		string? commandName,
		JsonElement? parameter,
		bool listCommands,
		CancellationToken cancellationToken = default )
		=> OnUiAsync<object>( async () =>
		{
			object vm = ResolveViewModelTarget( target );
			PropertyInfo[] cmdProps = vm.GetType()
				.GetProperties( BindingFlags.Public | BindingFlags.Instance )
				.Where( p => typeof( ICommand ).IsAssignableFrom( p.PropertyType ) )
				.OrderBy( p => p.Name, StringComparer.Ordinal )
				.ToArray();

			if( listCommands || string.IsNullOrWhiteSpace( commandName ) )
			{
				return new
				{
					targetType = vm.GetType().Name,
					commands = cmdProps.Select( p =>
					{
						ICommand? c = p.GetValue( vm ) as ICommand;
						return new
						{
							name = p.Name,
							isAsync = c is IAsyncRelayCommand,
							canExecute = c?.CanExecute( null ) ?? false,
						};
					} ).ToList(),
				};
			}

			PropertyInfo? prop = cmdProps.FirstOrDefault( p =>
				string.Equals( p.Name, commandName, StringComparison.OrdinalIgnoreCase )
				|| string.Equals( p.Name, commandName + "Command", StringComparison.OrdinalIgnoreCase ) );
			if( prop is null || prop.GetValue( vm ) is not ICommand command )
				throw new InvalidOperationException( $"Command '{commandName}' not found on {vm.GetType().Name}. Available: {string.Join( ", ", cmdProps.Select( p => p.Name ) )}" );

			object? paramValue = parameter is { ValueKind: not ( JsonValueKind.Null or JsonValueKind.Undefined ) } el
				? OdooValues.FromJson( el )
				: null;

			if( !command.CanExecute( paramValue ) )
				throw new InvalidOperationException( $"Command '{prop.Name}' on {vm.GetType().Name} currently has CanExecute=false." );

			if( command is IAsyncRelayCommand asyncCmd )
				await asyncCmd.ExecuteAsync( paramValue );
			else
				command.Execute( paramValue );

			return new
			{
				ok = true,
				targetType = vm.GetType().Name,
				commandName = prop.Name,
				parameter = paramValue,
			};
		} );

	// ------------------------------------------------------------------ Navigation & Drawers

	public Task<object> NavigateAsync(
		string? page,
		bool goBack,
		string? statusAction,
		string? searchAction,
		bool openFileTypes,
		string? settingsSection,
		CancellationToken cancellationToken = default )
		=> OnUiAsync<object>( async () =>
		{
			HomeView home = HackApp.HomePage ?? throw new InvalidOperationException( "HomeView is not initialized." );

			if( goBack )
				home.GoBackInternal();

			if( !string.IsNullOrWhiteSpace( page ) )
			{
				if( !Enum.TryParse<NavigatePageMenu>( page, ignoreCase: true, out NavigatePageMenu target ) )
					throw new ArgumentException( $"Unknown page '{page}'. Valid: {string.Join( ", ", Enum.GetNames<NavigatePageMenu>() )}" );
				AppComposition.Navigation.NavigateTo( target );
				await Task.Delay( 80, cancellationToken );
			}

			if( !string.IsNullOrWhiteSpace( settingsSection ) )
			{
				if( home.ContentFrame.Content is not ApplicationSettingsView )
				{
					AppComposition.Navigation.NavigateTo( NavigatePageMenu.Settings );
					await WaitUntilUiAsync( () => home.ContentFrame.Content is ApplicationSettingsView { IsLoaded: true }, 5000, cancellationToken );
				}
				( home.ContentFrame.Content as ApplicationSettingsView )?.SelectSection( settingsSection );
			}

			if( !string.IsNullOrWhiteSpace( statusAction ) )
			{
				switch( statusAction.Trim().ToLowerInvariant() )
				{
					case "show": AppComposition.Dialogs.ShowStatusWindow(); break;
					case "hide": AppComposition.Dialogs.CloseStatusWindow(); break;
					case "toggle": AppComposition.Dialogs.ToggleStatusWindow(); break;
					case "dock": AppComposition.Dialogs.DockStatusWindow(); break;
					case "detach": AppComposition.Dialogs.DetachStatusWindow(); break;
					case "minimal": AppComposition.Dialogs.SetStatusMinimalMode( true ); AppComposition.Dialogs.ShowStatusWindow(); break;
					case "full": AppComposition.Dialogs.ShowFullStatusWindow(); break;
					default: throw new ArgumentException( $"Unknown statusAction '{statusAction}'." );
				}
			}

			if( !string.IsNullOrWhiteSpace( searchAction ) )
			{
				switch( searchAction.Trim().ToLowerInvariant() )
				{
					case "show": AppComposition.Dialogs.ShowSearchWindow(); break;
					case "hide": AppComposition.Dialogs.CloseSearchWindow(); break;
					case "toggle": AppComposition.Dialogs.ToggleSearchWindow(); break;
					case "dock": AppComposition.Dialogs.DockSearchWindow(); break;
					case "detach": AppComposition.Dialogs.DetachSearchWindow(); break;
					case "minimal": AppComposition.Dialogs.SetSearchMinimalMode( true ); AppComposition.Dialogs.ShowSearchWindow(); break;
					case "full": AppComposition.Dialogs.ShowFullSearchWindow(); break;
					case "swapside": home.SwapSearchDockSide(); break;
					default: throw new ArgumentException( $"Unknown searchAction '{searchAction}'." );
				}
			}

			if( openFileTypes )
			{
				WindowHelper.CreateWindowAndPage<OdooFileTypeManagerView>( out var typesPage, out var typesWindow );
				typesWindow.Title = "File Types & Filters";
				typesPage.StoreWindowInstance( typesWindow );
			}

			return new
			{
				ok = true,
				activeMenu = home.CurrentMenu.ToString(),
				activePageType = home.ContentFrame.Content?.GetType().Name,
				isStatusDrawerOpen = home.IsStatusDrawerOpen,
				isSearchDrawerOpen = home.IsSearchDrawerOpen,
			};
		} );

	// ------------------------------------------------------------------ File Manager Screen Controls

	public Task<object> FileManagerSelectFolderAsync(
		string? treePath,
		int? directoryId,
		bool? expand,
		bool waitForEntries,
		int timeoutMs,
		CancellationToken cancellationToken = default )
		=> OnUiAsync<object>( async () =>
		{
			HackFileManagerView fm = await EnsureFileManagerLoadedAsync( timeoutMs, cancellationToken );
			TreeView tree = fm.GetOdooDirectoryTree();

			TreeData? targetData = null;
			if( !string.IsNullOrWhiteSpace( treePath ) )
			{
				targetData = tree.FindTreeNode( treePath )
					?? FlattenTree( tree.RootNodes.Select( n => n.LinkedData ) )
						.FirstOrDefault( d => string.Equals( d.FullPath, treePath, StringComparison.OrdinalIgnoreCase )
							|| string.Equals( d.Name, treePath, StringComparison.OrdinalIgnoreCase ) );
			}
			else if( directoryId is int id )
			{
				targetData = FlattenTree( tree.RootNodes.Select( n => n.LinkedData ) )
					.FirstOrDefault( d => d.DirectoryId == id );
			}
			else
			{
				throw new ArgumentException( "Specify either 'treePath' or 'directoryId'." );
			}

			if( targetData?.Node is not { } node )
				throw new InvalidOperationException( $"Folder '{treePath ?? directoryId?.ToString()}' was not found in the File Manager tree." );

			fm.SelectDirectoryForMcp( node );
			if( expand is bool exp )
				targetData.IsExpanded = exp;

			if( waitForEntries )
			{
				await WaitUntilUiAsync( () => fm.ViewModel.IsListLoaded && ( fm.TreeSelectionTask is null or { IsCompleted: true } ), timeoutMs, cancellationToken );
			}

			return new
			{
				ok = true,
				selectedFolder = new
				{
					name = targetData.Name,
					directoryId = targetData.DirectoryId,
					fullPath = targetData.FullPath,
					isExpanded = targetData.IsExpanded,
					origin = targetData.Origin.ToString(),
				},
				entryCount = fm.ViewModel.Entries.Count,
				entries = fm.ViewModel.Entries.Take( 200 ).Select( SerializeEntryRow ).ToList(),
			};
		} );

	public Task<object> FileManagerSelectEntriesAsync(
		IReadOnlyList<string>? names,
		IReadOnlyList<int>? entryIds,
		bool clear,
		bool waitForDetails,
		int timeoutMs,
		CancellationToken cancellationToken = default )
		=> OnUiAsync<object>( async () =>
		{
			HackFileManagerView fm = await EnsureFileManagerLoadedAsync( timeoutMs, cancellationToken );
			var grid = fm.GetOdooEntryList();

			grid.SelectedItems.Clear();
			if( !clear )
			{
				List<EntryRow> toSelect = [];
				foreach( EntryRow row in fm.ViewModel.Entries )
				{
					bool matchName = names is { Count: > 0 } && row.Name is { } n && names.Contains( n, StringComparer.OrdinalIgnoreCase );
					bool matchId = entryIds is { Count: > 0 } && row.Id is int id && entryIds.Contains( id );
					if( matchName || matchId )
						toSelect.Add( row );
				}

				if( toSelect.Count == 1 )
					grid.SelectedItem = toSelect[ 0 ];
				else
					foreach( EntryRow row in toSelect )
						grid.SelectedItems.Add( row );

				if( toSelect.Count > 0 )
					grid.ScrollIntoView( toSelect[ 0 ] );

				if( waitForDetails && toSelect.Count == 1 )
					await WaitUntilUiAsync( () => !fm.ViewModel.IsDetailsLoading, timeoutMs, cancellationToken );
			}

			return new
			{
				ok = true,
				selectedCount = fm.ViewModel.SelectedEntries.Count,
				selectedEntries = fm.ViewModel.SelectedEntries.Select( SerializeEntryRow ).ToList(),
				detailTab = fm.ViewModel.SelectedDetailTab,
				histories = fm.ViewModel.OHistories.Select( h => new { h.Version, modUser = h.ModUser?.name, h.ModDate, h.Size } ).ToList(),
				parents = fm.ViewModel.OParents.Select( p => new { p.Version, p.Name, p.BasePath } ).ToList(),
				children = fm.ViewModel.OChildren.Select( c => new { c.Version, c.Name, c.BasePath } ).ToList(),
				properties = fm.ViewModel.OProperties.Select( p => new { p.Version, p.Configuration, p.Name, type = p.Type.ToString(), value = p.ValueData } ).ToList(),
				versionInfo = fm.ViewModel.OVersions.Select( v => new { v.Id, v.Name, v.Checksum, v.FileSize, v.ModifyDate, v.OdooCompletePath } ).ToList(),
			};
		} );

	public Task<object> FileManagerControlAsync( JsonElement? rawArgs, CancellationToken cancellationToken = default )
		=> OnUiAsync<object>( async () =>
		{
			McpToolArgs args = new( rawArgs );
			HackFileManagerView fm = await EnsureFileManagerLoadedAsync( 30_000, cancellationToken );

			if( args.GetString( "topTab" ) is { Length: > 0 } topTab )
				fm.ShowTopTab( string.Equals( topTab, "Changes", StringComparison.OrdinalIgnoreCase ) );

			if( args.GetString( "detailTab" ) is { Length: > 0 } detailTab )
			{
				if( !fm.SelectDetailTabByName( detailTab ) )
					throw new ArgumentException( $"Detail tab '{detailTab}' not found." );
			}

			if( args.TryGetProperty( "folderFilter", out _ ) )
				fm.SetFolderFilter( args.GetString( "folderFilter", "" )! );

			if( args.GetNullableBool( "showInactive" ) is bool showInactive )
				fm.ViewModel.ShowInactive = showInactive;

			if( args.GetNullableBool( "groupingEnabled" ) is bool grouping )
				fm.ViewModel.IsGroupingEnabled = grouping;

			if( args.GetString( "groupBy" ) is { Length: > 0 } groupBy )
				fm.ViewModel.GroupByCommand.Execute( groupBy );

			if( args.GetString( "sortBy" ) is { Length: > 0 } sortBy )
				fm.ViewModel.SortEntries( sortBy, args.GetBool( "descending", false ) );

			if( args.GetString( "layoutPreset" ) is { Length: > 0 } presetName )
			{
				if( !Enum.TryParse<WorkspaceLayoutPreset>( presetName, ignoreCase: true, out var preset ) )
					throw new ArgumentException( $"Unknown layoutPreset '{presetName}'." );
				fm.ApplyLayoutPresetForTest( preset );
			}

			bool layoutDirty = false;
			if( args.GetNullableBool( "showFoldersPane" ) is bool sf ) { UIStorage.WorkspaceLayout.ShowFoldersPane = sf; layoutDirty = true; }
			if( args.GetNullableBool( "showDetailsPane" ) is bool sd ) { UIStorage.WorkspaceLayout.ShowDetailsPane = sd; layoutDirty = true; }
			if( args.GetNullableBool( "showPreviewPane" ) is bool sp ) { UIStorage.WorkspaceLayout.ShowPreviewPane = sp; layoutDirty = true; }
			if( layoutDirty )
				fm.ApplyWorkspaceLayoutForMcp();

			if( args.GetBool( "expandAll", false ) )
				fm.ExpandAllForMcp();
			if( args.GetBool( "collapseAll", false ) )
				fm.CollapseAllForMcp();
			if( args.GetBool( "togglePip", false ) )
				fm.TogglePipForMcp();
			if( args.GetBool( "refresh", false ) )
				fm.ViewModel.RefreshFromServer();

			return SnapshotFileManager( fm );
		} );

	// ------------------------------------------------------------------ Dependency Graph Screen Controls

	public Task<object> DependencyGraphControlAsync( JsonElement? rawArgs, CancellationToken cancellationToken = default )
		=> OnUiAsync<object>( async () =>
		{
			McpToolArgs args = new( rawArgs );
			HomeView home = HackApp.HomePage ?? throw new InvalidOperationException( "HomeView is not initialized." );
			if( home.ContentFrame.Content is not DependencyGraphView )
			{
				AppComposition.Navigation.NavigateTo( NavigatePageMenu.DependencyGraph );
				await WaitUntilUiAsync( () => home.ContentFrame.Content is DependencyGraphView { IsLoaded: true }, 15_000, cancellationToken );
			}
			DependencyGraphView dg = (DependencyGraphView)home.ContentFrame.Content;
			if( dg.PendingLoad is { IsCompleted: false } initLoad )
				await initLoad;

			bool resetFilters = args.GetBool( "resetFilters", false );
			if( resetFilters )
				await dg.ResetFiltersForMcpAsync( cancellationToken );

			string? folderPathArg = args.GetString( "folderPath" );
			if( folderPathArg is { Length: > 0 } folderPath )
			{
				TreeData? folder = dg.Folders.FindTreeNode( folderPath )
					?? FlattenTree( dg.Folders.RootNodes.Select( n => n.LinkedData ) )
						.FirstOrDefault( d => string.Equals( d.FullPath, folderPath, StringComparison.OrdinalIgnoreCase )
							|| string.Equals( d.Name, folderPath, StringComparison.OrdinalIgnoreCase ) );
				if( folder?.Node is not null )
					await dg.OpenFolderAsync( folder.Node );
				else if( !args.HasProperty( "viewMode" ) && dg.ViewMode == GraphViewMode.Dependencies )
					throw new InvalidOperationException( $"Folder '{folderPath}' not found in Dependency Graph tree." );
			}

			bool hasCriteriaChanges = HasAnyGraphCriteriaArgs( args )
				|| args.HasProperty( "entryIds" )
				|| args.HasProperty( "directoryId" );

			if( args.GetNullableBool( "selectAll" ) is bool sa )
				dg.SetSelectAll( sa );

			if( args.GetStringList( "selectEntries" ) is { Count: > 0 } entryNames )
				dg.SelectEntries( [ .. entryNames ] );

			if( hasCriteriaChanges )
			{
				GraphFilterCriteria baseline = resetFilters ? GraphFilterCriteria.Default : dg.CurrentCriteria;
				GraphFilterCriteria next = HackPdmApiService.ParseGraphFilterCriteria( args, baseline );
				await dg.ApplyQueryForMcpAsync(
					next,
					entryIds: args.GetIntList( "entryIds" ),
					folderPath: folderPathArg,
					directoryId: args.GetInt( "directoryId" ),
					resetMissingFilters: resetFilters,
					cancellationToken: cancellationToken );
			}
			else if( dg.PendingLoad is { IsCompleted: false } loadTask )
			{
				await loadTask;
			}

			if( args.GetInt( "expandNodeId" ) is int expandId and > 0 )
			{
				GraphDirection expandDir = args.GetString( "expandDirection" ) is { Length: > 0 } expText
					? HackPdmApiService.ParseGraphDirection( expText )
					: GraphDirection.Both;
				await dg.ExpandNodeForMcpAsync( expandId, expandDir, cancellationToken );
			}

			if( args.GetDouble( "zoomBy" ) is double zoomFactor )
				dg.GraphView.ZoomBy( zoomFactor );
			if( args.GetDouble( "panX" ) is double px || args.GetDouble( "panY" ) is double py )
				dg.GraphView.PanBy( args.GetDouble( "panX" ) ?? 0, args.GetDouble( "panY" ) ?? 0 );
			if( args.GetBool( "fit", false ) )
				dg.GraphView.Fit();

			if( args.GetInt( "selectNodeId" ) is int nodeId )
			{
				dg.GraphView.SelectNode( nodeId );
				if( dg.DetailsLoad is { IsCompleted: false } dl )
					await dl;
			}
			if( args.GetString( "detailTab" ) is { Length: > 0 } tabName )
			{
				dg.TryShowDetailTab( tabName );
				if( dg.DetailsLoad is { IsCompleted: false } dl )
					await dl;
			}
			if( args.GetBool( "closeDetails", false ) )
				dg.CloseDetailsForTest();

			return SnapshotDependencyGraph( dg );
		} );

	private static bool HasAnyGraphCriteriaArgs( McpToolArgs args )
	{
		string[] keys =
		[
			"viewMode", "direction", "maxDepth", "maxChildDepth", "offspringDepth",
			"maxParentDepth", "parentDepth", "maxChildBranches", "maxOffspringBranches",
			"maxParentBranches", "parentBranchLimit", "maxNodes",
			"extensions", "fileTypes", "excludeExtensions", "excludeFileTypes",
			"nameFilter", "pathFilter", "categories", "checkedOutOnly", "checkoutUser",
			"includeDeleted", "minSizeBytes", "maxSizeBytes",
			"propertyName", "propertyOperator", "propertyValue", "groupByProperty",
			"includeDependencyLinks", "keepRoots", "bridgeFilteredEdges",
		];
		foreach( string k in keys )
			if( args.HasProperty( k ) )
				return true;
		return false;
	}

	// ------------------------------------------------------------------ Search, Status, Theme, Dialogs, Screenshots

	public Task<object> SearchControlAsync( JsonElement? rawArgs, CancellationToken cancellationToken = default )
		=> OnUiAsync<object>( async () =>
		{
			McpToolArgs args = new( rawArgs );
			var vm = AppComposition.SearchOdoo;
			if( args.GetBool( "openPanel", true ) )
				AppComposition.Dialogs.ShowSearchWindow();
			vm.LoadLookups();

			if( args.GetBool( "reset", false ) )
				vm.ResetCommand.Execute( null );

			if( args.TryGetProperty( "fileName", out _ ) )
				vm.FileName = args.GetString( "fileName", "" )!;
			if( args.GetString( "comparer" ) is { Length: > 0 } comp )
			{
				vm.SelectedComparer = vm.OperatorList.FirstOrDefault( o =>
					string.Equals( o.OpRepr, comp, StringComparison.OrdinalIgnoreCase )
					|| string.Equals( o.Operator.ToString(), comp, StringComparison.OrdinalIgnoreCase ) ) ?? vm.SelectedComparer;
			}
			if( args.GetNullableBool( "checkedOutMe" ) is bool cm )
				vm.CheckedOutMe = cm;
			if( args.GetNullableBool( "deletedRemotely" ) is bool dr )
				vm.DeletedRemotely = dr;
			if( args.GetNullableBool( "localOnly" ) is bool lo )
				vm.LocalOnly = lo;
			if( args.GetInt( "maxResults" ) is int mr )
				vm.MaxResults = mr.ToString();

			if( args.GetBool( "runSearch", false ) )
				await vm.SearchCommand.ExecuteAsync( null );

			if( args.GetString( "locateResultName" ) is { Length: > 0 } locateName )
			{
				var match = vm.SearchResultsList.FirstOrDefault( r => string.Equals( r.Name, locateName, StringComparison.OrdinalIgnoreCase ) )
					?? throw new InvalidOperationException( $"Search result '{locateName}' not found." );
				await vm.LocateCommand.ExecuteAsync( match );
			}

			return new
			{
				vm.FileName,
				comparer = vm.SelectedComparer?.OpRepr,
				vm.CheckedOutMe,
				vm.DeletedRemotely,
				vm.LocalOnly,
				vm.MaxResults,
				vm.IsSearching,
				vm.SearchStatusText,
				resultCount = vm.SearchResultsList.Count,
				results = vm.SearchResultsList.Take( 200 ).Select( r => new { r.Id, r.Name, r.Directory } ).ToList(),
			};
		} );

	public Task<object> StatusPanelControlAsync( JsonElement? rawArgs, CancellationToken cancellationToken = default )
		=> OnUiAsync<object>( () =>
		{
			McpToolArgs args = new( rawArgs );
			var vm = AppComposition.StatusLog;

			if( args.GetBool( "cancelOperation", false ) && vm.CancelCommand.CanExecute( null ) )
				vm.CancelCommand.Execute( null );
			if( args.GetBool( "clearLogs", false ) )
				vm.ClearLogs();
			if( args.GetNullableBool( "autoClose" ) is bool ac )
				vm.AutoClose = ac;
			if( args.GetInt( "historyLength" ) is int hl )
				vm.SetHistoryLength( hl );

			int maxItems = Math.Clamp( args.GetInt( "maxItems", 50 ) ?? 50, 0, 500 );
			object SerializeLogs( IEnumerable<HackPDM.Domain.Representation.BasicStatusMessage> src )
				=> src.TakeLast( maxItems ).Select( m => new { m.TimestampText, status = m.Status.ToString(), m.Category, m.Message } ).ToList();

			return new
			{
				vm.IsInProcess,
				vm.AutoClose,
				historyLength = StatusDialogViewModel.HistoryLength,
				vm.MaxCount,
				vm.TotalProcessed,
				vm.SkipCounter,
				vm.DownloadedBytes,
				vm.SessionDownloadedBytes,
				vm.UploadedBytes,
				vm.SessionUploadedBytes,
				vm.CompactProgressText,
				vm.ProcessingCount,
				vm.SuccessCount,
				vm.InfoCount,
				vm.WarningCount,
				vm.ErrorCount,
				statusLogs = SerializeLogs( vm.OStatus ),
				infoLogs = SerializeLogs( vm.OInfo ),
				warnings = SerializeLogs( vm.OWarnings ),
				errors = SerializeLogs( vm.OErrorsOnly ),
			};
		}, cancellationToken );

	public Task<object> ThemeControlAsync( JsonElement? rawArgs, CancellationToken cancellationToken = default )
		=> OnUiAsync<object>( async () =>
		{
			McpToolArgs args = new( rawArgs );
			var theme = AppComposition.Theme;
			bool mutated = false;

			if( args.GetBool( "revert", false ) )
			{
				theme.Revert();
				mutated = false;
			}

			if( args.GetBool( "resetToDefaults", false ) )
			{
				theme.ResetToFactoryDefaults( clearCustomThemes: args.GetBool( "clearCustomThemes", false ) );
				mutated = true;
			}

			if( args.GetString( "loadCustomThemeName" ) is { Length: > 0 } loadCustom )
			{
				theme.LoadCustomTheme( loadCustom );
				mutated = true;
			}

			if( args.GetString( "palette" ) is { Length: > 0 } paletteName )
			{
				if( !Enum.TryParse<PaletteName>( paletteName, ignoreCase: true, out var palette ) )
					throw new ArgumentException( $"Unknown palette '{paletteName}'. Valid: {string.Join( ", ", Enum.GetNames<PaletteName>() )}" );
				theme.SetPalette( palette );
				mutated = true;
			}

			if( args.GetString( "mode" ) is { Length: > 0 } modeName )
			{
				if( !Enum.TryParse<AppThemeMode>( modeName, ignoreCase: true, out var mode ) )
					throw new ArgumentException( $"Unknown theme mode '{modeName}'. Valid: {string.Join( ", ", Enum.GetNames<AppThemeMode>() )}" );
				theme.SetMode( mode );
				mutated = true;
			}

			if( args.GetString( "form" ) is { Length: > 0 } formName )
			{
				if( !Enum.TryParse<FormName>( formName, ignoreCase: true, out var form ) )
					throw new ArgumentException( $"Unknown form '{formName}'. Valid: {string.Join( ", ", Enum.GetNames<FormName>() )}" );
				theme.SetForm( form );
				mutated = true;
			}

			if( args.GetNullableBool( "showTreeBranchLines" ) is bool lines )
			{
				theme.SetShowTreeBranchLines( lines );
				mutated = true;
			}

			string targetTheme = args.GetString( "targetTheme", "active" )!;

			if( args.GetBool( "resetAll", false ) )
			{
				theme.ResetAll();
				mutated = true;
			}

			if( args.GetString( "resetColorKey" ) is { Length: > 0 } resetKey )
			{
				theme.ResetColor( resetKey, targetTheme );
				mutated = true;
			}

			if( args.GetString( "setColorKey" ) is { Length: > 0 } colorKey )
			{
				string hex = args.RequireString( "setColorHex" );
				if( !ArgbColor.TryParse( hex, out var parsedColor ) )
					throw new ArgumentException( $"Invalid hex color '{hex}'. Expected '#AARRGGBB' or '#RRGGBB'." );
				theme.SetColor( colorKey, parsedColor, targetTheme );
				mutated = true;
			}

			if( args.GetElement( "colors" ) is { ValueKind: JsonValueKind.Object } colorMap )
			{
				foreach( JsonProperty prop in colorMap.EnumerateObject() )
				{
					if( prop.Value.ValueKind == JsonValueKind.Null )
					{
						theme.ResetColor( prop.Name, targetTheme );
						mutated = true;
					}
					else if( prop.Value.ValueKind == JsonValueKind.String )
					{
						string hex = prop.Value.GetString()!;
						if( !ArgbColor.TryParse( hex, out var parsed ) )
							throw new ArgumentException( $"Invalid hex color '{hex}' for color key '{prop.Name}'." );
						theme.SetColor( prop.Name, parsed, targetTheme );
						mutated = true;
					}
				}
			}

			if( args.GetStringList( "removeColors" ) is { Count: > 0 } removeKeys )
			{
				foreach( string k in removeKeys )
					theme.ResetColor( k, targetTheme );
				mutated = true;
			}

			if( args.GetBool( "resetStyle", false ) )
			{
				theme.ResetStyleOverrides();
				mutated = true;
			}

			if( args.GetElement( "style" ) is { ValueKind: JsonValueKind.Object } styleObj )
			{
				theme.ApplyStyleOverrides( s => HackPdmApiService.ApplyStylePatch( s, styleObj ) );
				mutated = true;
			}

			if( args.GetBool( "resetGraph", false ) )
			{
				theme.ResetGraphOverrides();
				mutated = true;
			}

			if( args.GetElement( "graph" ) is { ValueKind: JsonValueKind.Object } graphObj )
			{
				theme.ApplyGraphOverrides( g => HackPdmApiService.ApplyGraphPatch( g, graphObj ) );
				mutated = true;
			}

			if( args.GetString( "deleteCustomThemeName" ) is { Length: > 0 } delName )
			{
				theme.DeleteCustomTheme( delName );
				mutated = true;
			}

			if( args.GetString( "saveCustomThemeName" ) is { Length: > 0 } saveName )
			{
				theme.SaveCustomTheme( saveName, args.GetString( "customThemeDescription" ) );
				mutated = true;
			}

			string autoApplyRestart = ( args.GetString( "autoApplyRestart", "none" ) ?? "none" ).Trim().ToLowerInvariant();
			bool shouldSave = args.GetBool( "save", false ) || autoApplyRestart is "soft" or "auto" or "full";
			string? restorePointId = null;

			if( shouldSave )
			{
				McpRestorePoint? rp = await McpBridgeHost.RestorePoints.CapturePreMutationAsync(
					"ui_theme_control",
					"Save Theme changes to AppConfig.json",
					[],
					includeAppConfig: true,
					cancellationToken: cancellationToken );
				restorePointId = rp?.Id;
				theme.Save();
			}

			object? restartInfo = null;
			if( autoApplyRestart is "soft" or "auto" or "full" )
			{
				bool preserveNav = args.GetBool( "preserveNavigation", true );
				bool preserveConn = args.GetBool( "preserveConnection", true );
				restartInfo = await ExecuteRestartOnUiThreadAsync( autoApplyRestart, saveConfig: false, preserveNav, preserveConn );
			}

			return new
			{
				source = "live_gui",
				mutated,
				saved = shouldSave,
				restorePointId,
				restart = restartInfo,
				theme = SnapshotTheme(),
			};
		} );

	public Task<object> CustomizeConfigAsync( JsonElement? rawArgs, CancellationToken cancellationToken = default )
		=> OnUiAsync<object>( async () =>
		{
			McpToolArgs args = new( rawArgs );
			GlobalSerializedData cfg = UIStorage.GlobalData;
			bool mutated = false;
			bool layoutChanged = false;
			bool themeReset = false;

			if( args.GetString( "resetSection" ) is { Length: > 0 } resetSec )
			{
				switch( resetSec.Trim().ToLowerInvariant() )
				{
					case "entrycolors" or "odooentrycolors":
						cfg.EColors = new();
						mutated = true;
						break;
					case "columns" or "columnsettings":
						cfg.ColumnsSettings = new();
						mutated = true;
						break;
					case "workspacelayout" or "layout":
						cfg.WorkspaceLayout = new();
						mutated = true;
						layoutChanged = true;
						break;
					case "windowsettings" or "windows":
						cfg.Win = new();
						WindowConfig.PresetWindowConfig = cfg.Win.PresetWindowConfig;
						mutated = true;
						break;
					case "theme":
						AppComposition.Theme.ResetToFactoryDefaults( clearCustomThemes: args.GetBool( "clearCustomThemes", false ) );
						cfg.Theme = AppComposition.Theme.Settings;
						mutated = true;
						themeReset = true;
						break;
					case "all":
						cfg.EColors = new();
						cfg.ColumnsSettings = new();
						cfg.WorkspaceLayout = new();
						cfg.Win = new();
						WindowConfig.PresetWindowConfig = cfg.Win.PresetWindowConfig;
						AppComposition.Theme.ResetToFactoryDefaults( clearCustomThemes: args.GetBool( "clearCustomThemes", false ) );
						cfg.Theme = AppComposition.Theme.Settings;
						mutated = true;
						layoutChanged = true;
						themeReset = true;
						break;
					default:
						throw new ArgumentException( $"Unknown resetSection '{resetSec}'. Valid: entryColors, columns, workspaceLayout, windowSettings, theme, all." );
				}
			}

			if( args.GetElement( "entryColors" ) is { ValueKind: JsonValueKind.Object } ecObj )
			{
				foreach( JsonProperty p in ecObj.EnumerateObject() )
				{
					if( p.Value.ValueKind != JsonValueKind.String )
						continue;
					PropertyInfo? prop = typeof( ColorSettings ).GetProperty( p.Name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase );
					if( prop is null || prop.PropertyType != typeof( Windows.UI.Color ) )
						throw new ArgumentException( $"Unknown entry color property '{p.Name}'." );
					string hex = p.Value.GetString()!;
					if( !ArgbColor.TryParse( hex, out var parsed ) )
						throw new ArgumentException( $"Invalid hex color '{hex}' for entryColors.{p.Name}." );
					prop.SetValue( cfg.EColors, Windows.UI.Color.FromArgb( parsed.A, parsed.R, parsed.G, parsed.B ) );
					mutated = true;
				}
			}

			if( args.GetElement( "columns" ) is { ValueKind: JsonValueKind.Object } colsObj )
			{
				if( colsObj.TryGetProperty( "rowHeight", out JsonElement rh ) && rh.ValueKind == JsonValueKind.Number )
					cfg.ColumnsSettings.RowHeight = Math.Clamp( rh.GetDouble(), 12, 80 );
				if( colsObj.TryGetProperty( "fontSize", out JsonElement fs ) && fs.ValueKind == JsonValueKind.Number )
					cfg.ColumnsSettings.FontSize = Math.Clamp( fs.GetDouble(), 7, 32 );
				if( colsObj.TryGetProperty( "groupingEnabled", out JsonElement ge ) && ge.ValueKind is JsonValueKind.True or JsonValueKind.False )
					cfg.ColumnsSettings.GroupingEnabled = ge.GetBoolean();
				if( colsObj.TryGetProperty( "groupCollapsed", out JsonElement gc ) && gc.ValueKind is JsonValueKind.True or JsonValueKind.False )
					cfg.ColumnsSettings.GroupCollapsed = gc.GetBoolean();
				if( colsObj.TryGetProperty( "groupByField", out JsonElement gbf ) && gbf.ValueKind == JsonValueKind.String )
					cfg.ColumnsSettings.GroupBy.FieldName = gbf.GetString() ?? string.Empty;
				if( colsObj.TryGetProperty( "groupByDirection", out JsonElement gbd ) && gbd.ValueKind == JsonValueKind.String
					&& Enum.TryParse<SortDirection>( gbd.GetString(), ignoreCase: true, out SortDirection dir ) )
				{
					cfg.ColumnsSettings.GroupBy.SortDirection = dir;
				}
				if( colsObj.TryGetProperty( "columns", out JsonElement perCol ) && perCol.ValueKind == JsonValueKind.Object )
				{
					foreach( JsonProperty cp in perCol.EnumerateObject() )
					{
						if( cp.Value.ValueKind != JsonValueKind.Object )
							continue;
						if( !cfg.ColumnsSettings.Columns.TryGetValue( cp.Name, out OdooEntryColumnID? colId ) || colId is null )
						{
							colId = new OdooEntryColumnID { BindingPath = cp.Name, Column = new OdooEntryColumn { Header = cp.Name } };
							cfg.ColumnsSettings.Columns[ cp.Name ] = colId;
						}
						colId.Column ??= new OdooEntryColumn { Header = cp.Name };
						if( cp.Value.TryGetProperty( "displayIndex", out JsonElement di ) && di.TryGetInt32( out int idx ) )
							colId.Column.DisplayIndex = idx;
						if( cp.Value.TryGetProperty( "header", out JsonElement hd ) && hd.ValueKind == JsonValueKind.String )
							colId.Column.Header = hd.GetString()!;
						if( cp.Value.TryGetProperty( "width", out JsonElement wd ) && wd.ValueKind == JsonValueKind.Number )
							colId.Column.Width = Math.Clamp( wd.GetDouble(), 24, 1200 );
						if( cp.Value.TryGetProperty( "autoSize", out JsonElement asz ) && asz.ValueKind is JsonValueKind.True or JsonValueKind.False )
							colId.Column.AutoSize = asz.GetBoolean();
					}
				}
				mutated = true;
			}

			if( args.GetElement( "workspaceLayout" ) is { ValueKind: JsonValueKind.Object } layoutObj )
			{
				WorkspaceLayoutSettings wl = cfg.WorkspaceLayout;
				if( layoutObj.TryGetProperty( "preset", out JsonElement lp ) && lp.ValueKind == JsonValueKind.String
					&& Enum.TryParse<WorkspaceLayoutPreset>( lp.GetString(), ignoreCase: true, out var preset ) )
				{
					wl.ApplyPreset( preset );
				}
				if( layoutObj.TryGetProperty( "showFoldersPane", out JsonElement sfp ) ) wl.ShowFoldersPane = sfp.GetBoolean();
				if( layoutObj.TryGetProperty( "foldersDock", out JsonElement fd ) && Enum.TryParse<FolderDockSide>( fd.GetString(), true, out var fds ) ) wl.FoldersDock = fds;
				if( layoutObj.TryGetProperty( "foldersPaneWidth", out JsonElement fpw ) ) wl.FoldersPaneWidth = Math.Clamp( fpw.GetDouble(), 120, 900 );
				if( layoutObj.TryGetProperty( "showDetailsPane", out JsonElement sdp ) ) wl.ShowDetailsPane = sdp.GetBoolean();
				if( layoutObj.TryGetProperty( "detailsDock", out JsonElement dd ) && Enum.TryParse<DetailsDockPosition>( dd.GetString(), true, out var ddp ) ) wl.DetailsDock = ddp;
				if( layoutObj.TryGetProperty( "detailsPaneHeight", out JsonElement dph ) ) wl.DetailsPaneHeight = Math.Clamp( dph.GetDouble(), 100, 800 );
				if( layoutObj.TryGetProperty( "detailsRightWidth", out JsonElement drw ) ) wl.DetailsRightWidth = Math.Clamp( drw.GetDouble(), 160, 1000 );
				if( layoutObj.TryGetProperty( "showPreviewPane", out JsonElement spp ) ) wl.ShowPreviewPane = spp.GetBoolean();
				if( layoutObj.TryGetProperty( "previewPlacement", out JsonElement pp ) && Enum.TryParse<PreviewDockPlacement>( pp.GetString(), true, out var pdp ) ) wl.PreviewPlacement = pdp;
				if( layoutObj.TryGetProperty( "previewPaneSize", out JsonElement pps ) ) wl.PreviewPaneSize = Math.Clamp( pps.GetDouble(), 100, 900 );
				if( layoutObj.TryGetProperty( "graphFoldersWidth", out JsonElement gfw ) ) wl.GraphFoldersWidth = Math.Clamp( gfw.GetDouble(), 120, 800 );
				if( layoutObj.TryGetProperty( "graphFilesWidth", out JsonElement gfiw ) ) wl.GraphFilesWidth = Math.Clamp( gfiw.GetDouble(), 120, 800 );
				if( layoutObj.TryGetProperty( "searchDock", out JsonElement sd ) && Enum.TryParse<FolderDockSide>( sd.GetString(), true, out var sds ) ) wl.SearchDock = sds;
				if( layoutObj.TryGetProperty( "searchDrawerWidth", out JsonElement sdw ) ) wl.SearchDrawerWidth = Math.Clamp( sdw.GetDouble(), 200, 1000 );
				if( layoutObj.TryGetProperty( "statusDrawerHeight", out JsonElement sdh ) ) wl.StatusDrawerHeight = Math.Clamp( sdh.GetDouble(), 100, 800 );
				if( layoutObj.TryGetProperty( "searchMinimalMode", out JsonElement smm ) ) wl.SearchMinimalMode = smm.GetBoolean();
				if( layoutObj.TryGetProperty( "statusMinimalMode", out JsonElement stmm ) ) wl.StatusMinimalMode = stmm.GetBoolean();
				mutated = true;
				layoutChanged = true;
			}

			if( args.GetElement( "windowSettings" ) is { ValueKind: JsonValueKind.Object } winObj )
			{
				bool applyToOpen = args.GetBool( "applyToOpenWindows", true );
				foreach( JsonProperty wp in winObj.EnumerateObject() )
				{
					if( wp.Value.ValueKind != JsonValueKind.Object )
						continue;
					if( !cfg.Win.PresetWindowConfig.TryGetValue( wp.Name, out WindowConfig? wc ) || wc is null )
					{
						wc = new WindowConfig { Title = wp.Name };
						cfg.Win.PresetWindowConfig[ wp.Name ] = wc;
					}
					if( wp.Value.TryGetProperty( "title", out JsonElement tEl ) && tEl.ValueKind == JsonValueKind.String )
						wc.Title = tEl.GetString()!;
					if( wp.Value.TryGetProperty( "windowKind", out JsonElement wk ) && wk.ValueKind == JsonValueKind.String
						&& Enum.TryParse<AppWindowPresenterKind>( wk.GetString(), ignoreCase: true, out var kind ) )
					{
						wc.WindowKind = kind;
					}
					var cur = wc.PositionAndSize;
					int x = wp.Value.TryGetProperty( "x", out JsonElement xEl ) && xEl.TryGetInt32( out int xi ) ? xi : cur.x;
					int y = wp.Value.TryGetProperty( "y", out JsonElement yEl ) && yEl.TryGetInt32( out int yi ) ? yi : cur.y;
					int w = wp.Value.TryGetProperty( "width", out JsonElement wEl ) && wEl.TryGetInt32( out int wi ) ? Math.Clamp( wi, 300, 7680 ) : cur.z;
					int h = wp.Value.TryGetProperty( "height", out JsonElement hEl ) && hEl.TryGetInt32( out int hi ) ? Math.Clamp( hi, 200, 4320 ) : cur.w;
					wc.PositionAndSize = ( x, y, w, h );

					if( applyToOpen )
					{
						Window? liveWin = wp.Name.Equals( "main", StringComparison.OrdinalIgnoreCase )
							|| wp.Name.Equals( "HackFileManager", StringComparison.OrdinalIgnoreCase )
							? HackApp.Window
							: WindowHelper.GetOpenWindows().FirstOrDefault( ow => ow.Id.Equals( wp.Name, StringComparison.OrdinalIgnoreCase ) ).Window;
						if( liveWin is not null )
						{
							if( wp.Value.TryGetProperty( "title", out _ ) )
								liveWin.Title = wc.Title;
							if( ( wp.Value.TryGetProperty( "width", out _ ) || wp.Value.TryGetProperty( "height", out _ ) ) && liveWin.AppWindow is { } aw )
								aw.Resize( new Windows.Graphics.SizeInt32 { Width = w, Height = h } );
						}
					}
					mutated = true;
				}
			}

			if( layoutChanged && HackApp.HomePage?.ContentFrame?.Content is HackFileManagerView fmView )
				fmView.ApplyWorkspaceLayoutForMcp();

			bool shouldSave = args.GetBool( "save", mutated );
			string? restorePointId = null;
			if( mutated && shouldSave )
			{
				McpRestorePoint? rp = await McpBridgeHost.RestorePoints.CapturePreMutationAsync(
					"ui_customize_config",
					"Save UI configuration changes to AppConfig.json",
					[],
					includeAppConfig: true,
					cancellationToken: cancellationToken );
				restorePointId = rp?.Id;
				cfg.Theme = AppComposition.Theme.Settings;
				AppConfigStore.Save( cfg );
			}

			bool softReload = args.GetBool( "softReload", false );
			string? restoredNav = null;
			if( softReload )
				restoredNav = await HackApp.SoftRestartUiAsync( saveConfig: false, preserveNavigation: true );

			return new
			{
				source = "live_gui",
				mutated,
				saved = mutated && shouldSave,
				restorePointId,
				softReloaded = softReload,
				restoredNav,
				themeReset,
				filePath = AppConfigStore.FilePath,
				entryColors = SnapshotEntryColors( cfg.EColors ),
				columns = cfg.ColumnsSettings,
				workspaceLayout = cfg.WorkspaceLayout,
				windowSettings = cfg.Win.PresetWindowConfig.ToDictionary(
					kv => kv.Key,
					kv => new
					{
						kv.Value.Title,
						x = kv.Value.PositionAndSize.x,
						y = kv.Value.PositionAndSize.y,
						width = kv.Value.PositionAndSize.z,
						height = kv.Value.PositionAndSize.w,
						windowKind = kv.Value.WindowKind.ToString(),
					} ),
				theme = SnapshotTheme(),
			};
		} );

	public Task<object> RestartAppAsync(
		string mode,
		bool saveConfig,
		bool preserveNavigation,
		bool preserveConnection,
		CancellationToken cancellationToken = default )
		=> OnUiAsync<object>( async () =>
		{
			if( saveConfig )
			{
				await McpBridgeHost.RestorePoints.CapturePreMutationAsync(
					"ui_app_restart",
					$"Pre-restart AppConfig backup ({mode})",
					[],
					includeAppConfig: true,
					cancellationToken: cancellationToken );
			}
			return await ExecuteRestartOnUiThreadAsync( mode, saveConfig, preserveNavigation, preserveConnection );
		} );

	private static async Task<object> ExecuteRestartOnUiThreadAsync(
		string mode,
		bool saveConfig,
		bool preserveNavigation,
		bool preserveConnection )
	{
		string normalized = ( mode ?? "auto" ).Trim().ToLowerInvariant();
		bool useFull = normalized switch
		{
			"full" or "process" or "hard" => true,
			"soft" or "ui" or "reload" => false,
			_ => AppComposition.Theme.NeedsRestart,
		};

		if( !useFull )
		{
			string? restoredNav = await HackApp.SoftRestartUiAsync( saveConfig, preserveNavigation );
			return new
			{
				restarted = true,
				mode = "soft",
				scheduledFullRestart = false,
				connectionPreserved = true,
				pid = Environment.ProcessId,
				restoredNavigation = restoredNav,
				needsRestart = AppComposition.Theme.NeedsRestart,
				styleNeedsRestart = AppComposition.Theme.StyleNeedsRestart,
			};
		}

		return HackApp.ScheduleFullRestart( saveConfig, preserveNavigation, preserveConnection );
	}

	public Task<object> GuideHighlightAsync( JsonElement? rawArgs, CancellationToken cancellationToken = default )
		=> OnUiAsync( () =>
		{
			McpToolArgs args = new( rawArgs );
			return GuidedActionOverlayManager.ShowHighlightAsync(
				targetName: args.GetString( "name" ),
				targetAutomationId: args.GetString( "automationId" ),
				targetText: args.GetString( "text" ),
				targetControlType: args.GetString( "controlType" ),
				page: args.GetString( "page" ),
				settingsSection: args.GetString( "settingsSection" ),
				animation: args.GetString( "animation", "pulse" )!,
				title: args.GetString( "title" ),
				message: args.GetString( "message" ),
				badgeText: args.GetString( "badgeText" ),
				severity: args.GetString( "severity", "action" )!,
				accentColorHex: args.GetString( "accentColor" ),
				durationMs: args.GetInt( "durationMs", 8000 ) ?? 8000,
				dismissOnTargetClick: args.GetBool( "dismissOnTargetClick", true ),
				replaceExisting: args.GetBool( "replaceExisting", true ),
				cancellationToken: cancellationToken );
		} );

	public Task<object> GuidePopupAsync( JsonElement? rawArgs, CancellationToken cancellationToken = default )
		=> OnUiAsync( () =>
		{
			McpToolArgs args = new( rawArgs );
			List<GuidedButtonSpec> buttons = ParseGuidedButtons( args.GetElement( "buttons" ) );
			return GuidedActionOverlayManager.ShowPopupAsync(
				title: args.RequireString( "title" ),
				message: args.RequireString( "message" ),
				badge: args.GetString( "badge" ),
				severity: args.GetString( "severity", "action" )!,
				accentColorHex: args.GetString( "accentColor" ),
				placement: args.GetString( "placement", "" )!,
				targetName: args.GetString( "targetName" ),
				targetAutomationId: args.GetString( "targetAutomationId" ),
				targetText: args.GetString( "targetText" ),
				targetControlType: args.GetString( "targetControlType" ),
				page: args.GetString( "page" ),
				settingsSection: args.GetString( "settingsSection" ),
				highlightAnimation: args.GetString( "highlightAnimation", "spotlight" )!,
				buttons: buttons,
				durationMs: args.GetInt( "durationMs", 12000 ) ?? 12000,
				waitForUser: args.GetBool( "waitForUser", false ),
				timeoutSeconds: args.GetInt( "timeoutSeconds", 120 ) ?? 120,
				replaceExisting: args.GetBool( "replaceExisting", true ),
				cancellationToken: cancellationToken );
		} );

	public Task<object> GuideTourAsync( JsonElement? rawArgs, CancellationToken cancellationToken = default )
		=> OnUiAsync( () =>
		{
			McpToolArgs args = new( rawArgs );
			List<GuidedTourStep>? steps = ParseGuidedTourSteps( args.GetElement( "steps" ) );
			return GuidedActionOverlayManager.ControlTourAsync(
				action: args.GetString( "action", "start" )!,
				tourTitle: args.GetString( "title" ),
				steps: steps,
				stepIndex: args.GetInt( "stepIndex" ),
				cancellationToken: cancellationToken );
		} );

	public Task<object> GuideClearAsync( string? cueId, bool listOnly, CancellationToken cancellationToken = default )
		=> OnUiAsync( () => GuidedActionOverlayManager.ClearOrList( cueId, listOnly ), cancellationToken );

	// ------------------------------------------------------------------ Dynamic Supplemental AI Windows

	public Task<object> DynamicWindowCreateAsync( JsonElement? rawArgs, CancellationToken cancellationToken = default )
	{
		DynamicWindowSpec spec = DynamicWindowSpecParser.ParseCreate( rawArgs );
		return OnUiAsync( () => DynamicWindowManager.CreateOrReplaceWindowAsync( spec ) );
	}

	public Task<object> DynamicWindowUpdateAsync( JsonElement? rawArgs, CancellationToken cancellationToken = default )
	{
		DynamicWindowUpdateSpec update = DynamicWindowSpecParser.ParseUpdate( rawArgs );
		return OnUiAsync( () => DynamicWindowManager.UpdateWindowAsync( update ) );
	}

	public Task<object> DynamicWindowListAsync( CancellationToken cancellationToken = default )
		=> OnUiAsync( () => DynamicWindowManager.ListWindows(), cancellationToken );

	public Task<object> DynamicWindowEventsAsync(
		string windowId,
		bool waitForEvent,
		bool clearEvents,
		int timeoutSeconds,
		CancellationToken cancellationToken = default )
		=> OnUiAsync( () => DynamicWindowManager.GetOrWaitEventsAsync(
			windowId,
			waitForEvent,
			clearEvents,
			timeoutSeconds,
			cancellationToken ) );

	public Task<object> DynamicWindowCloseAsync( string? windowId, bool closeAll, CancellationToken cancellationToken = default )
		=> OnUiAsync( () => DynamicWindowManager.CloseWindow( windowId, closeAll ), cancellationToken );

	public Task<object> CommandPaletteAsync( string action, string? query, CancellationToken cancellationToken = default )
		=> OnUiAsync<object>( () =>
		{
			string act = ( action ?? "open" ).Trim().ToLowerInvariant();
			if( act == "close" )
			{
				CommandPaletteManager.Close();
			}
			else if( act == "toggle" )
			{
				CommandPaletteManager.Toggle( null, query );
			}
			else
			{
				string? tab = act is "commands" or "tools" or "ai" or "connect" ? act : null;
				CommandPaletteManager.Open( tab, query );
			}
			return new { ok = true, isOpen = CommandPaletteManager.IsOpen, action = act };
		}, cancellationToken );

	private static List<GuidedButtonSpec> ParseGuidedButtons( JsonElement? raw )
	{
		List<GuidedButtonSpec> list = [];
		if( raw is not { ValueKind: JsonValueKind.Array } arr )
			return list;

		int idx = 0;
		foreach( JsonElement item in arr.EnumerateArray() )
		{
			idx++;
			if( item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } label )
			{
				string slug = new( label.ToLowerInvariant().Select( ch => char.IsLetterOrDigit( ch ) ? ch : '_' ).ToArray() );
				list.Add( new GuidedButtonSpec( string.IsNullOrWhiteSpace( slug ) ? $"btn_{idx}" : slug, label, idx == 1 ) );
			}
			else if( item.ValueKind == JsonValueKind.Object )
			{
				string lbl = item.TryGetProperty( "label", out JsonElement lEl ) && lEl.ValueKind == JsonValueKind.String
					? lEl.GetString()!
					: $"Action {idx}";
				string id = item.TryGetProperty( "id", out JsonElement idEl ) && idEl.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace( idEl.GetString() )
					? idEl.GetString()!
					: $"btn_{idx}";
				bool isAccent = item.TryGetProperty( "accent", out JsonElement aEl )
					? aEl.ValueKind == JsonValueKind.True
					: idx == 1;
				list.Add( new GuidedButtonSpec( id, lbl, isAccent ) );
			}
		}
		return list;
	}

	private static List<GuidedTourStep>? ParseGuidedTourSteps( JsonElement? raw )
	{
		if( raw is not { ValueKind: JsonValueKind.Array } arr )
			return null;

		List<GuidedTourStep> steps = [];
		foreach( JsonElement item in arr.EnumerateArray() )
		{
			if( item.ValueKind != JsonValueKind.Object )
				continue;
			string Str( string key ) => item.TryGetProperty( key, out JsonElement el ) && el.ValueKind == JsonValueKind.String ? el.GetString() ?? "" : "";
			string? OptStr( string key ) => item.TryGetProperty( key, out JsonElement el ) && el.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace( el.GetString() ) ? el.GetString() : null;

			steps.Add( new GuidedTourStep(
				Title: Str( "title" ),
				Message: Str( "message" ),
				Page: OptStr( "page" ),
				SettingsSection: OptStr( "settingsSection" ),
				TargetName: OptStr( "targetName" ) ?? OptStr( "name" ),
				TargetAutomationId: OptStr( "targetAutomationId" ) ?? OptStr( "automationId" ),
				TargetText: OptStr( "targetText" ) ?? OptStr( "text" ),
				TargetControlType: OptStr( "targetControlType" ) ?? OptStr( "controlType" ),
				Animation: OptStr( "animation" ) ?? "spotlight",
				BadgeText: OptStr( "badgeText" ),
				Severity: OptStr( "severity" ),
				AccentColorHex: OptStr( "accentColor" ) ) );
		}
		return steps;
	}

	public Task<object> DialogRespondAsync( string? dialogId, string? response, bool listOnly, CancellationToken cancellationToken = default )
		=> OnUiAsync<object>( () =>
		{
			if( listOnly || string.IsNullOrWhiteSpace( response ) )
			{
				var active = MessageBox.GetActiveDialogs();
				return new { activeCount = active.Count, dialogs = active };
			}

			if( !Enum.TryParse<DialogResult>( response, ignoreCase: true, out DialogResult result ) )
				throw new ArgumentException( $"Invalid dialog response '{response}'." );

			bool responded = MessageBox.TryRespond( dialogId, result, out var info );
			if( !responded )
				return new { ok = false, message = "No matching active MessageBox dialog found.", remaining = MessageBox.GetActiveDialogs() };

			return new { ok = true, respondedDialog = info, clicked = result.ToString(), remaining = MessageBox.GetActiveDialogs() };
		}, cancellationToken );

	public Task<McpToolResult> CaptureScreenshotAsync(
		string? windowId,
		string? elementName,
		string? savePath,
		bool includeImageContent,
		CancellationToken cancellationToken = default )
		=> OnUiAsync( async () =>
		{
			var (resolvedId, win, root) = ResolveWindow( windowId );
			UIElement target = root;
			if( !string.IsNullOrWhiteSpace( elementName ) )
			{
				var located = VisualTreeInspector.ResolveControl( root, elementName, null, null )
					?? throw new InvalidOperationException( $"Element '{elementName}' not found in window '{resolvedId}'." );
				target = located.Element as UIElement
					?? throw new InvalidOperationException( $"Control '{elementName}' is not a UIElement." );
			}

			RenderTargetBitmap bitmap = new();
			await bitmap.RenderAsync( target );
			var pixels = await bitmap.GetPixelsAsync();
			byte[] bgra = pixels.ToArray();
			byte[] png = Png.FromBgra( bgra, bitmap.PixelWidth, bitmap.PixelHeight );

			string? saved = null;
			if( !string.IsNullOrWhiteSpace( savePath ) )
			{
				saved = Path.GetFullPath( savePath );
				if( Path.GetDirectoryName( saved ) is { Length: > 0 } dir )
					Directory.CreateDirectory( dir );
				await File.WriteAllBytesAsync( saved, png, cancellationToken );
			}

			object meta = new
			{
				windowId = resolvedId,
				windowTitle = win.Title,
				elementName,
				width = bitmap.PixelWidth,
				height = bitmap.PixelHeight,
				byteLength = png.Length,
				savedPath = saved,
			};

			return includeImageContent
				? McpToolResult.OkImage( png, "image/png", meta )
				: McpToolResult.Ok( meta );
		} );

	public Task<string> GetAppConfigJsonAsync( CancellationToken cancellationToken = default )
		=> OnUiAsync( () => AppConfigJsonService.Format( AppConfigJsonService.Load() ), cancellationToken );

	public Task SaveAppConfigJsonAsync( string json, CancellationToken cancellationToken = default )
		=> OnUiAsync( () =>
		{
			AppConfigJsonService.Save( json );
			// Reload saved AppConfig.json immediately into live in-memory state, ThemeService, and active layout
			UIStorage.GlobalData = AppConfigStore.Load();
			WindowConfig.PresetWindowConfig = UIStorage.GlobalData.Win.PresetWindowConfig;
			AppComposition.Theme.Revert();
			if( HackApp.HomePage?.ContentFrame?.Content is HackFileManagerView fmView )
				fmView.ApplyWorkspaceLayoutForMcp();
			return true;
		}, cancellationToken );

	// ------------------------------------------------------------------ Live Synchronization Hooks

	public void NotifyLoginChanged( bool isLoggedIn, string? accountKey )
	{
		UiDispatcher.Post( () =>
		{
			AppComposition.Session.IsLoggedIn = isLoggedIn;
			if( !isLoggedIn )
			{
				AppComposition.FileManagerState.Reset();
			}
			else if( accountKey is not null && AppComposition.Session.LoggedInAs( accountKey ) )
			{
				AppComposition.FileManagerState.Reset();
			}
		} );
	}

	public void NotifyServerMutated( bool treeChanged, bool entriesChanged )
	{
		FileManagerRefresh what = ( treeChanged ? FileManagerRefresh.Tree : FileManagerRefresh.None )
			| ( entriesChanged ? FileManagerRefresh.Entries : FileManagerRefresh.None );
		if( what == FileManagerRefresh.None )
			return;

		UiDispatcher.Post( () =>
		{
			AppComposition.FileManagerState.MarkStale( what );
			if( HackApp.HomePage?.ContentFrame.Content is HackFileManagerView fm )
			{
				if( treeChanged )
					fm.ViewModel.RefreshFromServer();
				else
					_ = fm.ViewModel.LoadEntriesAsync();
			}
		} );
	}

	public void MirrorStatusLog( StatusMessage status, string message )
		=> AppComposition.StatusLog.AddLog( new HackPDM.Domain.Representation.BasicStatusMessage { Status = status, Message = message } );

	// ------------------------------------------------------------------ Snapshot & Lookup Helpers

	private static async Task<HackFileManagerView> EnsureFileManagerLoadedAsync( int timeoutMs, CancellationToken cancellationToken )
	{
		HomeView home = HackApp.HomePage ?? throw new InvalidOperationException( "HomeView is not initialized." );
		if( home.ContentFrame.Content is not HackFileManagerView )
		{
			AppComposition.Navigation.NavigateTo( NavigatePageMenu.HackFileManager );
			await WaitUntilUiAsync( () => home.ContentFrame.Content is HackFileManagerView { IsLoaded: true }, timeoutMs, cancellationToken );
		}
		HackFileManagerView fm = home.ContentFrame.Content as HackFileManagerView
			?? throw new InvalidOperationException( $"HackFileManagerView could not be opened (current page: {home.ContentFrame.Content?.GetType().Name ?? "none"}; ensure logged in)." );
		await WaitUntilUiAsync( () => fm.ViewModel.IsTreeLoaded, timeoutMs, cancellationToken );
		return fm;
	}

	private static async Task WaitUntilUiAsync( Func<bool> condition, int timeoutMs, CancellationToken cancellationToken )
	{
		Stopwatch sw = Stopwatch.StartNew();
		while( !condition() )
		{
			cancellationToken.ThrowIfCancellationRequested();
			if( sw.ElapsedMilliseconds >= timeoutMs )
				throw new TimeoutException( $"Timed out after {timeoutMs} ms waiting for UI condition." );
			await Task.Delay( 50, cancellationToken );
		}
	}

	private static object ResolveViewModelTarget( string? target )
	{
		object? activePage = HackApp.HomePage?.ContentFrame.Content;
		string key = string.IsNullOrWhiteSpace( target ) ? "active" : target.Trim().ToLowerInvariant();
		return key switch
		{
			"search" => AppComposition.SearchOdoo,
			"status" => AppComposition.StatusLog,
			"filemanager" => ( activePage as HackFileManagerView )?.ViewModel
				?? throw new InvalidOperationException( "HackFileManagerView is not the active page." ),
			"profilemanager" => ( activePage as ProfileManagerView )?.ViewModel
				?? throw new InvalidOperationException( "ProfileManagerView is not the active page." ),
			"odoosettings" => ( activePage as ConfigurationView )?.OdooSettingsPage?.ViewModel
				?? throw new InvalidOperationException( "OdooSettingsView is not active." ),
			"hacksettings" => ( activePage as ConfigurationView )?.HackSettingsPage?.ViewModel
				?? throw new InvalidOperationException( "HackSettingsView is not active." ),
			"theme" => ( ( activePage as ApplicationSettingsView )?.ActiveSectionPage as ThemeEditorView )?.ViewModel
				?? throw new InvalidOperationException( "ThemeEditorView is not active." ),
			"mcpsettings" or "mcp" => ( ( activePage as ApplicationSettingsView )?.ActiveSectionPage as McpSettingsView )?.ViewModel
				?? throw new InvalidOperationException( "McpSettingsView is not active." ),
			"jsoneditor" => ( activePage as JsonEditorView )?.ViewModel
				?? throw new InvalidOperationException( "JsonEditorView is not active." ),
			_ => activePage switch
			{
				HackFileManagerView fm => fm.ViewModel,
				ProfileManagerView pm => pm.ViewModel,
				JsonEditorView je => je.ViewModel,
				ApplicationSettingsView s when s.ActiveSectionPage is ThemeEditorView te => te.ViewModel,
				ApplicationSettingsView s when s.ActiveSectionPage is StatusSettingsView ss => ss.ViewModel,
				ApplicationSettingsView s when s.ActiveSectionPage is McpSettingsView ms => ms.ViewModel,
				ConfigurationView cfg when cfg.OdooSettingsPage is { } o => o.ViewModel,
				_ => throw new InvalidOperationException( $"Active page '{activePage?.GetType().Name ?? "none"}' does not expose a single ViewModel; pass explicit target." ),
			},
		};
	}

	private static object SnapshotFileManager( HackFileManagerView fm )
	{
		var vm = fm.ViewModel;
		return new
		{
			vm.IsTreeLoaded,
			vm.IsTreeLoading,
			vm.IsListLoaded,
			vm.IsEntriesLoading,
			vm.IsDetailsLoading,
			vm.IsPreviewLoading,
			vm.IsPreviewVisible,
			vm.ShowInactive,
			vm.IsGroupingEnabled,
			topTab = fm.CurrentTopTabName,
			detailTab = vm.SelectedDetailTab,
			folderFilter = fm.FolderFilterText,
			isPipOpen = fm.IsPipOpen,
			selectedDirectory = vm.SelectedDirectory is null ? null : new
			{
				vm.SelectedDirectory.Name,
				vm.SelectedDirectory.DirectoryId,
				vm.SelectedDirectory.TreePath,
				vm.SelectedDirectory.IsRemoteOnly,
			},
			breadcrumb = fm.BreadcrumbPath.Select( b => b.Name ).ToList(),
			labels = new
			{
				vm.EntriesLabel,
				vm.EntriesLabelLocal,
				vm.EntriesLabelRemote,
				vm.EntriesLabelModified,
				vm.SelectionLabel,
				vm.ChangesTabHeader,
			},
			entryCount = vm.Entries.Count,
			localChangeCount = vm.LocalChanges.Count,
			remoteChangeCount = vm.RemoteChanges.Count,
			selectedEntryNames = vm.SelectedEntries.Select( e => e.Name ).ToList(),
			layout = new
			{
				UIStorage.WorkspaceLayout.ShowFoldersPane,
				UIStorage.WorkspaceLayout.ShowDetailsPane,
				UIStorage.WorkspaceLayout.ShowPreviewPane,
				foldersDock = UIStorage.WorkspaceLayout.FoldersDock.ToString(),
				detailsDock = UIStorage.WorkspaceLayout.DetailsDock.ToString(),
				previewPlacement = UIStorage.WorkspaceLayout.PreviewPlacement.ToString(),
			},
		};
	}

	private static object SnapshotDependencyGraph( DependencyGraphView dg ) => new
	{
		viewMode = dg.ViewMode.ToString(),
		direction = dg.Direction.ToString(),
		criteria = HackPdmApiService.SerializeGraphCriteria( dg.CurrentCriteria ),
		stats = new
		{
			dg.CurrentStats.TotalNodesBeforeFilter,
			dg.CurrentStats.TotalEdgesBeforeFilter,
			dg.CurrentStats.FilteredNodes,
			dg.CurrentStats.FilteredEdges,
			dg.CurrentStats.BridgedEdgesAdded,
			dg.CurrentStats.NodesTrimmedByLimits,
			dg.CurrentStats.EdgesTrimmedByChildBranchLimit,
			dg.CurrentStats.EdgesTrimmedByParentBranchLimit,
			dg.CurrentStats.TruncatedByLimits,
		},
		selectedFolder = dg.Folders.SelectedNode?.LinkedData.FullPath,
		listedEntryCount = dg.Entries.Count,
		listedEntries = dg.Entries.Take( 100 ).Select( e => new { e.Id, e.Name } ).ToList(),
		roots = dg.Roots,
		zoom = dg.GraphView.Zoom,
		truncated = dg.CurrentGraph.Truncated,
		unfilteredNodeCount = dg.RawGraph.Nodes.Count,
		nodeCount = dg.CurrentGraph.Nodes.Count,
		edgeCount = dg.CurrentGraph.Edges.Count,
		nodes = dg.CurrentGraph.Nodes.Select( n => new
		{
			n.Id,
			n.Name,
			role = n.Role.ToString(),
			n.Ext,
			n.Path,
			n.Size,
			n.Category,
			n.CheckedOut,
			n.CheckoutUser,
			n.Deleted,
			n.Subtitle,
			n.ItemCount,
			n.OmittedChildBranches,
			n.OmittedParentBranches,
			n.Properties,
		} ).ToList(),
		edges = dg.CurrentGraph.Edges.Select( e => new { parentId = e.Parent, childId = e.Child } ).ToList(),
		detailsOpen = dg.DetailsOpen,
		detailTab = dg.CurrentDetailTabHeader,
		detailNode = dg.CurrentDetailNode is { } dn ? new { dn.Id, dn.Name, role = dn.Role.ToString(), dn.Path } : null,
		detailsText = dg.DetailsOpen ? dg.DetailsText : null,
	};

	private static object SnapshotProfileManager( ProfileManagerView pm )
	{
		var vm = pm.ViewModel;
		return new
		{
			vm.IsLoggedIn,
			vm.IsLoggingIn,
			vm.IsTestingConnection,
			vm.ConnectionBadgeText,
			vm.ConnectionTargetText,
			vm.LoginStatusText,
			vm.HasBanner,
			bannerStatus = vm.BannerStatus.ToString(),
			vm.BannerTitle,
			vm.BannerMessage,
			vm.ErrorCount,
			vm.WarningCount,
			vm.InfoCount,
			recentMessages = vm.FilteredStatus.Take( 25 ).Select( m => new { m.TimestampText, status = m.Status.ToString(), m.Category, m.Message } ).ToList(),
		};
	}

	private static object SnapshotTheme()
	{
		var theme = AppComposition.Theme;
		ThemeSettings s = theme.Settings;
		AppForm effForm = theme.EffectiveForm;
		uint effStroke = ( s.Style ?? new() ).ResolvePanelStroke( ThemePresets.Palette( theme.Palette ), effForm );

		return new
		{
			mode = theme.Mode.ToString(),
			palette = theme.Palette.ToString(),
			form = theme.Form.ToString(),
			isDarkActive = theme.IsDarkActive,
			showTreeBranchLines = theme.ShowTreeBranchLines,
			activeCustomTheme = s.ActiveCustomTheme,
			needsRestart = theme.NeedsRestart,
			styleNeedsRestart = theme.StyleNeedsRestart,
			savePath = theme.SavePath,
			availablePalettes = Enum.GetNames<PaletteName>(),
			availableForms = Enum.GetNames<FormName>(),
			availableColorKeys = ThemeColorCatalog.All.Select( c => new
			{
				c.Key,
				c.Label,
				c.Group,
				hex = theme.GetColor( c.Key )?.ToString(),
				isOverridden = theme.IsOverridden( c.Key ),
			} ).ToList(),
			darkOverrides = new Dictionary<string, string>( s.Dark ),
			lightOverrides = new Dictionary<string, string>( s.Light ),
			styleOverrides = s.Style,
			effectiveStyle = new
			{
				effForm.CompactDensity,
				effForm.ControlCornerRadius,
				effForm.OverlayCornerRadius,
				effForm.PanelCornerRadius,
				effForm.PanelBorderThickness,
				effForm.AccentPanels,
				panelStrokeColor = ThemeColorParse.FormatHex( effStroke ),
				fontFamily = s.Style?.FontFamily,
				baseFontSize = s.Style?.BaseFontSize,
				contentPadding = s.Style?.ContentPadding,
			},
			graphOverrides = s.Graph,
			effectiveGraph = HackPdmApiService.SummarizeGraphTheme( theme.GraphTheme ),
			customThemes = theme.ListCustomThemes().Select( t => new
			{
				t.Name,
				t.Description,
				t.Mode,
				palette = t.Palette.ToString(),
				form = t.Form.ToString(),
			} ).ToList(),
		};
	}

	private static Dictionary<string, string> SnapshotEntryColors( ColorSettings ec )
	{
		Dictionary<string, string> map = [];
		foreach( PropertyInfo p in typeof( ColorSettings ).GetProperties( BindingFlags.Public | BindingFlags.Instance ) )
		{
			if( p.PropertyType == typeof( Windows.UI.Color ) && p.GetValue( ec ) is Windows.UI.Color c )
				map[ p.Name ] = $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";
		}
		return map;
	}

	private static object SerializeEntryRow( EntryRow e ) => new
	{
		id = e.Id,
		name = e.Name,
		type = e.Type,
		size = e.Size,
		status = e.Status.ToString(),
		reprType = e.ReprType.ToString(),
		latestId = e.LatestId,
		latestReleaseId = e.LatestReleaseId,
		checkoutUser = e.Checkout?.name,
		category = e.Category?.name,
		remoteDate = e.RemoteDate,
		localDate = e.LocalDate,
		fullName = e.FullName,
		localPath = e.LocalFile?.FullName,
	};

	private static IEnumerable<TreeData> FlattenTree( IEnumerable<TreeData> nodes )
	{
		foreach( TreeData node in nodes )
		{
			yield return node;
			foreach( TreeData child in FlattenTree( node.Children ?? [] ) )
				yield return child;
		}
	}
}

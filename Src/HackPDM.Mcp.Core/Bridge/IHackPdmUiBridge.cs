using System.Text.Json;

using HackPDM.Mcp.Protocol;
using HackPDM.Shared.GlobalData;

namespace HackPDM.Mcp.Bridge;

/// <summary>
/// Live HackPDM desktop client UI &amp; application-state bridge implemented inside <c>HackPDM</c> (WinUI / Uno).
/// Gives MCP tools direct access to open windows, the live visual tree, XAML controls, MVVM commands,
/// navigation, drawers, dialogs, themes, screenshots, and UI-synchronized notifications.
/// </summary>
public interface IHackPdmUiBridge
{
	/// <summary>Complete live snapshot of the HackPDM desktop UI (windows, active page, drawers, dialogs, active ViewModel state).</summary>
	Task<object> GetUiStateAsync( CancellationToken cancellationToken = default );

	/// <summary>Lists all open HackPDM windows with IDs, titles, sizes, and root page types.</summary>
	Task<object> ListWindowsAsync( CancellationToken cancellationToken = default );

	/// <summary>Walks and serializes the XAML visual tree of a window or named subtree.</summary>
	Task<object> GetVisualTreeAsync(
		string? windowId,
		string? rootName,
		int maxDepth,
		bool includeInvisible,
		bool interactiveOnly,
		CancellationToken cancellationToken = default );

	/// <summary>Searches controls across an open window by name, type, text, or AutomationId.</summary>
	Task<object> FindControlsAsync(
		string? windowId,
		string? name,
		string? typeName,
		string? text,
		string? automationId,
		bool visibleOnly,
		bool interactiveOnly,
		int maxResults,
		CancellationToken cancellationToken = default );

	/// <summary>Inspects a single control in depth (properties, items, bounds, command, parent chain, children).</summary>
	Task<object> GetControlAsync(
		string? windowId,
		string? name,
		string? automationId,
		string? path,
		CancellationToken cancellationToken = default );

	/// <summary>Clicks or invokes an interactive control (Button, CheckBox, ToggleButton, ToggleSwitch, RadioButton, TabViewItem, NavigationViewItem, MenuFlyoutItem, etc.).</summary>
	Task<object> ClickControlAsync(
		string? windowId,
		string? name,
		string? automationId,
		string? path,
		string? text,
		CancellationToken cancellationToken = default );

	/// <summary>Sets the value or selection on an input/selector control (TextBox, PasswordBox, AutoSuggestBox, NumberBox, Slider, CheckBox, ToggleSwitch, ComboBox, TabView, ListView, SelectorBar).</summary>
	Task<object> SetControlValueAsync(
		string? windowId,
		string? name,
		string? automationId,
		string? path,
		JsonElement? value,
		int? selectedIndex,
		bool? isChecked,
		CancellationToken cancellationToken = default );

	/// <summary>Lists or invokes an <see cref="System.Windows.Input.ICommand"/> on the active page's ViewModel or a named ViewModel target.</summary>
	Task<object> InvokeCommandAsync(
		string? target,
		string? commandName,
		JsonElement? parameter,
		bool listCommands,
		CancellationToken cancellationToken = default );

	/// <summary>Navigates between HackPDM pages, navigates back, or controls Search / Status drawers, File Types window, and Settings sections.</summary>
	Task<object> NavigateAsync(
		string? page,
		bool goBack,
		string? statusAction,
		string? searchAction,
		bool openFileTypes,
		string? settingsSection,
		CancellationToken cancellationToken = default );

	/// <summary>Selects, expands, or collapses a directory in the live File Manager tree and optionally waits for entries to load.</summary>
	Task<object> FileManagerSelectFolderAsync(
		string? treePath,
		int? directoryId,
		bool? expand,
		bool waitForEntries,
		int timeoutMs,
		CancellationToken cancellationToken = default );

	/// <summary>Selects entry rows in the live File Manager list by name or ID, or clears selection, and optionally waits for details.</summary>
	Task<object> FileManagerSelectEntriesAsync(
		IReadOnlyList<string>? names,
		IReadOnlyList<int>? entryIds,
		bool clear,
		bool waitForDetails,
		int timeoutMs,
		CancellationToken cancellationToken = default );

	/// <summary>Controls live File Manager tabs, folder filter, ShowInactive, grouping, sorting, workspace layout presets/panes, PiP preview, and refresh.</summary>
	Task<object> FileManagerControlAsync( JsonElement? args, CancellationToken cancellationToken = default );

	/// <summary>Controls the live Dependency Graph view (folder, entries, selectAll, direction, zoom/pan/fit, node selection, detail tabs, focus/locate).</summary>
	Task<object> DependencyGraphControlAsync( JsonElement? args, CancellationToken cancellationToken = default );

	/// <summary>Controls the live Search Vault panel (filters, properties, run search, select results, locate/checkout/open/reset).</summary>
	Task<object> SearchControlAsync( JsonElement? args, CancellationToken cancellationToken = default );

	/// <summary>Reads or controls the live Status Log panel (progress, counters, log buffers, clear, cancel, autoClose, historyLength).</summary>
	Task<object> StatusPanelControlAsync( JsonElement? args, CancellationToken cancellationToken = default );

	/// <summary>Reads or modifies live ThemeService settings (palette, form, mode, custom brush colors, independent style/font/density overrides, graph overrides, custom themes, factory reset, save, revert, optional auto-restart).</summary>
	Task<object> ThemeControlAsync( JsonElement? args, CancellationToken cancellationToken = default );

	/// <summary>Reads or modifies any AppConfig.json UI section live (entryColors, columns, workspaceLayout, windowSettings, factory reset, save, softReload).</summary>
	Task<object> CustomizeConfigAsync( JsonElement? args, CancellationToken cancellationToken = default )
		=> Task.FromResult<object>( new { supported = false, message = "CustomizeConfigAsync is not implemented by this UI bridge." } );

	/// <summary>Performs a soft in-process UI reload or schedules a full OS process restart (optionally preserving MCP connection &amp; active page).</summary>
	Task<object> RestartAppAsync(
		string mode,
		bool saveConfig,
		bool preserveNavigation,
		bool preserveConnection,
		CancellationToken cancellationToken = default )
		=> Task.FromResult<object>( new { supported = false, message = "RestartAppAsync is not implemented by this UI bridge." } );

	/// <summary>Displays a temporary attention highlight/animation (pulse, spotlight, bounce, shake, flash, outline) and optional callout on a target live UI control.</summary>
	Task<object> GuideHighlightAsync( JsonElement? args, CancellationToken cancellationToken = default )
		=> Task.FromResult<object>( new { supported = false, message = "GuideHighlightAsync is not implemented by this UI bridge." } );

	/// <summary>Displays a floating or anchored guided action popup card with custom action buttons and optional waitForUser response.</summary>
	Task<object> GuidePopupAsync( JsonElement? args, CancellationToken cancellationToken = default )
		=> Task.FromResult<object>( new { supported = false, message = "GuidePopupAsync is not implemented by this UI bridge." } );

	/// <summary>Starts, advances, inspects, or stops an interactive multi-step guided UI tour with auto-navigation and spotlights.</summary>
	Task<object> GuideTourAsync( JsonElement? args, CancellationToken cancellationToken = default )
		=> Task.FromResult<object>( new { supported = false, message = "GuideTourAsync is not implemented by this UI bridge." } );

	/// <summary>Lists or clears active guided action highlights, popups, and tours.</summary>
	Task<object> GuideClearAsync( string? cueId, bool listOnly, CancellationToken cancellationToken = default )
		=> Task.FromResult<object>( new { supported = false, message = "GuideClearAsync is not implemented by this UI bridge." } );

	/// <summary>Creates or replaces a declarative AI-constructed supplemental dynamic window with structured formatted components.</summary>
	Task<object> DynamicWindowCreateAsync( JsonElement? args, CancellationToken cancellationToken = default )
		=> Task.FromResult<object>( new { supported = false, message = "DynamicWindowCreateAsync is not implemented by this UI bridge." } );

	/// <summary>Updates an open dynamic supplemental window in place (title/subtitle, replace/append components, or patch elements by id).</summary>
	Task<object> DynamicWindowUpdateAsync( JsonElement? args, CancellationToken cancellationToken = default )
		=> Task.FromResult<object>( new { supported = false, message = "DynamicWindowUpdateAsync is not implemented by this UI bridge." } );

	/// <summary>Lists all open AI-created dynamic supplemental windows along with their live input values and pending event counts.</summary>
	Task<object> DynamicWindowListAsync( CancellationToken cancellationToken = default )
		=> Task.FromResult<object>( new { supported = false, message = "DynamicWindowListAsync is not implemented by this UI bridge." } );

	/// <summary>Reads recorded button clicks, row selections, and current user input values from an open dynamic window (optionally waiting for the next event).</summary>
	Task<object> DynamicWindowEventsAsync(
		string windowId,
		bool waitForEvent,
		bool clearEvents,
		int timeoutSeconds,
		CancellationToken cancellationToken = default )
		=> Task.FromResult<object>( new { supported = false, message = "DynamicWindowEventsAsync is not implemented by this UI bridge." } );

	/// <summary>Closes one or all open AI-created dynamic supplemental windows.</summary>
	Task<object> DynamicWindowCloseAsync( string? windowId, bool closeAll, CancellationToken cancellationToken = default )
		=> Task.FromResult<object>( new { supported = false, message = "DynamicWindowCloseAsync is not implemented by this UI bridge." } );

	/// <summary>Opens, switches tabs in, or closes the HackPDM GUI Command Palette overlay.</summary>
	Task<object> CommandPaletteAsync( string action, string? query, CancellationToken cancellationToken = default )
		=> Task.FromResult<object>( new { supported = false, message = "CommandPaletteAsync is not implemented by this UI bridge." } );

	/// <summary>Lists active MessageBox popups and optionally clicks a button (OK, Cancel, Yes, No, Retry, Ignore, Abort, TryAgain, Continue).</summary>
	Task<object> DialogRespondAsync( string? dialogId, string? response, bool listOnly, CancellationToken cancellationToken = default );

	/// <summary>Captures a live PNG screenshot of a window, active page, or named UI control.</summary>
	Task<McpToolResult> CaptureScreenshotAsync(
		string? windowId,
		string? elementName,
		string? savePath,
		bool includeImageContent,
		CancellationToken cancellationToken = default );

	/// <summary>Returns live AppConfig.json text/object from the UI layer.</summary>
	Task<string> GetAppConfigJsonAsync( CancellationToken cancellationToken = default );

	/// <summary>Saves AppConfig.json via the UI layer.</summary>
	Task SaveAppConfigJsonAsync( string json, CancellationToken cancellationToken = default );

	// ---- Live UI synchronization hooks called by HackPdmApiService when running inside the GUI ----

	/// <summary>Synchronizes the GUI's LoginSession and resets FileManagerState when account changes.</summary>
	void NotifyLoginChanged( bool isLoggedIn, string? accountKey );

	/// <summary>Marks FileManagerState stale and asks an open FileManagerView to refresh after server mutations.</summary>
	void NotifyServerMutated( bool treeChanged, bool entriesChanged );

	/// <summary>Mirrors an MCP operation log line into the live StatusDialogViewModel.</summary>
	void MirrorStatusLog( StatusMessage status, string message );
}

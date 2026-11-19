using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using HackPDM.Core.Hack;
using HackPDM.Mcp.Security;
using HackPDM.Mcp.State;
using HackPDM.Mvvm.ViewModels.Common;
using HackPDM.Mvvm.Views.Common.Settings;
using HackPDM.Shell;
using HackPDM.Shell.Mcp;

using Microsoft.UI.Xaml;

using Windows.ApplicationModel.DataTransfer;

namespace HackPDM.Mvvm.ViewModels.Settings;

/// <summary>Display row for an MCP restore point in <see cref="McpSettingsViewModel"/>.</summary>
public sealed partial class McpRestorePointRow : ObservableObject
{
	public string Id { get; init; } = string.Empty;
	public string Label { get; init; } = string.Empty;
	public string TimestampText { get; init; } = string.Empty;
	public string KindBadge { get; init; } = string.Empty;
	public string TriggerText { get; init; } = string.Empty;
	public string DetailsText { get; init; } = string.Empty;
	public string RestoredBadge { get; init; } = string.Empty;
	public bool WasRestored { get; init; }
}

/// <summary>Display row for an MCP activity log entry in <see cref="McpSettingsViewModel"/>.</summary>
public sealed partial class McpActivityRow : ObservableObject
{
	public string Id { get; init; } = string.Empty;
	public string TimestampText { get; init; } = string.Empty;
	public string ToolName { get; init; } = string.Empty;
	public string StatusBadge { get; init; } = string.Empty;
	public bool IsSuccess { get; init; }
	public string DurationText { get; init; } = string.Empty;
	public string RestorePointBadge { get; init; } = string.Empty;
	public bool HasRestorePoint { get; init; }
	public string SummaryText { get; init; } = string.Empty;
}

/// <summary>Display row for a live MCP tool action waiting for human approval.</summary>
public sealed partial class McpPendingReviewRow : ObservableObject
{
	public string Id { get; init; } = string.Empty;
	public string ToolName { get; init; } = string.Empty;
	public string RiskBadge { get; init; } = string.Empty;
	public string CategoryText { get; init; } = string.Empty;
	public string ReasonText { get; init; } = string.Empty;
	public string ArgsSummaryText { get; init; } = string.Empty;
	public string ExpiresText { get; init; } = string.Empty;
}

/// <summary>Selectable execution mode option for the permission mode ComboBox.</summary>
public sealed partial class McpModeOption : ObservableObject
{
	public McpExecutionMode Mode { get; init; } = McpExecutionMode.ReviewCritical;
	public string WireName { get; init; } = "review-critical";
	public string DisplayLabel { get; init; } = string.Empty;
	public string Description { get; init; } = string.Empty;

	public override string ToString() => DisplayLabel;
}

/// <summary>Selectable preset or custom profile option for the permission profile ComboBox.</summary>
public sealed partial class McpProfileOption : ObservableObject
{
	public string Key { get; init; } = string.Empty;
	public string Name { get; init; } = string.Empty;
	public string DisplayLabel { get; init; } = string.Empty;
	public string Mode { get; init; } = string.Empty;
	public string Description { get; init; } = string.Empty;
	public bool IsBuiltInPreset { get; init; }

	public override string ToString() => DisplayLabel;
}

/// <summary>Interactive checkbox row representing one granular <see cref="McpCapabilityCategory"/>.</summary>
public sealed partial class McpCapabilityToggleRow : ObservableObject
{
	public McpCapabilityCategory Category { get; init; }
	public string Key { get; init; } = string.Empty;
	public string DisplayName { get; init; } = string.Empty;
	public string Description { get; init; } = string.Empty;
	internal Action<McpCapabilityCategory, bool>? OnToggled { get; init; }

	[ObservableProperty] public partial bool IsEnabled { get; set; }

	partial void OnIsEnabledChanged( bool value ) => OnToggled?.Invoke( Category, value );
}

/// <summary>
/// ViewModel for the HackPDM MCP Server, Connection Details, Permissions/Presets/Profiles,
/// Interactive Action Review Queue, Activity Log, and Restore Points GUI panel.
/// </summary>
public partial class McpSettingsViewModel : ObservableObject, IDisposable
{
	private readonly IDialogService _dialogs;
	private bool _disposed;
	private bool _syncingPermissions;

	// ---- Server & Connection Details ----
	[ObservableProperty] public partial bool IsBridgeEnabled { get; set; }
	[ObservableProperty] public partial bool IsBridgeRunning { get; set; }
	[ObservableProperty] public partial string BridgePortText { get; set; } = "0";
	[ObservableProperty] public partial string StatusBadgeText { get; set; } = "Stopped";
	[ObservableProperty] public partial string PermissionBadgeText { get; set; } = "Mode: review-critical";
	[ObservableProperty] public partial string EndpointText { get; set; } = "(not running)";
	[ObservableProperty] public partial bool IsTokenRevealed { get; set; }
	[ObservableProperty] public partial string DisplayTokenText { get; set; } = "(not running)";
	[ObservableProperty] public partial string DiscoveryFileText { get; set; } = string.Empty;
	[ObservableProperty] public partial string ClientConfigJson { get; set; } = string.Empty;
	[ObservableProperty] public partial string HttpClientConfigJson { get; set; } = string.Empty;
	[ObservableProperty] public partial string ClientCommandPreview { get; set; } = string.Empty;
	[ObservableProperty] public partial string BannerText { get; set; } = string.Empty;
	[ObservableProperty] public partial bool HasBanner { get; set; }

	// ---- Pending Interactive Reviews ----
	[ObservableProperty] public partial bool HasPendingReviews { get; set; }
	[ObservableProperty] public partial string PendingReviewsHeaderText { get; set; } = "No pending AI actions";
	public Visibility PendingReviewsVisibility => HasPendingReviews ? Visibility.Visible : Visibility.Collapsed;

	// ---- Permissions, Execution Mode, Presets & Profiles ----
	[ObservableProperty] public partial McpModeOption? SelectedModeOption { get; set; }
	[ObservableProperty] public partial string ModeDescriptionText { get; set; } = string.Empty;
	[ObservableProperty] public partial McpProfileOption? SelectedProfileOption { get; set; }
	[ObservableProperty] public partial string SelectedProfileDescriptionText { get; set; } = string.Empty;
	[ObservableProperty] public partial bool CanDeleteSelectedProfile { get; set; }
	[ObservableProperty] public partial string NewProfileName { get; set; } = string.Empty;
	[ObservableProperty] public partial string ReviewTimeoutText { get; set; } = "120";
	[ObservableProperty] public partial bool PromptDialogWhenHidden { get; set; } = true;
	[ObservableProperty] public partial string PermissionsFilePathText { get; set; } = string.Empty;
	[ObservableProperty] public partial string SessionApprovalsText { get; set; } = "No session approvals";
	[ObservableProperty] public partial bool HasSessionApprovals { get; set; }
	[ObservableProperty] public partial string CliQuickCommandText { get; set; } = string.Empty;

	// ---- Restore Points ----
	[ObservableProperty] public partial bool AutoRestorePoints { get; set; } = true;
	[ObservableProperty] public partial string MaxRestorePointsText { get; set; } = "50";
	[ObservableProperty] public partial string RestorePointsStorageText { get; set; } = string.Empty;
	[ObservableProperty] public partial string NewCheckpointLabel { get; set; } = string.Empty;
	[ObservableProperty] public partial string NewCheckpointPaths { get; set; } = string.Empty;
	[ObservableProperty] public partial bool IncludeSettingsInCheckpoint { get; set; } = true;
	[ObservableProperty] public partial bool IncludeAppConfigInCheckpoint { get; set; }
	[ObservableProperty] public partial McpRestorePointRow? SelectedRestorePoint { get; set; }
	[ObservableProperty] public partial string RestorePointsSummaryText { get; set; } = "0 restore points";

	// ---- Activity Log ----
	[ObservableProperty] public partial string LogFilterText { get; set; } = string.Empty;
	[ObservableProperty] public partial bool LogErrorsOnly { get; set; }
	[ObservableProperty] public partial string LogFileText { get; set; } = string.Empty;
	[ObservableProperty] public partial string LogSummaryText { get; set; } = "0 recorded tool calls";

	public ObservableCollection<McpModeOption> ModeOptions { get; } = [];
	public ObservableCollection<McpProfileOption> ProfileOptions { get; } = [];
	public ObservableCollection<McpCapabilityToggleRow> LeftCapabilities { get; } = [];
	public ObservableCollection<McpCapabilityToggleRow> RightCapabilities { get; } = [];
	public ObservableCollection<McpPendingReviewRow> PendingReviews { get; } = [];
	public ObservableCollection<McpRestorePointRow> RestorePoints { get; } = [];
	public ObservableCollection<McpActivityRow> ActivityEntries { get; } = [];

	public string ToggleBridgeButtonText => IsBridgeRunning ? "Stop MCP Server" : "Start MCP Server";
	public string RevealTokenButtonText => IsTokenRevealed ? "Hide" : "Show";
	public Visibility SessionApprovalsVisibility => HasSessionApprovals ? Visibility.Visible : Visibility.Collapsed;

	public McpSettingsViewModel( IDialogService dialogs )
	{
		_dialogs = dialogs;

		ModeOptions.Add( new McpModeOption
		{
			Mode = McpExecutionMode.ReviewCritical,
			WireName = "review-critical",
			DisplayLabel = "Review Critical (review-critical)",
			Description = McpExecutionModeHelper.Describe( McpExecutionMode.ReviewCritical ),
		} );
		ModeOptions.Add( new McpModeOption
		{
			Mode = McpExecutionMode.Review,
			WireName = "review",
			DisplayLabel = "Review All Actions (review)",
			Description = McpExecutionModeHelper.Describe( McpExecutionMode.Review ),
		} );
		ModeOptions.Add( new McpModeOption
		{
			Mode = McpExecutionMode.ReadOnly,
			WireName = "read-only",
			DisplayLabel = "Read-Only (get only)",
			Description = McpExecutionModeHelper.Describe( McpExecutionMode.ReadOnly ),
		} );
		ModeOptions.Add( new McpModeOption
		{
			Mode = McpExecutionMode.LocalOnly,
			WireName = "local-only",
			DisplayLabel = "Local-Only (no RPC)",
			Description = McpExecutionModeHelper.Describe( McpExecutionMode.LocalOnly ),
		} );
		ModeOptions.Add( new McpModeOption
		{
			Mode = McpExecutionMode.Auto,
			WireName = "auto",
			DisplayLabel = "Auto (no review)",
			Description = McpExecutionModeHelper.Describe( McpExecutionMode.Auto ),
		} );

		// Build 2-column capability checkboxes once; IsEnabled syncs on every profile/preset change.
		IReadOnlyList<McpCapabilityInfo> caps = McpPermissionManager.Capabilities;
		int split = ( caps.Count + 1 ) / 2;
		for( int i = 0; i < caps.Count; i++ )
		{
			McpCapabilityInfo info = caps[ i ];
			McpCapabilityToggleRow row = new()
			{
				Category = info.Category,
				Key = info.Key,
				DisplayName = info.DisplayName,
				Description = info.Description,
				OnToggled = OnCapabilityCheckboxToggled,
			};
			if( i < split )
				LeftCapabilities.Add( row );
			else
				RightCapabilities.Add( row );
		}

		IsBridgeEnabled = AppComposition.Settings.Get( "McpBridgeEnabled", true );
		BridgePortText = AppComposition.Settings.Get( "McpBridgePort", 0 ).ToString();
		AutoRestorePoints = McpBridgeHost.RestorePoints.AutoRestorePointsEnabled;
		MaxRestorePointsText = McpBridgeHost.RestorePoints.MaxRestorePoints.ToString();
		DiscoveryFileText = McpBridgeHost.DiscoveryFilePath;
		RestorePointsStorageText = McpBridgeHost.RestorePoints.StorageDirectory;
		LogFileText = McpBridgeHost.ActivityLog.LogFilePath;
		PermissionsFilePathText = McpBridgeHost.Permissions.ConfigFilePath;

		if( !string.IsNullOrWhiteSpace( HackDefaults.Instance.PwaPathAbsolute ) )
			NewCheckpointPaths = HackDefaults.Instance.PwaPathAbsolute;

		McpBridgeHost.StatusChanged += OnHostStateChanged;
		McpBridgeHost.Permissions.PermissionsChanged += OnPermissionsChanged;
		McpBridgeHost.Permissions.PendingReviewsChanged += OnPendingReviewsChanged;
		McpBridgeHost.RestorePoints.Changed += OnRestorePointsChanged;
		McpBridgeHost.ActivityLog.EntryAdded += OnActivityAdded;
		McpBridgeHost.ActivityLog.Cleared += OnActivityCleared;

		RefreshBridgeStatus();
		RefreshPermissions();
		RefreshPendingReviews();
		RefreshRestorePoints();
		RefreshActivityLog();
	}

	partial void OnIsBridgeRunningChanged( bool value ) => OnPropertyChanged( nameof( ToggleBridgeButtonText ) );
	partial void OnIsTokenRevealedChanged( bool value )
	{
		OnPropertyChanged( nameof( RevealTokenButtonText ) );
		UpdateDisplayToken();
	}
	partial void OnHasPendingReviewsChanged( bool value ) => OnPropertyChanged( nameof( PendingReviewsVisibility ) );
	partial void OnHasSessionApprovalsChanged( bool value ) => OnPropertyChanged( nameof( SessionApprovalsVisibility ) );

	partial void OnAutoRestorePointsChanged( bool value )
	{
		McpBridgeHost.RestorePoints.AutoRestorePointsEnabled = value;
	}

	partial void OnPromptDialogWhenHiddenChanged( bool value )
	{
		if( _syncingPermissions )
			return;
		McpBridgeHost.Permissions.SetPromptDialogWhenHidden( value );
	}

	partial void OnSelectedModeOptionChanged( McpModeOption? value )
	{
		if( _syncingPermissions || value is null )
			return;
		McpBridgeHost.Permissions.SetExecutionMode( value.Mode );
		ModeDescriptionText = value.Description;
		ShowBanner( $"MCP execution mode switched to '{value.WireName}'." );
	}

	partial void OnSelectedProfileOptionChanged( McpProfileOption? value )
	{
		if( _syncingPermissions || value is null )
			return;

		try
		{
			McpPermissionProfile applied = McpBridgeHost.Permissions.SelectProfile( value.Key );
			CanDeleteSelectedProfile = !value.IsBuiltInPreset;
			SelectedProfileDescriptionText = applied.Description;
			ShowBanner( $"Applied MCP profile '{applied.Name}' (mode: {applied.Mode})." );
		}
		catch( Exception ex )
		{
			ShowBanner( $"Could not apply profile: {ex.Message}" );
		}
	}

	partial void OnLogFilterTextChanged( string value ) => RefreshActivityLog();
	partial void OnLogErrorsOnlyChanged( bool value ) => RefreshActivityLog();

	private void OnCapabilityCheckboxToggled( McpCapabilityCategory category, bool enabled )
	{
		if( _syncingPermissions )
			return;
		McpBridgeHost.Permissions.SetCategoryEnabled( category, enabled );
	}

	// ---- Server & Connection Commands ----

	[RelayCommand]
	private void SaveBridgeSettings()
	{
		if( !int.TryParse( BridgePortText?.Trim(), out int port ) || port is < 0 or > 65535 )
		{
			ShowBanner( "Port must be an integer between 0 (automatic) and 65535." );
			return;
		}
		if( !int.TryParse( MaxRestorePointsText?.Trim(), out int maxRp ) || maxRp is < 5 or > 500 )
		{
			ShowBanner( "Max automatic restore points must be between 5 and 500." );
			return;
		}
		if( int.TryParse( ReviewTimeoutText?.Trim(), out int timeoutSec ) && timeoutSec is >= 5 and <= 3600 )
		{
			McpBridgeHost.Permissions.SetReviewTimeout( timeoutSec );
		}

		AppComposition.Settings.Set( "McpBridgeEnabled", IsBridgeEnabled );
		AppComposition.Settings.Set( "McpBridgePort", port );
		McpBridgeHost.RestorePoints.AutoRestorePointsEnabled = AutoRestorePoints;
		McpBridgeHost.RestorePoints.MaxRestorePoints = maxRp;

		if( IsBridgeEnabled )
			McpBridgeHost.Restart();
		else
			McpBridgeHost.Stop();

		RefreshBridgeStatus();
		ShowBanner( IsBridgeEnabled
			? $"MCP server active at {McpBridgeHost.McpEndpointUrl ?? "(starting...)"}."
			: "Embedded MCP server stopped and disabled." );
	}

	[RelayCommand]
	private void ToggleBridge()
	{
		if( McpBridgeHost.IsRunning )
		{
			McpBridgeHost.Stop();
			ShowBanner( "Embedded MCP server stopped." );
		}
		else
		{
			AppComposition.Settings.Set( "McpBridgeEnabled", true );
			IsBridgeEnabled = true;
			McpBridgeHost.Start( force: true );
			ShowBanner( $"Embedded MCP server started at {McpBridgeHost.McpEndpointUrl}. Copy the URL/Token or Client JSON to connect." );
		}
		RefreshBridgeStatus();
	}

	[RelayCommand]
	private void ToggleShowToken() => IsTokenRevealed = !IsTokenRevealed;

	[RelayCommand]
	private void CopyEndpoint()
	{
		if( !McpBridgeHost.IsRunning || string.IsNullOrEmpty( McpBridgeHost.McpEndpointUrl ) )
		{
			ShowBanner( "Start the MCP server first to get the live Streamable HTTP endpoint URL." );
			return;
		}
		CopyToClipboard( McpBridgeHost.McpEndpointUrl, "Copied live MCP Streamable HTTP endpoint URL to clipboard." );
	}

	[RelayCommand]
	private void CopyToken()
	{
		if( !McpBridgeHost.IsRunning || string.IsNullOrEmpty( McpBridgeHost.AuthToken ) )
		{
			ShowBanner( "Start the MCP server first to generate a Bearer authentication token." );
			return;
		}
		CopyToClipboard( McpBridgeHost.AuthToken, "Copied live MCP Bearer authentication token to clipboard." );
	}

	[RelayCommand]
	private void CopyClientConfig() => CopyStdioConfig();

	[RelayCommand]
	private void CopyStdioConfig()
		=> CopyToClipboard( ClientConfigJson, "Copied Stdio Auto-Attach mcp.json snippet to clipboard (works with Claude Desktop, Cursor, VS Code, Windsurf)." );

	[RelayCommand]
	private void CopyHttpConfig()
	{
		if( !McpBridgeHost.IsRunning )
		{
			ShowBanner( "Start the MCP server first so the live URL and Bearer token are filled into the HTTP JSON snippet." );
			return;
		}
		CopyToClipboard( HttpClientConfigJson, "Copied Direct Streamable HTTP mcp.json snippet (with live URL + Bearer token) to clipboard." );
	}

	// ---- Permissions, Presets, Profiles & Reviews Commands ----

	[RelayCommand]
	private void ApplySelectedProfile()
	{
		if( SelectedProfileOption is null )
			return;
		try
		{
			McpPermissionProfile applied = McpBridgeHost.Permissions.SelectProfile( SelectedProfileOption.Key );
			RefreshPermissions();
			ShowBanner( $"Reset active permissions to '{applied.Name}' (mode: {applied.Mode})." );
		}
		catch( Exception ex )
		{
			ShowBanner( $"Failed to apply profile: {ex.Message}" );
		}
	}

	[RelayCommand]
	private void SaveCurrentAsProfile()
	{
		string name = NewProfileName?.Trim() ?? string.Empty;
		if( string.IsNullOrWhiteSpace( name ) )
		{
			ShowBanner( "Enter a custom profile name before saving." );
			return;
		}

		try
		{
			if( int.TryParse( ReviewTimeoutText?.Trim(), out int sec ) && sec is >= 5 and <= 3600 )
				McpBridgeHost.Permissions.SetReviewTimeout( sec );

			McpPermissionProfile saved = McpBridgeHost.Permissions.SaveCustomProfile( name, activate: true );
			NewProfileName = string.Empty;
			RefreshPermissions();
			ShowBanner( $"Saved and activated custom permission profile '{saved.Name}' ({saved.Mode}) in mcp-permissions.json." );
		}
		catch( Exception ex )
		{
			ShowBanner( $"Could not save profile: {ex.Message}" );
		}
	}

	[RelayCommand]
	private void DeleteSelectedProfile()
	{
		if( SelectedProfileOption is null || SelectedProfileOption.IsBuiltInPreset )
		{
			ShowBanner( "Built-in presets cannot be deleted. Select a custom profile to delete it." );
			return;
		}

		string name = SelectedProfileOption.Name;
		if( McpBridgeHost.Permissions.DeleteCustomProfile( SelectedProfileOption.Key ) )
		{
			RefreshPermissions();
			ShowBanner( $"Deleted custom permission profile '{name}'." );
		}
	}

	[RelayCommand]
	private void ClearSessionApprovals()
	{
		McpBridgeHost.Permissions.ClearSessionApprovals();
		RefreshPermissions();
		ShowBanner( "Cleared all session-scoped AI tool approvals." );
	}

	public void ApproveReviewOnce( string? reviewId )
	{
		if( string.IsNullOrWhiteSpace( reviewId ) )
			return;
		if( McpBridgeHost.Permissions.ResolveReview( reviewId, McpReviewDecision.ApprovedOnce ) )
			ShowBanner( $"Approved AI action ({reviewId}) once." );
	}

	public void ApproveReviewSession( string? reviewId )
	{
		if( string.IsNullOrWhiteSpace( reviewId ) )
			return;
		if( McpBridgeHost.Permissions.ResolveReview( reviewId, McpReviewDecision.ApprovedForSession ) )
			ShowBanner( $"Approved AI tool ({reviewId}) for this session." );
	}

	public void DenyReview( string? reviewId )
	{
		if( string.IsNullOrWhiteSpace( reviewId ) )
			return;
		if( McpBridgeHost.Permissions.ResolveReview( reviewId, McpReviewDecision.Denied ) )
			ShowBanner( $"Denied AI action ({reviewId})." );
	}

	// ---- Restore Points & Log Commands ----

	[RelayCommand]
	private async Task CreateCheckpointAsync()
	{
		string label = string.IsNullOrWhiteSpace( NewCheckpointLabel )
			? $"Manual checkpoint {DateTime.Now:HH:mm:ss}"
			: NewCheckpointLabel.Trim();

		List<string> paths = ( NewCheckpointPaths ?? string.Empty )
			.Split( [ ',', ';', '\n', '\r' ], StringSplitOptions.RemoveEmptyEntries )
			.Select( p => p.Trim() )
			.Where( p => p.Length > 0 )
			.ToList();

		if( paths.Count == 0 && !IncludeSettingsInCheckpoint && !IncludeAppConfigInCheckpoint )
		{
			ShowBanner( "Enter at least one file/folder path or select settings.json / AppConfig.json." );
			return;
		}

		try
		{
			McpRestorePoint rp = await McpBridgeHost.RestorePoints.CreateRestorePointAsync(
				label: label,
				description: "Created from HackPDM MCP Server GUI.",
				paths: paths,
				includeSettings: IncludeSettingsInCheckpoint,
				includeAppConfig: IncludeAppConfigInCheckpoint,
				automatic: false,
				triggerTool: "gui_manual_checkpoint" );

			NewCheckpointLabel = string.Empty;
			RefreshRestorePoints();
			ShowBanner( $"Created restore point '{rp.Label}' ({rp.Id}) with {rp.Entries.Count} tracked item(s)." );
		}
		catch( Exception ex )
		{
			ShowBanner( $"Failed to create restore point: {ex.Message}" );
		}
	}

	[RelayCommand]
	private async Task RestorePointByIdAsync( string? id )
	{
		string? targetId = string.IsNullOrWhiteSpace( id ) ? SelectedRestorePoint?.Id : id;
		if( string.IsNullOrWhiteSpace( targetId ) )
		{
			ShowBanner( "Select a restore point to restore." );
			return;
		}

		try
		{
			McpRestoreResult res = await McpBridgeHost.RestorePoints.RestoreAsync( targetId, createSafetyBackup: true, dryRun: false );
			RefreshRestorePoints();
			ShowBanner( $"Restored '{res.Label}' ({res.RestoredFiles} file(s) restored, {res.RemovedCreatedFiles} created file(s) removed). Safety backup: {res.SafetyBackupRestorePointId ?? "none"}." );
		}
		catch( Exception ex )
		{
			ShowBanner( $"Restore failed: {ex.Message}" );
			await _dialogs.ShowMessageAsync( $"Restore failed: {ex.Message}" );
		}
	}

	[RelayCommand]
	private void DeletePointById( string? id )
	{
		string? targetId = string.IsNullOrWhiteSpace( id ) ? SelectedRestorePoint?.Id : id;
		if( string.IsNullOrWhiteSpace( targetId ) )
		{
			ShowBanner( "Select a restore point to delete." );
			return;
		}

		if( McpBridgeHost.RestorePoints.DeleteRestorePoint( targetId ) )
		{
			RefreshRestorePoints();
			ShowBanner( $"Deleted restore point '{targetId}'." );
		}
	}

	[RelayCommand]
	private async Task ResetUiAndThemeDefaultsAsync()
	{
		try
		{
			await McpBridgeHost.RestorePoints.CreateRestorePointAsync(
				label: "Before Reset UI & Theme to Factory Defaults",
				description: "Automatic safety backup before resetting AppConfig.json theme, layout, columns, entry colors, and window settings.",
				paths: [],
				includeSettings: false,
				includeAppConfig: true,
				automatic: true,
				triggerTool: "gui_reset_ui_defaults" );

			// Reset all non-theme UI config sections + Theme to factory defaults (keeping user's CustomThemes library)
			var preservedThemes = AppComposition.Theme.Settings.CustomThemes;
			UIStorage.GlobalData.Win = new();
			UIStorage.GlobalData.EColors = new();
			UIStorage.GlobalData.ColumnsSettings = new();
			UIStorage.GlobalData.WorkspaceLayout = new();
			AppComposition.Theme.ResetToFactoryDefaults( clearCustomThemes: false );
			AppComposition.Theme.Settings.CustomThemes = preservedThemes;
			UIStorage.GlobalData.Theme = AppComposition.Theme.Settings;
			AppConfigStore.Save( UIStorage.GlobalData );

			if( AppComposition.Theme.NeedsRestart )
			{
				ShowBanner( "UI & Theme reset to factory defaults and saved (backup restore point captured). Click Restart HackPDM in Theme settings to complete base Light/Dark switch." );
			}
			else
			{
				await HackApp.SoftRestartUiAsync( saveConfig: true, preserveNavigation: true );
			}
		}
		catch( Exception ex )
		{
			ShowBanner( $"Reset UI defaults failed: {ex.Message}" );
		}
	}

	[RelayCommand]
	private void ClearActivityLog()
	{
		McpBridgeHost.ActivityLog.Clear();
		RefreshActivityLog();
		ShowBanner( "Cleared MCP activity log." );
	}

	[RelayCommand]
	private void RefreshAll()
	{
		RefreshBridgeStatus();
		RefreshPermissions();
		RefreshPendingReviews();
		RefreshRestorePoints();
		RefreshActivityLog();
	}

	// ---- Refresh Methods ----

	public void RefreshBridgeStatus()
	{
		IsBridgeRunning = McpBridgeHost.IsRunning;
		StatusBadgeText = IsBridgeRunning
			? $"Running • Port {McpBridgeHost.Port} (PID {Environment.ProcessId})"
			: "Stopped";
		EndpointText = McpBridgeHost.McpEndpointUrl ?? "(not running — click Start MCP Server)";
		DiscoveryFileText = McpBridgeHost.DiscoveryFilePath;
		ClientConfigJson = McpBridgeHost.BuildStdioClientConfigSnippet();
		HttpClientConfigJson = McpBridgeHost.BuildHttpClientConfigSnippet();
		ClientCommandPreview = McpBridgeHost.BuildClientCommandPreview();
		UpdateDisplayToken();
	}

	public void RefreshPermissions()
	{
		_syncingPermissions = true;
		try
		{
			McpPermissionManager mgr = McpBridgeHost.Permissions;
			McpPermissionProfile active = mgr.ActiveProfile;

			PermissionBadgeText = $"Mode: {active.Mode} • {active.Name}";
			ModeDescriptionText = McpExecutionModeHelper.Describe( active.ParsedMode );
			SelectedModeOption = ModeOptions.FirstOrDefault( m => m.Mode == active.ParsedMode ) ?? ModeOptions[ 0 ];

			IReadOnlyList<McpPermissionProfile> allProfiles = mgr.ListAllProfiles();
			ProfileOptions.Clear();
			foreach( McpPermissionProfile p in allProfiles )
			{
				string tag = p.IsBuiltInPreset ? "Preset" : "Custom";
				ProfileOptions.Add( new McpProfileOption
				{
					Key = p.Key,
					Name = p.Name,
					DisplayLabel = $"[{tag}] {p.Name} ({p.Mode})",
					Mode = p.Mode,
					Description = p.Description,
					IsBuiltInPreset = p.IsBuiltInPreset,
				} );
			}

			SelectedProfileOption = ProfileOptions.FirstOrDefault( o => string.Equals( o.Key, active.Key, StringComparison.OrdinalIgnoreCase ) )
				?? ProfileOptions.FirstOrDefault();
			SelectedProfileDescriptionText = SelectedProfileOption?.Description ?? active.Description;
			CanDeleteSelectedProfile = SelectedProfileOption is { IsBuiltInPreset: false };

			ReviewTimeoutText = active.ReviewTimeoutSeconds.ToString();
			PromptDialogWhenHidden = mgr.PromptDialogWhenHidden;
			PermissionsFilePathText = mgr.ConfigFilePath;

			foreach( McpCapabilityToggleRow row in LeftCapabilities )
				row.IsEnabled = active.IsCategoryEnabled( row.Category );
			foreach( McpCapabilityToggleRow row in RightCapabilities )
				row.IsEnabled = active.IsCategoryEnabled( row.Category );

			IReadOnlyList<string> sessionApproved = mgr.SessionApprovedTools;
			HasSessionApprovals = sessionApproved.Count > 0;
			SessionApprovalsText = sessionApproved.Count == 0
				? "No session approvals"
				: $"Session approved ({sessionApproved.Count}): {string.Join( ", ", sessionApproved )}";

			CliQuickCommandText = $"HackPDM.McpServer --permission-profile \"{active.Key}\" --permission-mode {active.Mode}";
		}
		finally
		{
			_syncingPermissions = false;
		}
	}

	public void RefreshPendingReviews()
	{
		IReadOnlyList<McpPendingReview> pending = McpBridgeHost.Permissions.PendingReviews;
		PendingReviews.Clear();
		foreach( McpPendingReview r in pending )
		{
			PendingReviews.Add( new McpPendingReviewRow
			{
				Id = r.Id,
				ToolName = r.ToolName,
				RiskBadge = r.RiskLevel == McpToolRiskLevel.CriticalAction ? "CRITICAL" : "ACTION",
				CategoryText = r.Category.ToString(),
				ReasonText = r.Reason,
				ArgsSummaryText = string.IsNullOrWhiteSpace( r.ArgsSummary ) ? "(no arguments)" : r.ArgsSummary,
				ExpiresText = $"Expires {r.ExpiresAtUtc.ToLocalTime():HH:mm:ss}",
			} );
		}

		HasPendingReviews = PendingReviews.Count > 0;
		PendingReviewsHeaderText = PendingReviews.Count == 1
			? "1 AI Action Awaiting Your Approval"
			: $"{PendingReviews.Count} AI Actions Awaiting Your Approval";
	}

	public void RefreshRestorePoints()
	{
		IReadOnlyList<McpRestorePoint> list = McpBridgeHost.RestorePoints.ListRestorePoints( 100 );
		string? keepId = SelectedRestorePoint?.Id;
		RestorePoints.Clear();
		foreach( McpRestorePoint rp in list )
		{
			int fileCount = rp.Entries.Count( e => !e.IsDirectory );
			string sizeText = FormatBytes( rp.TotalBackupBytes );
			string sample = rp.Entries.Count > 0 ? Path.GetFileName( rp.Entries[ 0 ].OriginalPath ) : "empty";
			if( rp.Entries.Count > 1 )
				sample += $" (+{rp.Entries.Count - 1} more)";

			RestorePoints.Add( new McpRestorePointRow
			{
				Id = rp.Id,
				Label = rp.Label,
				TimestampText = rp.CreatedAtUtc.ToLocalTime().ToString( "yyyy-MM-dd HH:mm:ss" ),
				KindBadge = rp.Automatic ? "AUTO" : "MANUAL",
				TriggerText = rp.TriggerTool ?? "manual",
				DetailsText = $"{fileCount} file(s), {sizeText} — {sample}",
				RestoredBadge = rp.RestoredAtUtc is { } rt ? $"Restored {rt.ToLocalTime():HH:mm:ss}" : string.Empty,
				WasRestored = rp.RestoredAtUtc.HasValue,
			} );
		}

		SelectedRestorePoint = RestorePoints.FirstOrDefault( r => r.Id == keepId ) ?? RestorePoints.FirstOrDefault();
		long totalBytes = list.Sum( r => r.TotalBackupBytes );
		RestorePointsSummaryText = $"{list.Count} restore point(s) • {FormatBytes( totalBytes )} stored";
	}

	public void RefreshActivityLog()
	{
		IReadOnlyList<McpActivityEntry> entries = McpBridgeHost.ActivityLog.GetEntries(
			limit: 150,
			errorsOnly: LogErrorsOnly,
			search: string.IsNullOrWhiteSpace( LogFilterText ) ? null : LogFilterText );

		ActivityEntries.Clear();
		foreach( McpActivityEntry e in entries )
		{
			string summary = !e.Success
				? ( e.ErrorMessage ?? "Error" )
				: ( string.IsNullOrWhiteSpace( e.ArgsSummary ) ? ( e.ResultSummary ?? "ok" ) : $"{e.ArgsSummary}  →  {e.ResultSummary}" );
			if( summary.Length > 200 )
				summary = summary[ ..200 ] + "…";

			ActivityEntries.Add( new McpActivityRow
			{
				Id = e.Id,
				TimestampText = e.TimestampUtc.ToLocalTime().ToString( "HH:mm:ss" ),
				ToolName = e.ToolName,
				StatusBadge = e.Success ? "OK" : "ERR",
				IsSuccess = e.Success,
				DurationText = $"{e.DurationMs} ms",
				RestorePointBadge = e.RestorePointId is { Length: > 0 } rpId ? $"RP:{rpId[ ^8.. ]}" : string.Empty,
				HasRestorePoint = !string.IsNullOrEmpty( e.RestorePointId ),
				SummaryText = summary,
			} );
		}

		LogSummaryText = $"{McpBridgeHost.ActivityLog.Count} total call(s) • showing {ActivityEntries.Count}";
	}

	private void UpdateDisplayToken()
	{
		DisplayTokenText = !McpBridgeHost.IsRunning
			? "(not running)"
			: ( IsTokenRevealed ? ( McpBridgeHost.AuthToken ?? "(none)" ) : McpBridgeHost.MaskedAuthToken );
	}

	private void CopyToClipboard( string text, string successMessage )
	{
		try
		{
			DataPackage pkg = new();
			pkg.SetText( text );
			Clipboard.SetContent( pkg );
			ShowBanner( successMessage );
		}
		catch( Exception ex )
		{
			ShowBanner( $"Could not access clipboard: {ex.Message}" );
		}
	}

	private void ShowBanner( string message )
	{
		BannerText = message;
		HasBanner = !string.IsNullOrWhiteSpace( message );
	}

	private void OnHostStateChanged() => UiDispatcher.Post( () => { if( !_disposed ) RefreshBridgeStatus(); } );
	private void OnPermissionsChanged() => UiDispatcher.Post( () => { if( !_disposed ) RefreshPermissions(); } );
	private void OnPendingReviewsChanged() => UiDispatcher.Post( () => { if( !_disposed ) RefreshPendingReviews(); } );
	private void OnRestorePointsChanged() => UiDispatcher.Post( () => { if( !_disposed ) RefreshRestorePoints(); } );
	private void OnActivityAdded( McpActivityEntry _ ) => UiDispatcher.Post( () => { if( !_disposed ) RefreshActivityLog(); } );
	private void OnActivityCleared() => UiDispatcher.Post( () => { if( !_disposed ) RefreshActivityLog(); } );

	private static string FormatBytes( long bytes ) => bytes switch
	{
		>= 1024L * 1024 * 1024 => $"{bytes / ( 1024.0 * 1024 * 1024 ):0.##} GiB",
		>= 1024L * 1024 => $"{bytes / ( 1024.0 * 1024 ):0.##} MiB",
		>= 1024L => $"{bytes / 1024.0:0.#} KiB",
		_ => $"{bytes} B",
	};

	public void Dispose()
	{
		if( _disposed )
			return;
		_disposed = true;
		McpBridgeHost.StatusChanged -= OnHostStateChanged;
		McpBridgeHost.Permissions.PermissionsChanged -= OnPermissionsChanged;
		McpBridgeHost.Permissions.PendingReviewsChanged -= OnPendingReviewsChanged;
		McpBridgeHost.RestorePoints.Changed -= OnRestorePointsChanged;
		McpBridgeHost.ActivityLog.EntryAdded -= OnActivityAdded;
		McpBridgeHost.ActivityLog.Cleared -= OnActivityCleared;
	}
}

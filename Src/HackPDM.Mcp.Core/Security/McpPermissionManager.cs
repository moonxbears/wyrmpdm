using System.Text;
using System.Text.Json;

using HackPDM.Mcp.Protocol;
using HackPDM.Mcp.State;
using HackPDM.Shared.GlobalData;

namespace HackPDM.Mcp.Security;

/// <summary>Human-readable metadata for one <see cref="McpCapabilityCategory"/>.</summary>
public sealed record McpCapabilityInfo(
	McpCapabilityCategory Category,
	string Key,
	string DisplayName,
	string Description );

/// <summary>
/// Manages HackPDM MCP permissions, execution modes (<c>auto</c>, <c>review</c>,
/// <c>review-critical</c>, <c>read-only</c>), built-in presets, persistent custom profiles
/// (<c>&lt;AppDataPaths.Config&gt;/mcp-permissions.json</c>), CLI runtime overrides, and the
/// live interactive human-in-the-loop review queue.
/// </summary>
public sealed class McpPermissionManager
{
	public const string ConfigFileName = "mcp-permissions.json";

	private static readonly JsonSerializerOptions PrettyJsonOptions = new( McpJson.Options )
	{
		WriteIndented = true,
	};

	private static readonly IReadOnlyList<McpCapabilityInfo> CapabilityCatalogList =
	[
		new( McpCapabilityCategory.ReadVaultAndOdoo, nameof( McpCapabilityCategory.ReadVaultAndOdoo ),
			"Read Vault, Entries & Odoo Queries",
			"Read directory trees, entries, version history, previews, dependency graphs, search, SolidWorks inspection, settings, logs, and Odoo ORM reads." ),
		new( McpCapabilityCategory.PdmWorkingActions, nameof( McpCapabilityCategory.PdmWorkingActions ),
			"Routine PDM Working Actions",
			"Get latest files, checkout, uncheckout, restore logically deleted entries, and download specific versions." ),
		new( McpCapabilityCategory.PdmCommitAndPush, nameof( McpCapabilityCategory.PdmCommitAndPush ),
			"PDM Commit & Push to Server",
			"Commit/check-in modified or new files to Odoo, upload preview images, and create remote vault directories." ),
		new( McpCapabilityCategory.PdmDeleteAndAdmin, nameof( McpCapabilityCategory.PdmDeleteAndAdmin ),
			"PDM Delete & Raw Odoo Write/RPC",
			"Logical or permanent server deletes and raw odoo_create, odoo_write, odoo_unlink, or odoo_execute RPC calls." ),
		new( McpCapabilityCategory.LocalVaultFileEdit, nameof( McpCapabilityCategory.LocalVaultFileEdit ),
			"Local PWA Vault File Editing",
			"Write or delete files inside the local HackPDM PWA working folder." ),
		new( McpCapabilityCategory.ExternalFilesystemAccess, nameof( McpCapabilityCategory.ExternalFilesystemAccess ),
			"Computer-Wide Filesystem & PWA Import",
			"Browse, create folders, copy, move, delete, or import CAD files located outside the PWA vault folder." ),
		new( McpCapabilityCategory.SolidWorksEditing, nameof( McpCapabilityCategory.SolidWorksEditing ),
			"SolidWorks Properties & Dependency Rewriting",
			"Modify custom properties and rewrite internal assembly/drawing dependency paths in .SLDPRT/.SLDASM/.SLDDRW files." ),
		new( McpCapabilityCategory.LiveUiRead, nameof( McpCapabilityCategory.LiveUiRead ),
			"Live Desktop GUI Inspection & Screenshots",
			"Read live UI state, open windows, XAML visual tree, control values, and capture screenshots." ),
		new( McpCapabilityCategory.LiveUiControl, nameof( McpCapabilityCategory.LiveUiControl ),
			"Live Desktop GUI Control & Automation",
			"Navigate pages, select folders/entries, adjust filters/themes, edit inputs, click controls, and invoke commands." ),
		new( McpCapabilityCategory.SettingsAndSession, nameof( McpCapabilityCategory.SettingsAndSession ),
			"Settings, Session & Connection Profiles",
			"Sign in/out, configure Odoo/PWA settings, load/save .hp profiles, edit settings.json or AppConfig.json, attach/launch GUI." ),
		new( McpCapabilityCategory.McpAdminAndRestore, nameof( McpCapabilityCategory.McpAdminAndRestore ),
			"MCP Restore Points & Admin",
			"Create/restore/delete restore points, clear MCP activity log, and configure MCP automatic backup settings." ),
	];

	private static readonly IReadOnlyList<McpPermissionProfile> BuiltInPresetList = BuildBuiltInPresets();

	private readonly Lock _gate = new();
	private readonly Dictionary<string, PendingReviewEntry> _pendingReviews = new( StringComparer.Ordinal );
	private readonly HashSet<string> _sessionApprovedTools = new( StringComparer.OrdinalIgnoreCase );

	private McpPermissionsConfigFile _config = new();
	private McpPermissionProfile _activeProfile;
	private bool _hasRuntimeOverrides;

	public string ConfigFilePath { get; }
	public bool PersistChanges { get; }

	/// <summary>
	/// Set to <c>true</c> when hosted inside the live HackPDM desktop GUI so tool calls requiring review
	/// pause in <see cref="PendingReviews"/> waiting for the user to approve or deny.
	/// </summary>
	public bool HasInteractiveReviewer { get; set; }

	/// <summary>True when runtime CLI flags/overrides modified permissions beyond the persisted config file.</summary>
	public bool HasRuntimeOverrides
	{
		get { lock( _gate ) return _hasRuntimeOverrides; }
	}

	/// <summary>True when a popup prompt should be offered if a review arrives while the MCP Settings view is not currently visible.</summary>
	public bool PromptDialogWhenHidden
	{
		get { lock( _gate ) return _config.PromptDialogWhenHidden; }
	}

	public event Action? PermissionsChanged;
	public event Action? PendingReviewsChanged;
	public event Action<McpPendingReview>? ReviewRequested;

	public static IReadOnlyList<McpCapabilityInfo> Capabilities => CapabilityCatalogList;

	public McpPermissionManager(
		string? configFilePathOverride = null,
		string defaultPresetWhenUnconfigured = "review-critical",
		bool persistChanges = true )
	{
		ConfigFilePath = string.IsNullOrWhiteSpace( configFilePathOverride )
			? Path.Combine( AppDataPaths.Config, ConfigFileName )
			: Path.GetFullPath( configFilePathOverride );
		PersistChanges = persistChanges;

		_activeProfile = ResolveBuiltInPreset( defaultPresetWhenUnconfigured )
			?? ResolveBuiltInPreset( "review-critical" )!;

		LoadFromDisk( defaultPresetWhenUnconfigured );
	}

	public McpPermissionProfile ActiveProfile
	{
		get
		{
			lock( _gate )
				return _activeProfile.DeepClone();
		}
	}

	public McpExecutionMode ActiveMode
	{
		get
		{
			lock( _gate )
				return _activeProfile.ParsedMode;
		}
	}

	public IReadOnlyList<string> SessionApprovedTools
	{
		get
		{
			lock( _gate )
				return _sessionApprovedTools.OrderBy( s => s, StringComparer.OrdinalIgnoreCase ).ToList();
		}
	}

	public IReadOnlyList<McpPendingReview> PendingReviews
	{
		get
		{
			lock( _gate )
				return _pendingReviews.Values
					.Select( p => p.Review )
					.OrderBy( r => r.CreatedAtUtc )
					.ToList();
		}
	}

	public static IReadOnlyList<McpPermissionProfile> ListBuiltInPresets()
		=> BuiltInPresetList.Select( p => p.DeepClone() ).ToList();

	public IReadOnlyList<McpPermissionProfile> ListAllProfiles()
	{
		lock( _gate )
		{
			List<McpPermissionProfile> result = BuiltInPresetList.Select( p => p.DeepClone() ).ToList();
			foreach( McpPermissionProfile custom in _config.CustomProfiles.Values.OrderBy( p => p.Name, StringComparer.OrdinalIgnoreCase ) )
				result.Add( custom.DeepClone( isBuiltIn: false ) );
			return result;
		}
	}

	public McpPermissionProfile? FindProfile( string keyOrName )
	{
		if( string.IsNullOrWhiteSpace( keyOrName ) )
			return null;
		string q = keyOrName.Trim();

		lock( _gate )
		{
			if( ResolveBuiltInPreset( q ) is { } builtIn )
				return builtIn.DeepClone();

			if( _config.CustomProfiles.TryGetValue( q, out McpPermissionProfile? exact ) )
				return exact.DeepClone( isBuiltIn: false );

			McpPermissionProfile? byName = _config.CustomProfiles.Values.FirstOrDefault( p =>
				string.Equals( p.Name, q, StringComparison.OrdinalIgnoreCase )
				|| string.Equals( p.Key, q, StringComparison.OrdinalIgnoreCase ) );
			return byName?.DeepClone( isBuiltIn: false );
		}
	}

	/// <summary>Selects a built-in preset or saved custom profile by key or name and persists it.</summary>
	public McpPermissionProfile SelectProfile( string keyOrName, bool? persistOverride = null )
	{
		McpPermissionProfile updated;
		lock( _gate )
		{
			McpPermissionProfile? found = ResolveBuiltInPreset( keyOrName );
			if( found is null )
			{
				if( _config.CustomProfiles.TryGetValue( keyOrName.Trim(), out McpPermissionProfile? custom ) )
					found = custom.DeepClone( isBuiltIn: false );
				else
					found = _config.CustomProfiles.Values.FirstOrDefault( p =>
						string.Equals( p.Name, keyOrName.Trim(), StringComparison.OrdinalIgnoreCase ) )?.DeepClone( isBuiltIn: false );
			}

			if( found is null )
				throw new KeyNotFoundException( $"Unknown MCP permission preset or profile '{keyOrName}'." );

			_config.ActiveProfile = found.Key;
			_config.ActiveOverrides = null;
			_activeProfile = NormalizeProfile( found );
			_sessionApprovedTools.Clear();
			updated = _activeProfile.DeepClone();

			if( persistOverride ?? PersistChanges )
				SaveToDiskLocked();
		}

		NotifyPermissionsChanged();
		return updated;
	}

	/// <summary>Changes the active execution mode (<c>auto</c>, <c>review</c>, <c>review-critical</c>, <c>read-only</c>).</summary>
	public McpPermissionProfile SetExecutionMode( McpExecutionMode mode, bool? persistOverride = null )
	{
		McpPermissionProfile updated;
		lock( _gate )
		{
			_activeProfile = _activeProfile with { Mode = McpExecutionModeHelper.ToWireName( mode ) };
			_sessionApprovedTools.Clear();
			UpdateActivePersistenceLocked();
			updated = _activeProfile.DeepClone();

			if( persistOverride ?? PersistChanges )
				SaveToDiskLocked();
		}

		NotifyPermissionsChanged();
		return updated;
	}

	/// <summary>Enables or disables a capability category on the active profile.</summary>
	public McpPermissionProfile SetCategoryEnabled( McpCapabilityCategory category, bool enabled, bool? persistOverride = null )
	{
		McpPermissionProfile updated;
		lock( _gate )
		{
			Dictionary<string, bool> cats = new( _activeProfile.Categories, StringComparer.OrdinalIgnoreCase )
			{
				[ category.ToString() ] = enabled,
			};
			_activeProfile = _activeProfile with { Categories = cats };
			UpdateActivePersistenceLocked();
			updated = _activeProfile.DeepClone();

			if( persistOverride ?? PersistChanges )
				SaveToDiskLocked();
		}

		NotifyPermissionsChanged();
		return updated;
	}

	/// <summary>Updates the review timeout (seconds) on the active profile.</summary>
	public McpPermissionProfile SetReviewTimeout( int seconds, bool? persistOverride = null )
	{
		int clamped = Math.Clamp( seconds, 5, 3600 );
		McpPermissionProfile updated;
		lock( _gate )
		{
			_activeProfile = _activeProfile with { ReviewTimeoutSeconds = clamped };
			UpdateActivePersistenceLocked();
			updated = _activeProfile.DeepClone();

			if( persistOverride ?? PersistChanges )
				SaveToDiskLocked();
		}

		NotifyPermissionsChanged();
		return updated;
	}

	/// <summary>Updates whether hidden reviews trigger a modal confirmation prompt in the GUI.</summary>
	public void SetPromptDialogWhenHidden( bool enabled, bool? persistOverride = null )
	{
		lock( _gate )
		{
			_config.PromptDialogWhenHidden = enabled;
			if( persistOverride ?? PersistChanges )
				SaveToDiskLocked();
		}

		NotifyPermissionsChanged();
	}

	/// <summary>Saves the current effective permission configuration as a named custom profile.</summary>
	public McpPermissionProfile SaveCustomProfile( string name, string? description = null, bool activate = true, bool? persistOverride = null )
	{
		if( string.IsNullOrWhiteSpace( name ) )
			throw new ArgumentException( "Profile name is required.", nameof( name ) );

		string trimmedName = name.Trim();
		string key = SlugifyProfileKey( trimmedName );
		if( BuiltInPresetList.Any( p => string.Equals( p.Key, key, StringComparison.OrdinalIgnoreCase ) ) )
			throw new InvalidOperationException( $"'{trimmedName}' conflicts with built-in preset key '{key}'. Choose a distinct profile name." );

		McpPermissionProfile saved;
		lock( _gate )
		{
			McpPermissionProfile profile = _activeProfile.DeepClone( newKey: key, newName: trimmedName, isBuiltIn: false );
			if( description is not null )
				profile = profile with { Description = description.Trim() };
			else if( string.IsNullOrWhiteSpace( profile.Description ) )
				profile = profile with { Description = $"Custom profile ({profile.Mode})" };

			_config.CustomProfiles[ key ] = NormalizeProfile( profile );
			if( activate )
			{
				_config.ActiveProfile = key;
				_config.ActiveOverrides = null;
				_activeProfile = _config.CustomProfiles[ key ].DeepClone( isBuiltIn: false );
			}
			saved = _config.CustomProfiles[ key ].DeepClone( isBuiltIn: false );

			if( persistOverride ?? PersistChanges )
				SaveToDiskLocked();
		}

		NotifyPermissionsChanged();
		return saved;
	}

	/// <summary>Deletes a saved custom profile (built-in presets cannot be deleted).</summary>
	public bool DeleteCustomProfile( string keyOrName, bool? persistOverride = null )
	{
		if( string.IsNullOrWhiteSpace( keyOrName ) )
			return false;

		string q = keyOrName.Trim();
		bool removed;
		lock( _gate )
		{
			string? matchKey = _config.CustomProfiles.Keys.FirstOrDefault( k => string.Equals( k, q, StringComparison.OrdinalIgnoreCase ) )
				?? _config.CustomProfiles.FirstOrDefault( kv => string.Equals( kv.Value.Name, q, StringComparison.OrdinalIgnoreCase ) ).Key;

			if( matchKey is null )
				return false;

			removed = _config.CustomProfiles.Remove( matchKey );
			if( removed && string.Equals( _config.ActiveProfile, matchKey, StringComparison.OrdinalIgnoreCase ) )
			{
				McpPermissionProfile fallback = ResolveBuiltInPreset( "review-critical" )!;
				_config.ActiveProfile = fallback.Key;
				_config.ActiveOverrides = null;
				_activeProfile = fallback.DeepClone();
			}

			if( removed && ( persistOverride ?? PersistChanges ) )
				SaveToDiskLocked();
		}

		if( removed )
			NotifyPermissionsChanged();
		return removed;
	}

	/// <summary>
	/// Applies ephemeral runtime overrides (e.g., from CLI flags or environment variables)
	/// on top of the loaded configuration without overwriting <c>mcp-permissions.json</c>.
	/// </summary>
	public McpPermissionProfile ApplyRuntimeOverrides(
		string? presetOrProfile = null,
		McpExecutionMode? mode = null,
		IEnumerable<McpCapabilityCategory>? allowCategories = null,
		IEnumerable<McpCapabilityCategory>? denyCategories = null,
		IEnumerable<string>? allowTools = null,
		IEnumerable<string>? denyTools = null,
		IEnumerable<string>? requireReviewTools = null,
		int? reviewTimeoutSeconds = null )
	{
		McpPermissionProfile updated;
		lock( _gate )
		{
			bool anyApplied = false;
			if( !string.IsNullOrWhiteSpace( presetOrProfile ) )
			{
				McpPermissionProfile? found = ResolveBuiltInPreset( presetOrProfile );
				if( found is null )
				{
					found = _config.CustomProfiles.TryGetValue( presetOrProfile.Trim(), out McpPermissionProfile? c )
						? c.DeepClone( isBuiltIn: false )
						: _config.CustomProfiles.Values.FirstOrDefault( p =>
							string.Equals( p.Name, presetOrProfile.Trim(), StringComparison.OrdinalIgnoreCase ) )?.DeepClone( isBuiltIn: false );
				}
				if( found is null )
					throw new ArgumentException( $"Unknown MCP permission preset or profile '{presetOrProfile}'." );
				_activeProfile = NormalizeProfile( found );
				anyApplied = true;
			}

			if( mode is McpExecutionMode m )
			{
				_activeProfile = _activeProfile with { Mode = McpExecutionModeHelper.ToWireName( m ) };
				anyApplied = true;
			}

			Dictionary<string, bool> cats = new( _activeProfile.Categories, StringComparer.OrdinalIgnoreCase );
			if( allowCategories is not null )
			{
				foreach( McpCapabilityCategory c in allowCategories )
				{
					cats[ c.ToString() ] = true;
					anyApplied = true;
				}
			}
			if( denyCategories is not null )
			{
				foreach( McpCapabilityCategory c in denyCategories )
				{
					cats[ c.ToString() ] = false;
					anyApplied = true;
				}
			}

			List<string> allowed = [ .. _activeProfile.AllowedTools ];
			List<string> denied = [ .. _activeProfile.DeniedTools ];
			List<string> review = [ .. _activeProfile.RequireReviewTools ];

			if( allowTools is not null )
			{
				foreach( string t in allowTools.Where( s => !string.IsNullOrWhiteSpace( s ) ).Select( s => s.Trim() ) )
				{
					denied.RemoveAll( x => string.Equals( x, t, StringComparison.OrdinalIgnoreCase ) );
					if( !ContainsIgnoreCase( allowed, t ) )
						allowed.Add( t );
					anyApplied = true;
				}
			}
			if( denyTools is not null )
			{
				foreach( string t in denyTools.Where( s => !string.IsNullOrWhiteSpace( s ) ).Select( s => s.Trim() ) )
				{
					allowed.RemoveAll( x => string.Equals( x, t, StringComparison.OrdinalIgnoreCase ) );
					if( !ContainsIgnoreCase( denied, t ) )
						denied.Add( t );
					anyApplied = true;
				}
			}
			if( requireReviewTools is not null )
			{
				foreach( string t in requireReviewTools.Where( s => !string.IsNullOrWhiteSpace( s ) ).Select( s => s.Trim() ) )
				{
					if( !ContainsIgnoreCase( review, t ) )
						review.Add( t );
					anyApplied = true;
				}
			}

			int timeout = reviewTimeoutSeconds is int sec ? Math.Clamp( sec, 5, 3600 ) : _activeProfile.ReviewTimeoutSeconds;
			if( reviewTimeoutSeconds.HasValue )
				anyApplied = true;

			_activeProfile = _activeProfile with
			{
				Categories = cats,
				AllowedTools = allowed,
				DeniedTools = denied,
				RequireReviewTools = review,
				ReviewTimeoutSeconds = timeout,
			};

			if( anyApplied )
				_hasRuntimeOverrides = true;

			updated = _activeProfile.DeepClone();
		}

		NotifyPermissionsChanged();
		return updated;
	}

	/// <summary>Evaluates whether a tool call is allowed, denied, or requires human review under the active profile.</summary>
	public McpPermissionEvaluation Evaluate( string toolName, JsonElement? rawArguments = null )
	{
		McpToolClassification cls = McpToolCatalog.Classify( toolName, rawArguments );

		// Anti-self-escalation guard: AI may never click its own Approve/Mode/Preset controls via ui_* tools.
		if( McpToolCatalog.TargetsProtectedPermissionControl( toolName, rawArguments ) )
		{
			return new McpPermissionEvaluation(
				Allowed: false,
				RequiresReview: false,
				Category: cls.Category,
				RiskLevel: McpToolRiskLevel.CriticalAction,
				Reason: "Blocked: MCP AI cannot modify MCP permission settings, approve its own reviews, or toggle the MCP bridge via live UI automation. Update permissions directly in the HackPDM GUI, mcp-permissions.json, or CLI." );
		}

		McpPermissionProfile profile;
		bool hasPendingReviews;
		lock( _gate )
		{
			profile = _activeProfile;
			hasPendingReviews = _pendingReviews.Count > 0;
		}

		// Anti-self-approval guard: AI may not answer MessageBox prompts via ui_dialog_respond while a review prompt is pending.
		if( hasPendingReviews && toolName == "ui_dialog_respond" && cls.RiskLevel != McpToolRiskLevel.ReadOnly )
		{
			return new McpPermissionEvaluation(
				Allowed: false,
				RequiresReview: false,
				Category: cls.Category,
				RiskLevel: McpToolRiskLevel.CriticalAction,
				Reason: "Blocked: MCP AI cannot respond to GUI dialogs while an MCP action review is pending user approval." );
		}

		// 1. Explicit per-tool deny always wins.
		if( ContainsIgnoreCase( profile.DeniedTools, toolName ) )
		{
			return new McpPermissionEvaluation(
				Allowed: false,
				RequiresReview: false,
				Category: cls.Category,
				RiskLevel: cls.RiskLevel,
				Reason: $"Tool '{toolName}' is explicitly denied by permission profile '{profile.Name}'." );
		}

		// 2. Permission introspection tools are always allowed (unless explicitly denied above).
		if( toolName is "mcp_permissions_get" or "mcp_permissions_list_presets" )
		{
			return new McpPermissionEvaluation(
				Allowed: true,
				RequiresReview: false,
				Category: cls.Category,
				RiskLevel: McpToolRiskLevel.ReadOnly,
				Reason: "Permission introspection is always permitted." );
		}

		McpExecutionMode mode = profile.ParsedMode;

		// 3. Read-Only mode hard barrier blocks all non-ReadOnly operations (no pushing/writing/mutating).
		if( mode == McpExecutionMode.ReadOnly && cls.RiskLevel != McpToolRiskLevel.ReadOnly )
		{
			return new McpPermissionEvaluation(
				Allowed: false,
				RequiresReview: false,
				Category: cls.Category,
				RiskLevel: cls.RiskLevel,
				Reason: $"Blocked by Read-Only mode ('{profile.Name}'): '{toolName}' is a {cls.RiskLevel} operation ({cls.Category}). No pushing, editing, deleting, or mutating UI control is allowed.",
				IsRemoteRpc: cls.IsRemoteRpc );
		}

		// 3b. Local-Only mode hard barrier blocks ALL remote Odoo/PDM server RPC operations (both remote reads and writes).
		if( mode == McpExecutionMode.LocalOnly && cls.IsRemoteRpc )
		{
			return new McpPermissionEvaluation(
				Allowed: false,
				RequiresReview: false,
				Category: cls.Category,
				RiskLevel: cls.RiskLevel,
				Reason: $"Blocked by Local-Only mode ('{profile.Name}'): '{toolName}' performs remote Odoo/PDM server RPC operations. Only local computer files, SolidWorks CAD inspection/editing, local config, restore points, and local GUI are permitted.",
				IsRemoteRpc: true );
		}

		bool explicitlyAllowed = ContainsIgnoreCase( profile.AllowedTools, toolName );
		bool explicitlyRequireReview = ContainsIgnoreCase( profile.RequireReviewTools, toolName );

		// 4. Category gate (unless tool is explicitly allow-listed).
		if( !explicitlyAllowed && !profile.IsCategoryEnabled( cls.Category ) )
		{
			return new McpPermissionEvaluation(
				Allowed: false,
				RequiresReview: false,
				Category: cls.Category,
				RiskLevel: cls.RiskLevel,
				Reason: $"Blocked: capability category '{cls.Category}' is disabled in profile '{profile.Name}' (tool '{toolName}').",
				IsRemoteRpc: cls.IsRemoteRpc );
		}

		// 5. Explicit per-tool review requirement.
		if( explicitlyRequireReview )
		{
			return new McpPermissionEvaluation(
				Allowed: true,
				RequiresReview: true,
				Category: cls.Category,
				RiskLevel: cls.RiskLevel,
				Reason: $"Tool '{toolName}' requires explicit human review under profile '{profile.Name}'.",
				IsRemoteRpc: cls.IsRemoteRpc );
		}

		// 6. Read-only tools and explicitly allow-listed tools run immediately without review.
		if( cls.RiskLevel == McpToolRiskLevel.ReadOnly || explicitlyAllowed )
		{
			return new McpPermissionEvaluation(
				Allowed: true,
				RequiresReview: false,
				Category: cls.Category,
				RiskLevel: cls.RiskLevel,
				Reason: cls.RiskLevel == McpToolRiskLevel.ReadOnly
					? "Read-only inspection permitted."
					: $"Tool '{toolName}' is explicitly allow-listed in profile '{profile.Name}'.",
				IsRemoteRpc: cls.IsRemoteRpc );
		}

		// 7. Execution mode review policy for mutating tools.
		return mode switch
		{
			McpExecutionMode.Auto => new McpPermissionEvaluation(
				Allowed: true,
				RequiresReview: false,
				Category: cls.Category,
				RiskLevel: cls.RiskLevel,
				Reason: "Permitted automatically in 'auto' mode.",
				IsRemoteRpc: cls.IsRemoteRpc ),

			McpExecutionMode.LocalOnly => new McpPermissionEvaluation(
				Allowed: true,
				RequiresReview: false,
				Category: cls.Category,
				RiskLevel: cls.RiskLevel,
				Reason: "Permitted locally in 'local-only' mode (no remote RPC).",
				IsRemoteRpc: false ),

			McpExecutionMode.Review => new McpPermissionEvaluation(
				Allowed: true,
				RequiresReview: true,
				Category: cls.Category,
				RiskLevel: cls.RiskLevel,
				Reason: $"Mode 'review' requires your approval before running '{toolName}' ({cls.Description}).",
				IsRemoteRpc: cls.IsRemoteRpc ),

			McpExecutionMode.ReviewCritical when cls.RiskLevel == McpToolRiskLevel.CriticalAction => new McpPermissionEvaluation(
				Allowed: true,
				RequiresReview: true,
				Category: cls.Category,
				RiskLevel: cls.RiskLevel,
				Reason: $"Mode 'review-critical' requires your approval for critical action '{toolName}' ({cls.Description}).",
				IsRemoteRpc: cls.IsRemoteRpc ),

			McpExecutionMode.ReviewCritical => new McpPermissionEvaluation(
				Allowed: true,
				RequiresReview: false,
				Category: cls.Category,
				RiskLevel: cls.RiskLevel,
				Reason: $"Routine procedure '{toolName}' permitted automatically in 'review-critical' mode.",
				IsRemoteRpc: cls.IsRemoteRpc ),

			_ => new McpPermissionEvaluation(
				Allowed: false,
				RequiresReview: false,
				Category: cls.Category,
				RiskLevel: cls.RiskLevel,
				Reason: $"Blocked by mode '{profile.Mode}'.",
				IsRemoteRpc: cls.IsRemoteRpc ),
		};
	}

	/// <summary>
	/// Enforces the active permission policy for a tool call.
	/// Returns <c>null</c> when execution is permitted (including after live human approval),
	/// or a structured <see cref="McpToolResult"/> error when blocked, denied, or timed out.
	/// </summary>
	public async Task<McpToolResult?> EnforceAsync( string toolName, JsonElement? rawArguments, CancellationToken cancellationToken )
	{
		McpPermissionEvaluation eval = Evaluate( toolName, rawArguments );
		if( !eval.Allowed )
		{
			McpPermissionProfile snap = ActiveProfile;
			return McpToolResult.Error( eval.Reason, new
			{
				code = "permission_denied",
				tool = toolName,
				category = eval.Category.ToString(),
				riskLevel = eval.RiskLevel.ToString(),
				isRemoteRpc = eval.IsRemoteRpc,
				mode = snap.Mode,
				profile = snap.Key,
				profileName = snap.Name,
				configFile = ConfigFilePath,
			} );
		}

		if( !eval.RequiresReview )
			return null;

		lock( _gate )
		{
			if( _sessionApprovedTools.Contains( toolName ) )
				return null;
		}

		if( !HasInteractiveReviewer )
		{
			McpPermissionProfile snap = ActiveProfile;
			return McpToolResult.Error(
				$"Human review required for '{toolName}' ({eval.Reason}), but no interactive HackPDM GUI reviewer is attached.",
				new
				{
					code = "review_required",
					tool = toolName,
					category = eval.Category.ToString(),
					riskLevel = eval.RiskLevel.ToString(),
					mode = snap.Mode,
					profile = snap.Key,
					profileName = snap.Name,
					hint = "Open HackPDM desktop GUI to approve actions interactively, switch to --permission-mode auto, or explicitly allow this tool with --allow-tool " + toolName,
				} );
		}

		McpReviewDecision decision = await RequestInteractiveReviewAsync( toolName, rawArguments, eval, cancellationToken );
		switch( decision )
		{
			case McpReviewDecision.ApprovedOnce:
				return null;

			case McpReviewDecision.ApprovedForSession:
				lock( _gate )
					_sessionApprovedTools.Add( toolName );
				NotifyPermissionsChanged();
				return null;

			case McpReviewDecision.TimedOut:
				return McpToolResult.Error(
					$"Review request for '{toolName}' timed out awaiting user approval.",
					new
					{
						code = "review_timeout",
						tool = toolName,
						category = eval.Category.ToString(),
						riskLevel = eval.RiskLevel.ToString(),
					} );

			default:
				return McpToolResult.Error(
					$"Action '{toolName}' was denied by the user during review.",
					new
					{
						code = "review_denied",
						tool = toolName,
						category = eval.Category.ToString(),
						riskLevel = eval.RiskLevel.ToString(),
					} );
		}
	}

	/// <summary>
	/// Pre-proxy check used only when standalone CLI runtime overrides were explicitly passed while
	/// proxying to a running GUI: blocks strictly disallowed calls without rejecting review-gated calls
	/// (which will be reviewed interactively by the live GUI).
	/// </summary>
	public Task<McpToolResult?> EnforcePreProxyDenyOnlyAsync( string toolName, JsonElement? rawArguments, CancellationToken cancellationToken )
	{
		_ = cancellationToken;
		McpPermissionEvaluation eval = Evaluate( toolName, rawArguments );
		if( eval.Allowed )
			return Task.FromResult<McpToolResult?>( null );

		McpPermissionProfile snap = ActiveProfile;
		return Task.FromResult<McpToolResult?>( McpToolResult.Error( eval.Reason, new
		{
			code = "permission_denied",
			enforcedBy = "cli_override",
			tool = toolName,
			category = eval.Category.ToString(),
			riskLevel = eval.RiskLevel.ToString(),
			mode = snap.Mode,
			profile = snap.Key,
			profileName = snap.Name,
		} ) );
	}

	/// <summary>Resolves a pending interactive review by ID (called by the HackPDM GUI when the user clicks Approve/Deny).</summary>
	public bool ResolveReview( string reviewId, McpReviewDecision decision )
	{
		PendingReviewEntry? entry;
		lock( _gate )
		{
			if( !_pendingReviews.TryGetValue( reviewId, out entry ) )
				return false;
		}
		return entry.Tcs.TrySetResult( decision );
	}

	/// <summary>Clears all session-scoped tool approvals granted during the current session.</summary>
	public void ClearSessionApprovals()
	{
		lock( _gate )
			_sessionApprovedTools.Clear();
		NotifyPermissionsChanged();
	}

	/// <summary>Returns a complete serializable summary of the active permissions, mode, capabilities, pending reviews, and profiles.</summary>
	public object GetStatusSnapshot()
	{
		McpPermissionProfile active;
		List<string> sessionApproved;
		List<McpPendingReview> pending;
		List<McpPermissionProfile> allProfiles;
		bool promptHidden;
		bool runtimeOverrides;

		lock( _gate )
		{
			active = _activeProfile.DeepClone();
			sessionApproved = _sessionApprovedTools.OrderBy( s => s, StringComparer.OrdinalIgnoreCase ).ToList();
			pending = _pendingReviews.Values.Select( v => v.Review ).OrderBy( r => r.CreatedAtUtc ).ToList();
			allProfiles = BuiltInPresetList.Select( p => p.DeepClone() )
				.Concat( _config.CustomProfiles.Values.Select( p => p.DeepClone( isBuiltIn: false ) ) )
				.ToList();
			promptHidden = _config.PromptDialogWhenHidden;
			runtimeOverrides = _hasRuntimeOverrides;
		}

		return new
		{
			configFilePath = ConfigFilePath,
			hasInteractiveReviewer = HasInteractiveReviewer,
			hasRuntimeOverrides = runtimeOverrides,
			promptDialogWhenHidden = promptHidden,
			activeMode = active.Mode,
			activeModeDescription = McpExecutionModeHelper.Describe( active.ParsedMode ),
			activeProfile = active,
			capabilities = CapabilityCatalogList.Select( c => new
			{
				key = c.Key,
				displayName = c.DisplayName,
				description = c.Description,
				enabled = active.IsCategoryEnabled( c.Category ),
			} ).ToList(),
			sessionApprovedTools = sessionApproved,
			pendingReviewCount = pending.Count,
			pendingReviews = pending,
			availableProfiles = allProfiles.Select( p => new
			{
				p.Key,
				p.Name,
				p.Mode,
				p.IsBuiltInPreset,
				p.Description,
			} ).ToList(),
		};
	}

	public static bool TryParseCategory( string? text, out McpCapabilityCategory category )
	{
		if( string.IsNullOrWhiteSpace( text ) )
		{
			category = default;
			return false;
		}

		string normalized = text.Trim().Replace( "-", "" ).Replace( "_", "" );
		foreach( McpCapabilityCategory c in Enum.GetValues<McpCapabilityCategory>() )
		{
			if( string.Equals( c.ToString(), normalized, StringComparison.OrdinalIgnoreCase ) )
			{
				category = c;
				return true;
			}
		}

		// Friendly short aliases for CLI ergonomics
		switch( normalized.ToLowerInvariant() )
		{
			case "read":
			case "vaultread":
			case "odooread":
				category = McpCapabilityCategory.ReadVaultAndOdoo;
				return true;
			case "pdmwork":
			case "working":
				category = McpCapabilityCategory.PdmWorkingActions;
				return true;
			case "commit":
			case "push":
				category = McpCapabilityCategory.PdmCommitAndPush;
				return true;
			case "delete":
			case "admin":
			case "odoowrite":
				category = McpCapabilityCategory.PdmDeleteAndAdmin;
				return true;
			case "local":
			case "localedit":
			case "pwa":
				category = McpCapabilityCategory.LocalVaultFileEdit;
				return true;
			case "fs":
			case "externalfs":
			case "filesystem":
				category = McpCapabilityCategory.ExternalFilesystemAccess;
				return true;
			case "sw":
			case "solidworks":
			case "cad":
				category = McpCapabilityCategory.SolidWorksEditing;
				return true;
			case "uiread":
				category = McpCapabilityCategory.LiveUiRead;
				return true;
			case "uicontrol":
			case "uiwrite":
			case "ui":
				category = McpCapabilityCategory.LiveUiControl;
				return true;
			case "settings":
			case "session":
				category = McpCapabilityCategory.SettingsAndSession;
				return true;
			case "mcpadmin":
			case "restore":
				category = McpCapabilityCategory.McpAdminAndRestore;
				return true;
			default:
				category = default;
				return false;
		}
	}

	private async Task<McpReviewDecision> RequestInteractiveReviewAsync(
		string toolName,
		JsonElement? rawArguments,
		McpPermissionEvaluation eval,
		CancellationToken cancellationToken )
	{
		int timeoutSeconds;
		lock( _gate )
			timeoutSeconds = Math.Clamp( _activeProfile.ReviewTimeoutSeconds, 5, 3600 );

		DateTimeOffset now = DateTimeOffset.UtcNow;
		McpPendingReview review = new(
			Id: Guid.NewGuid().ToString( "N" )[ ..8 ],
			ToolName: toolName,
			Category: eval.Category,
			RiskLevel: eval.RiskLevel,
			Reason: eval.Reason,
			ArgsSummary: McpActivityLog.SummarizeArguments( rawArguments ),
			CreatedAtUtc: now,
			ExpiresAtUtc: now.AddSeconds( timeoutSeconds ) );

		TaskCompletionSource<McpReviewDecision> tcs = new( TaskCreationOptions.RunContinuationsAsynchronously );
		PendingReviewEntry entry = new( review, tcs );

		lock( _gate )
			_pendingReviews[ review.Id ] = entry;

		NotifyPendingReviewsChanged();
		try { ReviewRequested?.Invoke( review ); } catch { }

		try
		{
			return await tcs.Task.WaitAsync( TimeSpan.FromSeconds( timeoutSeconds ), cancellationToken );
		}
		catch( TimeoutException )
		{
			tcs.TrySetResult( McpReviewDecision.TimedOut );
			return McpReviewDecision.TimedOut;
		}
		finally
		{
			lock( _gate )
				_pendingReviews.Remove( review.Id );
			NotifyPendingReviewsChanged();
		}
	}

	private void LoadFromDisk( string defaultPresetWhenUnconfigured )
	{
		lock( _gate )
		{
			if( !File.Exists( ConfigFilePath ) )
			{
				McpPermissionProfile preset = ResolveBuiltInPreset( defaultPresetWhenUnconfigured )
					?? ResolveBuiltInPreset( "review-critical" )!;
				_config = new McpPermissionsConfigFile
				{
					ActiveProfile = preset.Key,
					PromptDialogWhenHidden = true,
				};
				_activeProfile = NormalizeProfile( preset );
				return;
			}

			try
			{
				string json = File.ReadAllText( ConfigFilePath, Encoding.UTF8 );
				McpPermissionsConfigFile? loaded = JsonSerializer.Deserialize<McpPermissionsConfigFile>( json, PrettyJsonOptions );
				if( loaded is null )
					return;

				_config = loaded;
				_config.CustomProfiles = new Dictionary<string, McpPermissionProfile>(
					loaded.CustomProfiles ?? [],
					StringComparer.OrdinalIgnoreCase );

				if( _config.ActiveOverrides is not null )
				{
					_activeProfile = NormalizeProfile( _config.ActiveOverrides );
					return;
				}

				if( _config.CustomProfiles.TryGetValue( _config.ActiveProfile, out McpPermissionProfile? custom ) )
				{
					_activeProfile = NormalizeProfile( custom.DeepClone( isBuiltIn: false ) );
					return;
				}

				McpPermissionProfile fallback = ResolveBuiltInPreset( _config.ActiveProfile )
					?? ResolveBuiltInPreset( defaultPresetWhenUnconfigured )
					?? ResolveBuiltInPreset( "review-critical" )!;
				_activeProfile = NormalizeProfile( fallback );
			}
			catch
			{
				McpPermissionProfile fallback = ResolveBuiltInPreset( defaultPresetWhenUnconfigured )
					?? ResolveBuiltInPreset( "review-critical" )!;
				_config = new McpPermissionsConfigFile { ActiveProfile = fallback.Key };
				_activeProfile = NormalizeProfile( fallback );
			}
		}
	}

	private void UpdateActivePersistenceLocked()
	{
		if( !_activeProfile.IsBuiltInPreset && _config.CustomProfiles.ContainsKey( _activeProfile.Key ) )
		{
			_config.CustomProfiles[ _activeProfile.Key ] = _activeProfile.DeepClone( isBuiltIn: false );
			_config.ActiveProfile = _activeProfile.Key;
			_config.ActiveOverrides = null;
		}
		else
		{
			_config.ActiveProfile = _activeProfile.Key;
			_config.ActiveOverrides = _activeProfile.DeepClone();
		}
	}

	private void SaveToDiskLocked()
	{
		try
		{
			string? dir = Path.GetDirectoryName( ConfigFilePath );
			if( !string.IsNullOrEmpty( dir ) )
				Directory.CreateDirectory( dir );

			string json = JsonSerializer.Serialize( _config, PrettyJsonOptions );
			string tmp = ConfigFilePath + ".tmp";
			File.WriteAllText( tmp, json, Encoding.UTF8 );
			File.Move( tmp, ConfigFilePath, overwrite: true );
		}
		catch
		{
			// Never crash callers on config persistence errors
		}
	}

	private void NotifyPermissionsChanged()
	{
		try { PermissionsChanged?.Invoke(); } catch { }
	}

	private void NotifyPendingReviewsChanged()
	{
		try { PendingReviewsChanged?.Invoke(); } catch { }
	}

	private static McpPermissionProfile? ResolveBuiltInPreset( string? keyOrName )
	{
		if( string.IsNullOrWhiteSpace( keyOrName ) )
			return null;
		string q = keyOrName.Trim();
		McpPermissionProfile? match = BuiltInPresetList.FirstOrDefault( p =>
			string.Equals( p.Key, q, StringComparison.OrdinalIgnoreCase )
			|| string.Equals( p.Name, q, StringComparison.OrdinalIgnoreCase ) );
		if( match is not null )
			return match.DeepClone();

		// Allow mode aliases (e.g. "readonly" -> "read-only", "review_critical" -> "review-critical")
		if( McpExecutionModeHelper.TryParse( q, out McpExecutionMode mode ) )
		{
			string wire = McpExecutionModeHelper.ToWireName( mode );
			return BuiltInPresetList.FirstOrDefault( p => string.Equals( p.Key, wire, StringComparison.OrdinalIgnoreCase ) )?.DeepClone();
		}
		return null;
	}

	private static McpPermissionProfile NormalizeProfile( McpPermissionProfile profile )
	{
		Dictionary<string, bool> cats = new( StringComparer.OrdinalIgnoreCase );
		foreach( McpCapabilityCategory c in Enum.GetValues<McpCapabilityCategory>() )
			cats[ c.ToString() ] = profile.Categories.TryGetValue( c.ToString(), out bool val ) && val;

		return profile with
		{
			Mode = McpExecutionModeHelper.ToWireName( profile.ParsedMode ),
			ReviewTimeoutSeconds = Math.Clamp( profile.ReviewTimeoutSeconds <= 0 ? 120 : profile.ReviewTimeoutSeconds, 5, 3600 ),
			Categories = cats,
			AllowedTools = profile.AllowedTools?.Distinct( StringComparer.OrdinalIgnoreCase ).ToList() ?? [],
			DeniedTools = profile.DeniedTools?.Distinct( StringComparer.OrdinalIgnoreCase ).ToList() ?? [],
			RequireReviewTools = profile.RequireReviewTools?.Distinct( StringComparer.OrdinalIgnoreCase ).ToList() ?? [],
		};
	}

	private static string SlugifyProfileKey( string name )
	{
		StringBuilder sb = new( name.Length );
		bool prevDash = false;
		foreach( char ch in name.ToLowerInvariant() )
		{
			if( char.IsLetterOrDigit( ch ) )
			{
				sb.Append( ch );
				prevDash = false;
			}
			else if( !prevDash && sb.Length > 0 )
			{
				sb.Append( '-' );
				prevDash = true;
			}
		}
		string slug = sb.ToString().Trim( '-' );
		return string.IsNullOrEmpty( slug ) ? "custom-" + Guid.NewGuid().ToString( "N" )[ ..6 ] : slug;
	}

	private static Dictionary<string, bool> AllCategories( bool enabled )
	{
		Dictionary<string, bool> dict = new( StringComparer.OrdinalIgnoreCase );
		foreach( McpCapabilityCategory c in Enum.GetValues<McpCapabilityCategory>() )
			dict[ c.ToString() ] = enabled;
		return dict;
	}

	private static IReadOnlyList<McpPermissionProfile> BuildBuiltInPresets()
	{
		Dictionary<string, bool> readOnlyCats = AllCategories( false );
		readOnlyCats[ nameof( McpCapabilityCategory.ReadVaultAndOdoo ) ] = true;
		readOnlyCats[ nameof( McpCapabilityCategory.LiveUiRead ) ] = true;

		Dictionary<string, bool> localOnlyCats = AllCategories( true );
		localOnlyCats[ nameof( McpCapabilityCategory.PdmWorkingActions ) ] = false;
		localOnlyCats[ nameof( McpCapabilityCategory.PdmCommitAndPush ) ] = false;
		localOnlyCats[ nameof( McpCapabilityCategory.PdmDeleteAndAdmin ) ] = false;
		localOnlyCats[ nameof( McpCapabilityCategory.SettingsAndSession ) ] = false;

		Dictionary<string, bool> localCadCats = AllCategories( true );
		localCadCats[ nameof( McpCapabilityCategory.PdmCommitAndPush ) ] = false;
		localCadCats[ nameof( McpCapabilityCategory.PdmDeleteAndAdmin ) ] = false;
		localCadCats[ nameof( McpCapabilityCategory.SettingsAndSession ) ] = false;

		return
		[
			new McpPermissionProfile
			{
				Key = "read-only",
				Name = "Read-Only Observer (Get Only)",
				Description = "Strictly read/get operations across vault entries, SolidWorks files, settings, logs, and live UI. Blocks all pushing, editing, deleting, and mutating UI control.",
				IsBuiltInPreset = true,
				Mode = "read-only",
				ReviewTimeoutSeconds = 120,
				Categories = readOnlyCats,
			},
			new McpPermissionProfile
			{
				Key = "local-only",
				Name = "Local-Only (No Server RPC)",
				Description = "Computer-local operations only (local PWA files, external filesystem, SolidWorks inspection/editing, restore points, local GUI). Strictly blocks all remote Odoo/PDM server RPC calls.",
				IsBuiltInPreset = true,
				Mode = "local-only",
				ReviewTimeoutSeconds = 120,
				Categories = localOnlyCats,
			},
			new McpPermissionProfile
			{
				Key = "review",
				Name = "Supervised Assistant (Review All)",
				Description = "Read-only inspection runs freely; every mutating procedure, CAD edit, commit, delete, or UI interaction requires your explicit approval.",
				IsBuiltInPreset = true,
				Mode = "review",
				ReviewTimeoutSeconds = 120,
				Categories = AllCategories( true ),
			},
			new McpPermissionProfile
			{
				Key = "review-critical",
				Name = "CAD Engineer (Review Critical)",
				Description = "Routine procedures (get latest, checkout, local prep, UI navigation) run automatically; critical actions (commit, delete, Odoo RPC writes, SolidWorks edits, rollbacks) require your approval.",
				IsBuiltInPreset = true,
				Mode = "review-critical",
				ReviewTimeoutSeconds = 120,
				Categories = AllCategories( true ),
			},
			new McpPermissionProfile
			{
				Key = "local-cad-prep",
				Name = "Local CAD Prep (No Server Push)",
				Description = "Allows local workspace prep, external file import into PWA, and SolidWorks property/dependency editing (with review on binary edits), while blocking all Odoo server commits, deletes, and raw RPC writes.",
				IsBuiltInPreset = true,
				Mode = "review-critical",
				ReviewTimeoutSeconds = 120,
				Categories = localCadCats,
			},
			new McpPermissionProfile
			{
				Key = "auto",
				Name = "Autonomous (Full Auto)",
				Description = "Executes all permitted actions immediately without interactive review prompts. Keep Automatic Restore Points enabled for quick undo.",
				IsBuiltInPreset = true,
				Mode = "auto",
				ReviewTimeoutSeconds = 120,
				Categories = AllCategories( true ),
			},
		];
	}

	private static bool ContainsIgnoreCase( List<string> list, string value )
		=> list.Any( x => string.Equals( x, value, StringComparison.OrdinalIgnoreCase ) );

	private sealed record PendingReviewEntry( McpPendingReview Review, TaskCompletionSource<McpReviewDecision> Tcs );
}

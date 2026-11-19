using System.Text.Json.Serialization;

namespace HackPDM.Mcp.Security;

/// <summary>Execution/review policy controlling whether permitted MCP actions run automatically, require human review, or are strictly read-only.</summary>
[JsonConverter( typeof( JsonStringEnumConverter ) )]
public enum McpExecutionMode
{
	/// <summary>Execute all permitted actions immediately with no review step ("auto").</summary>
	Auto,
	/// <summary>Require human review before executing any mutating/action tool ("review").</summary>
	Review,
	/// <summary>Allow simple/routine procedures automatically, but require human review for critical operations like commit, delete, Odoo RPC writes, CAD binary edits, or rollbacks ("review-critical").</summary>
	ReviewCritical,
	/// <summary>Read-only get/inspection operations only — block all pushing, writing, editing, deleting, and mutating UI control ("read-only").</summary>
	ReadOnly,
	/// <summary>Local-computer operations only (local files, SolidWorks inspection/editing, local config, restore points, local GUI) — strictly block all remote Odoo/PDM server RPC operations ("local-only").</summary>
	LocalOnly,
}

/// <summary>Granular capability categories users can allow or deny per permission profile.</summary>
[JsonConverter( typeof( JsonStringEnumConverter ) )]
public enum McpCapabilityCategory
{
	/// <summary>Read vault directory trees, entries, versions, previews, dependency graphs, search, lookups, and Odoo read/schema queries.</summary>
	ReadVaultAndOdoo,
	/// <summary>Routine PDM working actions: get latest, checkout, uncheckout, restore logically deleted entries, download versions.</summary>
	PdmWorkingActions,
	/// <summary>Push changes to Odoo server: commit/check-in, upload previews, create remote directories.</summary>
	PdmCommitAndPush,
	/// <summary>Destructive or administrative Odoo server actions: logical/permanent delete, generic odoo_create/write/unlink/execute.</summary>
	PdmDeleteAndAdmin,
	/// <summary>Create, edit, or delete files inside the HackPDM local PWA vault folder.</summary>
	LocalVaultFileEdit,
	/// <summary>Browse, copy, move, delete, or import files located anywhere on the computer outside the PWA vault.</summary>
	ExternalFilesystemAccess,
	/// <summary>Modify SolidWorks (.SLDPRT/.SLDASM/.SLDDRW) custom properties and internal dependency paths.</summary>
	SolidWorksEditing,
	/// <summary>Read-only inspection of the live HackPDM desktop GUI: UI state, open windows, visual tree, control search, and screenshots.</summary>
	LiveUiRead,
	/// <summary>Interactive control of the live HackPDM desktop GUI: click buttons, edit inputs, invoke ViewModel commands, navigate, change theme, respond to dialogs.</summary>
	LiveUiControl,
	/// <summary>Modify HackPDM connection settings, login/logout, load/save profiles, settings.json, and AppConfig.json.</summary>
	SettingsAndSession,
	/// <summary>MCP administrative actions: clear activity log, restore or delete restore points, configure MCP settings.</summary>
	McpAdminAndRestore,
}

/// <summary>Risk tier for an MCP tool call, used by <see cref="McpExecutionMode"/>.</summary>
[JsonConverter( typeof( JsonStringEnumConverter ) )]
public enum McpToolRiskLevel
{
	/// <summary>Pure read/query/inspection — no side effects.</summary>
	ReadOnly,
	/// <summary>Routine/reversible procedure (e.g., navigation, get-latest, checkout, creating a checkpoint, local folder prep).</summary>
	StandardAction,
	/// <summary>Critical or high-impact operation (e.g., commit/push, delete, Odoo write/RPC, SolidWorks binary rewrite, external file delete/move, restore rollback, settings edit).</summary>
	CriticalAction,
}

/// <summary>Decision returned by a human reviewer for a pending MCP tool action.</summary>
[JsonConverter( typeof( JsonStringEnumConverter ) )]
public enum McpReviewDecision
{
	ApprovedOnce,
	ApprovedForSession,
	Denied,
	TimedOut,
}

/// <summary>Named MCP permission profile (either a built-in preset or user-saved custom profile).</summary>
public sealed record McpPermissionProfile
{
	public string Name { get; init; } = "CAD Engineer (Review Critical)";
	public string Key { get; init; } = "review-critical";
	public string Description { get; init; } = string.Empty;
	public bool IsBuiltInPreset { get; init; }
	public string Mode { get; init; } = "review-critical";
	public int ReviewTimeoutSeconds { get; init; } = 120;
	public Dictionary<string, bool> Categories { get; init; } = new( StringComparer.OrdinalIgnoreCase );
	public List<string> AllowedTools { get; init; } = [];
	public List<string> DeniedTools { get; init; } = [];
	public List<string> RequireReviewTools { get; init; } = [];

	public McpExecutionMode ParsedMode => McpExecutionModeHelper.Parse( Mode );

	public bool IsCategoryEnabled( McpCapabilityCategory category )
		=> Categories.TryGetValue( category.ToString(), out bool enabled ) ? enabled : false;

	public McpPermissionProfile DeepClone( string? newKey = null, string? newName = null, bool? isBuiltIn = null ) => new()
	{
		Key = newKey ?? Key,
		Name = newName ?? Name,
		Description = Description,
		IsBuiltInPreset = isBuiltIn ?? IsBuiltInPreset,
		Mode = Mode,
		ReviewTimeoutSeconds = ReviewTimeoutSeconds,
		Categories = new Dictionary<string, bool>( Categories, StringComparer.OrdinalIgnoreCase ),
		AllowedTools = [ .. AllowedTools ],
		DeniedTools = [ .. DeniedTools ],
		RequireReviewTools = [ .. RequireReviewTools ],
	};
}

/// <summary>Persistent JSON configuration stored at <c>&lt;AppDataPaths.Config&gt;/mcp-permissions.json</c>.</summary>
public sealed class McpPermissionsConfigFile
{
	public int SchemaVersion { get; set; } = 1;
	public string ActiveProfile { get; set; } = "review-critical";
	public bool PromptDialogWhenHidden { get; set; } = true;
	public Dictionary<string, McpPermissionProfile> CustomProfiles { get; set; } = new( StringComparer.OrdinalIgnoreCase );
	/// <summary>Optional overrides applied on top of a built-in preset when the user edits toggles directly without saving a new profile name.</summary>
	public McpPermissionProfile? ActiveOverrides { get; set; }
}

/// <summary>A live MCP tool call paused awaiting human approval in <c>review</c> or <c>review-critical</c> mode.</summary>
public sealed record McpPendingReview(
	string Id,
	string ToolName,
	McpCapabilityCategory Category,
	McpToolRiskLevel RiskLevel,
	string Reason,
	string? ArgsSummary,
	DateTimeOffset CreatedAtUtc,
	DateTimeOffset ExpiresAtUtc );

/// <summary>Result of evaluating a tool call against the active permission profile.</summary>
public sealed record McpPermissionEvaluation(
	bool Allowed,
	bool RequiresReview,
	McpCapabilityCategory Category,
	McpToolRiskLevel RiskLevel,
	string Reason,
	bool IsRemoteRpc = false );

public static class McpExecutionModeHelper
{
	public static string ToWireName( McpExecutionMode mode ) => mode switch
	{
		McpExecutionMode.Auto => "auto",
		McpExecutionMode.Review => "review",
		McpExecutionMode.ReviewCritical => "review-critical",
		McpExecutionMode.ReadOnly => "read-only",
		McpExecutionMode.LocalOnly => "local-only",
		_ => "review-critical",
	};

	public static McpExecutionMode Parse( string? value ) => ( value?.Trim().ToLowerInvariant() ) switch
	{
		"auto" or "autonomous" or "full" => McpExecutionMode.Auto,
		"review" or "review-all" or "approve" => McpExecutionMode.Review,
		"review-critical" or "review_critical" or "reviewcritical" or "critical" => McpExecutionMode.ReviewCritical,
		"read-only" or "read_only" or "readonly" or "ro" => McpExecutionMode.ReadOnly,
		"local-only" or "local_only" or "localonly" or "local" or "offline" => McpExecutionMode.LocalOnly,
		_ => McpExecutionMode.ReviewCritical,
	};

	public static bool TryParse( string? value, out McpExecutionMode mode )
	{
		switch( value?.Trim().ToLowerInvariant() )
		{
			case "auto" or "autonomous" or "full":
				mode = McpExecutionMode.Auto;
				return true;
			case "review" or "review-all" or "approve":
				mode = McpExecutionMode.Review;
				return true;
			case "review-critical" or "review_critical" or "reviewcritical" or "critical":
				mode = McpExecutionMode.ReviewCritical;
				return true;
			case "read-only" or "read_only" or "readonly" or "ro":
				mode = McpExecutionMode.ReadOnly;
				return true;
			case "local-only" or "local_only" or "localonly" or "local" or "offline":
				mode = McpExecutionMode.LocalOnly;
				return true;
			default:
				mode = McpExecutionMode.ReviewCritical;
				return false;
		}
	}

	public static string Describe( McpExecutionMode mode ) => mode switch
	{
		McpExecutionMode.Auto => "Auto — Executes all permitted actions immediately without asking for review.",
		McpExecutionMode.Review => "Review — Read-only tools run freely; every action or write requires your approval.",
		McpExecutionMode.ReviewCritical => "Review Critical — Routine procedures run automatically; critical actions (commit, delete, Odoo writes, CAD edits, rollbacks) require your approval.",
		McpExecutionMode.ReadOnly => "Read-Only — Get/read operations only (local + server reads); strictly blocks all pushing, writing, editing, deleting, and mutating UI actions.",
		McpExecutionMode.LocalOnly => "Local-Only — Computer-local operations only (local PWA files, external filesystem, SolidWorks inspection/editing, local config, restore points, local GUI); strictly blocks all remote Odoo/PDM server RPC operations.",
		_ => string.Empty,
	};
}

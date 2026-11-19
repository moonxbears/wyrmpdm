using System.Diagnostics;
using System.Text.Json;

using HackPDM.Core.Hack;
using HackPDM.Mcp;
using HackPDM.Mcp.Bridge;
using HackPDM.Mcp.Protocol;
using HackPDM.Mcp.Security;
using HackPDM.Tests.Characterization;
using HackPDM.Tests.Core;

namespace HackPDM.Tests.Mcp;

[Collection( SharedStateCollection.Name )]
public class McpPermissionsAndProfilesTests : IDisposable
{
	private readonly PwaScope _pwaScope;
	private readonly string _tempDir;
	private readonly string _pwaDir;
	private readonly string _permFile;

	public McpPermissionsAndProfilesTests()
	{
		_pwaScope = new PwaScope( create: true );
		_pwaDir = _pwaScope.Absolute;
		_tempDir = Path.Combine( Path.GetDirectoryName( _pwaDir )!, "mcp-perm-" + Guid.NewGuid().ToString( "N" )[ ..8 ] );
		Directory.CreateDirectory( _tempDir );
		_permFile = Path.Combine( _tempDir, "mcp-permissions.json" );
	}

	public void Dispose()
	{
		_pwaScope.Dispose();
		try { if( Directory.Exists( _tempDir ) ) Directory.Delete( _tempDir, recursive: true ); } catch { }
	}

	[Fact]
	public async Task AllRegisteredTools_AreExplicitlyClassifiedInCatalog_AndIntrospectionWorks()
	{
		McpPermissionManager perms = new( _permFile, defaultPresetWhenUnconfigured: "review-critical", persistChanges: true );
		using var bundle = HackPdmMcpServerFactory.Create(
			McpServerMode.Headless,
			dataDirOverride: _tempDir,
			sharedPermissions: perms );

		IReadOnlyList<McpToolDefinition> tools = bundle.Server.GetToolDefinitions();
		Assert.NotEmpty( tools );

		List<string> missing = tools
			.Select( t => t.Name )
			.Where( name => !McpToolCatalog.IsClassified( name ) )
			.ToList();

		Assert.Empty( missing );

		// Permission introspection tools and resource work end-to-end
		McpToolResult getRes = await bundle.Server.CallToolAsync( "mcp_permissions_get", null );
		Assert.NotEqual( true, getRes.IsError );
		using JsonDocument getDoc = JsonDocument.Parse( getRes.Content[ 0 ].Text! );
		Assert.Equal( "review-critical", getDoc.RootElement.GetProperty( "activeMode" ).GetString() );

		McpToolResult listRes = await bundle.Server.CallToolAsync( "mcp_permissions_list_presets", null );
		Assert.NotEqual( true, listRes.IsError );
		using JsonDocument listDoc = JsonDocument.Parse( listRes.Content[ 0 ].Text! );
		List<string> presetKeys = listDoc.RootElement.GetProperty( "profiles" )
			.EnumerateArray()
			.Select( p => p.GetProperty( "key" ).GetString()! )
			.ToList();
		Assert.Contains( "read-only", presetKeys );
		Assert.Contains( "local-only", presetKeys );
		Assert.Contains( "review", presetKeys );
		Assert.Contains( "review-critical", presetKeys );
		Assert.Contains( "local-cad-prep", presetKeys );
		Assert.Contains( "auto", presetKeys );

		McpResourceContent permRes = await bundle.Server.ReadResourceAsync( "hackpdm://mcp/permissions" );
		Assert.Equal( "application/json", permRes.MimeType );
		Assert.Contains( "review-critical", permRes.Text );
	}

	[Fact]
	public async Task ReadOnlyMode_AllowsGetsAndBlocksAllWritesAndPushes()
	{
		McpPermissionManager perms = new( _permFile, defaultPresetWhenUnconfigured: "read-only", persistChanges: true );
		using var bundle = HackPdmMcpServerFactory.Create(
			McpServerMode.Headless,
			dataDirOverride: _tempDir,
			sharedPermissions: perms );

		// Allowed get/read operations (both local and remote reads)
		foreach( string tool in new[]
		{
			"client_status", "local_list_files", "settings_list", "entry_get_details",
			"odoo_search_read", "pdm_commit_preview", "sw_inspect_file", "ui_get_state",
			"mcp_permissions_get", "mcp_permissions_list_presets",
		} )
		{
			McpPermissionEvaluation eval = perms.Evaluate( tool );
			Assert.True( eval.Allowed, $"Expected '{tool}' to be allowed in read-only mode: {eval.Reason}" );
			Assert.False( eval.RequiresReview );
		}

		// Strictly blocked mutating/pushing operations
		foreach( string tool in new[]
		{
			"local_write_file", "local_delete", "pdm_get_latest", "pdm_checkout", "pdm_commit",
			"pdm_logical_delete", "pdm_permanent_delete", "odoo_create", "odoo_write", "odoo_unlink",
			"sw_set_properties", "sw_set_dependencies", "fs_mkdir", "fs_delete", "fs_import_to_pwa",
			"settings_set", "ui_click_control", "ui_navigate", "mcp_restore_point_restore",
		} )
		{
			McpPermissionEvaluation eval = perms.Evaluate( tool );
			Assert.False( eval.Allowed, $"Expected '{tool}' to be blocked in read-only mode." );
		}

		// End-to-end tool call is blocked and does NOT write file
		string targetFile = Path.Combine( _pwaDir, "blocked_readonly.txt" );
		McpToolResult writeRes = await bundle.Server.CallToolAsync(
			"local_write_file",
			JsonArgs( new { path = targetFile, content = "should not write" } ) );

		Assert.True( writeRes.IsError );
		Assert.Contains( "permission_denied", writeRes.Content[ 0 ].Text! );
		Assert.False( File.Exists( targetFile ) );

		// Blocked attempt is recorded in MCP Activity Log for full auditability
		var logEntries = bundle.ActivityLog.GetEntries( 10 );
		Assert.Contains( logEntries, e => e.ToolName == "local_write_file" && !e.Success );

		// dryRun: true refines to ReadOnly when category is enabled
		perms.SetCategoryEnabled( McpCapabilityCategory.SolidWorksEditing, true );
		McpPermissionEvaluation dryEval = perms.Evaluate( "sw_set_dependencies", JsonArgs( new { path = "a.sldasm", dryRun = true } ) );
		Assert.True( dryEval.Allowed );
		Assert.False( dryEval.RequiresReview );
	}

	[Fact]
	public async Task LocalOnlyMode_AllowsComputerLocalTools_AndBlocksAllOdooRpcOperations()
	{
		McpPermissionManager perms = new( _permFile, defaultPresetWhenUnconfigured: "local-only", persistChanges: true );
		using var bundle = HackPdmMcpServerFactory.Create(
			McpServerMode.Headless,
			dataDirOverride: _tempDir,
			sharedPermissions: perms );

		// ALL Odoo / remote server RPC operations (both reads AND writes) must be strictly blocked
		foreach( string rpcTool in new[]
		{
			"session_test_connection", "session_check_permissions", "session_reload_lookups",
			"session_login", "session_logout", "metadata_get_lookups",
			"directory_get_entries", "directory_get_entry_ids", "directory_find_path", "directory_create",
			"entry_get_details", "entry_get_history", "entry_get_relationships", "entry_get_properties",
			"version_get_info", "version_get_preview", "version_read_contents", "version_upload_preview",
			"dependency_graph_load", "pdm_get_latest", "pdm_checkout", "pdm_uncheckout",
			"pdm_commit_preview", "pdm_commit", "pdm_logical_delete", "pdm_restore_deleted",
			"pdm_permanent_delete", "pdm_plan_version_download", "pdm_download_version",
			"search_entries", "search_get_options",
			"odoo_search", "odoo_search_count", "odoo_read", "odoo_search_read",
			"odoo_smart_read", "odoo_related_browse", "odoo_create", "odoo_write", "odoo_unlink", "odoo_execute",
		} )
		{
			McpPermissionEvaluation eval = perms.Evaluate( rpcTool );
			Assert.False( eval.Allowed, $"Expected RPC tool '{rpcTool}' to be blocked in local-only mode." );
			Assert.True( eval.IsRemoteRpc, $"Expected '{rpcTool}' to be flagged IsRemoteRpc=true." );
		}

		// Argument-aware RPC checks
		Assert.False( perms.Evaluate( "directory_get_tree", JsonArgs( new { source = "merged" } ) ).Allowed );
		Assert.True( perms.Evaluate( "directory_get_tree", JsonArgs( new { source = "local" } ) ).Allowed );
		Assert.True( perms.Evaluate( "odoo_model_schema", JsonArgs( new { model = "hp.entry", fetchServerFields = false } ) ).Allowed );
		Assert.False( perms.Evaluate( "odoo_model_schema", JsonArgs( new { model = "hp.entry", fetchServerFields = true } ) ).Allowed );
		Assert.True( perms.Evaluate( "ui_search_control", JsonArgs( new { open = true } ) ).Allowed );
		Assert.False( perms.Evaluate( "ui_search_control", JsonArgs( new { execute = true } ) ).Allowed );
		Assert.False( perms.Evaluate( "ui_dependency_graph_control", JsonArgs( new { loadEntryId = 42 } ) ).Allowed );

		// Local computer operations are allowed without RPC
		foreach( string localTool in new[]
		{
			"client_status", "local_list_files", "local_write_file", "local_delete", "path_convert",
			"fs_list", "fs_stat", "fs_mkdir", "fs_copy", "fs_move", "fs_delete", "fs_import_to_pwa",
			"sw_inspect_file", "sw_scan_dependencies", "sw_set_properties", "sw_set_dependencies",
			"mcp_log_get", "mcp_restore_point_create", "mcp_restore_point_list",
			"ui_get_state", "ui_navigate", "ui_screenshot",
		} )
		{
			McpPermissionEvaluation eval = perms.Evaluate( localTool );
			Assert.True( eval.Allowed, $"Expected local tool '{localTool}' to be allowed in local-only mode: {eval.Reason}" );
			Assert.False( eval.IsRemoteRpc );
		}

		// End-to-end: local_write_file succeeds, odoo_search_read is blocked with isRemoteRpc=true
		string localOkPath = Path.Combine( _pwaDir, "local_ok.txt" );
		McpToolResult writeRes = await bundle.Server.CallToolAsync(
			"local_write_file",
			JsonArgs( new { path = localOkPath, content = "local works" } ) );
		Assert.True( writeRes.IsError != true, writeRes.Content[ 0 ].Text );
		Assert.True( File.Exists( localOkPath ) );

		McpToolResult rpcRes = await bundle.Server.CallToolAsync(
			"odoo_search_read",
			JsonArgs( new { model = "hp.entry" } ) );
		Assert.True( rpcRes.IsError );
		using JsonDocument rpcDoc = JsonDocument.Parse( rpcRes.Content[ 0 ].Text! );
		Assert.Contains( "Local-Only mode", rpcDoc.RootElement.GetProperty( "error" ).GetString()! );
		Assert.True( rpcDoc.RootElement.GetProperty( "details" ).GetProperty( "isRemoteRpc" ).GetBoolean() );
	}

	[Fact]
	public async Task ReviewCritical_Review_Auto_AndLocalCadPrep_ModesBehaveAsSpecified()
	{
		McpPermissionManager perms = new( _permFile, defaultPresetWhenUnconfigured: "review-critical", persistChanges: true );
		using var bundle = HackPdmMcpServerFactory.Create(
			McpServerMode.Headless,
			dataDirOverride: _tempDir,
			sharedPermissions: perms );

		// 1. review-critical: StandardAction runs automatically, CriticalAction requires review
		Assert.False( perms.Evaluate( "local_write_file" ).RequiresReview );
		Assert.False( perms.Evaluate( "pdm_get_latest" ).RequiresReview );
		Assert.False( perms.Evaluate( "pdm_checkout" ).RequiresReview );
		Assert.False( perms.Evaluate( "ui_navigate" ).RequiresReview );

		Assert.True( perms.Evaluate( "pdm_commit" ).RequiresReview );
		Assert.True( perms.Evaluate( "pdm_permanent_delete" ).RequiresReview );
		Assert.True( perms.Evaluate( "odoo_write" ).RequiresReview );
		Assert.True( perms.Evaluate( "local_delete" ).RequiresReview );
		Assert.True( perms.Evaluate( "sw_set_properties" ).RequiresReview );
		Assert.True( perms.Evaluate( "sw_set_dependencies" ).RequiresReview );
		Assert.True( perms.Evaluate( "mcp_restore_point_restore" ).RequiresReview );

		// In headless mode (no GUI reviewer), critical action returns structured review_required without modifying disk
		string delTarget = Path.Combine( _pwaDir, "keep_me.txt" );
		File.WriteAllText( delTarget, "safe" );
		McpToolResult headlessCrit = await bundle.Server.CallToolAsync(
			"local_delete",
			JsonArgs( new { filePaths = new[] { delTarget }, confirm = true } ) );
		Assert.True( headlessCrit.IsError );
		Assert.Contains( "review_required", headlessCrit.Content[ 0 ].Text! );
		Assert.True( File.Exists( delTarget ) );

		// 2. review: ReadOnly runs freely, BOTH StandardAction and CriticalAction require review
		perms.SelectProfile( "review" );
		Assert.False( perms.Evaluate( "local_list_files" ).RequiresReview );
		Assert.True( perms.Evaluate( "local_write_file" ).RequiresReview );
		Assert.True( perms.Evaluate( "pdm_get_latest" ).RequiresReview );
		Assert.True( perms.Evaluate( "pdm_commit" ).RequiresReview );

		// 3. local-cad-prep: Server push/delete/settings strictly blocked, local write auto, SW edit reviewed
		perms.SelectProfile( "local-cad-prep" );
		Assert.False( perms.Evaluate( "pdm_commit" ).Allowed );
		Assert.False( perms.Evaluate( "pdm_permanent_delete" ).Allowed );
		Assert.False( perms.Evaluate( "odoo_write" ).Allowed );
		Assert.False( perms.Evaluate( "settings_set" ).Allowed );
		Assert.True( perms.Evaluate( "entry_get_details" ).Allowed );
		Assert.False( perms.Evaluate( "local_write_file" ).RequiresReview );
		Assert.True( perms.Evaluate( "sw_set_properties" ).Allowed );
		Assert.True( perms.Evaluate( "sw_set_properties" ).RequiresReview );

		// 4. auto: executes mutating and critical actions immediately with no review
		perms.SelectProfile( "auto" );
		Assert.False( perms.Evaluate( "pdm_commit" ).RequiresReview );
		Assert.False( perms.Evaluate( "local_delete" ).RequiresReview );
		McpToolResult autoDel = await bundle.Server.CallToolAsync(
			"local_delete",
			JsonArgs( new { filePaths = new[] { delTarget }, confirm = true } ) );
		Assert.True( autoDel.IsError != true, autoDel.Content[ 0 ].Text );
		Assert.False( File.Exists( delTarget ) );
	}

	[Fact]
	public async Task InteractiveReviewQueue_SupportsDeny_ApproveOnce_ApproveSession_AndClear()
	{
		McpPermissionManager perms = new( _permFile, defaultPresetWhenUnconfigured: "review-critical", persistChanges: true )
		{
			HasInteractiveReviewer = true,
		};
		using var bundle = HackPdmMcpServerFactory.Create(
			McpServerMode.Headless,
			dataDirOverride: _tempDir,
			sharedPermissions: perms );

		string fileA = Path.Combine( _pwaDir, "fileA.txt" );
		string fileB = Path.Combine( _pwaDir, "fileB.txt" );
		string fileC = Path.Combine( _pwaDir, "fileC.txt" );
		File.WriteAllText( fileA, "A" );
		File.WriteAllText( fileB, "B" );
		File.WriteAllText( fileC, "C" );

		// 1. Deny -> blocked, file remains on disk
		Task<McpToolResult> denyTask = bundle.Server.CallToolAsync(
			"local_delete",
			JsonArgs( new { filePaths = new[] { fileA }, confirm = true } ) );
		McpPendingReview rev1 = await WaitForSinglePendingReviewAsync( perms );
		Assert.Equal( "local_delete", rev1.ToolName );
		Assert.Equal( McpToolRiskLevel.CriticalAction, rev1.RiskLevel );
		Assert.Contains( "fileA.txt", rev1.ArgsSummary );

		// Anti-self-approval: ui_dialog_respond is blocked while a review is pending
		McpPermissionEvaluation dialogEval = perms.Evaluate( "ui_dialog_respond", JsonArgs( new { response = "Yes" } ) );
		Assert.False( dialogEval.Allowed );

		Assert.True( perms.ResolveReview( rev1.Id, McpReviewDecision.Denied ) );
		McpToolResult denyRes = await denyTask;
		Assert.True( denyRes.IsError );
		Assert.Contains( "review_denied", denyRes.Content[ 0 ].Text! );
		Assert.True( File.Exists( fileA ) );

		// 2. ApproveOnce -> succeeds once, next call still requires review
		Task<McpToolResult> onceTask = bundle.Server.CallToolAsync(
			"local_delete",
			JsonArgs( new { filePaths = new[] { fileA }, confirm = true } ) );
		McpPendingReview rev2 = await WaitForSinglePendingReviewAsync( perms );
		Assert.True( perms.ResolveReview( rev2.Id, McpReviewDecision.ApprovedOnce ) );
		McpToolResult onceRes = await onceTask;
		Assert.True( onceRes.IsError != true, onceRes.Content[ 0 ].Text );
		Assert.False( File.Exists( fileA ) );

		// 3. ApproveForSession -> succeeds AND subsequent calls run immediately without prompting
		Task<McpToolResult> sessTask = bundle.Server.CallToolAsync(
			"local_delete",
			JsonArgs( new { filePaths = new[] { fileB }, confirm = true } ) );
		McpPendingReview rev3 = await WaitForSinglePendingReviewAsync( perms );
		Assert.True( perms.ResolveReview( rev3.Id, McpReviewDecision.ApprovedForSession ) );
		McpToolResult sessRes = await sessTask;
		Assert.True( sessRes.IsError != true, sessRes.Content[ 0 ].Text );
		Assert.False( File.Exists( fileB ) );
		Assert.Contains( "local_delete", perms.SessionApprovedTools );

		// Subsequent call runs immediately with no pending review
		McpToolResult autoFollowup = await bundle.Server.CallToolAsync(
			"local_delete",
			JsonArgs( new { filePaths = new[] { fileC }, confirm = true } ) );
		Assert.True( autoFollowup.IsError != true, autoFollowup.Content[ 0 ].Text );
		Assert.False( File.Exists( fileC ) );
		Assert.Empty( perms.PendingReviews );

		// Clearing session approvals restores review requirement
		perms.ClearSessionApprovals();
		Assert.Empty( perms.SessionApprovedTools );
	}

	[Fact]
	public void CustomProfiles_PersistToJson_ReloadCleanly_AndDeleteSafely()
	{
		McpPermissionManager mgr1 = new( _permFile, defaultPresetWhenUnconfigured: "review-critical", persistChanges: true );
		mgr1.SelectProfile( "local-only" );
		mgr1.SetCategoryEnabled( McpCapabilityCategory.ExternalFilesystemAccess, false );
		mgr1.SetReviewTimeout( 45 );

		McpPermissionProfile saved = mgr1.SaveCustomProfile( "Shop Floor CAD", "Offline local PWA + SolidWorks only", activate: true );
		Assert.Equal( "shop-floor-cad", saved.Key );
		Assert.False( saved.IsBuiltInPreset );
		Assert.Equal( "local-only", saved.Mode );
		Assert.False( saved.IsCategoryEnabled( McpCapabilityCategory.ExternalFilesystemAccess ) );
		Assert.True( File.Exists( _permFile ) );

		// Reload from disk in a brand-new manager instance
		McpPermissionManager mgr2 = new( _permFile, defaultPresetWhenUnconfigured: "auto", persistChanges: true );
		Assert.Equal( "shop-floor-cad", mgr2.ActiveProfile.Key );
		Assert.Equal( McpExecutionMode.LocalOnly, mgr2.ActiveMode );
		Assert.Equal( 45, mgr2.ActiveProfile.ReviewTimeoutSeconds );
		Assert.False( mgr2.ActiveProfile.IsCategoryEnabled( McpCapabilityCategory.ExternalFilesystemAccess ) );
		Assert.True( mgr2.ActiveProfile.IsCategoryEnabled( McpCapabilityCategory.SolidWorksEditing ) );

		// Cannot overwrite or delete built-in preset
		Assert.Throws<InvalidOperationException>( () => mgr2.SaveCustomProfile( "read-only" ) );
		Assert.False( mgr2.DeleteCustomProfile( "read-only" ) );

		// Delete custom profile -> falls back to review-critical
		Assert.True( mgr2.DeleteCustomProfile( "Shop Floor CAD" ) );
		Assert.Equal( "review-critical", mgr2.ActiveProfile.Key );
	}

	[Fact]
	public void AntiSelfEscalation_BlocksUiAutomationTargetingPermissionOrReviewControls_EvenInAutoMode()
	{
		McpPermissionManager perms = new( _permFile, defaultPresetWhenUnconfigured: "auto", persistChanges: true );

		Assert.False( perms.Evaluate( "ui_click_control", JsonArgs( new { name = "ApproveReviewOnceButton" } ) ).Allowed );
		Assert.False( perms.Evaluate( "ui_click_control", JsonArgs( new { text = "Approve Once" } ) ).Allowed );
		Assert.False( perms.Evaluate( "ui_click_control", JsonArgs( new { name = "SavePermissionProfileButton" } ) ).Allowed );
		Assert.False( perms.Evaluate( "ui_set_control_value", JsonArgs( new { name = "PermissionModeCombo", value = "auto" } ) ).Allowed );
		Assert.False( perms.Evaluate( "ui_invoke_command", JsonArgs( new { commandName = "ApplySelectedProfileCommand" } ) ).Allowed );
		Assert.False( perms.Evaluate( "ui_click_control", JsonArgs( new { name = "ToggleMcpBridgeButton" } ) ).Allowed );

		// Normal non-permission UI controls still work normally in auto mode
		Assert.True( perms.Evaluate( "ui_click_control", JsonArgs( new { name = "RefreshMcpButton" } ) ).Allowed );
		Assert.True( perms.Evaluate( "ui_set_control_value", JsonArgs( new { name = "LogFilterBox", value = "sw_" } ) ).Allowed );
	}

	[Fact]
	public async Task CliManagementCommands_AndStdioLocalOnlyEnforcement_WorkEndToEnd()
	{
		string? mcpDll = LocateMcpServerDll();
		Assert.False( string.IsNullOrWhiteSpace( mcpDll ), "HackPDM.McpServer.dll not found — ensure HackPDM.McpServer is built." );

		// 1. --list-presets
		(int code1, string out1, string err1) = await RunCliAsync( mcpDll!, $"--permissions-file \"{_permFile}\" --list-presets" );
		Assert.True( code1 == 0, $"--list-presets failed ({code1}): {err1}" );
		Assert.Contains( "\"local-only\"", out1 );
		Assert.Contains( "\"review-critical\"", out1 );

		// 2. --save-profile "Night Shift" with --permission-mode local-only --deny-category ExternalFilesystemAccess
		(int code2, string out2, string err2) = await RunCliAsync(
			mcpDll!,
			$"--permissions-file \"{_permFile}\" --preset local-only --deny-category ExternalFilesystemAccess --save-profile \"Night Shift\"" );
		Assert.True( code2 == 0, $"--save-profile failed ({code2}): {err2}" );
		Assert.Contains( "\"night-shift\"", out2 );

		// 3. --show-permissions reads saved active profile
		(int code3, string out3, string err3) = await RunCliAsync( mcpDll!, $"--permissions-file \"{_permFile}\" --show-permissions" );
		Assert.True( code3 == 0, $"--show-permissions failed ({code3}): {err3}" );
		Assert.Contains( "\"activeMode\": \"local-only\"", out3 );
		Assert.Contains( "\"night-shift\"", out3 );

		// 4. --set-active-profile read-only and --delete-profile "Night Shift"
		(int code4, _, string err4) = await RunCliAsync( mcpDll!, $"--permissions-file \"{_permFile}\" --set-active-profile read-only" );
		Assert.True( code4 == 0, $"--set-active-profile failed ({code4}): {err4}" );

		(int code5, string out5, string err5) = await RunCliAsync( mcpDll!, $"--permissions-file \"{_permFile}\" --delete-profile \"Night Shift\"" );
		Assert.True( code5 == 0, $"--delete-profile failed ({code5}): {err5}" );
		Assert.Contains( "\"deleted\": true", out5 );

		// 5. Stdio MCP server with --mode headless --permission-mode local-only blocks RPC tool & allows local tool
		string stdioScript =
			"{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\",\"capabilities\":{},\"clientInfo\":{\"name\":\"test\",\"version\":\"1.0\"}}}\n" +
			"{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"mcp_permissions_get\",\"arguments\":{}}}\n" +
			"{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"odoo_search_read\",\"arguments\":{\"model\":\"hp.entry\"}}}\n" +
			"{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"tools/call\",\"params\":{\"name\":\"local_list_files\",\"arguments\":{}}}\n";

		(int code6, string out6, string err6) = await RunCliAsync(
			mcpDll!,
			$"--stdio --mode headless --data-dir \"{_tempDir}\" --permissions-file \"{_permFile}\" --preset local-only",
			stdinText: stdioScript );

		Assert.True( code6 == 0, $"stdio run failed ({code6}): {err6}" );
		string[] lines = out6.Split( '\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries );
		Assert.Equal( 4, lines.Length );
		Assert.Contains( "local-only", lines[ 1 ] );
		Assert.Contains( "permission_denied", lines[ 2 ] );
		Assert.Contains( "isRemoteRpc", lines[ 2 ] );
		Assert.DoesNotContain( "\"isError\":true", lines[ 3 ] );
	}

	private static async Task<McpPendingReview> WaitForSinglePendingReviewAsync( McpPermissionManager perms )
	{
		for( int i = 0; i < 100; i++ )
		{
			var list = perms.PendingReviews;
			if( list.Count > 0 )
				return list[ 0 ];
			await Task.Delay( 20 );
		}
		throw new TimeoutException( "Timed out waiting for pending review to appear." );
	}

	private static JsonElement JsonArgs( object obj )
	{
		string json = JsonSerializer.Serialize( obj );
		using JsonDocument doc = JsonDocument.Parse( json );
		return doc.RootElement.Clone();
	}

	private static string? LocateMcpServerDll()
	{
		for( DirectoryInfo? dir = new( AppContext.BaseDirectory ); dir is not null; dir = dir.Parent )
		{
			string dbg = Path.Combine( dir.FullName, "Src", "HackPDM.McpServer", "bin", "Debug", "net10.0", "HackPDM.McpServer.dll" );
			if( File.Exists( dbg ) )
				return dbg;
			string rel = Path.Combine( dir.FullName, "Src", "HackPDM.McpServer", "bin", "Release", "net10.0", "HackPDM.McpServer.dll" );
			if( File.Exists( rel ) )
				return rel;
		}
		return null;
	}

	private static async Task<(int ExitCode, string Stdout, string Stderr)> RunCliAsync( string dllPath, string args, string? stdinText = null )
	{
		ProcessStartInfo psi = new()
		{
			FileName = "dotnet",
			Arguments = $"\"{dllPath}\" {args}",
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true,
		};

		using Process proc = Process.Start( psi )!;
		if( stdinText is not null )
		{
			await proc.StandardInput.WriteAsync( stdinText );
			await proc.StandardInput.FlushAsync();
		}
		proc.StandardInput.Close();

		Task<string> outTask = proc.StandardOutput.ReadToEndAsync();
		Task<string> errTask = proc.StandardError.ReadToEndAsync();
		await proc.WaitForExitAsync();
		return ( proc.ExitCode, await outTask, await errTask );
	}
}

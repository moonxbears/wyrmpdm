using System.Text.Json;

using HackPDM.Core;
using HackPDM.Core.Configuration;
using HackPDM.Infrastructure.SldWrks;
using HackPDM.Mcp;
using HackPDM.Mcp.Bridge;
using HackPDM.Mcp.Protocol;
using HackPDM.Tests.Characterization;
using HackPDM.Tests.Core;

namespace HackPDM.Tests.Mcp;

[Collection( SharedStateCollection.Name )]
public sealed class McpLogRestoreAndSwEditTests
{
	private static async Task<JsonElement> CallToolAsync( McpServer server, string toolName, object? arguments = null )
	{
		string req = JsonSerializer.Serialize( new
		{
			jsonrpc = "2.0",
			id = 1,
			method = "tools/call",
			@params = new { name = toolName, arguments = arguments ?? new { } },
		} );
		string? raw = await server.HandleMessageAsync( req );
		Assert.NotNull( raw );
		using JsonDocument doc = JsonDocument.Parse( raw! );
		return doc.RootElement.GetProperty( "result" ).Clone();
	}

	private static JsonElement ParseOk( JsonElement toolResult )
	{
		Assert.False( toolResult.TryGetProperty( "isError", out JsonElement isErr ) && isErr.GetBoolean(),
			$"Expected tool success, got error: {toolResult}" );
		string text = toolResult.GetProperty( "content" )[ 0 ].GetProperty( "text" ).GetString()!;
		using JsonDocument doc = JsonDocument.Parse( text );
		return doc.RootElement.Clone();
	}

	[Fact]
	public async Task ActivityLog_RecordsToolCalls_RedactsSecrets_PersistsToDisk_AndLinksRestorePoints()
	{
		using PwaScope pwa = new( create: true );
		string dataDir = Path.Combine( Path.GetDirectoryName( pwa.Absolute )!, "mcp-data" );
		string settingsPath = Path.Combine( Path.GetDirectoryName( pwa.Absolute )!, "settings.json" );
		CoreSettings coreSettings = new( new JsonFileSettingsProvider( settingsPath ) );

		using var bundle = HackPdmMcpServerFactory.Create(
			mode: McpServerMode.Headless,
			settingsStore: coreSettings,
			dataDirOverride: dataDir );

		// Call with secret password argument -> must be redacted in activity log!
		ParseOk( await CallToolAsync( bundle.Server, "session_configure", new
		{
			address = "127.0.0.1",
			port = "8069",
			user = "engineer",
			password = "SuperSecretPassword123!",
			pwaPath = pwa.Absolute,
		} ) );

		// Call mutating file write -> must create an automatic restore point and link its ID in the log!
		JsonElement writeRes = ParseOk( await CallToolAsync( bundle.Server, "local_write_file", new
		{
			path = "root/spec.txt",
			content = "v1",
		} ) );
		string rpId = writeRes.GetProperty( "restorePointId" ).GetString()!;
		Assert.False( string.IsNullOrWhiteSpace( rpId ) );

		// Inspect log via tool
		JsonElement logRes = ParseOk( await CallToolAsync( bundle.Server, "mcp_log_get", new { limit = 20 } ) );
		string rawLog = logRes.GetRawText();
		Assert.DoesNotContain( "SuperSecretPassword123!", rawLog );
		Assert.Contains( "***", rawLog );
		Assert.Contains( rpId, rawLog );
		Assert.True( File.Exists( bundle.ActivityLog.LogFilePath ) );

		// Verify persistence across a fresh McpActivityLog instance
		var reloaded = new HackPDM.Mcp.State.McpActivityLog( dataDir );
		Assert.True( reloaded.Count >= 2 );
		Assert.Contains( reloaded.GetEntries( 20 ), e => e.ToolName == "local_write_file" && e.RestorePointId == rpId );

		// Clear log
		ParseOk( await CallToolAsync( bundle.Server, "mcp_log_clear" ) );
		Assert.Equal( 0, bundle.ActivityLog.Count );
	}

	[Fact]
	public async Task RestorePoints_ManualAndAutomatic_RestoreOverwrittenDeletedAndCreatedFiles()
	{
		using PwaScope pwa = new( create: true );
		string baseDir = Path.GetDirectoryName( pwa.Absolute )!;
		string dataDir = Path.Combine( baseDir, "mcp-data" );
		string externalDir = Path.Combine( baseDir, "external" );
		Directory.CreateDirectory( externalDir );

		string fileA = Path.Combine( externalDir, "docA.txt" );
		string fileB = Path.Combine( externalDir, "docB.txt" );
		await File.WriteAllTextAsync( fileA, "Original Content A" );

		string settingsPath = Path.Combine( baseDir, "settings.json" );
		CoreSettings coreSettings = new( new JsonFileSettingsProvider( settingsPath ) );

		using var bundle = HackPdmMcpServerFactory.Create(
			mode: McpServerMode.Headless,
			settingsStore: coreSettings,
			dataDirOverride: dataDir );

		// 1. Create manual checkpoint tracking existing fileA and not-yet-existing fileB
		JsonElement cp = ParseOk( await CallToolAsync( bundle.Server, "mcp_restore_point_create", new
		{
			label = "Baseline external docs",
			paths = new[] { fileA, fileB },
		} ) );
		string cpId = cp.GetProperty( "restorePoint" ).GetProperty( "id" ).GetString()!;

		// 2. Copy fileA -> fileB, then overwrite fileA, then delete fileA
		ParseOk( await CallToolAsync( bundle.Server, "fs_copy", new { sourcePath = fileA, destinationPath = fileB } ) );
		Assert.True( File.Exists( fileB ) );
		await File.WriteAllTextAsync( fileA, "Modified Content A" );
		ParseOk( await CallToolAsync( bundle.Server, "fs_delete", new { paths = new[] { fileA }, confirm = true } ) );
		Assert.False( File.Exists( fileA ) );

		// 3. Dry run restore -> does NOT modify disk
		JsonElement dry = ParseOk( await CallToolAsync( bundle.Server, "mcp_restore_point_restore", new { id = cpId, dryRun = true } ) );
		Assert.True( dry.GetProperty( "dryRun" ).GetBoolean() );
		Assert.False( File.Exists( fileA ) );
		Assert.True( File.Exists( fileB ) );

		// 4. Execute restore -> restores fileA verbatim AND deletes fileB which didn't exist before!
		JsonElement restored = ParseOk( await CallToolAsync( bundle.Server, "mcp_restore_point_restore", new { id = cpId, createSafetyBackup = true } ) );
		Assert.True( restored.GetProperty( "ok" ).GetBoolean() );
		Assert.Equal( 1, restored.GetProperty( "restoredFiles" ).GetInt32() );
		Assert.Equal( 1, restored.GetProperty( "removedCreatedFiles" ).GetInt32() );
		Assert.True( File.Exists( fileA ) );
		Assert.Equal( "Original Content A", await File.ReadAllTextAsync( fileA ) );
		Assert.False( File.Exists( fileB ) );

		// 5. Undo the restore using the automatic safety backup!
		string safetyId = restored.GetProperty( "safetyBackupRestorePointId" ).GetString()!;
		Assert.False( string.IsNullOrWhiteSpace( safetyId ) );
		ParseOk( await CallToolAsync( bundle.Server, "mcp_restore_point_restore", new { id = safetyId, createSafetyBackup = false } ) );
		Assert.False( File.Exists( fileA ) );
		Assert.True( File.Exists( fileB ) );

		// 6. Verify list & inspect & delete restore points
		JsonElement listed = ParseOk( await CallToolAsync( bundle.Server, "mcp_restore_point_list" ) );
		Assert.True( listed.GetProperty( "count" ).GetInt32() >= 3 );
		JsonElement inspected = ParseOk( await CallToolAsync( bundle.Server, "mcp_restore_point_inspect", new { id = cpId } ) );
		Assert.Equal( cpId, inspected.GetProperty( "id" ).GetString() );
		JsonElement deletedRp = ParseOk( await CallToolAsync( bundle.Server, "mcp_restore_point_delete", new { id = cpId } ) );
		Assert.True( deletedRp.GetProperty( "deleted" ).GetBoolean() );
	}

	[Fact]
	public async Task SolidWorksModern_SetProperties_And_RewriteDependencies_WithRestorePointRollback()
	{
		Assert.True( SwSamples.HasKitBot, "KitBot Modern SW sample fixtures should be auto-provisioned by SwSamples." );

		using PwaScope pwa = new( create: true );
		string baseDir = Path.GetDirectoryName( pwa.Absolute )!;
		string dataDir = Path.Combine( baseDir, "mcp-data" );
		string workDir = Path.Combine( baseDir, "sw-modern" );
		Directory.CreateDirectory( workDir );

		string asmSrc = SwSamples.KitBot( "0_Kitbot2026withBumpers.SLDASM" );
		string asmCopy = Path.Combine( workDir, "0_Kitbot2026withBumpers.SLDASM" );
		File.Copy( asmSrc, asmCopy );
		string originalSha1 = Checksums.OfFile( asmCopy )!;

		using var bundle = HackPdmMcpServerFactory.Create( McpServerMode.Headless, dataDirOverride: dataDir );

		// 1. Edit custom properties on Modern 2015+ SolidWorks file
		JsonElement propRes = ParseOk( await CallToolAsync( bundle.Server, "sw_set_properties", new
		{
			path = asmCopy,
			properties = new object[]
			{
				new { configuration = "", name = "Description", type = "text", value = "Updated KitBot via MCP" },
				new { configuration = "", name = "Revision", type = "text", value = "B" },
				new { configuration = "", name = "UnitCost", type = "number", value = 149.95 },
				new { configuration = "", name = "Approved", type = "yesno", value = true },
				new { configuration = "", name = "Source", remove = true },
			},
		} ) );
		string propRpId = propRes.GetProperty( "restorePointId" ).GetString()!;
		Assert.Equal( "Modern", propRes.GetProperty( "containerFormat" ).GetString() );

		// Verify with real SwFileReader
		SwFileReader reader = new();
		var readProps = reader.GetProperties( asmCopy )!.Where( p => p.Item1 == "" ).ToDictionary( p => p.Item2, p => p );
		Assert.Equal( "Updated KitBot via MCP", readProps[ "Description" ].Item4 );
		Assert.Equal( "B", readProps[ "Revision" ].Item4 );
		Assert.Equal( 149.95m, (decimal)readProps[ "UnitCost" ].Item4 );
		Assert.Equal( true, readProps[ "Approved" ].Item4 );
		Assert.False( readProps.ContainsKey( "Source" ) );

		// 2. Rewrite dependency paths on Modern 2015+ SolidWorks assembly
		JsonElement depRes = ParseOk( await CallToolAsync( bundle.Server, "sw_set_dependencies", new
		{
			path = asmCopy,
			oldPrefix = @"C:\KitBot",
			newPrefix = @"D:\HackPDM_Vault\Robotics\FRC2026",
		} ) );
		Assert.True( depRes.GetProperty( "rewrittenCount" ).GetInt32() >= 1 );

		var verifiedDeps = reader.GetDependencies( asmCopy )!;
		Assert.Contains( verifiedDeps, d => d[ 1 ] == @"D:\HackPDM_Vault\Robotics\FRC2026\Kitbot2026withBumpers(2).SLDASM" );
		Assert.NotNull( reader.GetPreviewPng( asmCopy ) ); // Untouched PreviewPNG chunk stays intact!

		// 3. Roll back to pre-property-edit restore point -> exact original SHA-1 restored!
		ParseOk( await CallToolAsync( bundle.Server, "mcp_restore_point_restore", new { id = propRpId, createSafetyBackup = false } ) );
		Assert.Equal( originalSha1, Checksums.OfFile( asmCopy ) );
	}

	[Fact]
	public async Task SolidWorksOle_RealPartAndAssembly_SetProperties_And_RewriteDependencies_WithRollback()
	{
		using PwaScope pwa = new( create: true );
		string baseDir = Path.GetDirectoryName( pwa.Absolute )!;
		string dataDir = Path.Combine( baseDir, "mcp-data" );
		string workDir = Path.Combine( baseDir, "sw-ole" );
		Directory.CreateDirectory( workDir );

		string prtCopy = Path.Combine( workDir, "old.SLDPRT" );
		string asmCopy = Path.Combine( workDir, "old.SLDASM" );
		File.Copy( SwSamples.Old( "old.SLDPRT" ), prtCopy );
		File.Copy( SwSamples.Old( "old.SLDASM" ), asmCopy );
		string origAsmSha1 = Checksums.OfFile( asmCopy )!;

		using var bundle = HackPdmMcpServerFactory.Create( McpServerMode.Headless, dataDirOverride: dataDir );
		SwFileReader reader = new();

		// 1. Edit BOTH file-level AND configuration-level ("X000545") custom properties on real 44MB OLE .SLDPRT (with DIFAT!)
		JsonElement propRes = ParseOk( await CallToolAsync( bundle.Server, "sw_set_properties", new
		{
			path = prtCopy,
			properties = new object[]
			{
				new { configuration = "", name = "Description", type = "text", value = "Engine QSB6.7 - MCP Updated" },
				new { configuration = "", name = "McpTag", type = "text", value = "CrossPlatformOLE" },
				new { configuration = "", name = "WeightKg", type = "number", value = 485.5 },
				new { configuration = "", name = "Released", type = "yesno", value = true },
				new { configuration = "X000545", name = "PartNum", type = "text", value = "X000545-REV2" },
			},
		} ) );
		Assert.Equal( "Ole", propRes.GetProperty( "containerFormat" ).GetString() );

		var props = reader.GetProperties( prtCopy )!;
		Assert.Contains( props, p => p.Item1 == "" && p.Item2 == "Description" && (string)p.Item4 == "Engine QSB6.7 - MCP Updated" );
		Assert.Contains( props, p => p.Item1 == "" && p.Item2 == "McpTag" && (string)p.Item4 == "CrossPlatformOLE" );
		Assert.Contains( props, p => p.Item1 == "" && p.Item2 == "WeightKg" && (decimal)p.Item4 == 485.5m );
		Assert.Contains( props, p => p.Item1 == "" && p.Item2 == "Released" && (bool)p.Item4 );
		Assert.Contains( props, p => p.Item1 == "X000545" && p.Item2 == "PartNum" && (string)p.Item4 == "X000545-REV2" );
		// Untouched sibling configurations & preview stay intact!
		Assert.Contains( props, p => p.Item1 == "Mining Edition" );
		Assert.Contains( props, p => p.Item1 == "X002623" );
		Assert.NotNull( reader.GetPreviewPng( prtCopy ) );

		// 2. Rewrite dependency path inside real 9MB OLE .SLDASM (longer path -> tests MFC length prefix + ZLB repack + OLE sectors!)
		JsonElement depRes = ParseOk( await CallToolAsync( bundle.Server, "sw_set_dependencies", new
		{
			path = asmCopy,
			replacements = new object[]
			{
				new { oldPath = @"C:\temp\new.SLDPRT", newPath = @"C:\HackPDM\PWA\Projects\Engines\new.SLDPRT" },
			},
		} ) );
		string asmRpId = depRes.GetProperty( "restorePointId" ).GetString()!;
		Assert.Equal( "Ole", depRes.GetProperty( "containerFormat" ).GetString() );
		Assert.Equal( 1, depRes.GetProperty( "rewrittenCount" ).GetInt32() );

		var depsAfter = reader.GetDependencies( asmCopy )!;
		Assert.Equal( [ [ "new.SLDPRT", @"C:\HackPDM\PWA\Projects\Engines\new.SLDPRT" ] ], depsAfter );
		Assert.NotNull( reader.GetPreviewPng( asmCopy ) );

		// 3. Roll back OLE assembly via restore point -> exact original SHA-1 & C:\temp\new.SLDPRT restored!
		ParseOk( await CallToolAsync( bundle.Server, "mcp_restore_point_restore", new { id = asmRpId, createSafetyBackup = false } ) );
		Assert.Equal( origAsmSha1, Checksums.OfFile( asmCopy ) );
		Assert.Equal( [ [ "new.SLDPRT", @"C:\temp\new.SLDPRT" ] ], reader.GetDependencies( asmCopy )! );
	}

	[Fact]
	public async Task FsImportToPwa_MovesExternalAssemblyAndDependenciesIntoVault_RepathsAllReferences_AndCanRollback()
	{
		Assert.True( SwSamples.HasKitBot );

		using PwaScope pwa = new( create: true );
		string baseDir = Path.GetDirectoryName( pwa.Absolute )!;
		string dataDir = Path.Combine( baseDir, "mcp-data" );
		string externalStaging = Path.Combine( baseDir, "Downloads_External" );
		Directory.CreateDirectory( externalStaging );

		// Place a 3-level CAD assembly tree OUTSIDE the PWA vault
		string[] filesToStage =
		[
			"0_Kitbot2026withBumpers.SLDASM",
			"Kitbot2026withBumpers(2).SLDASM",
			"KB-26014_ Battery Access Panel_1.SLDPRT",
		];
		foreach( string f in filesToStage )
			File.Copy( SwSamples.KitBot( f ), Path.Combine( externalStaging, f ) );

		string topExternal = Path.Combine( externalStaging, "0_Kitbot2026withBumpers.SLDASM" );

		using var bundle = HackPdmMcpServerFactory.Create( McpServerMode.Headless, dataDirOverride: dataDir );

		// Confirm external file is outside PWA (cannot be committed yet!)
		JsonElement statBefore = ParseOk( await CallToolAsync( bundle.Server, "fs_stat", new { path = topExternal } ) );
		Assert.False( statBefore.GetProperty( "isInsidePwa" ).GetBoolean() );
		Assert.False( statBefore.GetProperty( "canCommitToPdm" ).GetBoolean() );

		// Import ONLY the top assembly with includeDependencies=true, mode="move", rewriteDependencies=true!
		JsonElement importRes = ParseOk( await CallToolAsync( bundle.Server, "fs_import_to_pwa", new
		{
			sourcePaths = new[] { topExternal },
			targetPwaFolder = "root/Projects/FRC2026",
			mode = "move",
			includeDependencies = true,
			rewriteDependencies = true,
			solidWorksPathPrefix = @"C:\PWA\Projects\FRC2026",
		} ) );

		Assert.True( importRes.GetProperty( "ok" ).GetBoolean() );
		Assert.True( importRes.GetProperty( "readyToCommit" ).GetBoolean() );
		Assert.Equal( 3, importRes.GetProperty( "importedCount" ).GetInt32() );
		string importRpId = importRes.GetProperty( "restorePointId" ).GetString()!;

		// All 3 files moved into PWA and removed from external staging
		string pwaTargetDir = Path.Combine( pwa.Absolute, "Projects", "FRC2026" );
		foreach( string f in filesToStage )
		{
			Assert.True( File.Exists( Path.Combine( pwaTargetDir, f ) ), $"Expected imported file {f}" );
			Assert.False( File.Exists( Path.Combine( externalStaging, f ) ), $"Expected source file {f} moved out" );
		}

		// Verify internal dependencies in BOTH assemblies now point to C:\PWA\Projects\FRC2026\...
		SwFileReader reader = new();
		var topDeps = reader.GetDependencies( Path.Combine( pwaTargetDir, "0_Kitbot2026withBumpers.SLDASM" ) )!;
		Assert.Contains( topDeps, d => d[ 1 ] == @"C:\PWA\Projects\FRC2026\Kitbot2026withBumpers(2).SLDASM" );
		var subDeps = reader.GetDependencies( Path.Combine( pwaTargetDir, "Kitbot2026withBumpers(2).SLDASM" ) )!;
		Assert.Contains( subDeps, d => d[ 1 ] == @"C:\PWA\Projects\FRC2026\KB-26014_ Battery Access Panel_1.SLDPRT" );

		// One-click rollback via Restore Point puts ALL 3 files back in externalStaging and cleans up PWA!
		JsonElement rollback = ParseOk( await CallToolAsync( bundle.Server, "mcp_restore_point_restore", new { id = importRpId, createSafetyBackup = false } ) );
		Assert.True( rollback.GetProperty( "ok" ).GetBoolean() );
		foreach( string f in filesToStage )
		{
			Assert.True( File.Exists( Path.Combine( externalStaging, f ) ), $"Expected {f} restored back to external staging" );
			Assert.False( File.Exists( Path.Combine( pwaTargetDir, f ) ), $"Expected {f} removed from PWA on rollback" );
		}
	}
}

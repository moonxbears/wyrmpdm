using System.Text;

using HackPDM.Core;
using HackPDM.Core.Hack;
using HackPDM.Infrastructure.Hack;
using HackPDM.Infrastructure.Odoo;
using HackPDM.Infrastructure.Odoo.Models;
using HackPDM.Infrastructure.Operations;
using HackPDM.Infrastructure.SldWrks;
using HackPDM.Shared.GlobalData;

namespace HackPDM.Mcp.Services;

public sealed partial class HackPdmApiService
{
	private static readonly string[] HeavyEntryFields = [ "type_id", "cat_id" ];
	private static readonly string[] DeleteExcludedFields = [ "type_id", "cat_id", "checkout_node" ];
	private static readonly string[] LatestVersionExcludedFields = [ "preview_image", "entry_id", "node_id", "file_modify_stamp", "attachment_id", "file_contents" ];

	// ------------------------------------------------------------------ PDM Workflows

	public async Task<object> GetLatestAsync(
		IReadOnlyList<int>? entryIds,
		int? directoryId,
		bool withSubdirectories,
		bool withDeleted,
		CancellationToken cancellationToken )
	{
		EnsureLoggedIn();
		McpOperationLog log = new( UiBridge );

		IReadOnlyList<int> targetIds = await ResolveTargetEntryIdsAsync( entryIds, directoryId, withSubdirectories, withDeleted, log );
		if( targetIds.Count == 0 )
		{
			return new { ok = true, requestedEntries = 0, downloaded = 0, skipped = 0, logs = log.Snapshot() };
		}

		log.Add( StatusMessage.INFO, $"Expanding dependencies for {targetIds.Count} entry ID(s)..." );
		IReadOnlyList<int> expandedIds = await ExpandEntriesWithDependenciesAsync( targetIds, log );
		HpVersion[]? versions = await PdmDownloadOperations.GetLatestVersionsAsync( expandedIds, LatestVersionExcludedFields );

		if( versions is not { Length: > 0 } )
		{
			return new
			{
				ok = true,
				requestedEntries = targetIds.Count,
				expandedEntries = expandedIds.Count,
				downloaded = 0,
				skipped = 0,
				message = "No versions to process.",
				logs = log.Snapshot(),
			};
		}

		List<List<HpVersion>> batches = PdmDownloadOperations.BatchBySize( versions, OdooDefaults.Instance.DownloadBatchSize );
		PdmDownloadOperations.DownloadSummary summary = await PdmDownloadOperations.ProcessDownloadsAsync(
			batches,
			cancellationToken,
			OdooDefaults.Instance.ConcurrencySize,
			log: log.Add,
			onBytesDownloaded: log.AddDownloadedBytes );

		UiBridge?.NotifyServerMutated( treeChanged: false, entriesChanged: true );

		string[] failed = [ .. summary.Failed ];
		string[] kept = [ .. summary.KeptLocalChanges ];
		return new
		{
			ok = failed.Length == 0,
			requestedEntries = targetIds.Count,
			expandedEntries = expandedIds.Count,
			versionCount = versions.Length,
			downloaded = summary.Downloaded,
			skipped = summary.Skipped,
			downloadedBytes = log.DownloadedBytes,
			keptLocalChanges = kept,
			failed,
			logs = log.Snapshot(),
		};
	}

	public async Task<object> CheckoutAsync(
		IReadOnlyList<int>? entryIds,
		int? directoryId,
		bool withSubdirectories,
		bool getLatestFirst,
		CancellationToken cancellationToken )
	{
		EnsureLoggedIn();
		McpOperationLog log = new( UiBridge );

		IReadOnlyList<int> targetIds = await ResolveTargetEntryIdsAsync( entryIds, directoryId, withSubdirectories, withDeleted: false, log );
		if( targetIds.Count == 0 )
			return new { ok = true, requestedEntries = 0, checkedOut = 0, logs = log.Snapshot() };

		object? downloadResult = null;
		if( getLatestFirst )
			downloadResult = await GetLatestAsync( targetIds, null, false, false, cancellationToken );

		IReadOnlyList<int> expandedIds = await ExpandEntriesWithDependenciesAsync( targetIds, log );
		HpEntry[] entries = await HpEntry.GetRecordsByIdsAsync( expandedIds, excludedFields: HeavyEntryFields ) ?? [];
		await PdmCheckoutOperations.CheckOutEntriesAsync( entries, log.Add );

		UiBridge?.NotifyServerMutated( treeChanged: false, entriesChanged: true );

		int checkedOutCount = entries.Count( e => e.is_checked_out is true && e.checkout_user?.id == OdooDefaults.Instance.OdooId );
		return new
		{
			ok = true,
			requestedEntries = targetIds.Count,
			expandedEntries = expandedIds.Count,
			checkedOutCount,
			download = downloadResult,
			logs = log.Snapshot(),
		};
	}

	public async Task<object> UnCheckoutAsync(
		IReadOnlyList<int>? entryIds,
		int? directoryId,
		bool withSubdirectories )
	{
		EnsureLoggedIn();
		McpOperationLog log = new( UiBridge );

		IReadOnlyList<int> targetIds = await ResolveTargetEntryIdsAsync( entryIds, directoryId, withSubdirectories, withDeleted: false, log );
		if( targetIds.Count == 0 )
			return new { ok = true, requestedEntries = 0, released = 0, logs = log.Snapshot() };

		int me = OdooDefaults.Instance.OdooId;
		IReadOnlyList<int> expandedIds = await ExpandEntriesWithDependenciesAsync( targetIds, log );
		HpEntry[] mine = await HpEntry.GetRecordsByIdsAsync( expandedIds, OdooDomain.Where( "checkout_user", Operators.Equal, me ), excludedFields: HeavyEntryFields ) ?? [];

		HpEntry[] others = await HpEntry.GetRecordsByIdsAsync( targetIds,
			OdooDomain.Where( "checkout_user", Operators.NotEqual, me ).And( "checkout_user", Operators.NotEqual, false ),
			includedFields: [ nameof( HpEntry.id ), nameof( HpEntry.name ), nameof( HpEntry.checkout_user ) ] ) ?? [];
		foreach( HpEntry other in others )
			log.Add( StatusMessage.ERROR, $"Cannot undo the check out of {other.name}: it is checked out to {other.checkout_user?.name ?? "another user"}" );

		if( mine.Length > 0 )
			await PdmCheckoutOperations.UnCheckOutEntriesAsync( mine, me, log.Add );

		UiBridge?.NotifyServerMutated( treeChanged: false, entriesChanged: true );

		return new
		{
			ok = others.Length == 0,
			requestedEntries = targetIds.Count,
			expandedEntries = expandedIds.Count,
			releasedCount = mine.Count( e => e.is_checked_out is false ),
			heldByOtherUsers = others.Select( o => new { id = o.id, name = o.name, checkoutUser = OdooJsonHelper.SerializeMany2One( o.checkout_user ) } ).ToList(),
			logs = log.Snapshot(),
		};
	}

	public async Task<object> PreviewCommitAsync( IReadOnlyList<string>? filePaths, string? folderPath )
	{
		EnsureLoggedIn();
		McpOperationLog log = new( UiBridge );
		var (collected, broken) = await CollectCommitCandidatesAsync( filePaths, folderPath, log );

		var (localOnly, remoteExisting, unchanged) = await PdmCommitOperations.FilterCommitHackFilesAsync( collected, log.Add );
		return new
		{
			collectedCount = collected.Count,
			localOnly = localOnly.Select( DescribeHackFile ).ToList(),
			remoteExisting = remoteExisting.Select( DescribeHackFile ).ToList(),
			unchangedCheckedOut = unchanged.Select( e => new { id = e.id, name = e.name, windowsCompleteName = e.windows_complete_name } ).ToList(),
			brokenDependencies = broken,
			logs = log.Snapshot(),
		};
	}

	public async Task<object> CommitAsync( IReadOnlyList<string>? filePaths, string? folderPath )
	{
		EnsureLoggedIn();
		McpOperationLog log = new( UiBridge );
		var (collected, broken) = await CollectCommitCandidatesAsync( filePaths, folderPath, log );

		if( collected.Count == 0 )
		{
			return new
			{
				ok = broken.Count == 0,
				committed = false,
				message = "No valid local files to commit.",
				brokenDependencies = broken,
				logs = log.Snapshot(),
			};
		}

		var (localOnly, remoteExisting, unchanged) = await PdmCommitOperations.FilterCommitHackFilesAsync( collected, log.Add );
		if( localOnly.Length == 0 && remoteExisting.Length == 0 )
		{
			if( unchanged.Length > 0 )
			{
				log.Add( StatusMessage.INFO, $"{unchanged.Length} unchanged file(s): checked in without a new version" );
				await PdmCheckoutOperations.UnCheckOutEntriesAsync( unchanged, OdooDefaults.Instance.OdooId, log.Add );
				UiBridge?.NotifyServerMutated( treeChanged: false, entriesChanged: true );
				return new
				{
					ok = true,
					committed = false,
					checkedInUnchangedCount = unchanged.Length,
					brokenDependencies = broken,
					logs = log.Snapshot(),
				};
			}

			return new
			{
				ok = false,
				committed = false,
				message = "No eligible entries to commit (see logs for skipped/not-checked-out reasons).",
				brokenDependencies = broken,
				logs = log.Snapshot(),
			};
		}

		HpPDMCommit commit = new() { node_by = OdooDefaults.Instance.MyNode?.id };
		int commitId = await commit.CreateCommitAsync();

		int stagedCount = 0;
		var stagedItems = localOnly.Select( f => ( File: f, InOdoo: false ) )
			.Concat( remoteExisting.Select( f => ( File: f, InOdoo: true ) ) );

		foreach( var (file, inOdoo) in stagedItems )
		{
			bool staged = await PdmCommitOperations.CommitRecordAsync(
				file,
				commit,
				inOdoo,
				log.Add,
				onRecordStaged: () => stagedCount++,
				onBytesUploaded: log.AddUploadedBytes );
			if( !staged )
			{
				return new
				{
					ok = false,
					committed = false,
					commitId,
					stagedCount,
					uploadedBytes = log.UploadedBytes,
					error = $"Staging failed on '{file.Name}'. Commit was cleared on the server.",
					brokenDependencies = broken,
					logs = log.Snapshot(),
				};
			}
		}

		var (committed, error) = await commit.ServerCommit();
		if( !committed )
		{
			log.Add( StatusMessage.ERROR, $"Commit failed on the server: {error}" );
			return new
			{
				ok = false,
				committed = false,
				commitId,
				stagedCount,
				uploadedBytes = log.UploadedBytes,
				error = error ?? "Server commit failed.",
				brokenDependencies = broken,
				logs = log.Snapshot(),
			};
		}

		if( unchanged.Length > 0 )
		{
			log.Add( StatusMessage.INFO, $"{unchanged.Length} unchanged file(s): checked in without a new version" );
			await PdmCheckoutOperations.UnCheckOutEntriesAsync( unchanged, OdooDefaults.Instance.OdooId, log.Add );
		}

		UiBridge?.NotifyServerMutated( treeChanged: localOnly.Length > 0, entriesChanged: true );

		return new
		{
			ok = true,
			committed = true,
			commitId,
			newEntriesCount = localOnly.Length,
			newVersionsCount = remoteExisting.Length,
			checkedInUnchangedCount = unchanged.Length,
			stagedCount,
			uploadedBytes = log.UploadedBytes,
			brokenDependencies = broken,
			logs = log.Snapshot(),
		};
	}

	public async Task<object> LogicalDeleteAsync( IReadOnlyList<int>? entryIds, int? directoryId, bool withSubdirectories )
	{
		EnsureLoggedIn();
		McpOperationLog log = new( UiBridge );
		IReadOnlyList<int> targetIds = await ResolveTargetEntryIdsAsync( entryIds, directoryId, withSubdirectories, withDeleted: false, log );
		if( targetIds.Count == 0 )
			return new { ok = true, affectedCount = 0, logs = log.Snapshot() };

		HpEntry[] entries = await HpEntry.GetRecordsByIdsAsync( targetIds, excludedFields: DeleteExcludedFields ) ?? [];
		bool ok = await PdmDeleteOperations.SetDeletedAsync( entries, deleted: true, log.Add );
		UiBridge?.NotifyServerMutated( treeChanged: true, entriesChanged: true );
		return new { ok, affectedCount = entries.Length, entryIds = targetIds, logs = log.Snapshot() };
	}

	public async Task<object> RestoreDeletedAsync( IReadOnlyList<int>? entryIds, int? directoryId, bool withSubdirectories )
	{
		EnsureLoggedIn();
		McpOperationLog log = new( UiBridge );
		HpEntry[]? entries;

		if( entryIds is { Count: > 0 } )
		{
			entries = await HpEntry.GetRecordsByIdsAsync( entryIds, excludedFields: DeleteExcludedFields );
		}
		else if( directoryId is int dirId and > 0 )
		{
			if( withSubdirectories )
			{
				IReadOnlyList<int> allIds = await ResolveTargetEntryIdsAsync( null, dirId, withSubdirectories: true, withDeleted: true, log );
				HpEntry[]? all = allIds.Count > 0 ? await HpEntry.GetRecordsByIdsAsync( allIds, excludedFields: DeleteExcludedFields ) : [];
				entries = all?.Where( e => e.deleted == true ).ToArray();
			}
			else
			{
				entries = await HpEntry.GetRecordsByIdsAsync( null,
					searchFilters: OdooDomain.Where( "deleted", true ).And( "dir_id", dirId ),
					excludedFields: DeleteExcludedFields );
			}
		}
		else
		{
			throw new ArgumentException( "Specify either 'entryIds' or 'directoryId'." );
		}

		if( entries is not { Length: > 0 } )
			return new { ok = true, restoredCount = 0, logs = log.Snapshot() };

		bool ok = await PdmDeleteOperations.SetDeletedAsync( entries, deleted: false, log.Add );
		UiBridge?.NotifyServerMutated( treeChanged: true, entriesChanged: true );
		return new
		{
			ok,
			restoredCount = entries.Length,
			restoredEntryIds = entries.Select( e => e.id ).OfType<int>().ToArray(),
			logs = log.Snapshot(),
		};
	}

	public async Task<object> PermanentDeleteAsync( IReadOnlyList<int> entryIds, CancellationToken cancellationToken )
	{
		EnsureLoggedIn();
		Result<bool> admin = await OdooRpc.ExecuteAsync<bool>( "res.users", "has_group", [ "hackpdm.group_pdm_admin" ], token: cancellationToken );
		if( !admin.ValueOr( false ) )
			throw new InvalidOperationException( "Permanent delete requires PDM Administrator permissions (hackpdm.group_pdm_admin)." );

		McpOperationLog log = new( UiBridge );
		bool deleted = await PdmDeleteOperations.PermanentDeleteEntry( entryIds, log.Add );
		if( deleted )
		{
			log.Add( StatusMessage.SUCCESS, "Completed permanent delete" );
			UiBridge?.NotifyServerMutated( treeChanged: true, entriesChanged: true );
		}
		return new { ok = deleted, deleted, entryIds, logs = log.Snapshot() };
	}

	public async Task<object> PlanVersionDownloadAsync( int versionId )
	{
		EnsureLoggedIn();
		HpVersion? version = ( await HpVersion.GetRecordsByIdsAsync( [ versionId ], excludedFields: HpVersion.UsualExcludedFields ) )?.FirstOrDefault();
		if( version is null )
			throw new InvalidOperationException( $"Version {versionId} not found." );

		HpEntry? entry = version.entry_id?.id is int eid ? await HpEntry.GetRecordByIdAsync( eid ) : null;
		Result<IReadOnlyList<int>> deps = await PdmDownloadOperations.GetVersionListAsync( versionId );
		IReadOnlyList<int> allIds = [ .. deps.ValueOr( [] ).Append( versionId ).Distinct() ];

		string localDir = version.LocalDirectory();
		string localFile = Path.Combine( localDir, version.name ?? "" );
		bool conflict = File.Exists( localFile ) && !Checksums.Matches( localFile, version.checksum );

		return new
		{
			versionId,
			name = version.name,
			entryId = version.entry_id?.id,
			isLatest = entry?.latest_version_id?.id == versionId,
			fileSize = version.file_size,
			checksum = version.checksum,
			localDirectory = localDir,
			localFilePath = localFile,
			hasConflictingLocalCopy = conflict,
			dependencyError = deps.Error,
			allVersionIds = allIds,
		};
	}

	public async Task<object> DownloadOrMoveVersionAsync( int versionId, string action, bool toTemp, bool withDependencies )
	{
		EnsureLoggedIn();
		HpVersion? version = await HpVersion.GetRecordByIdAsync( versionId, HpVersion.UsualExcludedFields );
		if( version is null )
			throw new InvalidOperationException( $"Version {versionId} not found." );

		string workingDir = version.LocalDirectory();
		version.WinPathway = workingDir;

		string act = string.IsNullOrWhiteSpace( action ) ? "download" : action.Trim().ToLowerInvariant();
		if( act is "movetotemp" or "movetoworking" )
		{
			bool moveToTemp = act == "movetotemp";
			string tempRoot = StorageBox.TemporaryPath ?? Path.GetTempPath();
			FileInfo from = moveToTemp
				? new FileInfo( Path.Combine( workingDir, version.name ?? "" ) )
				: new FileInfo( Path.Combine( tempRoot, version.name ?? "" ) );
			FileInfo to = moveToTemp
				? new FileInfo( Path.Combine( tempRoot, version.name ?? "" ) )
				: new FileInfo( Path.Combine( workingDir, version.name ?? "" ) );

			string targetDir = to.DirectoryName ?? tempRoot;
			if( from.Exists )
			{
				Directory.CreateDirectory( targetDir );
				from.MoveTo( to.FullName, overwrite: true );
			}
			else
			{
				await version.DownloadFileAsync( targetDir );
			}

			UiBridge?.NotifyServerMutated( treeChanged: false, entriesChanged: true );
			return new { ok = File.Exists( to.FullName ), action = act, versionId, destination = to.FullName };
		}

		if( withDependencies )
		{
			Result<IReadOnlyList<int>> deps = await PdmDownloadOperations.GetVersionListAsync( versionId );
			IReadOnlyList<int> allIds = [ .. deps.ValueOr( [] ).Append( versionId ).Distinct() ];
			HpVersion[] versions = await HpVersion.GetRecordsByIdsAsync( allIds, excludedFields: HpVersion.UsualExcludedFields ) ?? [];
			List<HpVersion> failed = await versions.DownloadAllAsync();
			UiBridge?.NotifyServerMutated( treeChanged: false, entriesChanged: true );
			return new
			{
				ok = failed.Count == 0,
				versionId,
				withDependencies = true,
				downloadedVersionIds = allIds.Except( failed.Select( f => f.id ?? 0 ) ).ToArray(),
				failedVersionIds = failed.Select( f => f.id ?? 0 ).ToArray(),
			};
		}

		string destFolder;
		if( toTemp )
		{
			string rel = PwaPaths.ToRelative( OdooValues.Many2OneName( version.HashedValues.GetValueOrDefault( "dir_id" ) ) );
			destFolder = Path.Combine( StorageBox.TemporaryPath ?? Path.GetTempPath(), rel );
		}
		else
		{
			destFolder = workingDir;
		}

		bool ok = await version.DownloadFileAsync( destFolder );
		if( !toTemp )
			UiBridge?.NotifyServerMutated( treeChanged: false, entriesChanged: true );
		return new
		{
			ok,
			versionId,
			toTemp,
			destinationFolder = destFolder,
			destinationFile = Path.Combine( destFolder, version.name ?? "" ),
		};
	}

	// ------------------------------------------------------------------ SolidWorks & Local Vault Files

	public async Task<object> InspectSolidWorksFileAsync( string path, bool resolveReferences, CancellationToken cancellationToken )
	{
		string fullPath = ResolveExistingLocalPath( path );
		bool isSw = SwDocumentTypes.IsSolidWorks( fullPath );
		ISwDocumentReader reader = SolidWorksUtil.DocMgr;

		return await Task.Run( () =>
		{
			List<string[]>? rawDeps = isSw ? reader.GetDependencies( fullPath, deep: false, noInterrupt: true ) : null;
			SwReferenceResolver? resolver = resolveReferences ? new SwReferenceResolver( HackDefaults.Instance.PwaPathAbsolute ) : null;

			List<object> references = [];
			foreach( string[] pair in rawDeps ?? [] )
			{
				string refName = pair.Length > 0 ? pair[ 0 ] : "";
				string storedPath = pair.Length > 1 ? pair[ 1 ] : "";
				string? resolved = resolver?.Resolve( storedPath, fullPath );
				references.Add( new
				{
					name = refName,
					storedPath,
					resolvedLocalPath = resolved,
					resolvedExists = resolved is not null && File.Exists( resolved ),
					insideVault = resolved is not null && PwaPaths.IsInside( resolved ),
				} );
			}

			List<object> properties = [];
			if( isSw )
			{
				try
				{
					foreach( var prop in reader.GetProperties( fullPath ) ?? [] )
					{
						properties.Add( new
						{
							configuration = prop.Item1,
							name = prop.Item2,
							type = prop.Item3,
							value = prop.Item4,
						} );
					}
				}
				catch( Exception ex )
				{
					properties.Add( new { error = ex.Message } );
				}
			}

			byte[]? preview = isSw ? reader.GetPreviewPng( fullPath ) : null;

			return (object)new
			{
				localPath = fullPath,
				treePath = PwaPaths.ToServerName( fullPath ),
				isSolidWorks = isSw,
				documentType = Path.GetExtension( fullPath ).ToLowerInvariant() switch
				{
					".sldprt" => "Part",
					".sldasm" => "Assembly",
					".slddrw" => "Drawing",
					_ => "Unknown",
				},
				readerType = reader.GetType().Name,
				fileSize = new FileInfo( fullPath ).Length,
				checksum = Checksums.OfFile( fullPath ),
				hasPreviewPng = preview is { Length: > 0 },
				previewBytes = preview?.Length ?? 0,
				referenceCount = references.Count,
				references,
				propertyCount = properties.Count,
				properties,
			};
		}, cancellationToken );
	}

	public async Task<object> ScanLocalDependenciesAsync( string? filePath, string? folderPath )
	{
		if( !string.IsNullOrWhiteSpace( folderPath ) )
		{
			string fullDir = ResolveLocalDirectoryPath( folderPath );
			if( !Directory.Exists( fullDir ) )
				throw new DirectoryNotFoundException( $"Local directory not found: '{fullDir}'." );
			var (files, broken) = await DependencyScanner.GetHackFolderWithDependenciesAsync( fullDir );
			return new
			{
				folderPath = fullDir,
				success = broken.Count == 0,
				fileCount = files.Count,
				files = files.Select( DescribeHackFile ).ToList(),
				broken = broken.Select( b => new { result = b.Value?.Result.ToString(), path = b.Value?.Hack?.FullPath } ).ToList(),
			};
		}

		if( string.IsNullOrWhiteSpace( filePath ) )
			throw new ArgumentException( "Provide either 'filePath' or 'folderPath'." );

		string fullFile = ResolveExistingLocalPath( filePath );
		HackFile hack = HackFile.GetFromPath( fullFile ) ?? throw new FileNotFoundException( "File not found.", fullFile );
		var (success, depFiles, depBroken) = await hack.GetHackFileWithDependencies();
		return new
		{
			filePath = fullFile,
			success,
			fileCount = depFiles.Count,
			files = depFiles.Select( DescribeHackFile ).ToList(),
			broken = depBroken.Select( b => new { result = b.Value?.Result.ToString(), path = b.Value?.Hack?.FullPath } ).ToList(),
		};
	}

	public object ConvertPath( string path )
	{
		string pwa = HackDefaults.Instance.PwaPathAbsolute;
		bool isLocal = Path.IsPathFullyQualified( path );
		string[]? segments = isLocal ? PwaPaths.LocalSegments( path ) : PwaPaths.Segments( path );
		string? localAbsolute = isLocal ? Path.GetFullPath( path ) : PwaPaths.ToAbsolute( path );
		string? serverName = isLocal ? PwaPaths.ToServerName( path ) : PwaPaths.ToServerName( segments ?? [] );
		string? serverDirectory = isLocal ? PwaPaths.ToServerDirectory( path ) : string.Join( PwaPaths.DirectorySeparator, [ PwaPaths.Root, .. ( segments ?? [] ) ] );
		return new
		{
			input = path,
			vaultRoot = pwa,
			isFullyQualifiedLocal = isLocal,
			isInsideVault = localAbsolute is not null && PwaPaths.IsInside( localAbsolute ),
			segments,
			relativeToVault = segments is null ? null : string.Join( Path.DirectorySeparatorChar, segments ),
			localAbsolute,
			serverName,
			serverDirectory,
			folderOf = serverName is null ? null : PwaPaths.FolderOf( serverName ),
		};
	}

	public object ListLocalFiles( string? treePath, string? localPath, bool recursive, bool computeChecksums )
	{
		string folder = !string.IsNullOrWhiteSpace( localPath )
			? Path.GetFullPath( localPath )
			: PwaPaths.ToAbsolute( treePath ) ?? PwaPaths.Vault;

		if( string.IsNullOrEmpty( folder ) || !Directory.Exists( folder ) )
			return new { folder, exists = false, directories = Array.Empty<object>(), files = Array.Empty<object>() };

		SearchOption opt = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
		DirectoryInfo dir = new( folder );

		var dirs = dir.EnumerateDirectories( "*", opt ).Select( d => new
		{
			name = d.Name,
			fullPath = d.FullName,
			treePath = PwaPaths.ToServerName( d.FullName ),
			lastWriteTime = d.LastWriteTime,
		} ).OrderBy( d => d.fullPath, PathComparer ).ToList();

		var files = dir.EnumerateFiles( "*", opt ).Select( f => new
		{
			name = f.Name,
			extension = f.Extension,
			fullPath = f.FullName,
			treePath = PwaPaths.ToServerName( f.FullName ),
			size = f.Length,
			isReadOnly = f.IsReadOnly,
			lastWriteTime = f.LastWriteTime,
			checksum = computeChecksums ? Checksums.OfFile( f.FullName ) : null,
		} ).OrderBy( f => f.fullPath, PathComparer ).ToList();

		return new
		{
			folder,
			treePath = PwaPaths.ToServerName( folder ),
			exists = true,
			directoryCount = dirs.Count,
			fileCount = files.Count,
			directories = dirs,
			files,
		};
	}

	public async Task<object> ReadLocalFileAsync( string path, string encoding, int maxBytes, bool includeChecksum, CancellationToken cancellationToken )
	{
		string fullPath = ResolveVaultBoundedPath( path );
		if( !File.Exists( fullPath ) )
			throw new FileNotFoundException( $"Local file not found: '{fullPath}'.", fullPath );
		FileInfo info = new( fullPath );
		int limit = Math.Clamp( maxBytes, 1024, 10 * 1024 * 1024 );

		byte[] bytes;
		bool truncated;
		await using( FileStream fs = File.OpenRead( fullPath ) )
		{
			int toRead = (int)Math.Min( info.Length, limit );
			bytes = new byte[ toRead ];
			int read = 0;
			while( read < toRead )
			{
				int n = await fs.ReadAsync( bytes.AsMemory( read, toRead - read ), cancellationToken );
				if( n == 0 )
					break;
				read += n;
			}
			if( read < toRead )
				Array.Resize( ref bytes, read );
			truncated = info.Length > read;
		}

		bool useBase64 = string.Equals( encoding, "base64", StringComparison.OrdinalIgnoreCase )
			|| ( string.Equals( encoding, "auto", StringComparison.OrdinalIgnoreCase ) && LooksBinary( bytes ) );

		return new
		{
			localPath = fullPath,
			treePath = PwaPaths.ToServerName( fullPath ),
			totalBytes = info.Length,
			returnedBytes = bytes.Length,
			truncated,
			isReadOnly = info.IsReadOnly,
			lastWriteTime = info.LastWriteTime,
			checksum = includeChecksum ? Checksums.OfFile( fullPath ) : null,
			encoding = useBase64 ? "base64" : "utf8",
			content = useBase64 ? Convert.ToBase64String( bytes ) : Encoding.UTF8.GetString( bytes ),
		};
	}

	public async Task<object> WriteLocalFileAsync( string path, string content, string encoding, bool forceWritable, CancellationToken cancellationToken )
	{
		string fullPath = ResolveVaultBoundedPath( path );
		if( Path.GetDirectoryName( fullPath ) is { Length: > 0 } dir )
			Directory.CreateDirectory( dir );

		if( File.Exists( fullPath ) )
		{
			FileInfo existing = new( fullPath );
			if( existing.IsReadOnly )
			{
				if( !forceWritable )
					throw new UnauthorizedAccessException( $"File '{fullPath}' is read-only (check it out first, or pass forceWritable=true)." );
				existing.Attributes &= ~FileAttributes.ReadOnly;
			}
		}

		State.McpRestorePoint? rp = RestorePoints is null ? null
			: await RestorePoints.CapturePreMutationAsync(
				"local_write_file",
				$"Write local vault file {Path.GetFileName( fullPath )}",
				[ fullPath ],
				cancellationToken: cancellationToken );

		byte[] bytes = string.Equals( encoding, "base64", StringComparison.OrdinalIgnoreCase )
			? Convert.FromBase64String( content )
			: Encoding.UTF8.GetBytes( content );

		await File.WriteAllBytesAsync( fullPath, bytes, cancellationToken );
		FileInfo written = new( fullPath );
		return new
		{
			ok = true,
			localPath = fullPath,
			treePath = PwaPaths.ToServerName( fullPath ),
			size = written.Length,
			checksum = Checksums.OfFile( fullPath ),
			restorePointId = rp?.Id,
		};
	}

	public object DeleteLocal( IReadOnlyList<string>? filePaths, string? directoryPath, bool forceWritable )
	{
		List<string> backupTargets = [];
		foreach( string p in filePaths ?? [] )
			backupTargets.Add( ResolveVaultBoundedPath( p ) );
		if( !string.IsNullOrWhiteSpace( directoryPath ) )
			backupTargets.Add( ResolveVaultBoundedPath( directoryPath ) );

		State.McpRestorePoint? rp = RestorePoints is null || backupTargets.Count == 0
			? null
			: RestorePoints.CapturePreMutationAsync( "local_delete", $"Delete {backupTargets.Count} local vault item(s)", backupTargets ).GetAwaiter().GetResult();

		List<string> deletedFiles = [];
		foreach( string p in filePaths ?? [] )
		{
			string full = ResolveVaultBoundedPath( p );
			if( !File.Exists( full ) )
				continue;
			FileInfo f = new( full );
			if( f.IsReadOnly )
			{
				if( !forceWritable )
					throw new UnauthorizedAccessException( $"File '{full}' is read-only. Pass forceWritable=true to delete it." );
				f.Attributes &= ~FileAttributes.ReadOnly;
			}
			f.Delete();
			deletedFiles.Add( full );
		}

		string? deletedDir = null;
		if( !string.IsNullOrWhiteSpace( directoryPath ) )
		{
			string fullDir = ResolveVaultBoundedPath( directoryPath );
			string vaultRoot = Path.GetFullPath( HackDefaults.Instance.PwaPathAbsolute );
			StringComparison pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
			if( string.Equals( Path.TrimEndingDirectorySeparator( fullDir ), Path.TrimEndingDirectorySeparator( vaultRoot ), pathComparison ) )
				throw new InvalidOperationException( "Refusing to delete the root PWA vault directory itself." );
			if( Directory.Exists( fullDir ) )
			{
				if( forceWritable )
				{
					foreach( FileInfo f in new DirectoryInfo( fullDir ).EnumerateFiles( "*", SearchOption.AllDirectories ) )
					{
						if( f.IsReadOnly )
							f.Attributes &= ~FileAttributes.ReadOnly;
					}
				}
				Directory.Delete( fullDir, recursive: true );
				deletedDir = fullDir;
			}
		}

		return new { ok = true, deletedFiles, deletedDirectory = deletedDir, restorePointId = rp?.Id };
	}

	public object ComputeLocalChecksums( IReadOnlyList<string> paths, string? expectedChecksum )
	{
		List<object> items = [];
		foreach( string p in paths )
		{
			string full = ResolveLocalPath( p );
			bool exists = File.Exists( full );
			string? sha1 = exists ? Checksums.OfFile( full ) : null;
			items.Add( new
			{
				input = p,
				localPath = full,
				treePath = PwaPaths.ToServerName( full ),
				exists,
				checksum = sha1,
				matchesExpected = expectedChecksum is not null && sha1 is not null
					? string.Equals( sha1, expectedChecksum, StringComparison.OrdinalIgnoreCase )
					: (bool?)null,
			} );
		}
		return new { count = items.Count, files = items };
	}

	// ------------------------------------------------------------------ Internal Workflow Helpers

	private static async Task<IReadOnlyList<int>> ResolveTargetEntryIdsAsync(
		IReadOnlyList<int>? entryIds,
		int? directoryId,
		bool withSubdirectories,
		bool withDeleted,
		McpOperationLog log )
	{
		if( entryIds is { Count: > 0 } )
			return [ .. entryIds.Where( id => id > 0 ).Distinct() ];

		if( directoryId is int dirId and > 0 )
		{
			Result<IReadOnlyList<int>> ids = await HpDirectory.GetDirectoryEntryIDsAsync( dirId, withSubdirectories, withDeleted );
			if( !ids.Ok )
			{
				log.Add( StatusMessage.ERROR, $"Could not list directory {dirId} entries: {ids.Error}" );
				throw new InvalidOperationException( ids.Error ?? $"Could not list directory {dirId} entries." );
			}
			return ids.Value ?? [];
		}

		throw new ArgumentException( "Specify either 'entryIds' or 'directoryId'." );
	}

	private static async Task<IReadOnlyList<int>> ExpandEntriesWithDependenciesAsync( IReadOnlyList<int> entryIds, McpOperationLog log )
	{
		HpEntry[]? entries = await HpEntry.GetRecordsByIdsAsync( entryIds, includedFields: [ nameof( HpEntry.latest_version_id ) ] );
		int[] latestVersionIds = [ .. entries?.Select( e => e.latest_version_id?.id ?? 0 ).Where( id => id > 0 ) ?? [] ];
		if( latestVersionIds.Length == 0 )
			return entryIds;

		Result<IReadOnlyList<int>> deps = await PdmDownloadOperations.GetAllEntriesAndDependenciesListAsync( latestVersionIds );
		if( !deps.Ok )
			log.Add( StatusMessage.WARNING, $"Dependencies could not be listed: {deps.Error}" );

		return [ .. deps.ValueOr( [] ).Concat( entryIds ).Distinct() ];
	}

	private async Task<(List<HackFile> Files, List<object> Broken)> CollectCommitCandidatesAsync(
		IReadOnlyList<string>? filePaths,
		string? folderPath,
		McpOperationLog log )
	{
		HashSet<HackFile> collected = [];
		List<object> broken = [];

		if( !string.IsNullOrWhiteSpace( folderPath ) )
		{
			string fullDir = ResolveLocalDirectoryPath( folderPath );
			if( !Directory.Exists( fullDir ) )
				throw new DirectoryNotFoundException( $"Local folder does not exist: '{fullDir}'." );
			var (folderFiles, folderBroken) = await DependencyScanner.GetHackFolderWithDependenciesAsync( fullDir );
			foreach( HackFile hf in folderFiles )
				collected.Add( hf );
			foreach( var b in folderBroken )
			{
				log.Add( StatusMessage.ERROR, $"({b.Value?.Result}) file: {b.Value?.Hack?.FullPath}" );
				broken.Add( new { result = b.Value?.Result.ToString(), path = b.Value?.Hack?.FullPath } );
			}
		}

		foreach( string rawPath in filePaths ?? [] )
		{
			string fullPath = ResolveLocalPath( rawPath );
			if( !File.Exists( fullPath ) )
			{
				log.Add( StatusMessage.ERROR, $"File does not exist locally: {fullPath}" );
				broken.Add( new { result = "NotFound", path = fullPath } );
				continue;
			}
			HackFile? hack = HackFile.GetFromPath( fullPath );
			if( hack is not { Exists: true } )
				continue;

			if( SwDocumentTypes.IsSolidWorks( fullPath ) )
			{
				var (success, depFiles, depBroken) = await hack.GetHackFileWithDependencies();
				if( success )
				{
					foreach( HackFile df in depFiles )
						collected.Add( df );
				}
				else
				{
					foreach( var b in depBroken )
					{
						log.Add( StatusMessage.ERROR, $"({b.Value?.Result}) file: {b.Value?.Hack?.FullPath}" );
						broken.Add( new { result = b.Value?.Result.ToString(), path = b.Value?.Hack?.FullPath } );
					}
				}
			}
			else
			{
				collected.Add( hack );
			}
		}

		return ( [ .. collected ], broken );
	}

	private static object DescribeHackFile( HackFile f ) => new
	{
		name = f.Name,
		fullPath = f.FullPath,
		relativePath = f.RelativePath,
		typeExt = f.TypeExt,
		fileSize = f.FileSize,
		checksum = f.Checksum,
		modifiedDate = f.ModifiedDate,
	};

	private static string ResolveLocalPath( string path )
	{
		if( Path.IsPathFullyQualified( path ) )
			return Path.GetFullPath( path );
		string? mapped = PwaPaths.ToAbsolute( path );
		return string.IsNullOrEmpty( mapped ) ? Path.GetFullPath( path ) : Path.GetFullPath( mapped );
	}

	private static string ResolveLocalDirectoryPath( string path )
		=> ResolveLocalPath( path );

	private static string ResolveExistingLocalPath( string path )
	{
		string full = ResolveLocalPath( path );
		if( !File.Exists( full ) )
			throw new FileNotFoundException( $"Local file not found: '{full}'.", full );
		return full;
	}

	private static string ResolveVaultBoundedPath( string path )
	{
		string vault = HackDefaults.Instance.PwaPathAbsolute;
		if( string.IsNullOrWhiteSpace( vault ) )
			throw new InvalidOperationException( "Local working folder (PWA) is not configured. Set it with session_configure first." );

		string full = ResolveLocalPath( path );
		if( !PwaPaths.IsInside( full ) )
			throw new UnauthorizedAccessException( $"Path '{full}' is outside the configured HackPDM working folder '{vault}'." );
		return full;
	}
}

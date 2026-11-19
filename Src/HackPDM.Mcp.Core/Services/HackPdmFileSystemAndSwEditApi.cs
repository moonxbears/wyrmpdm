using HackPDM.Core;
using HackPDM.Core.Hack;
using HackPDM.Infrastructure.SldWrks;
using HackPDM.Mcp.State;

namespace HackPDM.Mcp.Services;

public sealed partial class HackPdmApiService
{
	// =========================================================================
	// Cross-Platform Computer-Wide Filesystem Manipulation & PWA Import
	// =========================================================================

	public object FsList( string path, string? searchPattern, bool recursive, bool computeChecksums, int maxEntries )
	{
		string fullDir = ResolveAnyPath( path );
		if( !Directory.Exists( fullDir ) )
			throw new DirectoryNotFoundException( $"Directory not found: '{fullDir}'." );

		int cap = Math.Clamp( maxEntries, 1, 2000 );
		string pattern = string.IsNullOrWhiteSpace( searchPattern ) ? "*" : searchPattern.Trim();
		EnumerationOptions opt = new()
		{
			RecurseSubdirectories = recursive,
			IgnoreInaccessible = true,
			ReturnSpecialDirectories = false,
		};

		DirectoryInfo dirInfo = new( fullDir );
		List<object> directories = [];
		foreach( DirectoryInfo d in dirInfo.EnumerateDirectories( pattern, opt ).Take( cap ) )
		{
			bool inside = PwaPaths.IsInside( d.FullName );
			directories.Add( new
			{
				name = d.Name,
				fullPath = d.FullName,
				isInsidePwa = inside,
				treePath = inside ? PwaPaths.ToServerName( d.FullName ) : null,
				lastWriteTimeUtc = d.LastWriteTimeUtc,
			} );
		}

		List<object> files = [];
		bool truncated = false;
		foreach( FileInfo f in dirInfo.EnumerateFiles( pattern, opt ) )
		{
			if( files.Count >= cap )
			{
				truncated = true;
				break;
			}
			bool inside = PwaPaths.IsInside( f.FullName );
			files.Add( new
			{
				name = f.Name,
				extension = f.Extension,
				fullPath = f.FullName,
				isInsidePwa = inside,
				treePath = inside ? PwaPaths.ToServerName( f.FullName ) : null,
				isSolidWorks = SwDocumentTypes.IsSolidWorks( f.FullName ),
				sizeBytes = f.Length,
				isReadOnly = f.IsReadOnly,
				lastWriteTimeUtc = f.LastWriteTimeUtc,
				checksum = computeChecksums ? Checksums.OfFile( f.FullName ) : null,
			} );
		}

		bool rootInside = PwaPaths.IsInside( fullDir );
		return new
		{
			path = fullDir,
			isInsidePwa = rootInside,
			treePath = rootInside ? PwaPaths.ToServerName( fullDir ) : null,
			pwaRoot = HackDefaults.Instance.PwaPathAbsolute,
			directoryCount = directories.Count,
			fileCount = files.Count,
			truncated,
			directories,
			files,
		};
	}

	public object FsStat( string path, bool computeChecksum )
	{
		string fullPath = ResolveAnyPath( path );
		bool inside = PwaPaths.IsInside( fullPath );

		if( File.Exists( fullPath ) )
		{
			FileInfo f = new( fullPath );
			return new
			{
				exists = true,
				kind = "file",
				name = f.Name,
				extension = f.Extension,
				fullPath = f.FullName,
				directory = f.DirectoryName,
				isInsidePwa = inside,
				treePath = inside ? PwaPaths.ToServerName( f.FullName ) : null,
				canCommitToPdm = inside,
				isSolidWorks = SwDocumentTypes.IsSolidWorks( f.FullName ),
				sizeBytes = f.Length,
				isReadOnly = f.IsReadOnly,
				createdUtc = f.CreationTimeUtc,
				lastWriteTimeUtc = f.LastWriteTimeUtc,
				checksum = computeChecksum ? Checksums.OfFile( f.FullName ) : null,
			};
		}

		if( Directory.Exists( fullPath ) )
		{
			DirectoryInfo d = new( fullPath );
			return new
			{
				exists = true,
				kind = "directory",
				name = d.Name,
				fullPath = d.FullName,
				parent = d.Parent?.FullName,
				isInsidePwa = inside,
				treePath = inside ? PwaPaths.ToServerName( d.FullName ) : null,
				canCommitToPdm = inside,
				createdUtc = d.CreationTimeUtc,
				lastWriteTimeUtc = d.LastWriteTimeUtc,
			};
		}

		return new
		{
			exists = false,
			kind = "missing",
			fullPath,
			isInsidePwa = inside,
			treePath = inside ? PwaPaths.ToServerName( fullPath ) : null,
			canCommitToPdm = false,
		};
	}

	public async Task<object> FsMkdirAsync( string path, CancellationToken cancellationToken )
	{
		string fullPath = ResolveAnyPath( path );
		bool alreadyExists = Directory.Exists( fullPath );
		McpRestorePoint? rp = null;
		if( !alreadyExists && RestorePoints is not null )
		{
			rp = await RestorePoints.CapturePreMutationAsync(
				"fs_mkdir",
				$"Create directory {Path.GetFileName( fullPath.TrimEnd( Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar ) )}",
				[ fullPath.TrimEnd( Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar ) + Path.DirectorySeparatorChar ],
				cancellationToken: cancellationToken );
		}

		Directory.CreateDirectory( fullPath );
		bool inside = PwaPaths.IsInside( fullPath );
		return new
		{
			ok = true,
			path = fullPath,
			alreadyExists,
			isInsidePwa = inside,
			treePath = inside ? PwaPaths.ToServerName( fullPath ) : null,
			restorePointId = rp?.Id,
		};
	}

	public async Task<object> FsCopyAsync( string sourcePath, string destinationPath, bool overwrite, CancellationToken cancellationToken )
	{
		string src = ResolveAnyPath( sourcePath );
		string dst = ResolveAnyPath( destinationPath );

		if( File.Exists( src ) )
		{
			// If destination is an existing directory, place the file inside it
			if( Directory.Exists( dst ) )
				dst = Path.Combine( dst, Path.GetFileName( src ) );
			if( File.Exists( dst ) && !overwrite )
				throw new IOException( $"Destination file already exists: '{dst}'. Pass overwrite=true to replace it." );

			McpRestorePoint? rp = RestorePoints is null ? null
				: await RestorePoints.CapturePreMutationAsync(
					"fs_copy",
					$"Copy {Path.GetFileName( src )} -> {Path.GetFileName( dst )}",
					[ dst ],
					cancellationToken: cancellationToken );

			if( Path.GetDirectoryName( dst ) is { Length: > 0 } parent )
				Directory.CreateDirectory( parent );
			if( File.Exists( dst ) )
				ClearReadOnlyFlag( dst );
			File.Copy( src, dst, overwrite: true );

			bool dstInside = PwaPaths.IsInside( dst );
			return new
			{
				ok = true,
				kind = "file",
				sourcePath = src,
				destinationPath = dst,
				isInsidePwa = dstInside,
				treePath = dstInside ? PwaPaths.ToServerName( dst ) : null,
				sizeBytes = new FileInfo( dst ).Length,
				restorePointId = rp?.Id,
			};
		}

		if( Directory.Exists( src ) )
		{
			List<string> dstTargets = [ dst ];
			if( !Directory.Exists( dst ) )
				dstTargets = [ dst.TrimEnd( Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar ) + Path.DirectorySeparatorChar ];

			McpRestorePoint? rp = RestorePoints is null ? null
				: await RestorePoints.CapturePreMutationAsync(
					"fs_copy",
					$"Copy directory {Path.GetFileName( src )}",
					dstTargets,
					cancellationToken: cancellationToken );

			int copiedFiles = CopyDirectoryRecursive( src, dst, overwrite, cancellationToken );
			bool dstInside = PwaPaths.IsInside( dst );
			return new
			{
				ok = true,
				kind = "directory",
				sourcePath = src,
				destinationPath = dst,
				copiedFiles,
				isInsidePwa = dstInside,
				treePath = dstInside ? PwaPaths.ToServerName( dst ) : null,
				restorePointId = rp?.Id,
			};
		}

		throw new FileNotFoundException( $"Source path not found: '{src}'.", src );
	}

	public async Task<object> FsMoveAsync( string sourcePath, string destinationPath, bool overwrite, CancellationToken cancellationToken )
	{
		string src = ResolveAnyPath( sourcePath );
		string dst = ResolveAnyPath( destinationPath );

		if( File.Exists( src ) )
		{
			if( Directory.Exists( dst ) )
				dst = Path.Combine( dst, Path.GetFileName( src ) );
			if( File.Exists( dst ) && !overwrite )
				throw new IOException( $"Destination file already exists: '{dst}'. Pass overwrite=true to replace it." );

			McpRestorePoint? rp = RestorePoints is null ? null
				: await RestorePoints.CapturePreMutationAsync(
					"fs_move",
					$"Move {Path.GetFileName( src )} -> {dst}",
					[ src, dst ],
					cancellationToken: cancellationToken );

			if( Path.GetDirectoryName( dst ) is { Length: > 0 } parent )
				Directory.CreateDirectory( parent );
			if( File.Exists( dst ) )
				ClearReadOnlyFlag( dst );
			ClearReadOnlyFlag( src );
			File.Move( src, dst, overwrite: true );

			bool dstInside = PwaPaths.IsInside( dst );
			return new
			{
				ok = true,
				kind = "file",
				sourcePath = src,
				destinationPath = dst,
				isInsidePwa = dstInside,
				treePath = dstInside ? PwaPaths.ToServerName( dst ) : null,
				restorePointId = rp?.Id,
			};
		}

		if( Directory.Exists( src ) )
		{
			if( Directory.Exists( dst ) && !overwrite )
				throw new IOException( $"Destination directory already exists: '{dst}'. Pass overwrite=true to merge/overwrite." );

			McpRestorePoint? rp = RestorePoints is null ? null
				: await RestorePoints.CapturePreMutationAsync(
					"fs_move",
					$"Move directory {Path.GetFileName( src )}",
					[ src, Directory.Exists( dst ) ? dst : dst.TrimEnd( Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar ) + Path.DirectorySeparatorChar ],
					cancellationToken: cancellationToken );

			if( Path.GetDirectoryName( dst ) is { Length: > 0 } parent )
				Directory.CreateDirectory( parent );

			try
			{
				if( !Directory.Exists( dst ) )
				{
					Directory.Move( src, dst );
				}
				else
				{
					CopyDirectoryRecursive( src, dst, overwrite: true, cancellationToken );
					DeleteDirectoryForce( src );
				}
			}
			catch( IOException )
			{
				// Cross-volume directory move fallback
				CopyDirectoryRecursive( src, dst, overwrite: true, cancellationToken );
				DeleteDirectoryForce( src );
			}

			bool dstInside = PwaPaths.IsInside( dst );
			return new
			{
				ok = true,
				kind = "directory",
				sourcePath = src,
				destinationPath = dst,
				isInsidePwa = dstInside,
				treePath = dstInside ? PwaPaths.ToServerName( dst ) : null,
				restorePointId = rp?.Id,
			};
		}

		throw new FileNotFoundException( $"Source path not found: '{src}'.", src );
	}

	public async Task<object> FsDeleteAsync( IReadOnlyList<string> paths, bool forceWritable, CancellationToken cancellationToken )
	{
		if( paths is null || paths.Count == 0 )
			throw new ArgumentException( "Specify at least one path to delete." );

		List<string> resolved = [ .. paths.Where( p => !string.IsNullOrWhiteSpace( p ) ).Select( ResolveAnyPath ) ];
		McpRestorePoint? rp = RestorePoints is null ? null
			: await RestorePoints.CapturePreMutationAsync(
				"fs_delete",
				$"Delete {resolved.Count} path(s)",
				resolved,
				cancellationToken: cancellationToken );

		List<string> deletedFiles = [];
		List<string> deletedDirs = [];

		foreach( string p in resolved )
		{
			if( File.Exists( p ) )
			{
				FileInfo fi = new( p );
				if( fi.IsReadOnly )
				{
					if( !forceWritable )
						throw new UnauthorizedAccessException( $"File '{p}' is read-only. Pass forceWritable=true to delete it." );
					fi.Attributes &= ~FileAttributes.ReadOnly;
				}
				fi.Delete();
				deletedFiles.Add( p );
			}
			else if( Directory.Exists( p ) )
			{
				if( forceWritable )
					DeleteDirectoryForce( p );
				else
					Directory.Delete( p, recursive: true );
				deletedDirs.Add( p );
			}
		}

		return new
		{
			ok = true,
			deletedFiles,
			deletedDirectories = deletedDirs,
			restorePointId = rp?.Id,
		};
	}

	// =========================================================================
	// SolidWorks Property & Dependency Path Editing
	// =========================================================================

	public async Task<object> SwSetPropertiesAsync(
		string path,
		IReadOnlyList<SwPropertyEdit> edits,
		bool forceWritable,
		CancellationToken cancellationToken )
	{
		string fullPath = ResolveAnyExistingFile( path );
		if( !SwDocumentTypes.IsSolidWorks( fullPath ) )
			throw new ArgumentException( $"File '{fullPath}' is not a SolidWorks file (.sldprt, .sldasm, .slddrw)." );

		EnsureFileWritable( fullPath, forceWritable );

		McpRestorePoint? rp = RestorePoints is null ? null
			: await RestorePoints.CapturePreMutationAsync(
				"sw_set_properties",
				$"Edit SolidWorks properties on {Path.GetFileName( fullPath )}",
				[ fullPath ],
				cancellationToken: cancellationToken );

		SwPropertyWriteResult res = await Task.Run( () => SwFileWriter.SetProperties( fullPath, edits ), cancellationToken );

		return new
		{
			ok = true,
			filePath = res.FilePath,
			isInsidePwa = PwaPaths.IsInside( res.FilePath ),
			treePath = PwaPaths.IsInside( res.FilePath ) ? PwaPaths.ToServerName( res.FilePath ) : null,
			containerFormat = res.ContainerFormat,
			engine = res.Engine,
			appliedCount = res.AppliedCount,
			modifiedStreams = res.ModifiedStreams,
			restorePointId = rp?.Id,
			checksum = Checksums.OfFile( res.FilePath ),
			currentProperties = res.CurrentProperties.Select( p => new
			{
				configuration = p.Configuration,
				name = p.Name,
				type = p.Type,
				value = p.Value,
			} ).ToList(),
		};
	}

	public async Task<object> SwSetDependenciesAsync(
		string path,
		IReadOnlyList<SwDependencyReplacement>? replacements,
		string? oldPrefix,
		string? newPrefix,
		string? remapToDirectory,
		bool dryRun,
		bool forceWritable,
		CancellationToken cancellationToken )
	{
		string fullPath = ResolveAnyExistingFile( path );
		if( !SwDocumentTypes.IsSolidWorks( fullPath ) )
			throw new ArgumentException( $"File '{fullPath}' is not a SolidWorks file (.sldprt, .sldasm, .slddrw)." );

		// Resolve remapToDirectory if given as a vault tree path (e.g. "root/Projects/KitBot")
		string? resolvedRemapDir = remapToDirectory;
		if( !string.IsNullOrWhiteSpace( remapToDirectory ) && !LooksLikeWindowsPath( remapToDirectory ) && !Path.IsPathFullyQualified( remapToDirectory ) )
			resolvedRemapDir = ResolveLocalPath( remapToDirectory );

		McpRestorePoint? rp = null;
		if( !dryRun )
		{
			EnsureFileWritable( fullPath, forceWritable );
			if( RestorePoints is not null )
			{
				rp = await RestorePoints.CapturePreMutationAsync(
					"sw_set_dependencies",
					$"Rewrite SolidWorks dependencies in {Path.GetFileName( fullPath )}",
					[ fullPath ],
					cancellationToken: cancellationToken );
			}
		}

		SwDependencyWriteResult res = await Task.Run(
			() => SwFileWriter.RewriteDependencies( fullPath, replacements, oldPrefix, newPrefix, resolvedRemapDir, dryRun ),
			cancellationToken );

		return new
		{
			ok = true,
			filePath = res.FilePath,
			isInsidePwa = PwaPaths.IsInside( res.FilePath ),
			treePath = PwaPaths.IsInside( res.FilePath ) ? PwaPaths.ToServerName( res.FilePath ) : null,
			containerFormat = res.ContainerFormat,
			engine = res.Engine,
			dryRun = res.DryRun,
			rewrittenCount = res.RewrittenCount,
			rewrites = res.Rewrites,
			modifiedStreams = res.ModifiedStreams,
			verifiedDependencies = res.VerifiedDependencies,
			restorePointId = rp?.Id,
			checksum = Checksums.OfFile( res.FilePath ),
		};
	}

	/// <summary>
	/// Imports one or more external files (e.g. SolidWorks assemblies/drawings/parts located outside the PWA vault)
	/// into a target folder inside the PWA vault, optionally discovers all local SolidWorks dependencies recursively,
	/// rewrites internal SolidWorks dependency paths to point inside the PWA, and records a single atomic restore point.
	/// </summary>
	public async Task<object> FsImportToPwaAsync(
		IReadOnlyList<string> sourcePaths,
		string targetPwaFolder,
		string mode,
		bool includeDependencies,
		bool overwrite,
		bool rewriteDependencies,
		string? solidWorksPathPrefix,
		CancellationToken cancellationToken )
	{
		if( sourcePaths is null || sourcePaths.Count == 0 )
			throw new ArgumentException( "Specify at least one source file or folder path." );

		bool moveFiles = string.Equals( mode, "move", StringComparison.OrdinalIgnoreCase );
		string targetDir = ResolveVaultBoundedPath( string.IsNullOrWhiteSpace( targetPwaFolder ) ? "root" : targetPwaFolder );

		// 1. Collect initial source files
		List<string> queue = [];
		HashSet<string> collected = new( PathComparer );
		List<object> unresolved = [];

		void EnqueueFile( string f )
		{
			string full = Path.GetFullPath( f );
			if( File.Exists( full ) && collected.Add( full ) )
				queue.Add( full );
		}

		foreach( string raw in sourcePaths )
		{
			string full = ResolveAnyPath( raw );
			if( File.Exists( full ) )
			{
				EnqueueFile( full );
			}
			else if( Directory.Exists( full ) )
			{
				foreach( string f in Directory.EnumerateFiles( full, "*", SearchOption.AllDirectories ) )
					EnqueueFile( f );
			}
			else
			{
				throw new FileNotFoundException( $"Source path not found: '{full}'.", full );
			}
		}

		// 2. Recursively discover local SolidWorks dependencies when enabled
		SwFileReader reader = new();
		List<string> searchRoots = [ .. queue.Select( f => Path.GetDirectoryName( f ) ).OfType<string>().Distinct( PathComparer ) ];

		if( includeDependencies )
		{
			for( int i = 0; i < queue.Count; i++ )
			{
				cancellationToken.ThrowIfCancellationRequested();
				string currentFile = queue[ i ];
				if( !SwDocumentTypes.IsSolidWorks( currentFile ) )
					continue;

				List<string[]>? deps = reader.GetDependencies( currentFile, deep: false, noInterrupt: true );
				if( deps is null )
					continue;

				string currentDir = Path.GetDirectoryName( currentFile ) ?? "";
				foreach( string[] pair in deps )
				{
					string depName = pair[ 0 ];
					string storedPath = pair[ 1 ];
					string? foundLocal = LocateDependencyOnDisk( storedPath, depName, currentDir, searchRoots );
					if( foundLocal is not null )
						EnqueueFile( foundLocal );
					else
						unresolved.Add( new { referencedBy = currentFile, dependencyName = depName, storedPath } );
				}
			}
		}

		// 3. Plan destination paths inside PWA
		Dictionary<string, string> srcToDst = new( PathComparer );
		Dictionary<string, string> fileNameToSwTarget = new( StringComparer.OrdinalIgnoreCase );
		string swPrefixDir = !string.IsNullOrWhiteSpace( solidWorksPathPrefix )
			? SwFileWriter.NormalizeSolidWorksPath( solidWorksPathPrefix ).TrimEnd( '\\' )
			: SwFileWriter.NormalizeSolidWorksPath( targetDir ).TrimEnd( '\\' );

		foreach( string src in queue )
		{
			string fileName = Path.GetFileName( src );
			string dst = Path.Combine( targetDir, fileName );
			if( File.Exists( dst ) && !overwrite && !PathComparer.Equals( src, dst ) )
				throw new IOException( $"Destination file already exists inside PWA: '{dst}'. Pass overwrite=true to replace it." );
			srcToDst[ src ] = dst;
			fileNameToSwTarget[ fileName ] = $"{swPrefixDir}\\{fileName}";
		}

		// 4. Create single atomic Restore Point covering all destinations (+ sources if moving!)
		List<string> rpTargets = [];
		if( !Directory.Exists( targetDir ) )
			rpTargets.Add( targetDir.TrimEnd( Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar ) + Path.DirectorySeparatorChar );
		foreach( var (src, dst) in srcToDst )
		{
			if( moveFiles )
				rpTargets.Add( src );
			rpTargets.Add( dst );
		}

		McpRestorePoint? rp = RestorePoints is null ? null
			: await RestorePoints.CapturePreMutationAsync(
				"fs_import_to_pwa",
				$"Import {srcToDst.Count} file(s) to {PwaPaths.ToServerName( targetDir )}",
				rpTargets,
				cancellationToken: cancellationToken );

		// 5. Copy or move files into PWA
		Directory.CreateDirectory( targetDir );
		List<object> importedFiles = [];
		foreach( var (src, dst) in srcToDst )
		{
			cancellationToken.ThrowIfCancellationRequested();
			if( !PathComparer.Equals( src, dst ) )
			{
				if( File.Exists( dst ) )
					ClearReadOnlyFlag( dst );
				if( moveFiles )
				{
					ClearReadOnlyFlag( src );
					File.Move( src, dst, overwrite: true );
				}
				else
				{
					File.Copy( src, dst, overwrite: true );
				}
			}
			importedFiles.Add( new
			{
				sourcePath = src,
				destinationPath = dst,
				treePath = PwaPaths.ToServerName( dst ),
				isSolidWorks = SwDocumentTypes.IsSolidWorks( dst ),
				mode = moveFiles ? "moved" : "copied",
			} );
		}

		// 6. Rewrite SolidWorks dependency paths inside every imported SW document
		List<object> rewriteReports = [];
		if( rewriteDependencies && fileNameToSwTarget.Count > 0 )
		{
			List<SwDependencyReplacement> replacements = [ .. fileNameToSwTarget.Select( kv => new SwDependencyReplacement( kv.Key, kv.Value ) ) ];
			foreach( string dst in srcToDst.Values )
			{
				if( !SwDocumentTypes.IsSolidWorks( dst ) )
					continue;
				SwDependencyWriteResult rw = await Task.Run(
					() => SwFileWriter.RewriteDependencies( dst, replacements: replacements, dryRun: false ),
					cancellationToken );
				if( rw.RewrittenCount > 0 || rw.VerifiedDependencies.Count > 0 )
				{
					rewriteReports.Add( new
					{
						file = Path.GetFileName( dst ),
						destinationPath = dst,
						treePath = PwaPaths.ToServerName( dst ),
						rewrittenCount = rw.RewrittenCount,
						rewrites = rw.Rewrites,
						modifiedStreams = rw.ModifiedStreams,
						verifiedDependencies = rw.VerifiedDependencies,
					} );
				}
			}
		}

		return new
		{
			ok = true,
			mode = moveFiles ? "move" : "copy",
			targetDirectory = targetDir,
			targetTreePath = PwaPaths.ToServerName( targetDir ),
			importedCount = importedFiles.Count,
			importedFiles,
			dependencyRewrites = rewriteReports,
			unresolvedDependencies = unresolved,
			readyToCommit = unresolved.Count == 0,
			restorePointId = rp?.Id,
		};
	}

	// ---- Helpers ----

	private static string ResolveAnyPath( string path )
	{
		if( string.IsNullOrWhiteSpace( path ) )
			throw new ArgumentException( "Path cannot be empty." );
		string trimmed = path.Trim();
		if( Path.IsPathFullyQualified( trimmed ) )
			return Path.GetFullPath( trimmed );
		// Allow "root\..." or "root/..." vault tree paths seamlessly
		if( trimmed.Equals( "root", StringComparison.OrdinalIgnoreCase )
			|| trimmed.StartsWith( "root\\", StringComparison.OrdinalIgnoreCase )
			|| trimmed.StartsWith( "root/", StringComparison.OrdinalIgnoreCase ) )
		{
			string? mapped = PwaPaths.ToAbsolute( trimmed );
			if( !string.IsNullOrEmpty( mapped ) )
				return Path.GetFullPath( mapped );
		}
		return Path.GetFullPath( trimmed );
	}

	private static string ResolveAnyExistingFile( string path )
	{
		string full = ResolveAnyPath( path );
		if( File.Exists( full ) )
			return full;
		// Also try vault-relative resolution as fallback
		string vaultMapped = ResolveLocalPath( path );
		if( File.Exists( vaultMapped ) )
			return vaultMapped;
		throw new FileNotFoundException( $"File not found: '{full}'.", full );
	}

	private static string? LocateDependencyOnDisk( string storedPath, string depName, string currentDir, List<string> searchRoots )
	{
		// 1. Direct stored path (works natively on Windows or if stored path exists as-is)
		if( File.Exists( storedPath ) )
			return Path.GetFullPath( storedPath );

		// 2. Sibling in same folder as parent document
		string sibling = Path.Combine( currentDir, depName );
		if( File.Exists( sibling ) )
			return Path.GetFullPath( sibling );

		// 3. Search inside source root directories (subfolders)
		foreach( string root in searchRoots )
		{
			if( !Directory.Exists( root ) )
				continue;
			string direct = Path.Combine( root, depName );
			if( File.Exists( direct ) )
				return Path.GetFullPath( direct );
			string? match = Directory.EnumerateFiles( root, depName, new EnumerationOptions
			{
				RecurseSubdirectories = true,
				IgnoreInaccessible = true,
				MatchCasing = MatchCasing.CaseInsensitive,
			} ).FirstOrDefault();
			if( match is not null )
				return Path.GetFullPath( match );
		}

		// 4. Already present inside the PWA vault (via SwReferenceResolver)
		string vault = HackDefaults.Instance.PwaPathAbsolute;
		if( !string.IsNullOrWhiteSpace( vault ) && Directory.Exists( vault ) )
		{
			SwReferenceResolver resolver = new( vault );
			if( resolver.Resolve( storedPath, currentDir ) is { } resolved && File.Exists( resolved ) )
				return Path.GetFullPath( resolved );
		}

		return null;
	}

	private static bool LooksLikeWindowsPath( string path )
		=> path.Length >= 2 && char.IsLetter( path[ 0 ] ) && path[ 1 ] == ':';

	private static void EnsureFileWritable( string fullPath, bool forceWritable )
	{
		FileInfo fi = new( fullPath );
		if( fi.IsReadOnly )
		{
			if( !forceWritable )
				throw new UnauthorizedAccessException( $"File '{fullPath}' is read-only (check it out first, or pass forceWritable=true)." );
			fi.Attributes &= ~FileAttributes.ReadOnly;
		}
	}

	private static void ClearReadOnlyFlag( string filePath )
	{
		try
		{
			FileInfo fi = new( filePath );
			if( fi.Exists && fi.IsReadOnly )
				fi.Attributes &= ~FileAttributes.ReadOnly;
		}
		catch { }
	}

	private static int CopyDirectoryRecursive( string srcDir, string dstDir, bool overwrite, CancellationToken ct )
	{
		Directory.CreateDirectory( dstDir );
		int count = 0;
		foreach( string subDir in Directory.EnumerateDirectories( srcDir, "*", SearchOption.AllDirectories ) )
		{
			string rel = Path.GetRelativePath( srcDir, subDir );
			Directory.CreateDirectory( Path.Combine( dstDir, rel ) );
		}
		foreach( string file in Directory.EnumerateFiles( srcDir, "*", SearchOption.AllDirectories ) )
		{
			ct.ThrowIfCancellationRequested();
			string rel = Path.GetRelativePath( srcDir, file );
			string target = Path.Combine( dstDir, rel );
			if( File.Exists( target ) && !overwrite )
				throw new IOException( $"Destination file already exists: '{target}'." );
			if( Path.GetDirectoryName( target ) is { Length: > 0 } p )
				Directory.CreateDirectory( p );
			if( File.Exists( target ) )
				ClearReadOnlyFlag( target );
			File.Copy( file, target, overwrite: true );
			count++;
		}
		return count;
	}

	private static void DeleteDirectoryForce( string dir )
	{
		foreach( string f in Directory.EnumerateFiles( dir, "*", SearchOption.AllDirectories ) )
			ClearReadOnlyFlag( f );
		Directory.Delete( dir, recursive: true );
	}
}

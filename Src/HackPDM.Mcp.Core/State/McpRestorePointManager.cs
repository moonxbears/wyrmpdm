using System.Text;
using System.Text.Json;

using HackPDM.Core;
using HackPDM.Core.Configuration;
using HackPDM.Mcp.Protocol;
using HackPDM.Shared.GlobalData;

namespace HackPDM.Mcp.State;

/// <summary>Snapshot record for a single file or directory tracked inside an MCP restore point.</summary>
public sealed record McpRestoreFileEntry(
	string OriginalPath,
	bool ExistedBefore,
	bool IsDirectory,
	long SizeBytes,
	string? Sha1,
	FileAttributes Attributes,
	DateTime? LastWriteTimeUtc,
	string? BackupFileName );

/// <summary>Manifest for an MCP restore point persisted to disk.</summary>
public sealed record McpRestorePoint(
	string Id,
	string Label,
	string? Description,
	string? TriggerTool,
	bool Automatic,
	DateTimeOffset CreatedAtUtc,
	DateTimeOffset? RestoredAtUtc,
	long TotalBackupBytes,
	IReadOnlyList<McpRestoreFileEntry> Entries );

/// <summary>Result of applying or dry-running a restore point.</summary>
public sealed record McpRestoreResult(
	bool Ok,
	string RestorePointId,
	string Label,
	bool DryRun,
	string? SafetyBackupRestorePointId,
	int RestoredFiles,
	int RemovedCreatedFiles,
	int CreatedDirectories,
	int RemovedCreatedDirectories,
	IReadOnlyList<object> Actions,
	IReadOnlyList<string> Warnings );

/// <summary>
/// Cross-platform restore point / undo snapshot manager for MCP file, SolidWorks CAD,
/// and configuration mutations. Stores manifests and exact byte-for-byte file backups under
/// <c>&lt;AppDataPaths.Data&gt;/mcp/restore-points/</c>.
/// </summary>
public sealed class McpRestorePointManager
{
	public const string AutoRestoreSettingKey = "McpAutoRestorePoints";
	public const string MaxRestorePointsSettingKey = "McpMaxRestorePoints";

	private const int MaxFilesPerDirectorySnapshot = 250;
	private const long MaxSingleFileBackupBytes = 256L * 1024 * 1024; // 256 MiB per file cap

	private sealed class CallContext { public string? RestorePointId; }
	private static readonly AsyncLocal<CallContext?> ActiveCallContext = new();

	private readonly Lock _gate = new();
	private readonly CoreSettings? _settings;
	private readonly Func<string>? _settingsFileResolver;
	private readonly Func<string>? _appConfigFileResolver;
	private bool _autoEnabledFallback = true;
	private int _maxPointsFallback = 50;

	public string StorageDirectory { get; }

	public event Action? Changed;

	public McpRestorePointManager(
		CoreSettings? settings = null,
		string? dataDirOverride = null,
		Func<string>? settingsFileResolver = null,
		Func<string>? appConfigFileResolver = null )
	{
		_settings = settings;
		_settingsFileResolver = settingsFileResolver;
		_appConfigFileResolver = appConfigFileResolver;
		string baseDir = dataDirOverride ?? AppDataPaths.Data;
		StorageDirectory = Path.Combine( baseDir, "mcp", "restore-points" );
	}

	/// <summary>Starts a per-tool-call ambient scope so any restore point created by nested async helpers flows back to the caller.</summary>
	public static IDisposable BeginCallScope()
	{
		CallContext? prior = ActiveCallContext.Value;
		ActiveCallContext.Value = new CallContext();
		return new ScopeRestorer( prior );
	}

	private sealed class ScopeRestorer( CallContext? prior ) : IDisposable
	{
		public void Dispose() => ActiveCallContext.Value = prior;
	}

	/// <summary>Gets or sets the restore point ID created during the currently executing MCP tool call (for activity log linkage).</summary>
	public static string? CurrentCallRestorePointId
	{
		get => ActiveCallContext.Value?.RestorePointId;
		set
		{
			if( ActiveCallContext.Value is { } ctx )
				ctx.RestorePointId = value;
		}
	}

	public bool AutoRestorePointsEnabled
	{
		get => _settings?.Get( AutoRestoreSettingKey, _autoEnabledFallback ) ?? _autoEnabledFallback;
		set
		{
			_autoEnabledFallback = value;
			_settings?.Set( AutoRestoreSettingKey, value );
			NotifyChanged();
		}
	}

	public int MaxRestorePoints
	{
		get => Math.Clamp( _settings?.Get( MaxRestorePointsSettingKey, _maxPointsFallback ) ?? _maxPointsFallback, 5, 500 );
		set
		{
			int clamped = Math.Clamp( value, 5, 500 );
			_maxPointsFallback = clamped;
			_settings?.Set( MaxRestorePointsSettingKey, clamped );
			PruneAutomaticOverflow();
			NotifyChanged();
		}
	}

	/// <summary>
	/// Captures an automatic restore point right before a mutating MCP tool modifies files or config
	/// (when <see cref="AutoRestorePointsEnabled"/> is true) and links it to the active tool call.
	/// </summary>
	public async Task<McpRestorePoint?> CapturePreMutationAsync(
		string triggerTool,
		string label,
		IEnumerable<string> affectedPaths,
		bool includeSettings = false,
		bool includeAppConfig = false,
		CancellationToken cancellationToken = default )
	{
		if( !AutoRestorePointsEnabled )
			return null;

		McpRestorePoint rp = await CreateRestorePointAsync(
			label: label,
			description: $"Automatic pre-mutation backup created by '{triggerTool}'.",
			paths: [ .. affectedPaths ],
			includeSettings: includeSettings,
			includeAppConfig: includeAppConfig,
			automatic: true,
			triggerTool: triggerTool,
			cancellationToken: cancellationToken );

		CurrentCallRestorePointId = rp.Id;
		return rp;
	}

	/// <summary>Creates a persistent restore point covering all specified files/directories and optional config files.</summary>
	public async Task<McpRestorePoint> CreateRestorePointAsync(
		string label,
		string? description = null,
		IReadOnlyList<string>? paths = null,
		bool includeSettings = false,
		bool includeAppConfig = false,
		bool automatic = false,
		string? triggerTool = null,
		CancellationToken cancellationToken = default )
	{
		string id = $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString( "N" )[ ..8 ]}";
		string rpDir = Path.Combine( StorageDirectory, id );
		string filesDir = Path.Combine( rpDir, "files" );
		Directory.CreateDirectory( filesDir );

		List<string> rawTargets = [];
		if( paths is not null )
		{
			foreach( string p in paths )
			{
				if( !string.IsNullOrWhiteSpace( p ) )
					rawTargets.Add( Path.GetFullPath( p ) );
			}
		}
		if( includeSettings )
		{
			string sp = _settingsFileResolver?.Invoke() ?? Path.Combine( AppDataPaths.Config, "settings.json" );
			rawTargets.Add( Path.GetFullPath( sp ) );
		}
		if( includeAppConfig )
		{
			string ap = _appConfigFileResolver?.Invoke()
				?? Path.Combine( AppDataPaths.IsConfigOverridden ? AppDataPaths.Config : Environment.GetFolderPath( Environment.SpecialFolder.Personal ), "AppConfig.json" );
			rawTargets.Add( Path.GetFullPath( ap ) );
		}

		// Deduplicate targets
		StringComparer pathCmp = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
		List<string> uniqueTargets = [ .. rawTargets.Distinct( pathCmp ) ];

		List<McpRestoreFileEntry> entries = [];
		HashSet<string> trackedPaths = new( pathCmp );
		long totalBackupBytes = 0;
		int fileCounter = 0;

		async Task CaptureFileAsync( string fullPath )
		{
			if( !trackedPaths.Add( fullPath ) )
				return;

			if( File.Exists( fullPath ) )
			{
				FileInfo info = new( fullPath );
				if( info.Length > MaxSingleFileBackupBytes )
				{
					// Record metadata without duplicating massive >256MiB blobs
					entries.Add( new McpRestoreFileEntry(
						OriginalPath: fullPath,
						ExistedBefore: true,
						IsDirectory: false,
						SizeBytes: info.Length,
						Sha1: null,
						Attributes: info.Attributes,
						LastWriteTimeUtc: info.LastWriteTimeUtc,
						BackupFileName: null ) );
					return;
				}

				string backupName = $"{fileCounter++}.bin";
				string backupPath = Path.Combine( filesDir, backupName );
				await CopyFileAsync( fullPath, backupPath, cancellationToken );
				totalBackupBytes += info.Length;

				entries.Add( new McpRestoreFileEntry(
					OriginalPath: fullPath,
					ExistedBefore: true,
					IsDirectory: false,
					SizeBytes: info.Length,
					Sha1: Checksums.OfFile( backupPath ),
					Attributes: info.Attributes,
					LastWriteTimeUtc: info.LastWriteTimeUtc,
					BackupFileName: backupName ) );
			}
			else if( Directory.Exists( fullPath ) )
			{
				entries.Add( new McpRestoreFileEntry(
					OriginalPath: fullPath,
					ExistedBefore: true,
					IsDirectory: true,
					SizeBytes: 0,
					Sha1: null,
					Attributes: File.GetAttributes( fullPath ),
					LastWriteTimeUtc: Directory.GetLastWriteTimeUtc( fullPath ),
					BackupFileName: null ) );

				// Recursively snapshot contained subdirectories and files (capped for safety)
				int capturedInDir = 0;
				foreach( string subDir in Directory.EnumerateDirectories( fullPath, "*", SearchOption.AllDirectories ) )
				{
					if( trackedPaths.Add( subDir ) )
					{
						entries.Add( new McpRestoreFileEntry(
							OriginalPath: subDir,
							ExistedBefore: true,
							IsDirectory: true,
							SizeBytes: 0,
							Sha1: null,
							Attributes: File.GetAttributes( subDir ),
							LastWriteTimeUtc: Directory.GetLastWriteTimeUtc( subDir ),
							BackupFileName: null ) );
					}
				}
				foreach( string subFile in Directory.EnumerateFiles( fullPath, "*", SearchOption.AllDirectories ) )
				{
					cancellationToken.ThrowIfCancellationRequested();
					if( capturedInDir >= MaxFilesPerDirectorySnapshot )
						break;
					await CaptureFileAsync( subFile );
					capturedInDir++;
				}
			}
			else
			{
				// Path did not exist before mutation — restoring will remove whatever was created at this path
				bool looksLikeDir = fullPath.EndsWith( Path.DirectorySeparatorChar ) || fullPath.EndsWith( Path.AltDirectorySeparatorChar );
				entries.Add( new McpRestoreFileEntry(
					OriginalPath: fullPath,
					ExistedBefore: false,
					IsDirectory: looksLikeDir,
					SizeBytes: 0,
					Sha1: null,
					Attributes: FileAttributes.Normal,
					LastWriteTimeUtc: null,
					BackupFileName: null ) );
			}
		}

		foreach( string target in uniqueTargets )
			await CaptureFileAsync( target );

		McpRestorePoint manifest = new(
			Id: id,
			Label: string.IsNullOrWhiteSpace( label ) ? ( triggerTool ?? "Manual Checkpoint" ) : label.Trim(),
			Description: description,
			TriggerTool: triggerTool,
			Automatic: automatic,
			CreatedAtUtc: DateTimeOffset.UtcNow,
			RestoredAtUtc: null,
			TotalBackupBytes: totalBackupBytes,
			Entries: entries );

		WriteManifest( rpDir, manifest );
		if( automatic )
			PruneAutomaticOverflow();

		CurrentCallRestorePointId ??= manifest.Id;
		NotifyChanged();
		return manifest;
	}

	public IReadOnlyList<McpRestorePoint> ListRestorePoints( int limit = 100 )
	{
		int cap = Math.Clamp( limit, 1, 500 );
		List<McpRestorePoint> all = [];
		lock( _gate )
		{
			if( !Directory.Exists( StorageDirectory ) )
				return all;

			foreach( string dir in Directory.EnumerateDirectories( StorageDirectory ) )
			{
				McpRestorePoint? rp = TryReadManifest( dir );
				if( rp is not null )
					all.Add( rp );
			}
		}

		return all.OrderByDescending( r => r.CreatedAtUtc ).Take( cap ).ToList();
	}

	public McpRestorePoint? GetRestorePoint( string id )
	{
		if( string.IsNullOrWhiteSpace( id ) )
			return null;
		string rpDir = ResolvePointDir( id );
		lock( _gate )
			return TryReadManifest( rpDir );
	}

	/// <summary>Restores all files/directories tracked by the specified restore point.</summary>
	public async Task<McpRestoreResult> RestoreAsync(
		string id,
		bool createSafetyBackup = true,
		bool dryRun = false,
		CancellationToken cancellationToken = default )
	{
		McpRestorePoint rp = GetRestorePoint( id )
			?? throw new FileNotFoundException( $"Restore point '{id}' was not found." );

		string rpDir = ResolvePointDir( id );
		string filesDir = Path.Combine( rpDir, "files" );

		string? safetyBackupId = null;
		if( !dryRun && createSafetyBackup )
		{
			List<string> pathsToGuard = [ .. rp.Entries.Select( e => e.OriginalPath ) ];
			McpRestorePoint safety = await CreateRestorePointAsync(
				label: $"Pre-restore backup (before restoring {rp.Id})",
				description: $"Automatic safety backup taken right before restoring '{rp.Label}' ({rp.Id}).",
				paths: pathsToGuard,
				automatic: true,
				triggerTool: "mcp_restore_point_restore",
				cancellationToken: cancellationToken );
			safetyBackupId = safety.Id;
		}

		int restoredFiles = 0;
		int removedCreatedFiles = 0;
		int createdDirs = 0;
		int removedCreatedDirs = 0;
		List<object> actions = [];
		List<string> warnings = [];

		// 1. Recreate directories that existed before (shortest paths first)
		foreach( McpRestoreFileEntry entry in rp.Entries.Where( e => e.IsDirectory && e.ExistedBefore ).OrderBy( e => e.OriginalPath.Length ) )
		{
			if( !Directory.Exists( entry.OriginalPath ) )
			{
				if( !dryRun )
					Directory.CreateDirectory( entry.OriginalPath );
				createdDirs++;
				actions.Add( new { action = "recreate_directory", path = entry.OriginalPath } );
			}
		}

		// 2. Restore files that existed before
		foreach( McpRestoreFileEntry entry in rp.Entries.Where( e => !e.IsDirectory && e.ExistedBefore ) )
		{
			cancellationToken.ThrowIfCancellationRequested();
			if( string.IsNullOrEmpty( entry.BackupFileName ) )
			{
				warnings.Add( $"Skipped '{entry.OriginalPath}': file exceeded backup size limit and has no snapshot payload." );
				continue;
			}
			string backupPath = Path.Combine( filesDir, entry.BackupFileName );
			if( !File.Exists( backupPath ) )
			{
				warnings.Add( $"Backup payload missing for '{entry.OriginalPath}' ({entry.BackupFileName})." );
				continue;
			}

			if( !dryRun )
			{
				if( Path.GetDirectoryName( entry.OriginalPath ) is { Length: > 0 } parentDir )
					Directory.CreateDirectory( parentDir );

				if( File.Exists( entry.OriginalPath ) )
				{
					FileInfo existing = new( entry.OriginalPath );
					if( existing.IsReadOnly )
						existing.Attributes &= ~FileAttributes.ReadOnly;
				}

				await CopyFileAsync( backupPath, entry.OriginalPath, cancellationToken );
				if( entry.LastWriteTimeUtc is DateTime mtime )
				{
					try { File.SetLastWriteTimeUtc( entry.OriginalPath, mtime ); } catch { }
				}
				try
				{
					if( ( entry.Attributes & FileAttributes.ReadOnly ) != 0 )
						File.SetAttributes( entry.OriginalPath, File.GetAttributes( entry.OriginalPath ) | FileAttributes.ReadOnly );
				}
				catch { }
			}

			restoredFiles++;
			actions.Add( new { action = "restore_file", path = entry.OriginalPath, sizeBytes = entry.SizeBytes, sha1 = entry.Sha1 } );
		}

		// 3. Remove files & directories that did NOT exist before (deepest paths first)
		foreach( McpRestoreFileEntry entry in rp.Entries.Where( e => !e.ExistedBefore ).OrderByDescending( e => e.OriginalPath.Length ) )
		{
			cancellationToken.ThrowIfCancellationRequested();
			if( File.Exists( entry.OriginalPath ) )
			{
				if( !dryRun )
				{
					FileInfo f = new( entry.OriginalPath );
					if( f.IsReadOnly )
						f.Attributes &= ~FileAttributes.ReadOnly;
					f.Delete();
				}
				removedCreatedFiles++;
				actions.Add( new { action = "remove_created_file", path = entry.OriginalPath } );
			}
			else if( Directory.Exists( entry.OriginalPath ) )
			{
				if( !dryRun )
				{
					ClearReadOnlyRecursive( entry.OriginalPath );
					Directory.Delete( entry.OriginalPath, recursive: true );
				}
				removedCreatedDirs++;
				actions.Add( new { action = "remove_created_directory", path = entry.OriginalPath } );
			}
		}

		if( !dryRun )
		{
			McpRestorePoint updated = rp with { RestoredAtUtc = DateTimeOffset.UtcNow };
			WriteManifest( rpDir, updated );
			NotifyChanged();
		}

		return new McpRestoreResult(
			Ok: warnings.Count == 0,
			RestorePointId: rp.Id,
			Label: rp.Label,
			DryRun: dryRun,
			SafetyBackupRestorePointId: safetyBackupId,
			RestoredFiles: restoredFiles,
			RemovedCreatedFiles: removedCreatedFiles,
			CreatedDirectories: createdDirs,
			RemovedCreatedDirectories: removedCreatedDirs,
			Actions: actions,
			Warnings: warnings );
	}

	public bool DeleteRestorePoint( string id )
	{
		if( string.IsNullOrWhiteSpace( id ) )
			return false;
		string rpDir = ResolvePointDir( id );
		lock( _gate )
		{
			if( !Directory.Exists( rpDir ) )
				return false;
			ClearReadOnlyRecursive( rpDir );
			Directory.Delete( rpDir, recursive: true );
		}
		NotifyChanged();
		return true;
	}

	private void PruneAutomaticOverflow()
	{
		int max = MaxRestorePoints;
		List<McpRestorePoint> all = [ .. ListRestorePoints( 500 ) ];
		List<McpRestorePoint> automatic = all.Where( r => r.Automatic ).OrderByDescending( r => r.CreatedAtUtc ).ToList();
		if( automatic.Count <= max )
			return;

		foreach( McpRestorePoint old in automatic.Skip( max ) )
		{
			try
			{
				string dir = ResolvePointDir( old.Id );
				if( Directory.Exists( dir ) )
					Directory.Delete( dir, recursive: true );
			}
			catch
			{
				// Best effort pruning
			}
		}
	}

	private string ResolvePointDir( string id )
	{
		string safeId = Path.GetFileName( id.Trim() );
		if( string.IsNullOrEmpty( safeId ) || safeId.Contains( ".." ) )
			throw new ArgumentException( $"Invalid restore point ID '{id}'." );
		return Path.Combine( StorageDirectory, safeId );
	}

	private void WriteManifest( string rpDir, McpRestorePoint manifest )
	{
		lock( _gate )
		{
			string path = Path.Combine( rpDir, "manifest.json" );
			string temp = path + ".tmp";
			File.WriteAllText( temp, JsonSerializer.Serialize( manifest, McpJson.IndentedOptions ), Encoding.UTF8 );
			File.Move( temp, path, overwrite: true );
		}
	}

	private static McpRestorePoint? TryReadManifest( string rpDir )
	{
		try
		{
			string path = Path.Combine( rpDir, "manifest.json" );
			if( !File.Exists( path ) )
				return null;
			return JsonSerializer.Deserialize<McpRestorePoint>( File.ReadAllText( path, Encoding.UTF8 ), McpJson.Options );
		}
		catch
		{
			return null;
		}
	}

	private static async Task CopyFileAsync( string source, string destination, CancellationToken ct )
	{
		await using FileStream src = new( source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81920, useAsync: true );
		await using FileStream dst = new( destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true );
		await src.CopyToAsync( dst, ct );
	}

	private static void ClearReadOnlyRecursive( string directoryPath )
	{
		foreach( string f in Directory.EnumerateFiles( directoryPath, "*", SearchOption.AllDirectories ) )
		{
			try
			{
				FileInfo fi = new( f );
				if( fi.IsReadOnly )
					fi.Attributes &= ~FileAttributes.ReadOnly;
			}
			catch { }
		}
	}

	private void NotifyChanged()
	{
		try { Changed?.Invoke(); } catch { }
	}
}

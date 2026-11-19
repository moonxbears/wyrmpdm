using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

using HackPDM.Core;
using HackPDM.Core.Configuration;
using HackPDM.Core.Graph;
using HackPDM.Core.Hack;
using HackPDM.Core.Logging;
using HackPDM.Domain.OdooModels;
using HackPDM.Domain.OdooModels.Models;
using HackPDM.Infrastructure.Odoo;
using HackPDM.Infrastructure.Odoo.Models;
using HackPDM.Infrastructure.Operations;
using HackPDM.Infrastructure.SldWrks;
using HackPDM.Mcp.Bridge;
using HackPDM.Mcp.State;
using HackPDM.Shared.GlobalData;

namespace HackPDM.Mcp.Services;

/// <summary>Captures structured logs and progress counters emitted by PDM operations.</summary>
public sealed class McpOperationLog( IHackPdmUiBridge? uiBridge = null )
{
	private readonly Lock _gate = new();
	private readonly List<object> _entries = [];
	private long _downloadedBytes;
	private long _uploadedBytes;

	public long DownloadedBytes => Interlocked.Read( ref _downloadedBytes );
	public long UploadedBytes => Interlocked.Read( ref _uploadedBytes );

	public void Add( StatusMessage status, string message )
	{
		lock( _gate )
		{
			_entries.Add( new
			{
				timestampUtc = DateTimeOffset.UtcNow.ToString( "O" ),
				status = status.ToString(),
				message,
			} );
		}
		uiBridge?.MirrorStatusLog( status, message );
	}

	public void AddDownloadedBytes( long bytes )
	{
		if( bytes > 0 )
			Interlocked.Add( ref _downloadedBytes, bytes );
	}

	public void AddUploadedBytes( long bytes )
	{
		if( bytes > 0 )
			Interlocked.Add( ref _uploadedBytes, bytes );
	}

	public IReadOnlyList<object> Snapshot()
	{
		lock( _gate )
			return [ .. _entries ];
	}
}

/// <summary>
/// Headless-capable and live-GUI-synchronized service facade covering every HackPDM client API:
/// sessions, lookups, directory tree, entries, details, previews, dependency graph, search,
/// generic Odoo ORM/RPC, settings, AppConfig, and diagnostics.
/// </summary>
public sealed partial class HackPdmApiService
{
	private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
		? StringComparer.OrdinalIgnoreCase
		: StringComparer.Ordinal;

	private static readonly string[] NoBlobs = [ "preview_image", "file_contents" ];

	public IHackPdmUiBridge? UiBridge { get; }
	public CoreSettings? SettingsStore { get; }
	public McpRestorePointManager? RestorePoints { get; set; }

	public HackPdmApiService(
		IHackPdmUiBridge? uiBridge = null,
		CoreSettings? settingsStore = null,
		McpRestorePointManager? restorePoints = null )
	{
		UiBridge = uiBridge;
		SettingsStore = settingsStore;
		RestorePoints = restorePoints;
	}

	/// <summary>Ensures <see cref="HackDefaults"/> and <see cref="OdooDefaults"/> are backed by <c>settings.json</c> when running standalone.</summary>
	public static CoreSettings EnsureHeadlessDefaultsInitialized( string? settingsPathOverride = null )
	{
		string path = settingsPathOverride ?? Path.Combine( AppDataPaths.Config, "settings.json" );
		JsonFileSettingsProvider provider = new( path );
		CoreSettings coreSettings = new( provider );
		_ = new HackDefaults( coreSettings );
		_ = new OdooDefaults( coreSettings );
		if( coreSettings.Get<string>( "TemporaryPath" ) is { Length: > 0 } tempPath )
			StorageBox.TemporaryPath = tempPath;
		return coreSettings;
	}

	public string SettingsFilePath
	{
		get
		{
			if( SettingsStore?.Provider is JsonFileSettingsProvider p )
				return p.FilePath;
			if( HackDefaults.Instance.SettingsProvider is JsonFileSettingsProvider hp )
				return hp.FilePath;
			if( HackDefaults.Instance.SettingsProvider is CoreSettings { Provider: JsonFileSettingsProvider cp } )
				return cp.FilePath;
			return Path.Combine( AppDataPaths.Config, "settings.json" );
		}
	}

	public string AppConfigFilePath => Path.Combine(
		AppDataPaths.IsConfigOverridden ? AppDataPaths.Config : Environment.GetFolderPath( Environment.SpecialFolder.Personal ),
		"AppConfig.json" );

	// ------------------------------------------------------------------ Session & Connection

	public object GetSessionInfo()
	{
		IOdooDefaults odoo = OdooDefaults.Instance;
		var hack = HackDefaults.Instance;
		bool hasOdooSettings = odoo.Settings is not null;
		bool hasHackSettings = hack.SettingsProvider is not null;
		string? pwaAbs = hasHackSettings ? hack.PwaPathAbsolute : null;
		string? pwaRel = hasHackSettings ? hack.PwaPathRelative : null;

		return new
		{
			isLoggedIn = odoo.OdooId > 0,
			odooId = odoo.OdooId,
			loginError = odoo.LoginError,
			odooAddress = hasOdooSettings ? odoo.OdooAddress : null,
			odooPort = hasOdooSettings ? odoo.OdooPort : null,
			odooUseHttps = hasOdooSettings ? odoo.OdooUseHttps : (bool?)null,
			odooUrl = hasOdooSettings ? odoo.OdooUrl : null,
			odooDb = hasOdooSettings ? odoo.OdooDb : null,
			odooUser = hasOdooSettings ? odoo.OdooUser : null,
			hasPasswordConfigured = hasOdooSettings && !string.IsNullOrEmpty( odoo.OdooPass ),
			odooCredentialTarget = hasOdooSettings ? odoo.OdooCredentialTarget : null,
			odooAreaFactor = hasOdooSettings ? odoo.OdooAreaFactor : null,
			hasSwLicenseKey = ( hasOdooSettings && !string.IsNullOrEmpty( odoo.OdooSwKey ) ) || !string.IsNullOrEmpty( odoo.SwApi ),
			myNode = odoo.MyNode is null ? null : new { id = odoo.MyNode.id, name = odoo.MyNode.name },
			rootDirectory = odoo.HpDirectoryRoot is null ? null : new { id = odoo.HpDirectoryRoot.id, name = odoo.HpDirectoryRoot.name },
			pwaPathAbsolute = pwaAbs,
			pwaPathRelative = pwaRel,
			pwaExists = !string.IsNullOrEmpty( pwaAbs ) && Directory.Exists( pwaAbs ),
			temporaryPath = StorageBox.TemporaryPath,
			clientVersion = ClientUpdate.CurrentText,
			configDirectory = AppDataPaths.Config,
			dataDirectory = AppDataPaths.Data,
			settingsFile = SettingsFilePath,
			lookupsLoaded = odoo.HpTypes is not null || odoo.HpUsers is not null,
		};
	}

	public object ConfigureSession(
		string? address,
		string? port,
		string? db,
		string? user,
		string? password,
		bool? useHttps,
		string? explicitUrl,
		string? swKey,
		decimal? areaFactor,
		string? credentialTarget,
		string? pwaPath,
		string? tempPath,
		bool saveCredentials,
		bool createLocalFolders )
	{
		IOdooDefaults odoo = OdooDefaults.Instance;
		if( odoo.Settings is null && SettingsStore is not null )
			odoo.Settings = SettingsStore;
		if( HackDefaults.Instance.SettingsProvider is null && SettingsStore is not null )
			HackDefaults.Instance.SettingsProvider = SettingsStore;
		bool authTargetChanged = false;

		if( useHttps is bool https )
		{
			if( odoo.OdooUseHttps != https )
				authTargetChanged = true;
			odoo.OdooUseHttps = https;
		}
		if( address is not null )
		{
			string trimmed = address.Trim();
			if( !string.Equals( odoo.OdooAddress, trimmed, StringComparison.OrdinalIgnoreCase ) )
				authTargetChanged = true;
			odoo.OdooAddress = trimmed;
		}
		if( port is not null )
		{
			string trimmed = port.Trim();
			if( !string.Equals( odoo.OdooPort, trimmed, StringComparison.Ordinal ) )
				authTargetChanged = true;
			odoo.OdooPort = trimmed;
		}
		if( db is not null )
		{
			string trimmed = db.Trim();
			if( !string.Equals( odoo.OdooDb, trimmed, StringComparison.Ordinal ) )
				authTargetChanged = true;
			odoo.OdooDb = trimmed;
		}
		if( user is not null )
		{
			string trimmed = user.Trim();
			if( !string.Equals( odoo.OdooUser, trimmed, StringComparison.Ordinal ) )
				authTargetChanged = true;
			odoo.OdooUser = trimmed;
		}
		if( password is not null )
		{
			if( !string.Equals( odoo.OdooPass, password, StringComparison.Ordinal ) )
				authTargetChanged = true;
			odoo.OdooPass = password;
		}
		if( explicitUrl is not null )
		{
			odoo.OdooUrl = explicitUrl.Trim();
			authTargetChanged = true;
		}
		else if( address is not null || port is not null || useHttps is not null )
		{
			odoo.OdooUrl = OdooEndpoint.BuildUrl( odoo.OdooAddress, odoo.OdooPort, odoo.OdooUseHttps );
		}
		if( swKey is not null )
			odoo.OdooSwKey = swKey.Trim();
		if( areaFactor is decimal af )
			odoo.OdooAreaFactor = af;
		if( credentialTarget is not null )
			odoo.OdooCredentialTarget = credentialTarget.Trim();

		if( pwaPath is not null )
		{
			string trimmedPwa = pwaPath.Trim();
			if( createLocalFolders && trimmedPwa.Length > 0 )
				Directory.CreateDirectory( trimmedPwa );
			HackDefaults.Instance.PwaPathAbsolute = trimmedPwa;
		}
		if( tempPath is not null )
		{
			string trimmedTemp = tempPath.Trim();
			if( createLocalFolders && trimmedTemp.Length > 0 )
				Directory.CreateDirectory( trimmedTemp );
			StorageBox.TemporaryPath = trimmedTemp;
			HackDefaults.Instance.SettingsProvider?.Set( "TemporaryPath", trimmedTemp );
		}

		if( authTargetChanged )
		{
			odoo.OdooId = 0;
			UiBridge?.NotifyLoginChanged( false, null );
		}

		bool credsSaved = false;
		if( saveCredentials && odoo is OdooDefaults concrete )
			credsSaved = concrete.SaveCredentials();

		return new
		{
			configured = true,
			sessionReset = authTargetChanged,
			credentialsSaved = credsSaved,
			session = GetSessionInfo(),
		};
	}

	public async Task<object> TestConnectionAsync( CancellationToken cancellationToken )
	{
		IOdooDefaults odoo = OdooDefaults.Instance;
		string host = odoo.OdooAddress?.Trim() ?? string.Empty;
		string portText = odoo.OdooPort?.Trim() ?? string.Empty;
		bool useHttps = odoo.OdooUseHttps;
		string url = !string.IsNullOrEmpty( odoo.OdooUrl )
			? odoo.OdooUrl!
			: OdooEndpoint.BuildUrl( host, portText, useHttps );

		var endpoint = OdooConnection.ResolveEndpoint( host, portText, useHttps );
		if( string.IsNullOrEmpty( host ) || endpoint is not var (resolvedHost, resolvedPort) )
		{
			return new
			{
				ok = false,
				stage = "configuration",
				error = "Odoo server address or port is missing or invalid.",
				url,
			};
		}

		if( OdooEndpoint.InsecureReason( url, allowPlainHttp: !useHttps ) is { } insecure )
		{
			return new
			{
				ok = false,
				stage = "tls_policy",
				error = insecure,
				url,
				resolvedHost,
				resolvedPort,
			};
		}

		bool hostReachable = await OdooConnection.CorrectOdooAddress();
		if( !hostReachable )
		{
			return new
			{
				ok = false,
				stage = "host_resolution",
				error = $"Host '{resolvedHost}' is unreachable or could not be resolved.",
				url,
				resolvedHost,
				resolvedPort,
			};
		}

		bool portOpen = await OdooConnection.CorrectOdooPort();
		if( !portOpen )
		{
			return new
			{
				ok = false,
				stage = "tcp_connect",
				error = $"TCP port {resolvedPort} on '{resolvedHost}' refused the connection or timed out.",
				url,
				resolvedHost,
				resolvedPort,
			};
		}

		Stopwatch sw = Stopwatch.StartNew();
		Result<JsonElement> versionProbe = await OdooRpc.CallAsync( "common", "version", [], timeoutMs: 5000, token: cancellationToken );
		sw.Stop();

		return new
		{
			ok = versionProbe.Ok,
			stage = "jsonrpc_version",
			url,
			resolvedHost,
			resolvedPort,
			latencyMs = sw.ElapsedMilliseconds,
			serverInfo = versionProbe.Ok ? OdooValues.FromJson( versionProbe.Value ) : null,
			error = versionProbe.Error,
		};
	}

	public async Task<object> LoginAsync( bool forceRelogin, bool saveCredentials, bool checkClientVersion, CancellationToken cancellationToken )
	{
		IOdooDefaults odoo = OdooDefaults.Instance;
		if( forceRelogin )
			odoo.OdooId = 0;

		List<string> stages = [];
		Progress<string> progress = new( msg => stages.Add( msg ) );

		try
		{
			if( odoo is OdooDefaults concrete )
				await concrete.InitializeAsync( progress );
			else
				await odoo.InitializeAsync();
		}
		catch( Exception ex )
		{
			UiBridge?.NotifyLoginChanged( false, null );
			return new
			{
				ok = false,
				error = ex.Message,
				loginError = odoo.LoginError,
				stages,
			};
		}

		string? requiredVersion = null;
		VersionCheckResult versionCheck = new( true, null );
		if( checkClientVersion )
		{
			requiredVersion = odoo.HpSettings?
				.FirstOrDefault( s => s.name == OdooDefaultsConstants.ODOO_VERSION_KEY_NAME )?.char_value;
			versionCheck = ClientUpdate.CheckVersion( ClientUpdate.Current, requiredVersion );
			if( !versionCheck.IsAllowed )
			{
				odoo.OdooId = 0;
				UiBridge?.NotifyLoginChanged( false, null );
				return new
				{
					ok = false,
					error = versionCheck.Reason,
					clientVersion = ClientUpdate.CurrentText,
					requiredVersion,
					stages,
				};
			}
		}

		bool credsSaved = false;
		if( saveCredentials && odoo is OdooDefaults concreteOdoo )
			credsSaved = concreteOdoo.SaveCredentials();

		string accountKey = $"{odoo.OdooUrl}|{odoo.OdooDb}|{odoo.OdooUser}";
		UiBridge?.NotifyLoginChanged( true, accountKey );

		return new
		{
			ok = true,
			odooId = odoo.OdooId,
			odooUser = odoo.OdooUser,
			odooDb = odoo.OdooDb,
			odooUrl = odoo.OdooUrl,
			myNode = odoo.MyNode is null ? null : new { id = odoo.MyNode.id, name = odoo.MyNode.name },
			rootDirectory = odoo.HpDirectoryRoot is null ? null : new { id = odoo.HpDirectoryRoot.id, name = odoo.HpDirectoryRoot.name },
			clientVersion = ClientUpdate.CurrentText,
			requiredVersion,
			versionAllowed = versionCheck.IsAllowed,
			credentialsSaved = credsSaved,
			lookupCounts = new
			{
				users = odoo.HpUsers?.Length ?? 0,
				nodes = odoo.HpNodes?.Length ?? 0,
				types = odoo.HpTypes?.Length ?? 0,
				categories = odoo.HpCategories?.Length ?? 0,
				properties = odoo.HpProperties?.Length ?? 0,
				filters = odoo.HpEntryNameFilters?.Length ?? 0,
				settings = odoo.HpSettings?.Length ?? 0,
			},
			stages,
		};
	}

	public object Logout()
	{
		OdooDefaults.Instance.OdooId = 0;
		if( OdooDefaults.Instance is OdooDefaults concrete )
			concrete.Lookups = OdooLookupCache.Empty;
		UiBridge?.NotifyLoginChanged( false, null );
		return new { ok = true, isLoggedIn = false };
	}

	public async Task<object> CheckPermissionsAsync( CancellationToken cancellationToken )
	{
		EnsureLoggedIn();
		Result<bool> adminResult = await OdooRpc.ExecuteAsync<bool>( "res.users", "has_group", [ "hackpdm.group_pdm_admin" ], token: cancellationToken );
		string? requiredVersion = OdooDefaults.Instance.HpSettings?
			.FirstOrDefault( s => s.name == OdooDefaultsConstants.ODOO_VERSION_KEY_NAME )?.char_value;
		VersionCheckResult versionCheck = ClientUpdate.CheckVersion( ClientUpdate.Current, requiredVersion );
		return new
		{
			odooId = OdooDefaults.Instance.OdooId,
			odooUser = OdooDefaults.Instance.OdooUser,
			isPdmAdmin = adminResult.ValueOr( false ),
			adminCheckError = adminResult.Error,
			clientVersion = ClientUpdate.CurrentText,
			requiredVersion,
			versionAllowed = versionCheck.IsAllowed,
			versionReason = versionCheck.Reason,
		};
	}

	public async Task<object> ReloadLookupsAsync()
	{
		EnsureLoggedIn();
		OdooLookupCache cache = await OdooLookupCache.LoadAsync();
		if( OdooDefaults.Instance is OdooDefaults concrete )
			concrete.Lookups = cache;
		return GetLookups( "all" );
	}

	// ------------------------------------------------------------------ Profiles

	public object GetCurrentProfile( bool includePassword )
	{
		IOdooDefaults odoo = OdooDefaults.Instance;
		return new
		{
			OdooAddress = odoo.OdooAddress ?? "",
			OdooPort = odoo.OdooPort ?? "",
			OdooUseHttps = odoo.OdooUseHttps,
			OdooDb = odoo.OdooDb ?? "",
			OdooCredentialTarget = odoo.OdooCredentialTarget ?? "",
			PWAPathAbsolute = HackDefaults.Instance.PwaPathAbsolute ?? "",
			Username = odoo.OdooUser ?? "",
			Password = includePassword ? odoo.OdooPass ?? "" : null,
		};
	}

	public async Task<object> LoadProfileAsync( string path, CancellationToken cancellationToken )
	{
		string fullPath = Path.GetFullPath( path );
		string json = await File.ReadAllTextAsync( fullPath, Encoding.UTF8, cancellationToken );
		using JsonDocument doc = JsonDocument.Parse( json );
		JsonElement root = doc.RootElement;
		if( root.ValueKind != JsonValueKind.Object )
			throw new InvalidOperationException( "Profile file must contain a JSON object." );

		string? ReadStr( string name ) => root.TryGetProperty( name, out JsonElement el ) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;
		bool? ReadBool( string name ) => root.TryGetProperty( name, out JsonElement el ) && el.ValueKind is JsonValueKind.True or JsonValueKind.False ? el.GetBoolean() : null;

		object configured = ConfigureSession(
			address: ReadStr( "OdooAddress" ),
			port: ReadStr( "OdooPort" ),
			db: ReadStr( "OdooDb" ),
			user: ReadStr( "Username" ),
			password: ReadStr( "Password" ),
			useHttps: ReadBool( "OdooUseHttps" ),
			explicitUrl: null,
			swKey: null,
			areaFactor: null,
			credentialTarget: ReadStr( "OdooCredentialTarget" ),
			pwaPath: ReadStr( "PWAPathAbsolute" ),
			tempPath: null,
			saveCredentials: false,
			createLocalFolders: false );

		return new { loadedFrom = fullPath, result = configured };
	}

	public async Task<object> SaveProfileAsync( string path, bool includePassword, bool template, CancellationToken cancellationToken )
	{
		string fullPath = Path.GetFullPath( path );
		if( Path.GetDirectoryName( fullPath ) is { Length: > 0 } dir )
			Directory.CreateDirectory( dir );

		object payload = template
			? new
			{
				OdooAddress = "10.0.0.68",
				OdooCredentialTarget = StorageBox.DEFAULT_ODOO_CREDENTIALS,
				OdooDb = "odoopdm",
				OdooPort = "443",
				OdooUseHttps = true,
				PWAPathAbsolute = @"C:\path\to\pwa",
				Username = "{username}",
				Password = "",
			}
			: GetCurrentProfile( includePassword );

		string json = JsonSerializer.Serialize( payload, new JsonSerializerOptions { WriteIndented = true } );
		await File.WriteAllTextAsync( fullPath, json, Encoding.UTF8, cancellationToken );
		return new { savedTo = fullPath, includePassword, template };
	}

	// ------------------------------------------------------------------ Metadata & Lookups

	public object GetLookups( string category )
	{
		IOdooDefaults odoo = OdooDefaults.Instance;
		string cat = string.IsNullOrWhiteSpace( category ) ? "all" : category.Trim().ToLowerInvariant();

		object? Users() => odoo.HpUsers?.Select( u => new { id = u.id, name = u.name, login = u.login } ).ToList();
		object? Nodes() => odoo.HpNodes?.Select( n => new { id = n.id, name = n.name } ).ToList();
		object? Types() => odoo.HpTypes?.Select( t => new
		{
			id = t.id,
			fileExt = t.file_ext,
			category = OdooJsonHelper.SerializeMany2One( t.cat_id ),
			typeRegex = t.type_regex,
			description = t.description,
		} ).ToList();
		object? Categories() => odoo.HpCategories?.Select( c => new
		{
			id = c.id,
			name = c.name,
			description = c.cat_description,
			trackDepends = c.track_depends,
			trackVersion = c.track_version,
		} ).ToList();
		object? Properties() => odoo.HpProperties?.Select( p => new
		{
			id = p.id,
			name = p.name,
			propType = p.prop_type,
		} ).ToList();
		object? Filters() => odoo.HpEntryNameFilters?.Select( f => new
		{
			id = f.id,
			nameProto = f.name_proto,
			nameRegex = f.name_regex,
			description = f.description,
		} ).ToList();
		object? Settings() => odoo.HpSettings?.Select( s => new
		{
			id = s.id,
			name = s.name,
			boolValue = s.bool_value,
			intValue = s.int_value,
			floatValue = s.float_value,
			charValue = s.name == OdooDefaultsConstants.SW_KEY_NAME && !string.IsNullOrEmpty( s.char_value ) ? "[REDACTED]" : s.char_value,
		} ).ToList();
		object? Root() => odoo.HpDirectoryRoot is null ? null : new { id = odoo.HpDirectoryRoot.id, name = odoo.HpDirectoryRoot.name };

		return cat switch
		{
			"users" => new { users = Users() },
			"nodes" => new { myNode = odoo.MyNode is null ? null : new { id = odoo.MyNode.id, name = odoo.MyNode.name }, nodes = Nodes() },
			"types" => new { restrictTypes = odoo.RestrictTypes, types = Types() },
			"categories" => new { categories = Categories() },
			"properties" => new { restrictProperties = odoo.RestrictProperties, properties = Properties() },
			"filters" => new { entryFilterPatterns = odoo.EntryFilterPatterns, filters = Filters() },
			"settings" => new { settings = Settings() },
			"root" => new { rootDirectory = Root() },
			_ => new
			{
				myNode = odoo.MyNode is null ? null : new { id = odoo.MyNode.id, name = odoo.MyNode.name },
				rootDirectory = Root(),
				restrictTypes = odoo.RestrictTypes,
				restrictProperties = odoo.RestrictProperties,
				downloadBatchSize = odoo.DownloadBatchSize,
				concurrencySize = odoo.ConcurrencySize,
				maxBatchSize = odoo.MaxBatchSize,
				maxConcurrency = odoo.MaxConcurrency,
				entryFilterPatterns = odoo.EntryFilterPatterns,
				users = Users(),
				nodes = Nodes(),
				types = Types(),
				categories = Categories(),
				properties = Properties(),
				filters = Filters(),
				settings = Settings(),
			},
		};
	}

	// ------------------------------------------------------------------ Directory Tree & Entries

	public async Task<object> GetDirectoryTreeAsync(
		string source,
		string? filter,
		int maxDepth,
		bool withEntries,
		int? rootDirectoryId,
		CancellationToken cancellationToken )
	{
		string mode = string.IsNullOrWhiteSpace( source ) ? "merged" : source.Trim().ToLowerInvariant();
		int effectiveMaxDepth = maxDepth > 0 ? maxDepth : int.MaxValue;

		if( mode == "local" )
		{
			string pwa = HackDefaults.Instance.PwaPathAbsolute;
			DirectoryNode localRoot = new( PwaPaths.Root, 0 ) { ExistsLocally = !string.IsNullOrEmpty( pwa ) && Directory.Exists( pwa ) };
			DirectoryTreeMerger.AddLocalDirectories( localRoot, ScanLocalTreePaths( pwa ) );
			localRoot.SortRecursive();
			DirectoryNode? filtered = string.IsNullOrWhiteSpace( filter ) ? localRoot : localRoot.Filter( filter );
			return new
			{
				source = "local",
				pwaPath = pwa,
				totalFolders = localRoot.CountRecursive(),
				matchedFolders = filtered?.CountRecursive() ?? 0,
				tree = filtered is null ? null : OdooJsonHelper.SerializeDirectoryNode( filtered, null, 0, effectiveMaxDepth ),
			};
		}

		EnsureLoggedIn();
		int dirId = rootDirectoryId ?? OdooDefaults.Instance.HpDirectoryRoot?.id ?? 1;
		HpDirectory? rootDir = dirId == OdooDefaults.Instance.HpDirectoryRoot?.id && OdooDefaults.Instance.HpDirectoryRoot is HpDirectory cachedRoot
			? cachedRoot
			: await HpDirectory.GetRecordByIdAsync( dirId );
		if( rootDir is null )
			throw new InvalidOperationException( $"Directory id {dirId} could not be loaded from Odoo." );

		Result<SubdirectoryDto> remoteResult = await rootDir.GetSubdirectoriesAsync( withEntries );
		cancellationToken.ThrowIfCancellationRequested();
		if( !remoteResult.Ok || remoteResult.Value is not { } remoteDto )
			throw new InvalidOperationException( $"Directory tree could not be read: {remoteResult.Error ?? "no data"}" );

		if( mode == "remote" || withEntries )
		{
			return new
			{
				source = "remote",
				rootDirectoryId = dirId,
				withEntries,
				tree = OdooJsonHelper.SerializeSubdirectoryDto( remoteDto, null, 0, effectiveMaxDepth, includeEntries: withEntries ),
			};
		}

		string vaultPath = HackDefaults.Instance.PwaPathAbsolute;
		HashSet<string> localPaths = ScanLocalTreePaths( vaultPath );
		HashSet<string> remotePaths = new( PathComparer );
		DirectoryNode merged = BuildMergedNode( remoteDto, null, localPaths, remotePaths );
		DirectoryTreeMerger.AddLocalDirectories( merged, localPaths.Where( p => !remotePaths.Contains( p ) ) );
		merged.SortRecursive();

		DirectoryNode? display = string.IsNullOrWhiteSpace( filter ) ? merged : merged.Filter( filter );
		return new
		{
			source = "merged",
			rootDirectoryId = dirId,
			pwaPath = vaultPath,
			totalFolders = merged.CountRecursive(),
			matchedFolders = display?.CountRecursive() ?? 0,
			tree = display is null ? null : OdooJsonHelper.SerializeDirectoryNode( display, null, 0, effectiveMaxDepth ),
		};
	}

	public async Task<object> GetDirectoryEntriesAsync(
		int? directoryId,
		string? treePath,
		bool includeDeleted,
		bool filterUnknownTypes,
		string? nameFilter,
		string? statusFilter,
		string? sortBy,
		bool descending,
		CancellationToken cancellationToken )
	{
		// Resolve missing directoryId from treePath if logged in
		int? resolvedDirId = directoryId;
		string? resolvedTreePath = treePath;

		if( resolvedDirId is null && !string.IsNullOrWhiteSpace( resolvedTreePath ) && OdooDefaults.Instance.OdooId > 0 )
		{
			string[] segs = [ PwaPaths.Root, .. PwaPaths.Segments( resolvedTreePath ) ];
			Result<LastAvailableDirectory> last = await HpDirectory.LastAvailableDirectory( segs );
			if( last.Ok && last.Value.Index == segs.Length - 1 )
				resolvedDirId = last.Value.DirectoryId;
		}
		else if( resolvedDirId is int idForPath and > 0 && string.IsNullOrWhiteSpace( resolvedTreePath ) && OdooDefaults.Instance.OdooId > 0 )
		{
			Result<IReadOnlyList<OdooRow>> dirRows = await OdooRpc.ReadAsync( HpDirectory.GetHpModel()!, [ idForPath ], [ "complete_name", "name" ], token: cancellationToken );
			if( dirRows.Value is [ var row, .. ] && row.Text( "complete_name" ) is { Length: > 0 } completeName )
				resolvedTreePath = PwaPaths.ToServerName( PwaPaths.Segments( completeName ) );
		}

		string localFolder = PwaPaths.ToAbsolute( resolvedTreePath ) ?? PwaPaths.Vault;
		Dictionary<string, FileInfo> localFiles = ScanLocalFiles( localFolder );

		IReadOnlyList<RemoteEntryDto>? remote = null;
		string? remoteError = null;
		if( resolvedDirId is int dirId and > 0 )
		{
			EnsureLoggedIn();
			Result<IReadOnlyList<RemoteEntryDto>> remoteRes = await HpDirectory.GetEntriesAsync( dirId, includeDeleted );
			cancellationToken.ThrowIfCancellationRequested();
			if( !remoteRes.Ok )
				remoteError = remoteRes.Error;
			else
				remote = remoteRes.Value;
		}

		HashSet<string> matchedLocals = new( PathComparer );
		List<MergedEntryDto> rows = [];
		IOdooDefaults defaults = OdooDefaults.Instance;

		if( remote is { Count: > 0 } )
		{
			foreach( RemoteEntryDto entry in remote )
			{
				FileInfo? localInfo = null;
				if( entry.FullName is { } remotePath && PwaPaths.ToAbsolute( remotePath ) is { } candidate )
				{
					string fullLocal = Path.GetFullPath( candidate );
					if( localFiles.TryGetValue( fullLocal, out FileInfo? found ) )
					{
						localInfo = found;
						matchedLocals.Add( fullLocal );
					}
				}

				bool needsChecksum = localInfo is not null && !entry.Deleted && entry.CheckoutUserId is null or 0 && entry.LatestChecksum is string;
				string? localChecksum = needsChecksum ? Checksums.OfFile( localInfo!.FullName ) : null;
				IHpUserModel? checkoutUser = entry.CheckoutUserId is int uid ? defaults.IdToUser.GetValueOrDefault( uid ) : null;

				FileStatus status = EntryStatusResolver.ResolveRemote(
					deleted: entry.Deleted,
					checkoutUserId: checkoutUser?.id,
					myUserId: defaults.OdooId,
					latestChecksum: entry.LatestChecksum ?? (object)false,
					localChecksum: localChecksum,
					remoteDate: entry.LatestDate ?? default,
					localModified: localInfo?.LastWriteTime );

				rows.Add( new MergedEntryDto(
					Id: entry.Id,
					Name: entry.Name,
					Type: entry.Type,
					Category: entry.Category,
					Size: entry.Size,
					Status: status.ToString(),
					StatusCode: status.ToString().ToLowerInvariant(),
					ReprType: localInfo is not null ? EntryReprType.Both.ToString() : EntryReprType.Remote.ToString(),
					LatestVersionId: entry.LatestVersionId,
					LatestReleaseId: entry.LatestReleaseId,
					CheckoutUserId: checkoutUser?.id ?? entry.CheckoutUserId,
					CheckoutUserName: checkoutUser?.name,
					RemoteDate: entry.LatestDate,
					LocalDate: localInfo?.LastWriteTime,
					FullName: entry.FullName,
					LocalPath: localInfo?.FullName,
					Deleted: entry.Deleted,
					LocalReadOnly: localInfo?.IsReadOnly ) );
			}
		}

		int localOnlyCount = 0;
		foreach( var (fullPath, info) in localFiles )
		{
			if( matchedLocals.Contains( fullPath ) )
				continue;
			string ext = info.Extension.ToLowerInvariant();
			FileStatus status = EntryStatusResolver.ResolveLocal(
				restrictTypes: defaults.RestrictTypes is true,
				filtered: filterUnknownTypes,
				knownType: defaults.ExtToType.ContainsKey( ext ),
				ignored: defaults.ExtToFilter.ContainsKey( ext ) );

			IHpCategoryModel? cat = defaults.ExtToCat.GetValueOrDefault( ext );
			rows.Add( new MergedEntryDto(
				Id: null,
				Name: info.Name,
				Type: ext.TrimStart( '.' ),
				Category: cat?.name,
				Size: info.Length,
				Status: status.ToString(),
				StatusCode: status.ToString().ToLowerInvariant(),
				ReprType: EntryReprType.Local.ToString(),
				LatestVersionId: null,
				LatestReleaseId: null,
				CheckoutUserId: null,
				CheckoutUserName: null,
				RemoteDate: null,
				LocalDate: info.LastWriteTime,
				FullName: PwaPaths.ToServerName( fullPath ) ?? fullPath,
				LocalPath: fullPath,
				Deleted: false,
				LocalReadOnly: info.IsReadOnly ) );
			localOnlyCount++;
		}

		IEnumerable<MergedEntryDto> query = rows;
		if( !string.IsNullOrWhiteSpace( nameFilter ) )
			query = query.Where( r => r.Name.Contains( nameFilter, StringComparison.OrdinalIgnoreCase ) );
		if( !string.IsNullOrWhiteSpace( statusFilter ) )
			query = query.Where( r => string.Equals( r.Status, statusFilter, StringComparison.OrdinalIgnoreCase )
				|| string.Equals( r.StatusCode, statusFilter, StringComparison.OrdinalIgnoreCase ) );

		query = ApplyEntrySort( query, sortBy, descending );
		List<MergedEntryDto> finalRows = [ .. query ];

		return new
		{
			directoryId = resolvedDirId,
			treePath = resolvedTreePath,
			localFolder,
			remoteError,
			totalCount = finalRows.Count,
			remoteCount = remote?.Count ?? 0,
			localOnlyCount,
			syncedCount = matchedLocals.Count,
			entries = finalRows,
		};
	}

	public async Task<object> GetDirectoryEntryIdsAsync( int directoryId, bool withSubdirectories, bool withDeleted )
	{
		EnsureLoggedIn();
		Result<IReadOnlyList<int>> result = await HpDirectory.GetDirectoryEntryIDsAsync( directoryId, withSubdirectories, withDeleted );
		if( !result.Ok )
			throw new InvalidOperationException( result.Error ?? "Failed to get directory entry IDs." );
		return new
		{
			directoryId,
			withSubdirectories,
			withDeleted,
			count = result.Value!.Count,
			entryIds = result.Value,
		};
	}

	public async Task<object> FindLastAvailableDirectoryAsync( IReadOnlyList<string> segments )
	{
		EnsureLoggedIn();
		List<string> path = segments.Count > 0 && string.Equals( segments[ 0 ], PwaPaths.Root, StringComparison.OrdinalIgnoreCase )
			? [ .. segments ]
			: [ PwaPaths.Root, .. segments ];
		Result<LastAvailableDirectory> result = await HpDirectory.LastAvailableDirectory( path );
		if( !result.Ok )
			throw new InvalidOperationException( result.Error ?? "last_available_directory failed." );

		bool existsCompletely = result.Value.Index >= path.Count - 1;
		return new
		{
			requestedPath = string.Join( "\\", path ),
			deepestExistingIndex = result.Value.Index,
			deepestExistingDirectoryId = result.Value.DirectoryId,
			deepestExistingPath = string.Join( "\\", path.Take( result.Value.Index + 1 ) ),
			existsCompletely,
			missingSegments = path.Skip( result.Value.Index + 1 ).ToArray(),
		};
	}

	public async Task<object> CreateDirectoryAsync( IReadOnlyList<string> segments, bool createRemote, bool createLocal )
	{
		string[] cleanSegments = [ .. segments.Where( s => !string.IsNullOrWhiteSpace( s ) ) ];
		if( cleanSegments.Length > 0 && string.Equals( cleanSegments[ 0 ], PwaPaths.Root, StringComparison.OrdinalIgnoreCase ) )
			cleanSegments = cleanSegments[ 1.. ];

		List<object> createdRemote = [];
		if( createRemote )
		{
			EnsureLoggedIn();
			HpDirectory[]? dirs = await HpDirectory.CreateNew( cleanSegments );
			if( dirs is not null )
			{
				foreach( HpDirectory d in dirs )
					createdRemote.Add( new { id = d.id, name = d.name, parentId = d.parent_id?.id } );
			}
			UiBridge?.NotifyServerMutated( treeChanged: true, entriesChanged: false );
		}

		string? localPath = null;
		if( createLocal )
		{
			string treePath = PwaPaths.ToServerName( cleanSegments );
			localPath = PwaPaths.ToAbsolute( treePath );
			if( !string.IsNullOrEmpty( localPath ) )
				Directory.CreateDirectory( localPath );
		}

		return new
		{
			treePath = PwaPaths.ToServerName( cleanSegments ),
			remoteDirectories = createdRemote,
			localPath,
		};
	}

	// ------------------------------------------------------------------ Entry Details, Versions, Previews & Dependency Graph

	public async Task<object> GetEntryDetailsAsync(
		int entryId,
		bool includeHistory,
		bool includeParents,
		bool includeChildren,
		bool includeProperties,
		bool includeVersionInfo )
	{
		EnsureLoggedIn();
		HpEntry? entry = await HpEntry.GetRecordByIdAsync( entryId );
		if( entry is null )
			throw new InvalidOperationException( $"Entry {entryId} not found." );

		string? localPath = entry.windows_complete_name is { Length: > 0 } w ? PwaPaths.ToAbsolute( w ) : null;
		bool localExists = localPath is not null && File.Exists( localPath );
		string? localChecksum = localExists ? Checksums.OfFile( localPath! ) : null;

		object? history = includeHistory ? await GetEntryHistoryAsync( entryId ) : null;
		object? parents = includeParents ? await GetEntryRelationshipsAsync( entryId, entry.latest_version_id?.id, "parents", recursive: false ) : null;
		object? children = includeChildren ? await GetEntryRelationshipsAsync( entryId, entry.latest_version_id?.id, "children", recursive: false ) : null;
		object? properties = includeProperties ? await GetEntryPropertiesAsync( entryId, null ) : null;
		object? versionInfo = includeVersionInfo && entry.latest_version_id?.id is int latestId and > 0
			? await GetVersionsInfoAsync( [ latestId ] )
			: null;

		return new
		{
			entry = new
			{
				id = entry.id,
				name = entry.name,
				windowsCompleteName = entry.windows_complete_name,
				directory = OdooJsonHelper.SerializeMany2One( entry.dir_id ),
				latestVersion = OdooJsonHelper.SerializeMany2One( entry.latest_version_id ),
				type = OdooJsonHelper.SerializeMany2One( entry.type_id ),
				category = OdooJsonHelper.SerializeMany2One( entry.cat_id ),
				deleted = entry.deleted,
				isCheckedOut = entry.is_checked_out,
				checkoutUser = OdooJsonHelper.SerializeMany2One( entry.checkout_user ),
				checkoutNode = OdooJsonHelper.SerializeMany2One( entry.checkout_node ),
				checkoutDate = entry.checkout_date,
				canCheckOut = entry.CanCheckOut(),
				canUnCheckOut = entry.CanUnCheckOut(),
				localPath,
				localExists,
				localChecksum,
			},
			history,
			parents,
			children,
			properties,
			latestVersionInfo = versionInfo,
		};
	}

	public async Task<object> GetEntryHistoryAsync( int entryId )
	{
		EnsureLoggedIn();
		HpVersion[] versions = await HpEntry.GetRelatedRecordByIdsAsync<HpVersion>( [ entryId ], "version_ids", excludedFields: NoBlobs ) ?? [];
		var rows = versions.OrderByDescending( v => v.id ?? 0 ).Select( v =>
		{
			int? createUid = OdooValues.Many2OneId( v.HashedValues.GetValueOrDefault( "create_uid" ) );
			string? createUserName = createUid is int uid && OdooDefaults.Instance.IdToUser.TryGetValue( uid, out var u )
				? u.name
				: OdooValues.Many2OneName( v.HashedValues.GetValueOrDefault( "create_uid" ) );
			return new
			{
				versionId = v.id ?? 0,
				name = v.name,
				fileExt = v.file_ext,
				fileSize = v.file_size,
				checksum = v.checksum,
				modifyDate = v.file_modify_stamp,
				modUserId = createUid,
				modUserName = createUserName,
				node = OdooJsonHelper.SerializeMany2One( v.node_id ),
				directory = OdooJsonHelper.SerializeMany2One( v.dir_id ),
				checkoutUser = OdooJsonHelper.SerializeMany2One( v.checkout_user ),
				attachment = OdooJsonHelper.SerializeMany2One( v.attachment_id ),
				winPathway = v.WinPathway,
			};
		} ).ToList();

		return new { entryId, count = rows.Count, versions = rows };
	}

	public async Task<object> GetEntryRelationshipsAsync( int? entryId, int? versionId, string direction, bool recursive )
	{
		EnsureLoggedIn();
		int? resolvedVersionId = versionId;
		if( resolvedVersionId is null or 0 && entryId is int eid and > 0 )
		{
			HpEntry? entry = await HpEntry.GetRecordByIdAsync( eid, includedFields: [ nameof( HpEntry.latest_version_id ) ] );
			resolvedVersionId = entry?.latest_version_id?.id;
		}
		if( resolvedVersionId is not int vid || vid <= 0 )
			throw new InvalidOperationException( "Could not resolve a version ID for relationship lookup." );

		string dir = string.IsNullOrWhiteSpace( direction ) ? "both" : direction.Trim().ToLowerInvariant();
		object? parents = null;
		object? children = null;
		object? recursiveEntries = null;
		object? recursiveVersions = null;

		if( dir is "both" or "parents" )
		{
			HpVersion[] parentRows = await HpVersion.GetRelatedRecordByIdsAsync<HpVersion>(
				[ vid ], nameof( HpVersion.parent_ids ),
				includedFields: [ "name", "entry_id", "file_size" ],
				insertFields: [ "directory_complete_name" ] ) ?? [];
			parents = parentRows.OrderByDescending( v => v.id ?? 0 ).Select( v => new
			{
				versionId = v.id ?? 0,
				entryId = v.entry_id?.id,
				name = v.name,
				basePath = v.WinPathway,
				fileSize = v.file_size,
			} ).ToList();
		}

		if( dir is "both" or "children" )
		{
			HpVersion[] childRows = await HpVersion.GetRelatedRecordByIdsAsync<HpVersion>(
				[ vid ], nameof( HpVersion.child_ids ),
				includedFields: [ "name", "entry_id", "file_size" ],
				insertFields: [ "directory_complete_name" ] ) ?? [];
			children = childRows.OrderByDescending( v => v.id ?? 0 ).Select( v => new
			{
				versionId = v.id ?? 0,
				entryId = v.entry_id?.id,
				name = v.name,
				basePath = v.WinPathway,
				fileSize = v.file_size,
			} ).ToList();
		}

		if( recursive )
		{
			Result<IReadOnlyList<int>> recVer = await PdmDownloadOperations.GetVersionListAsync( vid );
			recursiveVersions = recVer.Ok ? recVer.Value : null;
			Result<IReadOnlyList<int>> recEnt = await PdmDownloadOperations.GetAllEntriesAndDependenciesListAsync( [ vid ] );
			recursiveEntries = recEnt.Ok ? recEnt.Value : null;
		}

		return new
		{
			entryId,
			versionId = vid,
			direction = dir,
			parents,
			children,
			recursiveDependencyVersionIds = recursiveVersions,
			recursiveDependencyEntryIds = recursiveEntries,
		};
	}

	public async Task<object> GetEntryPropertiesAsync( int? entryId, IReadOnlyList<int>? versionIds )
	{
		EnsureLoggedIn();
		List<int> targetVersions = [];
		if( versionIds is { Count: > 0 } )
		{
			targetVersions.AddRange( versionIds.Where( id => id > 0 ).Distinct() );
		}
		else if( entryId is int eid and > 0 )
		{
			HpVersion[] versions = await HpEntry.GetRelatedRecordByIdsAsync<HpVersion>( [ eid ], "version_ids", includedFields: [ "id" ] ) ?? [];
			targetVersions.AddRange( versions.Select( v => v.id ?? 0 ).Where( id => id > 0 ) );
		}
		else
		{
			throw new ArgumentException( "Specify either entryId or versionIds." );
		}

		if( targetVersions.Count == 0 )
			return new { entryId, versionIds = targetVersions, count = 0, properties = Array.Empty<object>() };

		List<HpVersionProperty[]?> allProps = await HpVersion.GetAllVersionPropertiesAsync( targetVersions );
		var rows = allProps
			.SelectMany( arr => arr ?? [] )
			.Where( p => p is { id: not 0 } )
			.OrderByDescending( p => p.version_id?.id ?? 0 )
			.ThenBy( p => p.sw_config_name, StringComparer.OrdinalIgnoreCase )
			.ThenBy( p => p.prop_name, StringComparer.OrdinalIgnoreCase )
			.Select( p =>
			{
				PropertyType pt = p.GetValueType();
				object? val = pt switch
				{
					PropertyType.Yesno => p.yesno_value,
					PropertyType.Number => p.number_value,
					PropertyType.Text => p.text_value,
					PropertyType.Date => p.date_value,
					_ => null,
				};
				return new
				{
					id = p.id,
					versionId = p.version_id?.id ?? 0,
					configuration = p.sw_config_name,
					name = p.prop_name,
					property = OdooJsonHelper.SerializeMany2One( p.prop_id ),
					type = pt.ToString(),
					value = val,
				};
			} )
			.ToList();

		return new { entryId, versionIds = targetVersions, count = rows.Count, properties = rows };
	}

	public async Task<object> GetVersionsInfoAsync( IReadOnlyList<int> versionIds )
	{
		EnsureLoggedIn();
		HpVersion[] versions = await HpVersion.GetRecordsByIdsAsync( versionIds, excludedFields: NoBlobs ) ?? [];
		var items = versions.Select( v =>
		{
			string localDir = v.LocalDirectory();
			string localPath = Path.Combine( localDir, v.name ?? "" );
			bool localExists = !string.IsNullOrEmpty( v.name ) && File.Exists( localPath );
			bool matchesLocal = localExists && Checksums.Matches( localPath, v.checksum );
			return new
			{
				id = v.id,
				name = v.name,
				fileExt = v.file_ext,
				checksum = v.checksum,
				fileSize = v.file_size,
				windowsCompleteName = v.windows_complete_name,
				winPathway = v.WinPathway,
				localPath,
				localExists,
				matchesLocalCopy = matchesLocal,
				hasConflictingLocalCopy = localExists && !matchesLocal,
				modifyDate = v.file_modify_stamp,
				entry = OdooJsonHelper.SerializeMany2One( v.entry_id ),
				directory = OdooJsonHelper.SerializeMany2One( v.dir_id ),
				odooCompletePath = OdooValues.Many2OneName( v.HashedValues.GetValueOrDefault( "dir_id" ) ),
				node = OdooJsonHelper.SerializeMany2One( v.node_id ),
				checkoutUser = OdooJsonHelper.SerializeMany2One( v.checkout_user ),
				attachment = OdooJsonHelper.SerializeMany2One( v.attachment_id ),
				parentVersionIds = v.parent_ids?.IdValues,
				childVersionIds = v.child_ids?.IdValues,
			};
		} ).ToList();

		return new { count = items.Count, versions = items };
	}

	public async Task<(byte[]? Bytes, string? MimeType, string Source, string? SavedPath)> GetPreviewImageAsync(
		int? versionId,
		string? localFilePath,
		string? saveToPath,
		CancellationToken cancellationToken )
	{
		byte[]? bytes = null;
		string source = "none";

		if( versionId is int vid and > 0 )
		{
			EnsureLoggedIn();
			HpVersion? v = ( await HpVersion.GetRecordsByIdsAsync( [ vid ], includedFields: [ "preview_image" ] ) )?.FirstOrDefault();
			if( v is not null )
			{
				if( v.preview_image is null )
					await v.GetPreviewImage();
				if( v.preview_image is { Length: > 0 } )
				{
					bytes = v.preview_image;
					source = $"odoo:hp.version:{vid}";
				}
			}
		}

		if( bytes is not { Length: > 0 } && !string.IsNullOrWhiteSpace( localFilePath ) )
		{
			string full = ResolveExistingLocalPath( localFilePath );
			if( SwDocumentTypes.IsSolidWorks( full ) && SolidWorksUtil.DocMgr is { } reader )
			{
				bytes = await Task.Run( () => reader.GetPreviewPng( full ), cancellationToken );
				if( bytes is { Length: > 0 } )
					source = $"local_solidworks:{full}";
			}
		}

		string? savedPath = null;
		if( bytes is { Length: > 0 } && !string.IsNullOrWhiteSpace( saveToPath ) )
		{
			savedPath = Path.GetFullPath( saveToPath );
			if( Path.GetDirectoryName( savedPath ) is { Length: > 0 } dir )
				Directory.CreateDirectory( dir );
			await File.WriteAllBytesAsync( savedPath, bytes, cancellationToken );
		}

		string? mime = bytes is { Length: > 0 } ? OdooJsonHelper.DetectImageMimeType( bytes ) : null;
		return ( bytes, mime, source, savedPath );
	}

	public async Task<object> UploadPreviewImageAsync( int versionId, string? filePath, string? base64Data, CancellationToken cancellationToken )
	{
		EnsureLoggedIn();
		if( versionId <= 0 )
			throw new ArgumentException( "versionId must be a positive hp.version ID." );

		byte[] bytes;
		if( !string.IsNullOrWhiteSpace( filePath ) )
			bytes = await File.ReadAllBytesAsync( Path.GetFullPath( filePath ), cancellationToken );
		else if( !string.IsNullOrWhiteSpace( base64Data ) )
			bytes = Convert.FromBase64String( base64Data );
		else
			throw new ArgumentException( "Provide either 'filePath' or 'base64Data'." );

		if( PreviewImageValidator.Validate( bytes ) is { } validationError )
			throw new InvalidOperationException( validationError );

		Dictionary<string, object?> values = new() { [ "preview_image" ] = Convert.ToBase64String( bytes ) };
		Result<bool> res = await OdooRpc.WriteAsync( "hp.version", [ versionId ], values, token: cancellationToken );
		if( !res.Ok || !res.Value )
			throw new InvalidOperationException( res.Error ?? "Server rejected preview image upload." );

		return new
		{
			ok = true,
			versionId,
			byteLength = bytes.Length,
			mimeType = OdooJsonHelper.DetectImageMimeType( bytes ),
		};
	}

	public async Task<object> ReadVersionContentsAsync( int versionId, string encoding, int maxBytes, string? saveToPath, CancellationToken cancellationToken )
	{
		EnsureLoggedIn();
		HpVersion? version = await HpVersion.GetRecordByIdAsync( versionId, HpVersion.UsualExcludedFields );
		if( version is null )
			throw new InvalidOperationException( $"Version {versionId} not found." );

		byte[]? bytes = await version.DownloadContentsAsync();
		if( bytes is null )
		{
			if( version.file_size is null or 0 )
				bytes = [];
			else
				throw new InvalidOperationException( $"Could not download file contents for version {versionId}." );
		}

		string? saved = null;
		if( !string.IsNullOrWhiteSpace( saveToPath ) )
		{
			saved = Path.GetFullPath( saveToPath );
			if( Path.GetDirectoryName( saved ) is { Length: > 0 } dir )
				Directory.CreateDirectory( dir );
			await File.WriteAllBytesAsync( saved, bytes, cancellationToken );
		}

		int limit = Math.Clamp( maxBytes, 1024, 10 * 1024 * 1024 );
		bool truncated = bytes.Length > limit;
		ReadOnlySpan<byte> slice = truncated ? bytes.AsSpan( 0, limit ) : bytes.AsSpan();
		bool useBase64 = string.Equals( encoding, "base64", StringComparison.OrdinalIgnoreCase )
			|| ( string.Equals( encoding, "auto", StringComparison.OrdinalIgnoreCase ) && LooksBinary( slice ) );

		return new
		{
			versionId,
			name = version.name,
			checksum = version.checksum,
			totalBytes = bytes.Length,
			returnedBytes = slice.Length,
			truncated,
			encoding = useBase64 ? "base64" : "utf8",
			savedPath = saved,
			content = useBase64 ? Convert.ToBase64String( slice ) : Encoding.UTF8.GetString( slice ),
		};
	}

	public Task<object> LoadDependencyGraphAsync( IReadOnlyList<int> entryIds, string directionText, bool includeLayout, CancellationToken cancellationToken )
		=> LoadDependencyGraphAsync(
			new GraphBuildRequest
			{
				EntryIds = entryIds,
				Criteria = GraphFilterCriteria.Default with { Direction = ParseGraphDirection( directionText ) },
			},
			includeLayout,
			cancellationToken );

	public async Task<object> LoadDependencyGraphAsync( GraphBuildRequest request, bool includeLayout, CancellationToken cancellationToken )
	{
		EnsureLoggedIn();
		GraphFilterCriteria criteria = request.Criteria ?? GraphFilterCriteria.Default;
		if( criteria.ViewMode == GraphViewMode.Dependencies && ( request.EntryIds is null || request.EntryIds.Count == 0 ) )
			throw new ArgumentException( "Provide 'entryIds' when viewMode is 'Dependencies', or select another viewMode (FolderStructure, FoldersAndFiles, ByProperty, SearchFilter)." );

		Result<GraphFilterResult> result = await DependencyGraphLoader.BuildGraphAsync( request, cancellationToken );
		if( !result.Ok || result.Value is not { } built )
			throw new InvalidOperationException( result.Error ?? "Failed to build node graph." );

		DependencyGraph graph = built.Graph;
		GraphFilterStats stats = built.Stats;
		GraphDirection layoutDir = criteria.ViewMode switch
		{
			GraphViewMode.FolderStructure => GraphDirection.Children,
			GraphViewMode.FoldersAndFiles or GraphViewMode.ByProperty => criteria.IncludeDependencyLinks ? GraphDirection.FullNetwork : GraphDirection.Children,
			GraphViewMode.SearchFilter => criteria.IncludeDependencyLinks ? criteria.Direction : GraphDirection.Children,
			_ => criteria.Direction,
		};

		object? layoutObj = null;
		if( includeLayout )
		{
			GraphLayoutResult layout = GraphLayout.Arrange( graph, layoutDir );
			layoutObj = new
			{
				width = layout.Width,
				height = layout.Height,
				nodes = layout.Nodes.Select( n => new
				{
					id = n.Node.Id,
					name = n.Node.Name,
					role = n.Node.Role.ToString(),
					layer = n.Layer,
					x = n.X,
					y = n.Y,
					width = n.Width,
					height = n.Height,
					isRoot = n.IsRoot,
				} ).ToList(),
			};
		}

		return new
		{
			viewMode = criteria.ViewMode.ToString(),
			direction = criteria.Direction.ToString(),
			criteria = SerializeGraphCriteria( criteria ),
			stats = new
			{
				stats.TotalNodesBeforeFilter,
				stats.TotalEdgesBeforeFilter,
				stats.FilteredNodes,
				stats.FilteredEdges,
				stats.BridgedEdgesAdded,
				stats.NodesTrimmedByLimits,
				stats.EdgesTrimmedByChildBranchLimit,
				stats.EdgesTrimmedByParentBranchLimit,
				stats.TruncatedByLimits,
			},
			roots = graph.Roots,
			truncated = graph.Truncated,
			nodeCount = graph.Nodes.Count,
			edgeCount = graph.Edges.Count,
			nodes = graph.Nodes.Select( n => new
			{
				id = n.Id,
				name = n.Name,
				role = n.Role.ToString(),
				ext = n.Ext,
				size = n.Size,
				category = n.Category,
				versionId = n.VersionId,
				deleted = n.Deleted,
				checkedOut = n.CheckedOut,
				checkoutUser = n.CheckoutUser,
				typeName = n.TypeName,
				modified = n.Modified,
				versions = n.Versions,
				path = n.Path,
				subtitle = n.Subtitle,
				itemCount = n.ItemCount,
				omittedChildBranches = n.OmittedChildBranches,
				omittedParentBranches = n.OmittedParentBranches,
				properties = n.Properties,
			} ).ToList(),
			edges = graph.Edges.Select( e => new { parentId = e.Parent, childId = e.Child } ).ToList(),
			layout = layoutObj,
		};
	}

	/// <summary>Parses graph view mode, direction, depth, branch limits, and filter parameters from MCP tool arguments.</summary>
	public static GraphFilterCriteria ParseGraphFilterCriteria( Tools.McpToolArgs args, GraphFilterCriteria? baseline = null )
	{
		GraphFilterCriteria b = baseline ?? GraphFilterCriteria.Default;

		GraphViewMode viewMode = args.GetString( "viewMode" ) is { Length: > 0 } vmText
			? ParseGraphViewMode( vmText )
			: b.ViewMode;

		GraphDirection direction = args.GetString( "direction" ) is { Length: > 0 } dirText
			? ParseGraphDirection( dirText )
			: b.Direction;

		int maxDepth = args.HasProperty( "maxDepth" )
			? ( args.IsNull( "maxDepth" ) ? 0 : Math.Max( 0, args.GetInt( "maxDepth", 0 ) ?? 0 ) )
			: b.MaxDepth;

		int? ParseOptLimit( string primary, string? alias, int? current )
		{
			string? key = args.HasProperty( primary ) ? primary : ( alias is not null && args.HasProperty( alias ) ? alias : null );
			if( key is null )
				return current;
			if( args.IsNull( key ) )
				return null;
			int? val = args.GetInt( key );
			return val is < 0 ? null : val;
		}

		int? maxChildDepth = ParseOptLimit( "maxChildDepth", "offspringDepth", b.MaxChildDepth );
		int? maxParentDepth = ParseOptLimit( "maxParentDepth", "parentDepth", b.MaxParentDepth );
		int? maxChildBranches = ParseOptLimit( "maxChildBranches", "maxOffspringBranches", b.MaxChildBranches );
		int? maxParentBranches = ParseOptLimit( "maxParentBranches", "parentBranchLimit", b.MaxParentBranches );

		int maxNodes = args.HasProperty( "maxNodes" )
			? ( args.IsNull( "maxNodes" ) ? 250 : Math.Max( 0, args.GetInt( "maxNodes", 250 ) ?? 250 ) )
			: b.MaxNodes;

		IReadOnlyList<string>? ReadExtList( string primary, string? alias, IReadOnlyList<string>? current )
		{
			string? key = args.HasProperty( primary ) ? primary : ( alias is not null && args.HasProperty( alias ) ? alias : null );
			if( key is null )
				return current;
			if( args.IsNull( key ) )
				return null;
			List<string>? list = args.GetStringList( key );
			IReadOnlyList<string> norm = GraphFilterEngine.NormalizeExtensions( list );
			return norm.Count > 0 ? norm : null;
		}

		IReadOnlyList<string>? extensions = ReadExtList( "extensions", "fileTypes", b.Extensions );
		IReadOnlyList<string>? excludeExtensions = ReadExtList( "excludeExtensions", "excludeFileTypes", b.ExcludeExtensions );

		string? ReadOptString( string name, string? current )
		{
			if( !args.HasProperty( name ) )
				return current;
			if( args.IsNull( name ) )
				return null;
			string? s = args.GetString( name );
			return string.IsNullOrWhiteSpace( s ) ? null : s.Trim();
		}

		IReadOnlyList<string>? categories = args.HasProperty( "categories" )
			? ( args.IsNull( "categories" ) ? null : args.GetStringList( "categories" )?.Where( s => !string.IsNullOrWhiteSpace( s ) ).ToList() )
			: b.Categories;
		if( categories is { Count: 0 } )
			categories = null;

		bool? checkedOutOnly = args.HasProperty( "checkedOutOnly" )
			? ( args.IsNull( "checkedOutOnly" ) ? null : args.GetNullableBool( "checkedOutOnly" ) )
			: b.CheckedOutOnly;

		long? minSizeBytes = args.HasProperty( "minSizeBytes" )
			? ( args.IsNull( "minSizeBytes" ) ? null : args.GetLong( "minSizeBytes" ) )
			: b.MinSizeBytes;

		long? maxSizeBytes = args.HasProperty( "maxSizeBytes" )
			? ( args.IsNull( "maxSizeBytes" ) ? null : args.GetLong( "maxSizeBytes" ) )
			: b.MaxSizeBytes;

		return new GraphFilterCriteria
		{
			ViewMode = viewMode,
			Direction = direction,
			MaxDepth = maxDepth,
			MaxChildDepth = maxChildDepth,
			MaxParentDepth = maxParentDepth,
			MaxChildBranches = maxChildBranches,
			MaxParentBranches = maxParentBranches,
			MaxNodes = maxNodes,
			Extensions = extensions,
			ExcludeExtensions = excludeExtensions,
			NameFilter = ReadOptString( "nameFilter", b.NameFilter ),
			PathFilter = ReadOptString( "pathFilter", b.PathFilter ),
			Categories = categories,
			CheckedOutOnly = checkedOutOnly,
			CheckoutUser = ReadOptString( "checkoutUser", b.CheckoutUser ),
			IncludeDeleted = args.GetNullableBool( "includeDeleted" ) ?? b.IncludeDeleted,
			MinSizeBytes = minSizeBytes,
			MaxSizeBytes = maxSizeBytes,
			PropertyName = ReadOptString( "propertyName", b.PropertyName ),
			PropertyOperator = ReadOptString( "propertyOperator", b.PropertyOperator ),
			PropertyValue = ReadOptString( "propertyValue", b.PropertyValue ),
			GroupByProperty = ReadOptString( "groupByProperty", b.GroupByProperty ),
			IncludeDependencyLinks = args.GetNullableBool( "includeDependencyLinks" ) ?? b.IncludeDependencyLinks,
			KeepRoots = args.GetNullableBool( "keepRoots" ) ?? b.KeepRoots,
			BridgeFilteredEdges = args.GetNullableBool( "bridgeFilteredEdges" ) ?? b.BridgeFilteredEdges,
		};
	}

	public static object SerializeGraphCriteria( GraphFilterCriteria c ) => new
	{
		viewMode = c.ViewMode.ToString(),
		direction = c.Direction.ToString(),
		maxDepth = c.MaxDepth,
		maxChildDepth = c.MaxChildDepth,
		maxParentDepth = c.MaxParentDepth,
		maxChildBranches = c.MaxChildBranches,
		maxParentBranches = c.MaxParentBranches,
		maxNodes = c.MaxNodes,
		extensions = c.Extensions,
		excludeExtensions = c.ExcludeExtensions,
		nameFilter = c.NameFilter,
		pathFilter = c.PathFilter,
		categories = c.Categories,
		checkedOutOnly = c.CheckedOutOnly,
		checkoutUser = c.CheckoutUser,
		includeDeleted = c.IncludeDeleted,
		minSizeBytes = c.MinSizeBytes,
		maxSizeBytes = c.MaxSizeBytes,
		propertyName = c.PropertyName,
		propertyOperator = c.PropertyOperator,
		propertyValue = c.PropertyValue,
		groupByProperty = c.GroupByProperty,
		includeDependencyLinks = c.IncludeDependencyLinks,
		keepRoots = c.KeepRoots,
		bridgeFilteredEdges = c.BridgeFilteredEdges,
	};

	public static GraphViewMode ParseGraphViewMode( string? text ) => ( text?.Trim().ToLowerInvariant().Replace( "_", "" ).Replace( "-", "" ).Replace( " ", "" ) ) switch
	{
		"folderstructure" or "folders" or "folder" or "tree" or "directory" or "directories" => GraphViewMode.FolderStructure,
		"foldersandfiles" or "folderfiles" or "filesinfolders" or "directoryfiles" => GraphViewMode.FoldersAndFiles,
		"byproperty" or "property" or "properties" or "groupbyproperty" or "group" => GraphViewMode.ByProperty,
		"searchfilter" or "search" or "filter" or "query" => GraphViewMode.SearchFilter,
		_ => GraphViewMode.Dependencies,
	};

	// ------------------------------------------------------------------ Search

	public async Task<object> SearchEntriesAsync(
		string fileName,
		string nameComparer,
		bool checkedOutToMe,
		bool deletedRemotely,
		bool localOnly,
		int maxResults,
		IReadOnlyList<PropertyFilterInput> propertyFilters,
		CancellationToken cancellationToken )
	{
		if( !localOnly )
			EnsureLoggedIn();

		OdooDomain domain = OdooDomain.All;
		if( !string.IsNullOrEmpty( fileName ) )
			domain = domain.And( "name", string.IsNullOrWhiteSpace( nameComparer ) ? "ilike" : nameComparer, fileName );

		if( !localOnly )
		{
			if( checkedOutToMe )
				domain = domain.And( "checkout_user", OdooDefaults.Instance.OdooId );
			if( deletedRemotely )
				domain = domain.And( "deleted", true );
			if( propertyFilters.Count > 0 )
			{
				HashSet<int> matchingIds = await FindEntryIdsMatchingPropertiesAsync( propertyFilters, cancellationToken );
				domain = domain.And( "id", "in", matchingIds.ToArray() );
			}
		}

		IReadOnlyList<OdooRow> remoteRows = [];
		if( OdooDefaults.Instance.OdooId > 0 )
		{
			Result<IReadOnlyList<OdooRow>> found = await OdooRpc.SearchReadAsync(
				HpEntry.GetHpModel()!,
				domain,
				[ "id", "name", "directory_complete_name" ],
				timeoutMs: OdooRpc.LongTimeout,
				token: cancellationToken );
			if( !found.Ok )
				throw new InvalidOperationException( $"Entry search failed: {found.Error}" );
			remoteRows = found.ValueOr( [] );
		}
		else if( !localOnly )
		{
			throw new InvalidOperationException( "Not logged in to Odoo." );
		}

		int limit = Math.Clamp( maxResults, 1, 5000 );
		List<object> results;

		if( localOnly || deletedRemotely )
		{
			results = SearchLocalFiles( remoteRows, fileName, limit, matchRemote: !localOnly );
		}
		else
		{
			results = remoteRows
				.Take( limit )
				.Select( r =>
				{
					string? dir = r.Text( "directory_complete_name" );
					string[] segs = string.IsNullOrEmpty( dir ) ? [] : [ .. dir.Split( " / " ).Skip( 1 ) ];
					return (object)new
					{
						id = r.Int( "id" ),
						name = r.Text( "name" ),
						directory = dir,
						treePath = PwaPaths.ToServerName( segs ),
						localPath = PwaPaths.ToAbsolute( PwaPaths.ToServerName( [ .. segs, r.Text( "name" ) ?? "" ] ) ),
					};
				} )
				.ToList();
		}

		return new
		{
			query = new { fileName, nameComparer, checkedOutToMe, deletedRemotely, localOnly, maxResults = limit, propertyFilterCount = propertyFilters.Count },
			count = results.Count,
			results,
		};
	}

	public object GetSearchOptions()
	{
		var properties = OdooDefaults.Instance.IdToProp.Select( pair => new
		{
			id = pair.Key,
			name = pair.Value.name,
			propType = pair.Value.prop_type,
		} ).OrderBy( p => p.name, StringComparer.OrdinalIgnoreCase ).ToList();

		var operators = Enum.GetValues<Operators>().Select( op => new
		{
			name = op.ToString(),
			odooOperator = op.OperatorConversion(),
		} ).ToList();

		return new { properties, operators };
	}

	// ------------------------------------------------------------------ Generic Odoo ORM / RPC

	public async Task<object> OdooModelSchemaAsync( string modelName, bool fetchServerFields, CancellationToken cancellationToken )
	{
		OdooModelSchema? local = OdooJsonHelper.FindModel( modelName );
		string technicalModel = local?.Model ?? modelName;
		object? serverFields = null;
		string? serverError = null;

		if( fetchServerFields && OdooDefaults.Instance.OdooId > 0 )
		{
			Result<OdooRow> fieldsRes = await OdooRpc.ExecuteAsync<OdooRow>(
				technicalModel, "fields_get", [ Array.Empty<string>(), new[] { "string", "type", "required", "readonly", "relation", "selection" } ],
				timeoutMs: OdooRpc.LongTimeout, token: cancellationToken );
			if( fieldsRes.Ok )
				serverFields = fieldsRes.Value;
			else
				serverError = fieldsRes.Error;
		}

		if( local is null && serverFields is null )
			throw new InvalidOperationException( $"Unknown model '{modelName}'. Use odoo_list_models to see client models or log in with fetchServerFields=true." );

		return new
		{
			model = technicalModel,
			clientSchema = local,
			serverFields,
			serverError,
		};
	}

	public async Task<object> OdooSearchAsync(
		string model,
		JsonElement? domainJson,
		int? limit,
		int? timeoutMs,
		CancellationToken cancellationToken,
		int? offset = null,
		string? order = null,
		JsonElement? contextJson = null )
	{
		EnsureLoggedIn();
		List<object?> domain = OdooJsonHelper.ParseRawDomain( domainJson );
		Result<IReadOnlyList<int>> res;
		if( offset.HasValue || !string.IsNullOrWhiteSpace( order ) || contextJson is { ValueKind: JsonValueKind.Object } )
		{
			Dictionary<string, object?> kw = [];
			if( offset.HasValue ) kw[ "offset" ] = offset.Value;
			if( limit.HasValue ) kw[ "limit" ] = limit.Value;
			if( !string.IsNullOrWhiteSpace( order ) ) kw[ "order" ] = order.Trim();
			if( contextJson is { ValueKind: JsonValueKind.Object } ctx ) kw[ "context" ] = OdooJsonHelper.ToDictionary( ctx );
			res = await OdooRpc.ExecuteKwAsync<IReadOnlyList<int>>( model, "search", [ domain ], kw, timeoutMs ?? OdooRpc.LongTimeout, cancellationToken );
		}
		else
		{
			object?[] args = limit is int lim ? [ domain, 0, lim ] : [ domain ];
			res = await OdooRpc.ExecuteAsync<IReadOnlyList<int>>( model, "search", args, timeoutMs ?? OdooRpc.LongTimeout, cancellationToken );
		}
		if( !res.Ok )
			throw new InvalidOperationException( res.Error ?? "Odoo search failed." );
		return new { model, count = res.Value!.Count, ids = res.Value };
	}

	public async Task<object> OdooSearchCountAsync( string model, JsonElement? domainJson, int? timeoutMs, CancellationToken cancellationToken )
	{
		EnsureLoggedIn();
		List<object?> domain = OdooJsonHelper.ParseRawDomain( domainJson );
		Result<int> res = await OdooRpc.ExecuteAsync<int>( model, "search_count", [ domain ], timeoutMs ?? OdooRpc.ObjectTimeout, cancellationToken );
		if( !res.Ok )
			throw new InvalidOperationException( res.Error ?? "Odoo search_count failed." );
		return new { model, count = res.Value };
	}

	public async Task<object> OdooReadAsync( string model, IReadOnlyList<int> ids, IReadOnlyList<string>? fields, int? timeoutMs, CancellationToken cancellationToken )
	{
		EnsureLoggedIn();
		Result<IReadOnlyList<OdooRow>> res = await OdooRpc.ReadAsync( model, ids, fields, timeoutMs ?? OdooRpc.LongTimeout, cancellationToken );
		if( !res.Ok )
			throw new InvalidOperationException( res.Error ?? "Odoo read failed." );
		return new { model, count = res.Value!.Count, records = res.Value };
	}

	public async Task<object> OdooSearchReadAsync(
		string model,
		JsonElement? domainJson,
		IReadOnlyList<string>? fields,
		int? limit,
		int? timeoutMs,
		CancellationToken cancellationToken,
		int? offset = null,
		string? order = null,
		JsonElement? contextJson = null )
	{
		EnsureLoggedIn();
		List<object?> domain = OdooJsonHelper.ParseRawDomain( domainJson );
		Result<IReadOnlyList<OdooRow>> res;
		if( offset.HasValue || !string.IsNullOrWhiteSpace( order ) || contextJson is { ValueKind: JsonValueKind.Object } )
		{
			Dictionary<string, object?> kw = [];
			if( fields is { Count: > 0 } ) kw[ "fields" ] = fields.ToList();
			if( offset.HasValue ) kw[ "offset" ] = offset.Value;
			if( limit.HasValue ) kw[ "limit" ] = limit.Value;
			if( !string.IsNullOrWhiteSpace( order ) ) kw[ "order" ] = order.Trim();
			if( contextJson is { ValueKind: JsonValueKind.Object } ctx ) kw[ "context" ] = OdooJsonHelper.ToDictionary( ctx );
			res = await OdooRpc.ExecuteKwAsync<IReadOnlyList<OdooRow>>( model, "search_read", [ domain ], kw, timeoutMs ?? OdooRpc.LongTimeout, cancellationToken );
		}
		else
		{
			object?[] args = limit is int lim
				? [ domain, fields ?? [], 0, lim ]
				: [ domain, fields ?? [] ];
			res = await OdooRpc.ExecuteAsync<IReadOnlyList<OdooRow>>( model, "search_read", args, timeoutMs ?? OdooRpc.LongTimeout, cancellationToken );
		}
		if( !res.Ok )
			throw new InvalidOperationException( res.Error ?? "Odoo search_read failed." );
		return new { model, count = res.Value!.Count, records = res.Value };
	}

	public async Task<object> OdooSmartReadAsync( string model, JsonElement? domainJson, IReadOnlyList<int>? ids, IReadOnlyList<string>? fields, int? timeoutMs, CancellationToken cancellationToken )
	{
		EnsureLoggedIn();
		List<object?> domain = OdooJsonHelper.ParseRawDomain( domainJson );
		IReadOnlyList<int> idList = ids ?? [];
		IReadOnlyList<string> fieldList = fields ?? [];
		Result<IReadOnlyList<OdooRow>> res = await OdooRpc.ExecuteAsync<IReadOnlyList<OdooRow>>( model, "smart_read", [ domain, idList, fieldList ], timeoutMs ?? OdooRpc.LongTimeout, cancellationToken );
		if( !res.Ok )
			throw new InvalidOperationException( res.Error ?? "Odoo smart_read failed." );
		return new { model, count = res.Value!.Count, records = res.Value };
	}

	public async Task<object> OdooRelatedBrowseAsync( string model, IReadOnlyList<int> ids, string relatedField, IReadOnlyList<string>? fields, int? timeoutMs, CancellationToken cancellationToken )
	{
		EnsureLoggedIn();
		Result<IReadOnlyList<OdooRow>> res = await OdooRpc.ExecuteAsync<IReadOnlyList<OdooRow>>( model, "related_browse", [ ids, relatedField, fields ?? [] ], timeoutMs ?? 60_000, cancellationToken );
		if( !res.Ok )
			throw new InvalidOperationException( res.Error ?? "Odoo related_browse failed." );
		return new { model, relatedField, count = res.Value!.Count, records = res.Value };
	}

	public async Task<object> OdooCreateAsync( string model, JsonElement? values, JsonElement? records, int? timeoutMs, CancellationToken cancellationToken )
	{
		EnsureLoggedIn();
		if( records is { ValueKind: JsonValueKind.Array } arr )
		{
			List<IReadOnlyDictionary<string, object?>> batch = [ .. arr.EnumerateArray().Select( OdooJsonHelper.ToDictionary ) ];
			Result<IReadOnlyList<int>> many = await OdooRpc.CreateManyAsync( model, batch, timeoutMs ?? OdooRpc.LongTimeout, cancellationToken );
			if( !many.Ok )
				throw new InvalidOperationException( many.Error ?? "Odoo batch create failed." );
			UiBridge?.NotifyServerMutated( model == OdooDefaultsConstants.HP_DIRECTORY, true );
			return new { model, createdIds = many.Value };
		}

		if( values is not { ValueKind: JsonValueKind.Object } obj )
			throw new ArgumentException( "Provide either 'values' (JSON object) or 'records' (array of JSON objects)." );

		Dictionary<string, object?> dict = OdooJsonHelper.ToDictionary( obj );
		Result<int> single = await OdooRpc.CreateAsync( model, dict, timeoutMs ?? OdooRpc.LongTimeout, cancellationToken );
		if( !single.Ok )
			throw new InvalidOperationException( single.Error ?? "Odoo create failed." );
		UiBridge?.NotifyServerMutated( model == OdooDefaultsConstants.HP_DIRECTORY, true );
		return new { model, id = single.Value };
	}

	public async Task<object> OdooWriteAsync( string model, IReadOnlyList<int> ids, JsonElement values, int? timeoutMs, CancellationToken cancellationToken )
	{
		EnsureLoggedIn();
		Dictionary<string, object?> dict = OdooJsonHelper.ToDictionary( values );
		Result<bool> res = await OdooRpc.WriteAsync( model, ids, dict, timeoutMs ?? OdooRpc.LongTimeout, cancellationToken );
		if( !res.Ok )
			throw new InvalidOperationException( res.Error ?? "Odoo write failed." );
		UiBridge?.NotifyServerMutated( model == OdooDefaultsConstants.HP_DIRECTORY, true );
		return new { model, ids, ok = res.Value };
	}

	public async Task<object> OdooUnlinkAsync( string model, IReadOnlyList<int> ids, int? timeoutMs, CancellationToken cancellationToken )
	{
		EnsureLoggedIn();
		Result<bool> res = await OdooRpc.UnlinkAsync( model, ids, timeoutMs ?? OdooRpc.LongTimeout, cancellationToken );
		if( !res.Ok )
			throw new InvalidOperationException( res.Error ?? "Odoo unlink failed." );
		UiBridge?.NotifyServerMutated( model == OdooDefaultsConstants.HP_DIRECTORY, true );
		return new { model, ids, ok = res.Value };
	}

	public async Task<object> OdooExecuteAsync(
		string? service,
		string? model,
		string method,
		JsonElement? argsJson,
		int? timeoutMs,
		CancellationToken cancellationToken,
		JsonElement? kwargsJson = null,
		JsonElement? contextJson = null )
	{
		List<object?> args = [];
		if( argsJson is { ValueKind: JsonValueKind.Array } arr )
		{
			foreach( JsonElement item in arr.EnumerateArray() )
				args.Add( OdooJsonHelper.ToRpcValue( item ) );
		}

		if( !string.IsNullOrWhiteSpace( service ) && !string.Equals( service, "object.execute", StringComparison.OrdinalIgnoreCase ) && !string.Equals( service, "object.execute_kw", StringComparison.OrdinalIgnoreCase ) )
		{
			Result<JsonElement> raw = await OdooRpc.CallAsync( service, method, args, timeoutMs ?? OdooRpc.LongTimeout, cancellationToken );
			if( !raw.Ok )
				throw new InvalidOperationException( raw.Error ?? $"Odoo RPC call {service}.{method} failed." );
			return new { service, method, result = OdooValues.FromJson( raw.Value ) };
		}

		if( string.IsNullOrWhiteSpace( model ) )
			throw new ArgumentException( "Provide 'model' for object.execute / object.execute_kw calls, or 'service' for raw JSON-RPC service calls." );

		EnsureLoggedIn();
		bool useKw = string.Equals( service, "object.execute_kw", StringComparison.OrdinalIgnoreCase )
			|| kwargsJson is { ValueKind: JsonValueKind.Object }
			|| contextJson is { ValueKind: JsonValueKind.Object };

		Result<JsonElement> exec;
		if( useKw )
		{
			Dictionary<string, object?> kw = kwargsJson is { ValueKind: JsonValueKind.Object } kwObj
				? OdooJsonHelper.ToDictionary( kwObj )
				: [];
			if( contextJson is { ValueKind: JsonValueKind.Object } ctxObj )
				kw[ "context" ] = OdooJsonHelper.ToDictionary( ctxObj );
			exec = await OdooRpc.ExecuteKwAsync<JsonElement>( model, method, args, kw.Count > 0 ? kw : null, timeoutMs ?? OdooRpc.LongTimeout, cancellationToken );
		}
		else
		{
			exec = await OdooRpc.ExecuteAsync<JsonElement>( model, method, args, timeoutMs ?? OdooRpc.LongTimeout, cancellationToken );
		}

		if( !exec.Ok )
			throw new InvalidOperationException( exec.Error ?? $"Odoo {model}.{method} failed." );
		return new { model, method, rpc = useKw ? "object.execute_kw" : "object.execute", result = OdooValues.FromJson( exec.Value ) };
	}

	// ------------------------------------------------------------------ Settings, AppConfig & Diagnostics

	public object ListSettings()
	{
		JsonFileSettingsProvider? provider = ResolveJsonSettingsProvider();
		if( provider is null )
			return new { filePath = SettingsFilePath, exists = File.Exists( SettingsFilePath ), values = new Dictionary<string, object?>() };

		Dictionary<string, object?> values = [];
		foreach( var (k, v) in provider.Snapshot() )
		{
			bool secret = k.Contains( "pass", StringComparison.OrdinalIgnoreCase )
				|| k.Contains( "secret", StringComparison.OrdinalIgnoreCase )
				|| k.Contains( "token", StringComparison.OrdinalIgnoreCase );
			values[ k ] = secret ? "[REDACTED]" : OdooValues.FromJson( v );
		}

		return new
		{
			filePath = provider.FilePath,
			existedAtLoad = provider.Existed,
			errors = provider.Errors,
			count = values.Count,
			values,
		};
	}

	public object GetSetting( string key )
	{
		JsonFileSettingsProvider? provider = ResolveJsonSettingsProvider();
		if( provider is not null )
		{
			IReadOnlyDictionary<string, JsonElement> snap = provider.Snapshot();
			bool exists = snap.TryGetValue( key, out JsonElement el );
			return new
			{
				key,
				exists,
				value = exists ? OdooValues.FromJson( el ) : null,
				filePath = provider.FilePath,
			};
		}

		var sp = HackDefaults.Instance.SettingsProvider;
		bool contains = sp?.Contains( key ) is true;
		return new
		{
			key,
			exists = contains,
			value = contains ? sp?.Get<object>( key ) : null,
		};
	}

	public object SetSetting( string key, JsonElement? value, bool remove )
	{
		var sp = SettingsStore ?? HackDefaults.Instance.SettingsProvider
			?? throw new InvalidOperationException( "No settings provider is configured." );

		McpRestorePoint? rp = RestorePoints is null
			? null
			: RestorePoints.CapturePreMutationAsync( "settings_set", $"Update setting '{key}'", [], includeSettings: true ).GetAwaiter().GetResult();

		if( remove || value is null or { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } )
		{
			sp.Remove( key );
			return new { key, removed = true, filePath = SettingsFilePath, restorePointId = rp?.Id };
		}

		object? plain = OdooValues.FromJson( value.Value );
		sp.Set( key, plain );
		return new { key, removed = false, value = plain, filePath = SettingsFilePath, restorePointId = rp?.Id };
	}

	public async Task<object> GetAppConfigAsync( CancellationToken cancellationToken )
	{
		if( UiBridge is not null )
		{
			string liveJson = await UiBridge.GetAppConfigJsonAsync( cancellationToken );
			return new
			{
				filePath = AppConfigFilePath,
				source = "live_ui",
				json = JsonDocument.Parse( liveJson ).RootElement.Clone(),
			};
		}

		string path = AppConfigFilePath;
		if( !File.Exists( path ) )
			return new { filePath = path, exists = false, json = (object?)null };

		string text = await File.ReadAllTextAsync( path, Encoding.UTF8, cancellationToken );
		using JsonDocument doc = JsonDocument.Parse( text );
		return new
		{
			filePath = path,
			exists = true,
			json = doc.RootElement.Clone(),
		};
	}

	public async Task<object> SaveAppConfigAsync( string jsonText, CancellationToken cancellationToken )
	{
		// Validate valid JSON first
		using( JsonDocument doc = JsonDocument.Parse( jsonText ) )
		{
			if( doc.RootElement.ValueKind != JsonValueKind.Object )
				throw new ArgumentException( "AppConfig.json root must be a JSON object." );
		}

		McpRestorePoint? rp = RestorePoints is null ? null
			: await RestorePoints.CapturePreMutationAsync( "app_config_save", "Save AppConfig.json", [], includeAppConfig: true, cancellationToken: cancellationToken );

		if( UiBridge is not null )
		{
			await UiBridge.SaveAppConfigJsonAsync( jsonText, cancellationToken );
			return new { ok = true, filePath = AppConfigFilePath, source = "live_ui", restorePointId = rp?.Id };
		}

		string path = AppConfigFilePath;
		if( Path.GetDirectoryName( path ) is { Length: > 0 } dir )
			Directory.CreateDirectory( dir );
		await File.WriteAllTextAsync( path, jsonText, Encoding.UTF8, cancellationToken );
		return new { ok = true, filePath = path, source = "file", restorePointId = rp?.Id };
	}

	public object GetDiagnostics( int tailLines, string? logLevelFilter )
	{
		IOdooDefaults odoo = OdooDefaults.Instance;
		string summary = HackLogger.BuildDiagnosticSummary(
			clientVersion: ClientUpdate.CurrentText,
			odooUrl: odoo.OdooUrl,
			odooDb: odoo.OdooDb );

		string logDir = HackLogger.LogDirectory;
		List<object> logFiles = [];
		List<string> recentLines = [];

		if( Directory.Exists( logDir ) )
		{
			FileInfo[] files = new DirectoryInfo( logDir )
				.GetFiles( "*.log" )
				.OrderByDescending( f => f.LastWriteTimeUtc )
				.ToArray();

			foreach( FileInfo f in files.Take( 15 ) )
			{
				logFiles.Add( new
				{
					name = f.Name,
					path = f.FullName,
					sizeBytes = f.Length,
					lastWriteUtc = f.LastWriteTimeUtc,
				} );
			}

			int maxLines = Math.Clamp( tailLines, 0, 2000 );
			if( maxLines > 0 && files.FirstOrDefault( f => f.Name.StartsWith( "hackpdm-", StringComparison.OrdinalIgnoreCase ) ) is { } latestLog )
			{
				try
				{
					string[] all = File.ReadAllLines( latestLog.FullName );
					IEnumerable<string> seq = all;
					if( !string.IsNullOrWhiteSpace( logLevelFilter ) )
						seq = seq.Where( l => l.Contains( $"[{logLevelFilter.Trim().ToUpperInvariant()}]", StringComparison.OrdinalIgnoreCase ) );
					recentLines = [ .. seq.TakeLast( maxLines ) ];
				}
				catch
				{
					// Ignore transient file read issues
				}
			}
		}

		return new
		{
			summary,
			logDirectory = logDir,
			minimumLevel = HackLogger.MinimumLevel.ToString(),
			recentErrorCount = HackLogger.RecentErrorCount,
			logFiles,
			recentLogLines = recentLines,
		};
	}

	// ------------------------------------------------------------------ Helpers

	internal static void EnsureLoggedIn()
	{
		if( OdooDefaults.Instance.OdooId <= 0 )
			throw new InvalidOperationException( "Not logged in to Odoo. Call session_configure and session_login first (or attach to a logged-in live HackPDM client)." );
	}

	private JsonFileSettingsProvider? ResolveJsonSettingsProvider()
	{
		if( SettingsStore?.Provider is JsonFileSettingsProvider p )
			return p;
		if( HackDefaults.Instance.SettingsProvider is JsonFileSettingsProvider hp )
			return hp;
		if( HackDefaults.Instance.SettingsProvider is CoreSettings { Provider: JsonFileSettingsProvider cp } )
			return cp;
		return null;
	}

	private static DirectoryNode BuildMergedNode( SubdirectoryDto dto, string? parentPath, HashSet<string> localPaths, HashSet<string> remotePaths )
	{
		string name = dto.Name is { Length: > 0 } n ? n : PwaPaths.Root;
		string treePath = parentPath is null ? name : $"{parentPath}\\{name}";
		remotePaths.Add( treePath );

		DirectoryNode node = new( name, dto.Id )
		{
			ExistsLocally = parentPath is null
				? !string.IsNullOrEmpty( HackDefaults.Instance.PwaPathAbsolute ) && Directory.Exists( HackDefaults.Instance.PwaPathAbsolute )
				: localPaths.Contains( treePath ),
		};
		foreach( SubdirectoryDto child in dto.Directories )
			node.Children.Add( BuildMergedNode( child, treePath, localPaths, remotePaths ) );
		return node;
	}

	private static HashSet<string> ScanLocalTreePaths( string pwa )
	{
		HashSet<string> set = new( PathComparer );
		if( string.IsNullOrEmpty( pwa ) || !Directory.Exists( pwa ) )
			return set;

		EnumerationOptions opts = new()
		{
			RecurseSubdirectories = true,
			IgnoreInaccessible = true,
			ReturnSpecialDirectories = false,
			AttributesToSkip = FileAttributes.None,
		};
		foreach( string dir in Directory.EnumerateDirectories( pwa, "*", opts ) )
		{
			if( PwaPaths.ToServerName( dir ) is { } tp )
				set.Add( tp );
		}
		return set;
	}

	private static Dictionary<string, FileInfo> ScanLocalFiles( string folderPath )
	{
		Dictionary<string, FileInfo> files = new( PathComparer );
		if( string.IsNullOrEmpty( folderPath ) || !Directory.Exists( folderPath ) )
			return files;

		EnumerationOptions opts = new()
		{
			RecurseSubdirectories = false,
			IgnoreInaccessible = true,
			ReturnSpecialDirectories = false,
			AttributesToSkip = FileAttributes.None,
		};
		DirectoryInfo dir = new( Path.GetFullPath( folderPath ) );
		foreach( FileInfo file in dir.EnumerateFiles( "*", opts ) )
			files[ file.FullName ] = file;
		return files;
	}

	private static IEnumerable<MergedEntryDto> ApplyEntrySort( IEnumerable<MergedEntryDto> rows, string? sortBy, bool descending )
	{
		Func<MergedEntryDto, IComparable?> selector = ( sortBy?.Trim().ToLowerInvariant() ) switch
		{
			"id" => r => r.Id,
			"size" => r => r.Size,
			"type" => r => r.Type,
			"category" => r => r.Category,
			"status" => r => r.Status,
			"remotedate" => r => r.RemoteDate,
			"localdate" => r => r.LocalDate,
			"checkout" => r => r.CheckoutUserName,
			"latestversionid" or "latestid" => r => r.LatestVersionId,
			_ => r => r.Name,
		};

		return descending ? rows.OrderByDescending( selector ) : rows.OrderBy( selector );
	}

	private static async Task<HashSet<int>> FindEntryIdsMatchingPropertiesAsync(
		IReadOnlyList<PropertyFilterInput> filters,
		CancellationToken cancellationToken )
	{
		HashSet<int>? candidates = null;
		foreach( PropertyFilterInput filter in filters )
		{
			int propId = filter.PropertyId ?? 0;
			if( propId <= 0 && !string.IsNullOrWhiteSpace( filter.PropertyName ) )
			{
				if( OdooDefaults.Instance.ExtToProp.TryGetValue( filter.PropertyName, out IHpPropertyModel? model ) && model.id is int foundId )
					propId = foundId;
				else
					propId = OdooDefaults.Instance.HpProperties?
						.FirstOrDefault( p => string.Equals( p.name, filter.PropertyName, StringComparison.OrdinalIgnoreCase ) )?.id ?? 0;
			}
			if( propId <= 0 )
				throw new ArgumentException( $"Unknown property filter '{filter.PropertyName ?? filter.PropertyId?.ToString()}'." );

			string op = string.IsNullOrWhiteSpace( filter.Operator ) ? "=" : filter.Operator;
			OdooDomain dom = OdooDomain.Where( "prop_id", propId ).And( "text_value", op, filter.Value );
			Result<IReadOnlyList<OdooRow>> matches = await OdooRpc.SearchReadAsync(
				HpVersionProperty.GetHpModel()!, dom, [ "entry_id" ], timeoutMs: OdooRpc.LongTimeout, token: cancellationToken );
			if( !matches.Ok )
				throw new InvalidOperationException( $"Property filter search failed: {matches.Error}" );

			IEnumerable<int> entryIds = matches.ValueOr( [] )
				.Select( m => m.RefId( "entry_id" ) )
				.OfType<int>()
				.Where( id => id != 0 );

			if( candidates is null )
				candidates = [ .. entryIds ];
			else
				candidates.IntersectWith( entryIds );
		}
		return candidates ?? [];
	}

	private static List<object> SearchLocalFiles( IReadOnlyList<OdooRow> remoteRows, string fileName, int limit, bool matchRemote )
	{
		string pwa = HackDefaults.Instance.PwaPathAbsolute;
		if( string.IsNullOrEmpty( pwa ) || !Directory.Exists( pwa ) )
			return [];

		Dictionary<string, HashSet<string>> remotePathsByName = new( StringComparer.OrdinalIgnoreCase );
		foreach( OdooRow entry in remoteRows )
		{
			if( entry.Text( "name" ) is not string name || entry.Text( "directory_complete_name" ) is not string path )
				continue;
			if( !remotePathsByName.TryGetValue( name, out HashSet<string>? paths ) )
				remotePathsByName[ name ] = paths = new( StringComparer.OrdinalIgnoreCase );
			paths.Add( path );
		}

		List<object> rows = [];
		DirectoryInfo dir = new( pwa );
		foreach( FileInfo file in dir.EnumerateFiles( $"*{fileName}*", SearchOption.AllDirectories ) )
		{
			if( rows.Count >= limit )
				break;
			string odooPath = PwaPaths.ToServerDirectory( file.DirectoryName ) ?? "";
			bool isRemote = remotePathsByName.TryGetValue( file.Name, out HashSet<string>? paths ) && paths.Contains( odooPath );
			if( isRemote == matchRemote )
			{
				rows.Add( new
				{
					id = (int?)null,
					name = file.Name,
					directory = file.DirectoryName,
					treePath = PwaPaths.ToServerName( file.DirectoryName ),
					localPath = file.FullName,
				} );
			}
		}
		return rows;
	}

	public static GraphDirection ParseGraphDirection( string? text ) => ( text?.Trim().ToLowerInvariant().Replace( "_", "" ).Replace( "-", "" ).Replace( " ", "" ) ) switch
	{
		"parents" or "whereused" => GraphDirection.Parents,
		"both" => GraphDirection.Both,
		"fullnetwork" or "full" or "network" or "neighborhood" or "recursive" or "all" => GraphDirection.FullNetwork,
		_ => GraphDirection.Children,
	};

	private static bool LooksBinary( ReadOnlySpan<byte> bytes )
	{
		int sample = Math.Min( bytes.Length, 4096 );
		for( int i = 0; i < sample; i++ )
		{
			if( bytes[ i ] == 0 )
				return true;
		}
		return false;
	}

	public sealed record PropertyFilterInput( int? PropertyId, string? PropertyName, string? Operator, string Value );

	public sealed record MergedEntryDto(
		int? Id,
		string Name,
		string? Type,
		string? Category,
		long? Size,
		string Status,
		string StatusCode,
		string ReprType,
		int? LatestVersionId,
		int? LatestReleaseId,
		int? CheckoutUserId,
		string? CheckoutUserName,
		DateTime? RemoteDate,
		DateTime? LocalDate,
		string? FullName,
		string? LocalPath,
		bool Deleted,
		bool? LocalReadOnly );
}

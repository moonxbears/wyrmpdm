using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

using HackPDM.Infrastructure.Odoo;
using HackPDM.Shared.GlobalData;

using Npgsql;

namespace HackPDM.Mcp.Database;

/// <summary>Persisted PostgreSQL connection configuration for direct Odoo database queries.</summary>
public sealed class PostgresConnectionConfig
{
	public int SchemaVersion { get; set; } = 1;
	public string? Host { get; set; }
	public int Port { get; set; } = 5432;
	public string? Database { get; set; }
	public string? Username { get; set; }
	public string? Password { get; set; }
	public string SslMode { get; set; } = "Prefer";
	public string? ConnectionString { get; set; }
	/// <summary>Explicit defense-in-depth opt-in required before <c>postgres_execute</c> will run modifying SQL.</summary>
	public bool AllowWriteQueries { get; set; }
	public int StatementTimeoutSeconds { get; set; } = 30;
	public int MaxRows { get; set; } = 500;
}

/// <summary>
/// Manages PostgreSQL credentials/settings (stored owner-only <c>0600</c> at
/// <c>&lt;AppDataPaths.Config&gt;/mcp-postgres.json</c> with smart defaults from HackPDM's Odoo
/// settings and environment variables) and executes safe, parameterized PostgreSQL queries
/// against the Odoo database using Npgsql.
/// </summary>
public sealed class OdooPostgresService
{
	private static readonly JsonSerializerOptions JsonOpts = new()
	{
		WriteIndented = true,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
	};

	private readonly object _gate = new();
	private readonly bool _persist;
	private PostgresConnectionConfig _config;

	public string ConfigFilePath { get; }

	public event Action? ConfigChanged;

	public OdooPostgresService( string? configFilePathOverride = null, bool persistChanges = true )
	{
		_persist = persistChanges;
		ConfigFilePath = string.IsNullOrWhiteSpace( configFilePathOverride )
			? Path.Combine( AppDataPaths.Config, "mcp-postgres.json" )
			: Path.GetFullPath( configFilePathOverride );
		_config = LoadOrDefault();
	}

	public PostgresConnectionConfig GetSnapshot()
	{
		lock( _gate )
			return Clone( _config );
	}

	/// <summary>Updates PostgreSQL connection settings and persists them with owner-only file permissions.</summary>
	public object Configure(
		string? host,
		int? port,
		string? database,
		string? username,
		string? password,
		bool clearPassword,
		string? sslMode,
		string? connectionString,
		bool clearConnectionString,
		bool? allowWriteQueries,
		int? statementTimeoutSeconds,
		int? maxRows )
	{
		lock( _gate )
		{
			if( host is not null )
				_config.Host = string.IsNullOrWhiteSpace( host ) ? null : NormalizeHost( host );
			if( port.HasValue )
				_config.Port = Math.Clamp( port.Value, 1, 65535 );
			if( database is not null )
				_config.Database = string.IsNullOrWhiteSpace( database ) ? null : database.Trim();
			if( username is not null )
				_config.Username = string.IsNullOrWhiteSpace( username ) ? null : username.Trim();
			if( clearPassword )
				_config.Password = null;
			else if( password is not null )
				_config.Password = password;
			if( !string.IsNullOrWhiteSpace( sslMode ) )
			{
				if( !Enum.TryParse<SslMode>( sslMode.Trim(), ignoreCase: true, out SslMode parsedSsl ) )
					throw new ArgumentException( $"Invalid sslMode '{sslMode}'. Valid: {string.Join( ", ", Enum.GetNames<SslMode>() )}." );
				_config.SslMode = parsedSsl.ToString();
			}
			if( clearConnectionString )
				_config.ConnectionString = null;
			else if( connectionString is not null )
				_config.ConnectionString = string.IsNullOrWhiteSpace( connectionString ) ? null : connectionString.Trim();
			if( allowWriteQueries.HasValue )
				_config.AllowWriteQueries = allowWriteQueries.Value;
			if( statementTimeoutSeconds.HasValue )
				_config.StatementTimeoutSeconds = Math.Clamp( statementTimeoutSeconds.Value, 1, 300 );
			if( maxRows.HasValue )
				_config.MaxRows = Math.Clamp( maxRows.Value, 1, 10000 );

			PersistLocked();
		}

		ConfigChanged?.Invoke();
		return BuildRedactedStatus( connected: null, serverInfo: null, error: null );
	}

	/// <summary>Returns redacted PostgreSQL configuration and optionally probes live connectivity.</summary>
	public async Task<object> GetStatusAsync( bool testConnection, CancellationToken cancellationToken = default )
	{
		if( !testConnection )
			return BuildRedactedStatus( connected: null, serverInfo: null, error: null );

		try
		{
			ResolvedConnection resolved = ResolveConnection();
			Stopwatch sw = Stopwatch.StartNew();
			await using NpgsqlConnection conn = new( resolved.ConnectionString );
			await conn.OpenAsync( cancellationToken );

			await using NpgsqlCommand cmd = new(
				"SELECT version(), current_database(), current_user, pg_is_in_recovery(), current_setting('default_transaction_read_only')",
				conn )
			{
				CommandTimeout = Math.Min( 15, resolved.StatementTimeoutSeconds ),
			};
			await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync( cancellationToken );
			object? info = null;
			if( await reader.ReadAsync( cancellationToken ) )
			{
				info = new
				{
					serverVersion = reader.IsDBNull( 0 ) ? null : reader.GetString( 0 ),
					currentDatabase = reader.IsDBNull( 1 ) ? null : reader.GetString( 1 ),
					currentUser = reader.IsDBNull( 2 ) ? null : reader.GetString( 2 ),
					isReplica = !reader.IsDBNull( 3 ) && reader.GetBoolean( 3 ),
					defaultReadOnly = !reader.IsDBNull( 4 ) && string.Equals( reader.GetString( 4 ), "on", StringComparison.OrdinalIgnoreCase ),
					latencyMs = sw.ElapsedMilliseconds,
				};
			}

			return BuildRedactedStatus( connected: true, serverInfo: info, error: null );
		}
		catch( Exception ex )
		{
			return BuildRedactedStatus( connected: false, serverInfo: null, error: ex.Message );
		}
	}

	/// <summary>Lists tables (and optional views) in the Odoo PostgreSQL database with estimated rows, sizes, and Odoo model mapping.</summary>
	public async Task<object> ListTablesAsync(
		string? schema,
		string? nameFilter,
		bool includeViews,
		int limit,
		CancellationToken cancellationToken = default )
	{
		ResolvedConnection resolved = ResolveConnection();
		string targetSchema = string.IsNullOrWhiteSpace( schema ) ? "public" : schema.Trim();
		int maxItems = Math.Clamp( limit <= 0 ? 200 : limit, 1, 2000 );

		await using NpgsqlConnection conn = new( resolved.ConnectionString );
		await conn.OpenAsync( cancellationToken );

		string sql =
			"""
			SELECT
			    t.table_schema,
			    t.table_name,
			    t.table_type,
			    COALESCE(s.n_live_tup, 0) AS estimated_rows,
			    CASE WHEN t.table_type = 'BASE TABLE'
			         THEN pg_total_relation_size(format('%I.%I', t.table_schema, t.table_name))
			         ELSE 0 END AS total_bytes
			FROM information_schema.tables t
			LEFT JOIN pg_stat_user_tables s
			       ON s.schemaname = t.table_schema AND s.relname = t.table_name
			WHERE t.table_schema = @schema
			  AND (@includeViews OR t.table_type = 'BASE TABLE')
			  AND (@filter IS NULL OR t.table_name ILIKE @filter)
			ORDER BY t.table_name
			LIMIT @limit
			""";

		await using NpgsqlCommand cmd = new( sql, conn ) { CommandTimeout = resolved.StatementTimeoutSeconds };
		cmd.Parameters.AddWithValue( "schema", targetSchema );
		cmd.Parameters.AddWithValue( "includeViews", includeViews );
		string? likePattern = string.IsNullOrWhiteSpace( nameFilter )
			? null
			: ( nameFilter.Contains( '%' ) || nameFilter.Contains( '_' ) ? nameFilter.Trim() : $"%{nameFilter.Trim()}%" );
		cmd.Parameters.AddWithValue( "filter", (object?)likePattern ?? DBNull.Value );
		cmd.Parameters.AddWithValue( "limit", maxItems );

		List<object> tables = [];
		await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync( cancellationToken );
		while( await reader.ReadAsync( cancellationToken ) )
		{
			string tbl = reader.GetString( 1 );
			tables.Add( new
			{
				schema = reader.GetString( 0 ),
				tableName = tbl,
				odooModelGuess = tbl.Replace( '_', '.' ),
				tableType = reader.GetString( 2 ),
				estimatedRows = reader.IsDBNull( 3 ) ? 0L : reader.GetInt64( 3 ),
				totalBytes = reader.IsDBNull( 4 ) ? 0L : reader.GetInt64( 4 ),
			} );
		}

		return new
		{
			database = resolved.Database,
			schema = targetSchema,
			filter = nameFilter,
			count = tables.Count,
			tables,
		};
	}

	/// <summary>Describes columns, primary key, foreign keys, and indexes for a PostgreSQL table or Odoo model name.</summary>
	public async Task<object> DescribeTableAsync(
		string tableOrModel,
		string? schema,
		CancellationToken cancellationToken = default )
	{
		if( string.IsNullOrWhiteSpace( tableOrModel ) )
			throw new ArgumentException( "tableOrModel is required." );

		ResolvedConnection resolved = ResolveConnection();
		string targetSchema = string.IsNullOrWhiteSpace( schema ) ? "public" : schema.Trim();
		string rawName = tableOrModel.Trim();
		string tableName = rawName.Contains( '.' ) ? rawName.Replace( '.', '_' ) : rawName;

		await using NpgsqlConnection conn = new( resolved.ConnectionString );
		await conn.OpenAsync( cancellationToken );

		// Columns
		List<object> columns = [];
		await using( NpgsqlCommand colCmd = new(
			"""
			SELECT column_name, data_type, udt_name, is_nullable, column_default, character_maximum_length
			FROM information_schema.columns
			WHERE table_schema = @schema AND table_name = @table
			ORDER BY ordinal_position
			""",
			conn ) { CommandTimeout = resolved.StatementTimeoutSeconds } )
		{
			colCmd.Parameters.AddWithValue( "schema", targetSchema );
			colCmd.Parameters.AddWithValue( "table", tableName );
			await using NpgsqlDataReader r = await colCmd.ExecuteReaderAsync( cancellationToken );
			while( await r.ReadAsync( cancellationToken ) )
			{
				columns.Add( new
				{
					name = r.GetString( 0 ),
					dataType = r.GetString( 1 ),
					udtName = r.GetString( 2 ),
					nullable = string.Equals( r.GetString( 3 ), "YES", StringComparison.OrdinalIgnoreCase ),
					defaultValue = r.IsDBNull( 4 ) ? null : r.GetString( 4 ),
					maxLength = r.IsDBNull( 5 ) ? (int?)null : r.GetInt32( 5 ),
				} );
			}
		}

		if( columns.Count == 0 )
			throw new InvalidOperationException( $"Table '{targetSchema}.{tableName}' was not found in database '{resolved.Database}'." );

		// Foreign keys
		List<object> foreignKeys = [];
		await using( NpgsqlCommand fkCmd = new(
			"""
			SELECT
			    kcu.column_name,
			    ccu.table_schema AS foreign_schema,
			    ccu.table_name AS foreign_table,
			    ccu.column_name AS foreign_column,
			    tc.constraint_name
			FROM information_schema.table_constraints AS tc
			JOIN information_schema.key_column_usage AS kcu
			  ON tc.constraint_name = kcu.constraint_name AND tc.table_schema = kcu.table_schema
			JOIN information_schema.constraint_column_usage AS ccu
			  ON ccu.constraint_name = tc.constraint_name AND ccu.table_schema = tc.table_schema
			WHERE tc.constraint_type = 'FOREIGN KEY'
			  AND tc.table_schema = @schema AND tc.table_name = @table
			ORDER BY kcu.column_name
			""",
			conn ) { CommandTimeout = resolved.StatementTimeoutSeconds } )
		{
			fkCmd.Parameters.AddWithValue( "schema", targetSchema );
			fkCmd.Parameters.AddWithValue( "table", tableName );
			await using NpgsqlDataReader r = await fkCmd.ExecuteReaderAsync( cancellationToken );
			while( await r.ReadAsync( cancellationToken ) )
			{
				foreignKeys.Add( new
				{
					column = r.GetString( 0 ),
					referencesSchema = r.GetString( 1 ),
					referencesTable = r.GetString( 2 ),
					referencesColumn = r.GetString( 3 ),
					constraintName = r.GetString( 4 ),
				} );
			}
		}

		// Indexes
		List<object> indexes = [];
		await using( NpgsqlCommand idxCmd = new(
			"SELECT indexname, indexdef FROM pg_indexes WHERE schemaname = @schema AND tablename = @table ORDER BY indexname",
			conn ) { CommandTimeout = resolved.StatementTimeoutSeconds } )
		{
			idxCmd.Parameters.AddWithValue( "schema", targetSchema );
			idxCmd.Parameters.AddWithValue( "table", tableName );
			await using NpgsqlDataReader r = await idxCmd.ExecuteReaderAsync( cancellationToken );
			while( await r.ReadAsync( cancellationToken ) )
				indexes.Add( new { name = r.GetString( 0 ), definition = r.GetString( 1 ) } );
		}

		return new
		{
			database = resolved.Database,
			schema = targetSchema,
			tableName,
			odooModelGuess = tableName.Replace( '_', '.' ),
			columnCount = columns.Count,
			columns,
			foreignKeys,
			indexes,
		};
	}

	/// <summary>
	/// Executes a strictly read-only SQL query inside a PostgreSQL <c>BEGIN TRANSACTION READ ONLY</c>
	/// transaction with <c>SET LOCAL statement_timeout</c>. Rejects mutating, DDL, write-CTE, and
	/// multi-statement SQL before sending anything to the server.
	/// </summary>
	public async Task<object> QueryReadOnlyAsync(
		string sql,
		JsonElement? parameters,
		int? maxRowsOverride,
		int? timeoutSecondsOverride,
		CancellationToken cancellationToken = default )
	{
		SqlClassificationResult analysis = PostgresSqlClassifier.Analyze( sql );
		if( !analysis.IsStrictlyReadOnly )
			throw new InvalidOperationException( $"postgres_query only allows single read-only queries (SELECT / WITH SELECT / EXPLAIN / SHOW). Rejected: {analysis.Reason} Use 'postgres_execute' for modifying SQL." );

		ResolvedConnection resolved = ResolveConnection();
		int maxRows = Math.Clamp( maxRowsOverride ?? resolved.MaxRows, 1, 10000 );
		int timeoutSec = Math.Clamp( timeoutSecondsOverride ?? resolved.StatementTimeoutSeconds, 1, 300 );

		Stopwatch sw = Stopwatch.StartNew();
		await using NpgsqlConnection conn = new( resolved.ConnectionString );
		await conn.OpenAsync( cancellationToken );
		await using NpgsqlTransaction tx = await conn.BeginTransactionAsync( cancellationToken );

		try
		{
			await using( NpgsqlCommand guardCmd = new( $"SET TRANSACTION READ ONLY; SET LOCAL statement_timeout = '{timeoutSec * 1000}ms';", conn, tx ) )
				await guardCmd.ExecuteNonQueryAsync( cancellationToken );

			await using NpgsqlCommand cmd = new( sql, conn, tx ) { CommandTimeout = timeoutSec };
			BindParameters( cmd, parameters );

			List<object> columns = [];
			List<Dictionary<string, object?>> rows = [];
			bool truncated = false;

			await using( NpgsqlDataReader reader = await cmd.ExecuteReaderAsync( cancellationToken ) )
			{
				int fieldCount = reader.FieldCount;
				string[] colNames = new string[ fieldCount ];
				for( int i = 0; i < fieldCount; i++ )
				{
					string colName = reader.GetName( i );
					// Disambiguate duplicate column aliases in joins
					string uniqueName = colName;
					int dup = 2;
					while( Array.IndexOf( colNames, uniqueName, 0, i ) >= 0 )
						uniqueName = $"{colName}_{dup++}";
					colNames[ i ] = uniqueName;
					columns.Add( new { name = uniqueName, pgType = reader.GetDataTypeName( i ) } );
				}

				while( await reader.ReadAsync( cancellationToken ) )
				{
					if( rows.Count >= maxRows )
					{
						truncated = true;
						break;
					}
					Dictionary<string, object?> row = new( fieldCount, StringComparer.Ordinal );
					for( int i = 0; i < fieldCount; i++ )
						row[ colNames[ i ] ] = ConvertDbValue( reader.IsDBNull( i ) ? null : reader.GetValue( i ) );
					rows.Add( row );
				}
			}

			await tx.RollbackAsync( cancellationToken );
			sw.Stop();

			return new
			{
				ok = true,
				readOnlyTransaction = true,
				database = resolved.Database,
				statementKind = analysis.Kind.ToString(),
				columns,
				rowCount = rows.Count,
				truncated,
				maxRows,
				elapsedMs = sw.ElapsedMilliseconds,
				rows,
			};
		}
		catch
		{
			try { await tx.RollbackAsync( CancellationToken.None ); } catch { }
			throw;
		}
	}

	/// <summary>
	/// Executes a parameterized mutating/DDL PostgreSQL statement inside an explicit transaction.
	/// Requires <see cref="PostgresConnectionConfig.AllowWriteQueries"/> = <c>true</c> and
	/// <paramref name="confirm"/> = <c>true</c> (unless <paramref name="dryRun"/> = <c>true</c>,
	/// which executes and rolls back cleanly).
	/// </summary>
	public async Task<object> ExecuteWriteAsync(
		string sql,
		JsonElement? parameters,
		bool dryRun,
		bool confirm,
		int? maxReturnRowsOverride,
		int? timeoutSecondsOverride,
		CancellationToken cancellationToken = default )
	{
		SqlClassificationResult analysis = PostgresSqlClassifier.Analyze( sql );
		if( analysis.Kind == SqlStatementKind.Empty )
			throw new ArgumentException( "SQL statement cannot be empty." );

		ResolvedConnection resolved = ResolveConnection();
		if( !resolved.AllowWriteQueries && !dryRun )
		{
			throw new InvalidOperationException(
				"PostgreSQL write execution is disabled in HackPDM's PostgreSQL configuration (allowWriteQueries=false). " +
				"Enable write queries via postgres_configure(allowWriteQueries: true) or the GUI Command Palette Connect tab, or pass dryRun=true to test safely with automatic rollback." );
		}
		if( !dryRun && !confirm )
			throw new InvalidOperationException( "postgres_execute requires confirm=true (or pass dryRun=true to preview affected rows with automatic rollback)." );

		int maxRows = Math.Clamp( maxReturnRowsOverride ?? 200, 1, 5000 );
		int timeoutSec = Math.Clamp( timeoutSecondsOverride ?? resolved.StatementTimeoutSeconds, 1, 300 );

		Stopwatch sw = Stopwatch.StartNew();
		await using NpgsqlConnection conn = new( resolved.ConnectionString );
		await conn.OpenAsync( cancellationToken );
		await using NpgsqlTransaction tx = await conn.BeginTransactionAsync( cancellationToken );

		try
		{
			await using( NpgsqlCommand timeoutCmd = new( $"SET LOCAL statement_timeout = '{timeoutSec * 1000}ms';", conn, tx ) )
				await timeoutCmd.ExecuteNonQueryAsync( cancellationToken );

			await using NpgsqlCommand cmd = new( sql, conn, tx ) { CommandTimeout = timeoutSec };
			BindParameters( cmd, parameters );

			List<Dictionary<string, object?>> returningRows = [];
			int rowsAffected;

			await using( NpgsqlDataReader reader = await cmd.ExecuteReaderAsync( cancellationToken ) )
			{
				rowsAffected = reader.RecordsAffected;
				if( reader.FieldCount > 0 )
				{
					while( await reader.ReadAsync( cancellationToken ) && returningRows.Count < maxRows )
					{
						Dictionary<string, object?> row = new( reader.FieldCount, StringComparer.Ordinal );
						for( int i = 0; i < reader.FieldCount; i++ )
							row[ reader.GetName( i ) ] = ConvertDbValue( reader.IsDBNull( i ) ? null : reader.GetValue( i ) );
						returningRows.Add( row );
					}
					if( rowsAffected < 0 )
						rowsAffected = returningRows.Count;
				}
			}

			if( dryRun )
				await tx.RollbackAsync( cancellationToken );
			else
				await tx.CommitAsync( cancellationToken );

			sw.Stop();
			return new
			{
				ok = true,
				dryRun,
				committed = !dryRun,
				database = resolved.Database,
				statementKind = analysis.Kind.ToString(),
				leadingKeyword = analysis.LeadingKeyword,
				rowsAffected,
				returningRowCount = returningRows.Count,
				returningRows,
				elapsedMs = sw.ElapsedMilliseconds,
			};
		}
		catch
		{
			try { await tx.RollbackAsync( CancellationToken.None ); } catch { }
			throw;
		}
	}

	// ------------------------------------------------------------------ Resolution & Parameter Binding

	internal ResolvedConnection ResolveConnection()
	{
		PostgresConnectionConfig snap = GetSnapshot();
		string? envConn = FirstEnv( "HACKPDM_PG_CONNECTION_STRING", "DATABASE_URL" );
		string? rawConn = !string.IsNullOrWhiteSpace( snap.ConnectionString ) ? snap.ConnectionString : envConn;

		string host = NormalizeHost(
			FirstNonEmpty(
				snap.Host,
				FirstEnv( "HACKPDM_PG_HOST", "PGHOST" ),
				TryReadOdooAddress() ) ?? "127.0.0.1" );

		int port = snap.Port is > 0 and <= 65535
			? snap.Port
			: ( int.TryParse( FirstEnv( "HACKPDM_PG_PORT", "PGPORT" ), out int ep ) ? ep : 5432 );

		string? database = FirstNonEmpty(
			snap.Database,
			FirstEnv( "HACKPDM_PG_DATABASE", "PGDATABASE" ),
			TryReadOdooDb() );

		string? username = FirstNonEmpty(
			snap.Username,
			FirstEnv( "HACKPDM_PG_USER", "PGUSER" ) );

		string? password = FirstNonEmpty(
			snap.Password,
			FirstEnv( "HACKPDM_PG_PASSWORD", "PGPASSWORD" ) );

		string sslModeStr = FirstNonEmpty( snap.SslMode, FirstEnv( "HACKPDM_PG_SSLMODE", "PGSSLMODE" ) ) ?? "Prefer";
		if( !Enum.TryParse<SslMode>( sslModeStr, ignoreCase: true, out SslMode sslMode ) )
			sslMode = SslMode.Prefer;

		NpgsqlConnectionStringBuilder builder;
		if( !string.IsNullOrWhiteSpace( rawConn ) )
		{
			builder = new NpgsqlConnectionStringBuilder( rawConn );
			if( string.IsNullOrWhiteSpace( builder.ApplicationName ) )
				builder.ApplicationName = "HackPDM MCP";
			return new ResolvedConnection(
				ConnectionString: builder.ConnectionString,
				Host: builder.Host ?? host,
				Port: builder.Port,
				Database: builder.Database ?? database ?? "(unspecified)",
				Username: builder.Username ?? username ?? "(unspecified)",
				HasPassword: !string.IsNullOrWhiteSpace( builder.Password ),
				SslMode: builder.SslMode.ToString(),
				UsingConnectionString: true,
				AllowWriteQueries: snap.AllowWriteQueries,
				StatementTimeoutSeconds: Math.Clamp( snap.StatementTimeoutSeconds, 1, 300 ),
				MaxRows: Math.Clamp( snap.MaxRows, 1, 10000 ) );
		}

		if( string.IsNullOrWhiteSpace( database ) )
			throw new InvalidOperationException( "PostgreSQL database name is not configured (set it via postgres_configure, HackPDM Odoo settings, or PGDATABASE)." );
		if( string.IsNullOrWhiteSpace( username ) )
			throw new InvalidOperationException( "PostgreSQL username is not configured (set it via postgres_configure, Command Palette Connect tab, or PGUSER)." );

		builder = new NpgsqlConnectionStringBuilder
		{
			Host = host,
			Port = port,
			Database = database,
			Username = username,
			Password = password,
			SslMode = sslMode,
			Timeout = 10,
			CommandTimeout = Math.Clamp( snap.StatementTimeoutSeconds, 1, 300 ),
			ApplicationName = "HackPDM MCP",
		};

		return new ResolvedConnection(
			ConnectionString: builder.ConnectionString,
			Host: host,
			Port: port,
			Database: database,
			Username: username,
			HasPassword: !string.IsNullOrWhiteSpace( password ),
			SslMode: sslMode.ToString(),
			UsingConnectionString: false,
			AllowWriteQueries: snap.AllowWriteQueries,
			StatementTimeoutSeconds: Math.Clamp( snap.StatementTimeoutSeconds, 1, 300 ),
			MaxRows: Math.Clamp( snap.MaxRows, 1, 10000 ) );
	}

	private object BuildRedactedStatus( bool? connected, object? serverInfo, string? error )
	{
		PostgresConnectionConfig snap = GetSnapshot();
		string inferredHost = NormalizeHost(
			FirstNonEmpty( snap.Host, FirstEnv( "HACKPDM_PG_HOST", "PGHOST" ), TryReadOdooAddress() ) ?? "127.0.0.1" );
		string? inferredDb = FirstNonEmpty( snap.Database, FirstEnv( "HACKPDM_PG_DATABASE", "PGDATABASE" ), TryReadOdooDb() );
		string? inferredUser = FirstNonEmpty( snap.Username, FirstEnv( "HACKPDM_PG_USER", "PGUSER" ) );
		bool hasPassword = !string.IsNullOrWhiteSpace( snap.Password )
			|| !string.IsNullOrWhiteSpace( FirstEnv( "HACKPDM_PG_PASSWORD", "PGPASSWORD" ) );
		bool hasConnStr = !string.IsNullOrWhiteSpace( snap.ConnectionString )
			|| !string.IsNullOrWhiteSpace( FirstEnv( "HACKPDM_PG_CONNECTION_STRING", "DATABASE_URL" ) );

		return new
		{
			configured = hasConnStr || ( !string.IsNullOrWhiteSpace( inferredDb ) && !string.IsNullOrWhiteSpace( inferredUser ) ),
			configFilePath = ConfigFilePath,
			usingConnectionString = hasConnStr,
			host = inferredHost,
			port = snap.Port,
			database = inferredDb,
			username = inferredUser,
			hasPassword,
			sslMode = snap.SslMode,
			allowWriteQueries = snap.AllowWriteQueries,
			statementTimeoutSeconds = snap.StatementTimeoutSeconds,
			maxRows = snap.MaxRows,
			connected,
			serverInfo,
			error,
		};
	}

	private static void BindParameters( NpgsqlCommand cmd, JsonElement? parameters )
	{
		if( parameters is null || parameters.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined )
			return;

		if( parameters.Value.ValueKind == JsonValueKind.Object )
		{
			foreach( JsonProperty p in parameters.Value.EnumerateObject() )
			{
				string key = p.Name.TrimStart( '@', ':' );
				cmd.Parameters.AddWithValue( key, JsonToParamValue( p.Value ) );
			}
		}
		else if( parameters.Value.ValueKind == JsonValueKind.Array )
		{
			foreach( JsonElement item in parameters.Value.EnumerateArray() )
				cmd.Parameters.Add( new NpgsqlParameter { Value = JsonToParamValue( item ) } );
		}
	}

	private static object JsonToParamValue( JsonElement el ) => el.ValueKind switch
	{
		JsonValueKind.Null or JsonValueKind.Undefined => DBNull.Value,
		JsonValueKind.String => el.GetString() ?? string.Empty,
		JsonValueKind.True => true,
		JsonValueKind.False => false,
		JsonValueKind.Number when el.TryGetInt64( out long l ) => l,
		JsonValueKind.Number when el.TryGetDecimal( out decimal d ) => d,
		JsonValueKind.Number => el.GetDouble(),
		JsonValueKind.Array => el.EnumerateArray().Select( JsonToParamValue ).ToArray(),
		_ => el.GetRawText(),
	};

	private static object? ConvertDbValue( object? val ) => val switch
	{
		null or DBNull => null,
		byte[] bytes => bytes.Length <= 256
			? Convert.ToBase64String( bytes )
			: $"<bytea {bytes.Length} bytes>",
		DateTime dt => dt.ToString( "O" ),
		DateTimeOffset dto => dto.ToString( "O" ),
		DateOnly d => d.ToString( "yyyy-MM-dd" ),
		TimeOnly t => t.ToString( "HH:mm:ss.FFFFFFF" ),
		Guid g => g.ToString(),
		string or bool or byte or short or int or long or float or double or decimal => val,
		Array arr => arr.Cast<object?>().Select( ConvertDbValue ).ToArray(),
		_ => val.ToString(),
	};

	internal static string NormalizeHost( string raw )
	{
		string s = raw.Trim();
		if( s.StartsWith( "http://", StringComparison.OrdinalIgnoreCase ) || s.StartsWith( "https://", StringComparison.OrdinalIgnoreCase ) )
		{
			if( Uri.TryCreate( s, UriKind.Absolute, out Uri? uri ) && !string.IsNullOrWhiteSpace( uri.Host ) )
				return uri.Host;
		}
		int colon = s.IndexOf( ':' );
		if( colon > 0 && s.IndexOf( ':', colon + 1 ) < 0 )
			return s[ ..colon ];
		return s;
	}

	private static string? TryReadOdooAddress()
	{
		try { return OdooDefaults.Instance?.OdooAddress; }
		catch { return null; }
	}

	private static string? TryReadOdooDb()
	{
		try { return OdooDefaults.Instance?.OdooDb; }
		catch { return null; }
	}

	private static string? FirstNonEmpty( params string?[] candidates )
		=> candidates.FirstOrDefault( c => !string.IsNullOrWhiteSpace( c ) )?.Trim();

	private static string? FirstEnv( params string[] names )
	{
		foreach( string n in names )
			if( Environment.GetEnvironmentVariable( n ) is { Length: > 0 } v )
				return v;
		return null;
	}

	private PostgresConnectionConfig LoadOrDefault()
	{
		try
		{
			if( File.Exists( ConfigFilePath ) )
			{
				PostgresConnectionConfig? loaded = JsonSerializer.Deserialize<PostgresConnectionConfig>( File.ReadAllText( ConfigFilePath ), JsonOpts );
				if( loaded is not null )
					return loaded;
			}
		}
		catch
		{
		}
		return new PostgresConnectionConfig();
	}

	private void PersistLocked()
	{
		if( !_persist )
			return;
		try
		{
			if( Path.GetDirectoryName( ConfigFilePath ) is { Length: > 0 } dir )
				Directory.CreateDirectory( dir );
			string tmp = ConfigFilePath + ".tmp";
			File.WriteAllText( tmp, JsonSerializer.Serialize( _config, JsonOpts ) );
			if( !RuntimeInformation.IsOSPlatform( OSPlatform.Windows ) )
			{
				try
				{
					File.SetUnixFileMode( tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite );
				}
				catch
				{
				}
			}
			File.Move( tmp, ConfigFilePath, overwrite: true );
		}
		catch
		{
		}
	}

	private static PostgresConnectionConfig Clone( PostgresConnectionConfig c ) => new()
	{
		SchemaVersion = c.SchemaVersion,
		Host = c.Host,
		Port = c.Port,
		Database = c.Database,
		Username = c.Username,
		Password = c.Password,
		SslMode = c.SslMode,
		ConnectionString = c.ConnectionString,
		AllowWriteQueries = c.AllowWriteQueries,
		StatementTimeoutSeconds = c.StatementTimeoutSeconds,
		MaxRows = c.MaxRows,
	};

	internal sealed record ResolvedConnection(
		string ConnectionString,
		string Host,
		int Port,
		string Database,
		string Username,
		bool HasPassword,
		string SslMode,
		bool UsingConnectionString,
		bool AllowWriteQueries,
		int StatementTimeoutSeconds,
		int MaxRows );
}

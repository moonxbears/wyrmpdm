using HackPDM.Mcp.Database;
using HackPDM.Mcp.Protocol;

namespace HackPDM.Mcp.Tools;

internal static class PostgresTools
{
	public static void Register( McpServer server, OdooPostgresService postgres )
	{
		server.RegisterTool(
			"postgres_status",
			"Returns the redacted PostgreSQL connection settings for direct Odoo database access (inheriting Host/Database defaults from HackPDM Odoo settings or PG* environment variables) and optionally tests live connectivity.",
			McpSchema.Object()
				.Prop( "testConnection", McpSchema.Boolean( "Open a live PostgreSQL connection and return server version, database, user, and latency (default false).", false ) )
				.Build(),
			async ( raw, ct ) =>
			{
				McpToolArgs args = new( raw );
				return McpToolResult.Ok( await postgres.GetStatusAsync( args.GetBool( "testConnection", false ), ct ) );
			},
			new McpToolAnnotations( "PostgreSQL Connection Status", ReadOnlyHint: true, IdempotentHint: true, OpenWorldHint: true ) );

		server.RegisterTool(
			"postgres_configure",
			"Configures direct PostgreSQL connection settings for the Odoo database (stored owner-only in mcp-postgres.json). Write SQL remains disabled unless allowWriteQueries=true is explicitly set.",
			McpSchema.Object()
				.Prop( "host", McpSchema.String( "PostgreSQL host or IP (empty inherits HackPDM OdooAddress)." ) )
				.Prop( "port", McpSchema.Integer( "PostgreSQL TCP port (default 5432).", 1, 65535 ) )
				.Prop( "database", McpSchema.String( "PostgreSQL database name (empty inherits HackPDM OdooDb)." ) )
				.Prop( "username", McpSchema.String( "PostgreSQL role / username (e.g. 'odoo' or read-only reporting role)." ) )
				.Prop( "password", McpSchema.String( "PostgreSQL password (stored owner-only, never returned unmasked)." ) )
				.Prop( "clearPassword", McpSchema.Boolean( "Clear stored PostgreSQL password.", false ) )
				.Prop( "sslMode", McpSchema.Enum( "Npgsql SSL mode (default 'Prefer').", "Disable", "Allow", "Prefer", "Require", "VerifyCA", "VerifyFull" ) )
				.Prop( "connectionString", McpSchema.String( "Optional full Npgsql connection string overriding individual fields." ) )
				.Prop( "clearConnectionString", McpSchema.Boolean( "Clear stored full connection string.", false ) )
				.Prop( "allowWriteQueries", McpSchema.Boolean( "Explicitly enable or disable modifying SQL via postgres_execute (default false)." ) )
				.Prop( "statementTimeoutSeconds", McpSchema.Integer( "Server-side statement_timeout in seconds (default 30).", 1, 300 ) )
				.Prop( "maxRows", McpSchema.Integer( "Default maximum rows returned by postgres_query (default 500).", 1, 10000 ) )
				.Build(),
			( raw, _ ) =>
			{
				McpToolArgs args = new( raw );
				object status = postgres.Configure(
					host: args.GetString( "host" ),
					port: args.GetInt( "port" ),
					database: args.GetString( "database" ),
					username: args.GetString( "username" ),
					password: args.GetString( "password" ),
					clearPassword: args.GetBool( "clearPassword", false ),
					sslMode: args.GetString( "sslMode" ),
					connectionString: args.GetString( "connectionString" ),
					clearConnectionString: args.GetBool( "clearConnectionString", false ),
					allowWriteQueries: args.GetNullableBool( "allowWriteQueries" ),
					statementTimeoutSeconds: args.GetInt( "statementTimeoutSeconds" ),
					maxRows: args.GetInt( "maxRows" ) );
				return Task.FromResult( McpToolResult.Ok( status ) );
			},
			new McpToolAnnotations( "Configure Odoo PostgreSQL Connection", ReadOnlyHint: false ) );

		server.RegisterTool(
			"postgres_list_tables",
			"Lists PostgreSQL tables (and optional views) in the Odoo database with estimated row counts, disk sizes, and inferred Odoo model names (e.g. hp_entry -> hp.entry). Supports filtering like 'hp_%'.",
			McpSchema.Object()
				.Prop( "schema", McpSchema.String( "PostgreSQL schema name (default 'public').", "public" ) )
				.Prop( "nameFilter", McpSchema.String( "Optional ILIKE substring or SQL wildcard pattern (e.g. 'hp_%', 'mrp_%', 'product')." ) )
				.Prop( "includeViews", McpSchema.Boolean( "Include SQL views as well as base tables (default false).", false ) )
				.Prop( "limit", McpSchema.Integer( "Maximum tables to return (default 200).", 1, 2000, 200 ) )
				.Build(),
			async ( raw, ct ) =>
			{
				McpToolArgs args = new( raw );
				return McpToolResult.Ok( await postgres.ListTablesAsync(
					args.GetString( "schema", "public" ),
					args.GetString( "nameFilter" ),
					args.GetBool( "includeViews", false ),
					args.GetInt( "limit", 200 ) ?? 200,
					ct ) );
			},
			new McpToolAnnotations( "List Odoo PostgreSQL Tables", ReadOnlyHint: true, IdempotentHint: true, OpenWorldHint: true ) );

		server.RegisterTool(
			"postgres_describe_table",
			"Inspects columns, data types, nullability, defaults, foreign keys, and indexes for any PostgreSQL table in the Odoo database. Accepts either PostgreSQL table names ('hp_entry') or Odoo model names ('hp.entry').",
			McpSchema.Object()
				.Prop( "table", McpSchema.String( "PostgreSQL table name (e.g. 'hp_entry') or Odoo model name (e.g. 'hp.entry')." ), required: true )
				.Prop( "schema", McpSchema.String( "PostgreSQL schema name (default 'public').", "public" ) )
				.Build(),
			async ( raw, ct ) =>
			{
				McpToolArgs args = new( raw );
				return McpToolResult.Ok( await postgres.DescribeTableAsync(
					args.RequireString( "table" ),
					args.GetString( "schema", "public" ),
					ct ) );
			},
			new McpToolAnnotations( "Describe Odoo PostgreSQL Table", ReadOnlyHint: true, IdempotentHint: true, OpenWorldHint: true ) );

		server.RegisterTool(
			"postgres_query",
			"Runs a parameterized, strictly read-only PostgreSQL query (SELECT, WITH ... SELECT, EXPLAIN, SHOW) against the Odoo database inside an engine-enforced READ ONLY transaction with statement_timeout. Rejects multi-statement and write SQL.",
			McpSchema.Object()
				.Prop( "sql", McpSchema.String( "Read-only SQL query using named (@param) or positional ($1, $2) parameters." ), required: true )
				.Prop( "parameters", McpSchema.Any( "Optional JSON object of named parameters ({ \"name\": \"%bolt%\" }) or JSON array of positional parameters." ) )
				.Prop( "maxRows", McpSchema.Integer( "Maximum rows to return (default from config, up to 10000).", 1, 10000 ) )
				.Prop( "timeoutSeconds", McpSchema.Integer( "Statement timeout in seconds (1..300).", 1, 300 ) )
				.Build(),
			async ( raw, ct ) =>
			{
				McpToolArgs args = new( raw );
				return McpToolResult.Ok( await postgres.QueryReadOnlyAsync(
					args.RequireString( "sql" ),
					args.GetElement( "parameters" ),
					args.GetInt( "maxRows" ),
					args.GetInt( "timeoutSeconds" ),
					ct ) );
			},
			new McpToolAnnotations( "Run Read-Only Odoo PostgreSQL Query", ReadOnlyHint: true, IdempotentHint: true, OpenWorldHint: true ) );

		server.RegisterTool(
			"postgres_execute",
			"Runs a parameterized modifying/DDL PostgreSQL statement (INSERT, UPDATE, DELETE, etc.) inside an explicit transaction. Requires allowWriteQueries=true in PostgreSQL config AND confirm=true (or pass dryRun=true to preview affected/RETURNING rows with automatic rollback).",
			McpSchema.Object()
				.Prop( "sql", McpSchema.String( "Parameterized SQL statement to execute." ), required: true )
				.Prop( "parameters", McpSchema.Any( "Optional JSON object of named parameters or JSON array of positional parameters." ) )
				.Prop( "dryRun", McpSchema.Boolean( "Run inside a transaction and roll back cleanly to preview rowsAffected / RETURNING rows (default false).", false ) )
				.Prop( "confirm", McpSchema.Boolean( "Must be true to commit modifying SQL when dryRun=false.", false ) )
				.Prop( "maxReturnRows", McpSchema.Integer( "Maximum RETURNING rows to include (default 200).", 1, 5000, 200 ) )
				.Prop( "timeoutSeconds", McpSchema.Integer( "Statement timeout in seconds (1..300).", 1, 300 ) )
				.Build(),
			async ( raw, ct ) =>
			{
				McpToolArgs args = new( raw );
				return McpToolResult.Ok( await postgres.ExecuteWriteAsync(
					args.RequireString( "sql" ),
					args.GetElement( "parameters" ),
					args.GetBool( "dryRun", false ),
					args.GetBool( "confirm", false ),
					args.GetInt( "maxReturnRows" ),
					args.GetInt( "timeoutSeconds" ),
					ct ) );
			},
			new McpToolAnnotations( "Execute Odoo PostgreSQL Write/DDL", ReadOnlyHint: false, DestructiveHint: true, OpenWorldHint: true ) );
	}
}

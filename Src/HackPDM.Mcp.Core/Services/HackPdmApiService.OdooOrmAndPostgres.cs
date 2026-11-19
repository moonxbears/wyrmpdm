using System.Text.Json;

using HackPDM.Infrastructure.Odoo;
using HackPDM.Mcp.Database;

namespace HackPDM.Mcp.Services;

public sealed partial class HackPdmApiService
{
	public OdooPostgresService? Postgres { get; internal set; }

	/// <summary>Known pure read-only Odoo ORM and HackPDM model methods safe to invoke via JSON-RPC without mutating data.</summary>
	public static readonly HashSet<string> ReadOnlyOdooMethods = new( StringComparer.OrdinalIgnoreCase )
	{
		"search", "search_read", "read", "search_count", "read_group", "web_search_read",
		"web_read_group", "formatted_read_group", "fields_get", "name_search", "name_get",
		"default_get", "check_access_rights", "check_access_rule", "check_field_access_rights",
		"get_views", "get_view", "exists", "has_group",
		// HackPDM custom hp.* read methods
		"smart_read", "related_browse", "dependency_graph", "get_entries", "get_all_entry_ids",
		"get_children_directories_by_id", "get_recursive_dependency_entries",
		"get_recursive_dependency_versions", "read_contents", "last_available_directory",
	};

	private static readonly Dictionary<string, IReadOnlyList<(string Method, string RequiredAccess, string Description)>> KnownHackPdmModelMethods =
		new( StringComparer.OrdinalIgnoreCase )
		{
			[ "hp.entry" ] =
			[
				( "smart_read", "read", "Read hp.entry records with dotted relational field expansion" ),
				( "related_browse", "read", "Traverse relational fields from entry IDs" ),
				( "dependency_graph", "read", "Compute parent/child/both dependency graph for entry IDs" ),
			],
			[ "hp.directory" ] =
			[
				( "get_children_directories_by_id", "read", "Return subtree directory hierarchy beneath a directory ID" ),
				( "get_entries", "read", "Return entries belonging to a directory ID" ),
				( "get_all_entry_ids", "read", "Return all entry IDs beneath a directory (optional deleted & sub-entries)" ),
				( "last_available_directory", "read", "Resolve deepest existing vault directory for a list of paths" ),
			],
			[ "hp.version" ] =
			[
				( "read_contents", "read", "Download file contents for a version ID" ),
				( "get_recursive_dependency_entries", "read", "Return transitive child entry IDs for entry IDs" ),
				( "get_recursive_dependency_versions", "read", "Return transitive child version IDs for version IDs" ),
				( "checkout_entry", "write", "Check out an entry version for a workstation node" ),
			],
			[ "hp.pdm.commit" ] =
			[
				( "start_commit", "write", "Finalize a staged PDM commit batch" ),
				( "clear_commit", "write", "Clear/abort a staged PDM commit batch" ),
			],
		};

	/// <summary>Infers the required Odoo model CRUD access right (<c>read</c>, <c>create</c>, <c>write</c>, or <c>unlink</c>) for a method name.</summary>
	public static string InferRequiredOdooAccess( string method )
	{
		string m = ( method ?? string.Empty ).Trim();
		if( ReadOnlyOdooMethods.Contains( m ) )
			return "read";
		if( string.Equals( m, "create", StringComparison.OrdinalIgnoreCase ) || string.Equals( m, "copy", StringComparison.OrdinalIgnoreCase ) )
			return "create";
		if( string.Equals( m, "unlink", StringComparison.OrdinalIgnoreCase ) )
			return "unlink";
		return "write";
	}

	public static bool IsReadOnlyOdooMethod( string? method )
		=> !string.IsNullOrWhiteSpace( method ) && ReadOnlyOdooMethods.Contains( method.Trim() );

	// ------------------------------------------------------------------ Permission-Aware Odoo JSON-RPC ORM Methods

	/// <summary>
	/// Checks live Odoo <c>check_access_rights</c> permissions (<c>read</c>, <c>write</c>, <c>create</c>, <c>unlink</c>)
	/// for one or more specific models, OR discovers accessible models installed in Odoo via <c>ir.model</c>.
	/// </summary>
	public async Task<object> OdooCheckModelAccessAsync(
		IReadOnlyList<string>? models,
		string? modelFilter,
		bool discoverAccessibleModels,
		IReadOnlyList<string>? operations,
		int limit,
		CancellationToken cancellationToken )
	{
		EnsureLoggedIn();

		string[] ops = ( operations is { Count: > 0 }
			? operations.Select( o => o.Trim().ToLowerInvariant() ).Where( o => o is "read" or "write" or "create" or "unlink" ).Distinct().ToArray()
			: [ "read", "write", "create", "unlink" ] );
		if( ops.Length == 0 )
			ops = [ "read", "write", "create", "unlink" ];

		int maxModels = Math.Clamp( limit <= 0 ? 50 : limit, 1, 250 );
		List<(string Model, string? DisplayName)> targets = [];

		if( models is { Count: > 0 } )
		{
			foreach( string m in models.Where( s => !string.IsNullOrWhiteSpace( s ) ).Distinct( StringComparer.OrdinalIgnoreCase ).Take( maxModels ) )
				targets.Add( ( m.Trim(), null ) );
		}
		else if( discoverAccessibleModels || !string.IsNullOrWhiteSpace( modelFilter ) )
		{
			// Query ir.model via JSON-RPC to discover models installed on the Odoo server
			List<object?> domain = [];
			if( !string.IsNullOrWhiteSpace( modelFilter ) )
			{
				string f = modelFilter.Trim();
				domain.Add( new object?[] { "model", "ilike", f } );
			}

			Dictionary<string, object?> kw = new()
			{
				[ "fields" ] = new[] { "model", "name" },
				[ "limit" ] = maxModels,
				[ "order" ] = "model asc",
			};
			Result<IReadOnlyList<OdooRow>> irModels = await OdooRpc.ExecuteKwAsync<IReadOnlyList<OdooRow>>(
				"ir.model", "search_read", [ domain ], kw, OdooRpc.LongTimeout, cancellationToken );

			if( irModels.Ok && irModels.Value is { Count: > 0 } rows )
			{
				foreach( OdooRow r in rows )
				{
					string? mName = r.TryGetValue( "model", out object? mv ) ? mv?.ToString() : null;
					string? dName = r.TryGetValue( "name", out object? nv ) ? nv?.ToString() : null;
					if( !string.IsNullOrWhiteSpace( mName ) )
						targets.Add( ( mName!, dName ) );
				}
			}
			else
			{
				// Fallback to HackPDM's mapped client models when user cannot read ir.model directly
				IEnumerable<string> fallback = OdooJsonHelper.ModelCatalog.Select( m => m.Model );
				if( !string.IsNullOrWhiteSpace( modelFilter ) )
					fallback = fallback.Where( m => m.Contains( modelFilter.Trim(), StringComparison.OrdinalIgnoreCase ) );
				foreach( string m in fallback.Take( maxModels ) )
					targets.Add( ( m, null ) );
			}
		}
		else
		{
			foreach( var m in OdooJsonHelper.ModelCatalog.Take( maxModels ) )
				targets.Add( ( m.Model, m.ClrClassName ) );
		}

		List<object> results = [];
		foreach( var (model, displayName) in targets )
		{
			var matrix = await FetchModelAccessMatrixAsync( model, ops, cancellationToken );
			bool anyGranted = matrix.Rights.Values.Any( v => v );
			if( discoverAccessibleModels && !anyGranted )
				continue;

			results.Add( new
			{
				model,
				displayName,
				read = matrix.Rights.GetValueOrDefault( "read" ),
				write = matrix.Rights.GetValueOrDefault( "write" ),
				create = matrix.Rights.GetValueOrDefault( "create" ),
				unlink = matrix.Rights.GetValueOrDefault( "unlink" ),
				anyAccess = anyGranted,
				error = matrix.Error,
			} );
		}

		return new
		{
			userId = OdooDefaults.Instance.OdooId,
			userName = OdooDefaults.Instance.OdooUser,
			database = OdooDefaults.Instance.OdooDb,
			checkedCount = results.Count,
			accessibleCount = results.Count( r => (bool)r.GetType().GetProperty( "anyAccess" )!.GetValue( r )! ),
			models = results,
		};
	}

	/// <summary>
	/// Discovers callable Odoo JSON-RPC ORM methods, custom HackPDM model methods, bound server actions
	/// (<c>ir.actions.server</c>), and live field metadata for any Odoo model, annotated with the logged-in
	/// user's actual <c>read/write/create/unlink</c> permissions.
	/// </summary>
	public async Task<object> OdooDiscoverModelMethodsAsync(
		string model,
		bool includeServerFields,
		bool includeServerActions,
		CancellationToken cancellationToken )
	{
		EnsureLoggedIn();
		if( string.IsNullOrWhiteSpace( model ) )
			throw new ArgumentException( "model is required." );

		string technicalModel = OdooJsonHelper.FindModel( model )?.Model ?? model.Trim();
		var access = await FetchModelAccessMatrixAsync( technicalModel, [ "read", "write", "create", "unlink" ], cancellationToken );

		List<(string Method, string RequiredAccess, string Description, bool IsCustom)> catalog =
		[
			( "search", "read", "Search record IDs matching a domain (args: [domain], kwargs: {offset, limit, order, context})", false ),
			( "search_count", "read", "Count records matching a domain (args: [domain])", false ),
			( "search_read", "read", "Search and read records in one call (args: [domain], kwargs: {fields, offset, limit, order, context})", false ),
			( "read", "read", "Read fields for explicit record IDs (args: [ids], kwargs: {fields, context})", false ),
			( "read_group", "read", "SQL-like GROUP BY / SUM / COUNT / AVG / MIN / MAX aggregation (args: [domain, fields, groupby], kwargs: {offset, limit, orderby, lazy, context})", false ),
			( "name_search", "read", "Search records by display name (kwargs: {name, args, operator, limit})", false ),
			( "fields_get", "read", "Inspect model field definitions, types, relations, and labels", false ),
			( "default_get", "read", "Get default values for a list of field names", false ),
			( "check_access_rights", "read", "Verify CRUD access rights for the current user", false ),
			( "create", "create", "Create one or more records (args: [vals_or_list])", false ),
			( "copy", "create", "Duplicate record(s) with optional field overrides (ids + optional default dict)", false ),
			( "write", "write", "Update fields on record IDs (ids + [vals])", false ),
			( "toggle_active", "write", "Archive or unarchive record IDs (when model has 'active' field)", false ),
			( "unlink", "unlink", "Delete record IDs permanently", false ),
		];

		if( KnownHackPdmModelMethods.TryGetValue( technicalModel, out var customMethods ) )
		{
			foreach( var (m, req, desc) in customMethods )
				catalog.Add( ( m, req, desc, true ) );
		}

		List<object> permittedMethods = [];
		List<object> deniedMethods = [];
		foreach( var (m, req, desc, isCustom) in catalog )
		{
			bool allowed = access.Rights.GetValueOrDefault( req );
			object entry = new { method = m, requiredAccess = req, description = desc, isCustom, permitted = allowed };
			if( allowed )
				permittedMethods.Add( entry );
			else
				deniedMethods.Add( entry );
		}

		object? serverFields = null;
		if( includeServerFields && access.Rights.GetValueOrDefault( "read" ) )
		{
			Result<OdooRow> fg = await OdooRpc.ExecuteKwAsync<OdooRow>(
				technicalModel,
				"fields_get",
				[ Array.Empty<string>() ],
				new Dictionary<string, object?> { [ "attributes" ] = new[] { "string", "type", "required", "readonly", "relation", "selection" } },
				OdooRpc.LongTimeout,
				cancellationToken );
			if( fg.Ok )
				serverFields = fg.Value;
		}

		IReadOnlyList<OdooRow>? serverActions = null;
		if( includeServerActions )
		{
			List<object?> actionDomain = [ new object?[] { "model_name", "=", technicalModel } ];
			Result<IReadOnlyList<OdooRow>> actRes = await OdooRpc.ExecuteKwAsync<IReadOnlyList<OdooRow>>(
				"ir.actions.server",
				"search_read",
				[ actionDomain ],
				new Dictionary<string, object?> { [ "fields" ] = new[] { "id", "name", "state", "model_name" }, [ "limit" ] = 50 },
				OdooRpc.ObjectTimeout,
				cancellationToken );
			if( actRes.Ok )
				serverActions = actRes.Value;
		}

		return new
		{
			model = technicalModel,
			userId = OdooDefaults.Instance.OdooId,
			permissions = new
			{
				read = access.Rights.GetValueOrDefault( "read" ),
				write = access.Rights.GetValueOrDefault( "write" ),
				create = access.Rights.GetValueOrDefault( "create" ),
				unlink = access.Rights.GetValueOrDefault( "unlink" ),
				error = access.Error,
			},
			permittedMethodCount = permittedMethods.Count,
			permittedMethods,
			deniedMethods,
			serverActions = serverActions ?? [],
			serverFieldCount = serverFields is OdooRow row ? row.Count : 0,
			serverFields,
			usageHint = "Invoke any permitted ORM or custom model method via odoo_call_model_method (supports ids, positional args, keyword kwargs, context, and automatic check_access_rights verification).",
		};
	}

	/// <summary>Runs Odoo ORM <c>read_group</c> (SQL-like GROUP BY / SUM / COUNT / AVG / MIN / MAX) through JSON-RPC <c>execute_kw</c>.</summary>
	public async Task<object> OdooReadGroupAsync(
		string model,
		JsonElement? domainJson,
		IReadOnlyList<string> fields,
		IReadOnlyList<string> groupby,
		int? offset,
		int? limit,
		string? orderby,
		bool? lazy,
		JsonElement? contextJson,
		int? timeoutMs,
		CancellationToken cancellationToken )
	{
		EnsureLoggedIn();
		string technicalModel = OdooJsonHelper.FindModel( model )?.Model ?? model.Trim();
		List<object?> domain = OdooJsonHelper.ParseRawDomain( domainJson );

		Dictionary<string, object?> kwargs = [];
		if( offset.HasValue )
			kwargs[ "offset" ] = offset.Value;
		if( limit.HasValue )
			kwargs[ "limit" ] = limit.Value;
		if( !string.IsNullOrWhiteSpace( orderby ) )
			kwargs[ "orderby" ] = orderby.Trim();
		if( lazy.HasValue )
			kwargs[ "lazy" ] = lazy.Value;
		if( contextJson is { ValueKind: JsonValueKind.Object } ctxObj )
			kwargs[ "context" ] = OdooJsonHelper.ToDictionary( ctxObj );

		Result<IReadOnlyList<OdooRow>> res = await OdooRpc.ExecuteKwAsync<IReadOnlyList<OdooRow>>(
			technicalModel,
			"read_group",
			[ domain, fields.ToList(), groupby.ToList() ],
			kwargs,
			timeoutMs ?? OdooRpc.LongTimeout,
			cancellationToken );

		if( !res.Ok )
			throw new InvalidOperationException( res.Error ?? $"Odoo {technicalModel}.read_group failed." );

		return new
		{
			model = technicalModel,
			groupby,
			fields,
			groupCount = res.Value!.Count,
			groups = res.Value,
		};
	}

	/// <summary>
	/// Executes any Odoo ORM or model API method via JSON-RPC <c>object.execute_kw</c> with positional
	/// <paramref name="ids"/>/<paramref name="argsJson"/>, keyword <paramref name="kwargsJson"/>, and
	/// optional <paramref name="contextJson"/>, verifying the logged-in user's Odoo model permissions via
	/// <c>check_access_rights</c> first when <paramref name="verifyAccess"/> is <c>true</c>.
	/// </summary>
	public async Task<object> OdooCallModelMethodAsync(
		string model,
		string method,
		IReadOnlyList<int>? ids,
		JsonElement? argsJson,
		JsonElement? kwargsJson,
		JsonElement? contextJson,
		bool verifyAccess,
		string? requiredAccess,
		bool allowPrivateMethod,
		int? timeoutMs,
		CancellationToken cancellationToken )
	{
		EnsureLoggedIn();
		if( string.IsNullOrWhiteSpace( model ) )
			throw new ArgumentException( "model is required." );
		if( string.IsNullOrWhiteSpace( method ) )
			throw new ArgumentException( "method is required." );

		string technicalModel = OdooJsonHelper.FindModel( model )?.Model ?? model.Trim();
		string cleanMethod = method.Trim();

		if( cleanMethod.StartsWith( '_' ) && !allowPrivateMethod )
			throw new InvalidOperationException( $"Refusing to call private Odoo method '{cleanMethod}' via JSON-RPC (Odoo restricts '_'-prefixed methods on RPC). Pass allowPrivateMethod=true to override." );

		string accessOp = string.IsNullOrWhiteSpace( requiredAccess )
			? InferRequiredOdooAccess( cleanMethod )
			: requiredAccess.Trim().ToLowerInvariant();
		if( accessOp is not ( "read" or "write" or "create" or "unlink" ) )
			throw new ArgumentException( $"Invalid requiredAccess '{requiredAccess}'. Use read, write, create, or unlink." );

		object? verifiedPermissions = null;
		if( verifyAccess )
		{
			var matrix = await FetchModelAccessMatrixAsync( technicalModel, [ "read", "write", "create", "unlink" ], cancellationToken );
			verifiedPermissions = new
			{
				read = matrix.Rights.GetValueOrDefault( "read" ),
				write = matrix.Rights.GetValueOrDefault( "write" ),
				create = matrix.Rights.GetValueOrDefault( "create" ),
				unlink = matrix.Rights.GetValueOrDefault( "unlink" ),
			};
			if( !matrix.Rights.GetValueOrDefault( accessOp ) )
			{
				throw new UnauthorizedAccessException(
					$"Odoo user '{OdooDefaults.Instance.OdooUser}' (uid={OdooDefaults.Instance.OdooId}) lacks '{accessOp}' permission on model '{technicalModel}' required to invoke '{cleanMethod}'. " +
					$"Current model permissions: read={matrix.Rights.GetValueOrDefault( "read" )}, write={matrix.Rights.GetValueOrDefault( "write" )}, create={matrix.Rights.GetValueOrDefault( "create" )}, unlink={matrix.Rights.GetValueOrDefault( "unlink" )}." +
					( matrix.Error is { Length: > 0 } err ? $" ({err})" : "" ) );
			}
		}

		List<object?> posArgs = [];
		if( ids is { Count: > 0 } )
			posArgs.Add( ids.ToList() );
		if( argsJson is { ValueKind: JsonValueKind.Array } arr )
		{
			foreach( JsonElement item in arr.EnumerateArray() )
				posArgs.Add( OdooJsonHelper.ToRpcValue( item ) );
		}

		Dictionary<string, object?> kwargs = kwargsJson is { ValueKind: JsonValueKind.Object } kwObj
			? OdooJsonHelper.ToDictionary( kwObj )
			: [];
		if( contextJson is { ValueKind: JsonValueKind.Object } ctxObj )
			kwargs[ "context" ] = OdooJsonHelper.ToDictionary( ctxObj );

		Result<JsonElement> exec = await OdooRpc.ExecuteKwAsync<JsonElement>(
			technicalModel,
			cleanMethod,
			posArgs,
			kwargs.Count > 0 ? kwargs : null,
			timeoutMs ?? OdooRpc.LongTimeout,
			cancellationToken );

		if( !exec.Ok )
			throw new InvalidOperationException( exec.Error ?? $"Odoo {technicalModel}.{cleanMethod} failed." );

		if( accessOp != "read" && technicalModel.StartsWith( "hp.", StringComparison.OrdinalIgnoreCase ) )
			UiBridge?.NotifyServerMutated( technicalModel.Equals( "hp.directory", StringComparison.OrdinalIgnoreCase ), true );

		return new
		{
			ok = true,
			model = technicalModel,
			method = cleanMethod,
			rpc = "object.execute_kw",
			requiredAccess = accessOp,
			verifiedPermissions,
			result = OdooValues.FromJson( exec.Value ),
		};
	}

	private static async Task<(Dictionary<string, bool> Rights, string? Error)> FetchModelAccessMatrixAsync(
		string model,
		IEnumerable<string> ops,
		CancellationToken cancellationToken )
	{
		Dictionary<string, bool> rights = new( StringComparer.OrdinalIgnoreCase );
		string? lastError = null;
		foreach( string op in ops )
		{
			Result<bool> res = await OdooRpc.ExecuteKwAsync<bool>(
				model,
				"check_access_rights",
				[ op, false ],
				kwargs: null,
				timeoutMs: OdooRpc.ObjectTimeout,
				token: cancellationToken );
			if( res.Ok )
				rights[ op ] = res.Value;
			else
			{
				rights[ op ] = false;
				lastError ??= res.Error;
			}
		}
		return ( rights, lastError );
	}
}

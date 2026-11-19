using System.Text.Json;
using System.Text.Json.Nodes;

using HackPDM.Mcp.Protocol;
using HackPDM.Mcp.Services;

namespace HackPDM.Mcp.Tools;

internal static class OdooAndSearchTools
{
	public static void Register( McpServer server, HackPdmApiService api )
	{
		// ---- Vault Search ----

		JsonObject propFilterItem = McpSchema.Object()
			.Prop( "propertyId", McpSchema.Integer( "Odoo hp.property ID (optional if propertyName is supplied)." ) )
			.Prop( "propertyName", McpSchema.String( "Property name (alternative to propertyId)." ) )
			.Prop( "operator", McpSchema.String( "Odoo operator such as '=', 'ilike', 'like', '!=' (default '=').", "=" ) )
			.Prop( "value", McpSchema.String( "Property text value to match." ), required: true )
			.Build();

		server.RegisterTool(
			"search_entries",
			"Searches HackPDM entries across the Odoo vault (by filename, operator, checkedOutToMe, deletedRemotely, and custom property filters) or scans the local working folder for local-only / remotely-deleted files.",
			McpSchema.Object()
				.Prop( "fileName", McpSchema.String( "Filename search text (empty matches all).", "" ) )
				.Prop( "nameComparer", McpSchema.String( "Odoo comparison operator for fileName ('ilike', '=', '!=', 'like', etc.; default 'ilike').", "ilike" ) )
				.Prop( "checkedOutToMe", McpSchema.Boolean( "Filter to entries checked out to the currently logged-in user.", false ) )
				.Prop( "deletedRemotely", McpSchema.Boolean( "Find local files whose remote entry is marked deleted.", false ) )
				.Prop( "localOnly", McpSchema.Boolean( "Scan the local working folder for files that have no remote entry.", false ) )
				.Prop( "maxResults", McpSchema.Integer( "Maximum results to return (default 100).", 1, 5000, 100 ) )
				.Prop( "propertyFilters", McpSchema.Array( "Custom property filters; an entry must match all filters.", propFilterItem ) )
				.Build(),
			async ( raw, ct ) =>
			{
				McpToolArgs args = new( raw );
				List<HackPdmApiService.PropertyFilterInput> propFilters = [];
				if( args.GetElement( "propertyFilters" ) is { ValueKind: JsonValueKind.Array } arr )
				{
					foreach( JsonElement item in arr.EnumerateArray() )
					{
						McpToolArgs pa = new( item );
						propFilters.Add( new HackPdmApiService.PropertyFilterInput(
							PropertyId: pa.GetInt( "propertyId" ),
							PropertyName: pa.GetString( "propertyName" ),
							Operator: pa.GetString( "operator", "=" ),
							Value: pa.RequireString( "value" ) ) );
					}
				}

				return McpToolResult.Ok( await api.SearchEntriesAsync(
					fileName: args.GetString( "fileName", "" )!,
					nameComparer: args.GetString( "nameComparer", "ilike" )!,
					checkedOutToMe: args.GetBool( "checkedOutToMe", false ),
					deletedRemotely: args.GetBool( "deletedRemotely", false ),
					localOnly: args.GetBool( "localOnly", false ),
					maxResults: args.GetInt( "maxResults", 100 ) ?? 100,
					propertyFilters: propFilters,
					cancellationToken: ct ) );
			},
			new McpToolAnnotations( "Search Vault Entries", ReadOnlyHint: true, IdempotentHint: true, OpenWorldHint: true ) );

		server.RegisterTool(
			"search_get_options",
			"Lists all searchable custom properties (IDs, names, types) from Odoo lookups and all supported Odoo comparison operators.",
			McpSchema.EmptyObject(),
			( _, _ ) => Task.FromResult( McpToolResult.Ok( api.GetSearchOptions() ) ),
			new McpToolAnnotations( "Get Search Options", ReadOnlyHint: true, IdempotentHint: true ) );

		// ---- Model Introspection & Generic Odoo ORM / RPC ----

		server.RegisterTool(
			"odoo_list_models",
			"Lists every Odoo model mapped in HackPDM (hp.entry, hp.version, hp.directory, hp.release, hp.release.review, hp.pdm.commit, hp.settings, hp.type, hp.category, hp.property, etc.) with its C# model class and field count.",
			McpSchema.EmptyObject(),
			( _, _ ) => Task.FromResult( McpToolResult.Ok( new
			{
				count = OdooJsonHelper.ModelCatalog.Count,
				models = OdooJsonHelper.ModelCatalog.Select( m => new
				{
					model = m.Model,
					internalName = m.InternalName,
					clrClassName = m.ClrClassName,
					fieldCount = m.Fields.Count,
					fields = m.Fields.Select( f => f.FieldName ).ToArray(),
				} ).ToList(),
			} ) ),
			new McpToolAnnotations( "List HackPDM Odoo Models", ReadOnlyHint: true, IdempotentHint: true ) );

		server.RegisterTool(
			"odoo_model_schema",
			"Returns the detailed [OdooProp] client schema for a HackPDM Odoo model, and optionally fetches live Odoo server fields_get metadata (labels, types, relations, readonly/required flags).",
			McpSchema.Object()
				.Prop( "model", McpSchema.String( "Odoo model name (e.g. 'hp.entry', 'hp.version', 'hp.release') or C# class name (e.g. 'HpEntry')." ), required: true )
				.Prop( "fetchServerFields", McpSchema.Boolean( "Also call Odoo fields_get on the live server (requires active login).", false ) )
				.Build(),
			async ( raw, ct ) =>
			{
				McpToolArgs args = new( raw );
				return McpToolResult.Ok( await api.OdooModelSchemaAsync(
					args.RequireString( "model" ),
					args.GetBool( "fetchServerFields", false ),
					ct ) );
			},
			new McpToolAnnotations( "Get Odoo Model Schema", ReadOnlyHint: true, IdempotentHint: true, OpenWorldHint: true ) );

		server.RegisterTool(
			"odoo_search",
			"Runs Odoo ORM 'search' on any model with a JSON domain filter (e.g. [[\"name\", \"ilike\", \"%.sldprt\"], [\"deleted\", \"=\", false]]), optional offset, limit, order, and context, returning matching record IDs.",
			McpSchema.Object()
				.Prop( "model", McpSchema.String( "Odoo model technical name (e.g. 'hp.entry', 'hp.release', 'product.template', 'mrp.bom')." ), required: true )
				.Prop( "domain", McpSchema.AnyArray( "Odoo search domain array (terms [field, op, value] and optional prefix operators '&', '|', '!')." ) )
				.Prop( "offset", McpSchema.Integer( "Optional number of records to skip.", 0 ) )
				.Prop( "limit", McpSchema.Integer( "Optional maximum number of IDs to return.", 1 ) )
				.Prop( "order", McpSchema.String( "Optional Odoo ORDER BY clause (e.g. 'write_date desc, id asc')." ) )
				.Prop( "context", McpSchema.AnyObject( "Optional Odoo context dict (e.g. { \"active_test\": false })." ) )
				.Prop( "timeoutMs", McpSchema.Integer( "Optional RPC timeout in milliseconds.", 1000, 600000 ) )
				.Build(),
			async ( raw, ct ) =>
			{
				McpToolArgs args = new( raw );
				return McpToolResult.Ok( await api.OdooSearchAsync(
					args.RequireString( "model" ),
					args.GetElement( "domain" ),
					args.GetInt( "limit" ),
					args.GetInt( "timeoutMs" ),
					ct,
					offset: args.GetInt( "offset" ),
					order: args.GetString( "order" ),
					contextJson: args.GetElement( "context" ) ) );
			},
			new McpToolAnnotations( "Odoo Search", ReadOnlyHint: true, IdempotentHint: true, OpenWorldHint: true ) );

		server.RegisterTool(
			"odoo_search_count",
			"Runs Odoo ORM 'search_count' on any model with a JSON domain filter.",
			McpSchema.Object()
				.Prop( "model", McpSchema.String( "Odoo model technical name." ), required: true )
				.Prop( "domain", McpSchema.AnyArray( "Odoo search domain array." ) )
				.Prop( "timeoutMs", McpSchema.Integer( "Optional RPC timeout in milliseconds.", 1000, 600000 ) )
				.Build(),
			async ( raw, ct ) =>
			{
				McpToolArgs args = new( raw );
				return McpToolResult.Ok( await api.OdooSearchCountAsync(
					args.RequireString( "model" ),
					args.GetElement( "domain" ),
					args.GetInt( "timeoutMs" ),
					ct ) );
			},
			new McpToolAnnotations( "Odoo Search Count", ReadOnlyHint: true, IdempotentHint: true, OpenWorldHint: true ) );

		server.RegisterTool(
			"odoo_read",
			"Runs Odoo ORM 'read' on any model for specific record IDs and optional field list.",
			McpSchema.Object()
				.Prop( "model", McpSchema.String( "Odoo model technical name." ), required: true )
				.Prop( "ids", McpSchema.IntegerArray( "Record IDs to read." ), required: true )
				.Prop( "fields", McpSchema.StringArray( "Field names to fetch (omit or pass empty array for all fields; avoid binary fields unless needed)." ) )
				.Prop( "timeoutMs", McpSchema.Integer( "Optional RPC timeout in milliseconds.", 1000, 600000 ) )
				.Build(),
			async ( raw, ct ) =>
			{
				McpToolArgs args = new( raw );
				return McpToolResult.Ok( await api.OdooReadAsync(
					args.RequireString( "model" ),
					args.GetIntList( "ids" ) ?? [],
					args.GetStringList( "fields" ),
					args.GetInt( "timeoutMs" ),
					ct ) );
			},
			new McpToolAnnotations( "Odoo Read", ReadOnlyHint: true, IdempotentHint: true, OpenWorldHint: true ) );

		server.RegisterTool(
			"odoo_search_read",
			"Runs Odoo ORM 'search_read' on any model with a JSON domain filter, field projection, optional offset, limit, order, and Odoo context via JSON-RPC.",
			McpSchema.Object()
				.Prop( "model", McpSchema.String( "Odoo model technical name." ), required: true )
				.Prop( "domain", McpSchema.AnyArray( "Odoo search domain array." ) )
				.Prop( "fields", McpSchema.StringArray( "Field names to return." ) )
				.Prop( "offset", McpSchema.Integer( "Optional number of records to skip.", 0 ) )
				.Prop( "limit", McpSchema.Integer( "Optional maximum number of records to return.", 1 ) )
				.Prop( "order", McpSchema.String( "Optional Odoo ORDER BY clause (e.g. 'write_date desc, name asc')." ) )
				.Prop( "context", McpSchema.AnyObject( "Optional Odoo context dict (e.g. { \"active_test\": false })." ) )
				.Prop( "timeoutMs", McpSchema.Integer( "Optional RPC timeout in milliseconds.", 1000, 600000 ) )
				.Build(),
			async ( raw, ct ) =>
			{
				McpToolArgs args = new( raw );
				return McpToolResult.Ok( await api.OdooSearchReadAsync(
					args.RequireString( "model" ),
					args.GetElement( "domain" ),
					args.GetStringList( "fields" ),
					args.GetInt( "limit" ),
					args.GetInt( "timeoutMs" ),
					ct,
					offset: args.GetInt( "offset" ),
					order: args.GetString( "order" ),
					contextJson: args.GetElement( "context" ) ) );
			},
			new McpToolAnnotations( "Odoo Search Read", ReadOnlyHint: true, IdempotentHint: true, OpenWorldHint: true ) );

		server.RegisterTool(
			"odoo_read_group",
			"Runs Odoo ORM 'read_group' via JSON-RPC execute_kw for SQL-like GROUP BY / SUM / COUNT / AVG / MIN / MAX aggregations across any accessible Odoo model (e.g. group hp.entry by directory_id with size:sum, or group by create_date:month).",
			McpSchema.Object()
				.Prop( "model", McpSchema.String( "Odoo model technical name (e.g. 'hp.entry', 'hp.version', 'mrp.bom', 'product.template')." ), required: true )
				.Prop( "domain", McpSchema.AnyArray( "Odoo search domain filter array." ) )
				.Prop( "fields", McpSchema.StringArray( "Fields and aggregate expressions to return (e.g. ['directory_id', 'size:sum', 'id:count'])." ), required: true )
				.Prop( "groupby", McpSchema.StringArray( "Field(s) to group by, optionally with date granularity (e.g. ['directory_id', 'create_date:month'])." ), required: true )
				.Prop( "offset", McpSchema.Integer( "Optional group offset.", 0 ) )
				.Prop( "limit", McpSchema.Integer( "Optional maximum groups to return.", 1 ) )
				.Prop( "orderby", McpSchema.String( "Optional ORDER BY clause." ) )
				.Prop( "lazy", McpSchema.Boolean( "Odoo lazy grouping (true = group by first field only; false = group by all groupby fields at once; default false).", false ) )
				.Prop( "context", McpSchema.AnyObject( "Optional Odoo context dict." ) )
				.Prop( "timeoutMs", McpSchema.Integer( "Optional RPC timeout in milliseconds.", 1000, 600000 ) )
				.Build(),
			async ( raw, ct ) =>
			{
				McpToolArgs args = new( raw );
				return McpToolResult.Ok( await api.OdooReadGroupAsync(
					args.RequireString( "model" ),
					args.GetElement( "domain" ),
					args.GetStringList( "fields" ) ?? [],
					args.GetStringList( "groupby" ) ?? [],
					args.GetInt( "offset" ),
					args.GetInt( "limit" ),
					args.GetString( "orderby" ),
					args.GetNullableBool( "lazy" ) ?? false,
					args.GetElement( "context" ),
					args.GetInt( "timeoutMs" ),
					ct ) );
			},
			new McpToolAnnotations( "Odoo ORM Group/Aggregate (read_group)", ReadOnlyHint: true, IdempotentHint: true, OpenWorldHint: true ) );

		server.RegisterTool(
			"odoo_check_model_access",
			"Checks the currently logged-in Odoo user's live CRUD permissions (read, write, create, unlink) via check_access_rights on one or more models, OR discovers accessible models installed in Odoo via ir.model (filterable by modelFilter like 'hp.' or 'mrp.').",
			McpSchema.Object()
				.Prop( "models", McpSchema.StringArray( "Specific Odoo model names to check (e.g. ['hp.entry', 'mrp.bom', 'product.product']). Omit to discover models via ir.model or client catalog." ) )
				.Prop( "modelFilter", McpSchema.String( "Substring filter against ir.model technical names when discovering models (e.g. 'hp.', 'mrp.', 'stock.')." ) )
				.Prop( "discoverAccessibleModels", McpSchema.Boolean( "Query ir.model on the live Odoo server and return only models where the user has at least one granted access right (default false).", false ) )
				.Prop( "operations", McpSchema.StringArray( "Subset of CRUD operations to check (defaults to ['read', 'write', 'create', 'unlink'])." ) )
				.Prop( "limit", McpSchema.Integer( "Maximum models to evaluate (default 50, max 250).", 1, 250, 50 ) )
				.Build(),
			async ( raw, ct ) =>
			{
				McpToolArgs args = new( raw );
				return McpToolResult.Ok( await api.OdooCheckModelAccessAsync(
					args.GetStringList( "models" ),
					args.GetString( "modelFilter" ),
					args.GetBool( "discoverAccessibleModels", false ),
					args.GetStringList( "operations" ),
					args.GetInt( "limit", 50 ) ?? 50,
					ct ) );
			},
			new McpToolAnnotations( "Check Odoo Model Permissions", ReadOnlyHint: true, IdempotentHint: true, OpenWorldHint: true ) );

		server.RegisterTool(
			"odoo_discover_model_methods",
			"Inspects any Odoo model to show the logged-in user's live read/write/create/unlink permissions, all callable JSON-RPC ORM & HackPDM model methods split into permitted vs denied, bound ir.actions.server actions, and optional live fields_get metadata.",
			McpSchema.Object()
				.Prop( "model", McpSchema.String( "Odoo model technical name (e.g. 'hp.entry', 'mrp.bom', 'product.template', 'sale.order')." ), required: true )
				.Prop( "includeServerFields", McpSchema.Boolean( "Include live fields_get schema if the user has read access (default true).", true ) )
				.Prop( "includeServerActions", McpSchema.Boolean( "Also query ir.actions.server for model-bound server actions (default true).", true ) )
				.Build(),
			async ( raw, ct ) =>
			{
				McpToolArgs args = new( raw );
				return McpToolResult.Ok( await api.OdooDiscoverModelMethodsAsync(
					args.RequireString( "model" ),
					args.GetBool( "includeServerFields", true ),
					args.GetBool( "includeServerActions", true ),
					ct ) );
			},
			new McpToolAnnotations( "Discover Odoo Model API Methods & Permissions", ReadOnlyHint: true, IdempotentHint: true, OpenWorldHint: true ) );

		server.RegisterTool(
			"odoo_call_model_method",
			"Executes any public Odoo ORM or custom model API method via JSON-RPC object.execute_kw (supporting recordset ids, positional args, keyword kwargs, and Odoo context) AFTER verifying the logged-in user's model permissions via check_access_rights. Read-only ORM methods (search, search_read, read, read_group, name_search, fields_get, default_get, smart_read, etc.) are permitted in read-only mode; mutating/action methods require appropriate write/create/unlink permissions and MCP approval.",
			McpSchema.Object()
				.Prop( "model", McpSchema.String( "Odoo model technical name (e.g. 'hp.entry', 'mrp.bom', 'product.product', 'sale.order')." ), required: true )
				.Prop( "method", McpSchema.String( "Public Odoo model ORM or API method name (e.g. 'read_group', 'name_search', 'fields_get', 'action_confirm', 'copy', 'smart_read')." ), required: true )
				.Prop( "ids", McpSchema.IntegerArray( "Optional record IDs prepended as the first positional argument for recordset methods." ) )
				.Prop( "args", McpSchema.AnyArray( "Optional additional positional JSON arguments." ) )
				.Prop( "kwargs", McpSchema.AnyObject( "Optional keyword arguments dictionary passed via object.execute_kw (e.g. { \"fields\": [...], \"limit\": 20, \"order\": \"id desc\" })." ) )
				.Prop( "context", McpSchema.AnyObject( "Optional Odoo context dictionary merged into kwargs.context (e.g. { \"active_test\": false, \"lang\": \"en_US\" })." ) )
				.Prop( "verifyAccess", McpSchema.Boolean( "Verify Odoo check_access_rights on the model before executing (default true).", true ) )
				.Prop( "requiredAccess", McpSchema.Enum( "Explicit Odoo CRUD right to verify when verifyAccess=true (auto-inferred from method name if omitted).", "read", "write", "create", "unlink" ) )
				.Prop( "allowPrivateMethod", McpSchema.Boolean( "Allow calling '_'-prefixed method names (default false; Odoo RPC normally blocks private methods).", false ) )
				.Prop( "timeoutMs", McpSchema.Integer( "Optional RPC timeout in milliseconds.", 1000, 1200000 ) )
				.Build(),
			async ( raw, ct ) =>
			{
				McpToolArgs args = new( raw );
				return McpToolResult.Ok( await api.OdooCallModelMethodAsync(
					args.RequireString( "model" ),
					args.RequireString( "method" ),
					args.GetIntList( "ids" ),
					args.GetElement( "args" ),
					args.GetElement( "kwargs" ),
					args.GetElement( "context" ),
					args.GetBool( "verifyAccess", true ),
					args.GetString( "requiredAccess" ),
					args.GetBool( "allowPrivateMethod", false ),
					args.GetInt( "timeoutMs" ),
					ct ) );
			},
			new McpToolAnnotations( "Execute Permission-Verified Odoo Model API Method", ReadOnlyHint: false, OpenWorldHint: true ) );

		server.RegisterTool(
			"odoo_smart_read",
			"Calls HackPDM's hp.common.model 'smart_read' on any hp.* model (supports dotted relational field paths such as 'latest_version_id.checksum').",
			McpSchema.Object()
				.Prop( "model", McpSchema.String( "HackPDM Odoo model name (e.g. 'hp.entry', 'hp.version')." ), required: true )
				.Prop( "domain", McpSchema.AnyArray( "Optional Odoo search domain." ) )
				.Prop( "ids", McpSchema.IntegerArray( "Optional explicit record IDs." ) )
				.Prop( "fields", McpSchema.StringArray( "Field names (may include dotted relational paths)." ) )
				.Prop( "timeoutMs", McpSchema.Integer( "Optional RPC timeout in milliseconds.", 1000, 600000 ) )
				.Build(),
			async ( raw, ct ) =>
			{
				McpToolArgs args = new( raw );
				return McpToolResult.Ok( await api.OdooSmartReadAsync(
					args.RequireString( "model" ),
					args.GetElement( "domain" ),
					args.GetIntList( "ids" ),
					args.GetStringList( "fields" ),
					args.GetInt( "timeoutMs" ),
					ct ) );
			},
			new McpToolAnnotations( "Odoo Smart Read", ReadOnlyHint: true, IdempotentHint: true, OpenWorldHint: true ) );

		server.RegisterTool(
			"odoo_related_browse",
			"Calls HackPDM's hp.common.model 'related_browse' to traverse a relational field (many2one, one2many, many2many, or dotted chain) from record IDs and read fields on the target records.",
			McpSchema.Object()
				.Prop( "model", McpSchema.String( "Source Odoo model name (e.g. 'hp.entry')." ), required: true )
				.Prop( "ids", McpSchema.IntegerArray( "Source record IDs." ), required: true )
				.Prop( "relatedField", McpSchema.String( "Relational field name or dotted path (e.g. 'latest_version_id', 'version_ids', 'latest_version_id.child_ids')." ), required: true )
				.Prop( "fields", McpSchema.StringArray( "Target model fields to read." ) )
				.Prop( "timeoutMs", McpSchema.Integer( "Optional RPC timeout in milliseconds.", 1000, 600000 ) )
				.Build(),
			async ( raw, ct ) =>
			{
				McpToolArgs args = new( raw );
				return McpToolResult.Ok( await api.OdooRelatedBrowseAsync(
					args.RequireString( "model" ),
					args.GetIntList( "ids" ) ?? [],
					args.RequireString( "relatedField" ),
					args.GetStringList( "fields" ),
					args.GetInt( "timeoutMs" ),
					ct ) );
			},
			new McpToolAnnotations( "Odoo Related Browse", ReadOnlyHint: true, IdempotentHint: true, OpenWorldHint: true ) );

		server.RegisterTool(
			"odoo_create",
			"Creates one record ('values') or multiple records ('records') on any Odoo model (subject to Odoo access guards, e.g. hp.release, hp.release.review, hp.directory, hp.type, hp.property, hp.settings).",
			McpSchema.Object()
				.Prop( "model", McpSchema.String( "Odoo model technical name." ), required: true )
				.Prop( "values", McpSchema.AnyObject( "Field values JSON object for creating a single record." ) )
				.Prop( "records", McpSchema.AnyArray( "Array of field-value JSON objects for batch creating multiple records." ) )
				.Prop( "timeoutMs", McpSchema.Integer( "Optional RPC timeout in milliseconds.", 1000, 600000 ) )
				.Build(),
			async ( raw, ct ) =>
			{
				McpToolArgs args = new( raw );
				return McpToolResult.Ok( await api.OdooCreateAsync(
					args.RequireString( "model" ),
					args.GetElement( "values" ),
					args.GetElement( "records" ),
					args.GetInt( "timeoutMs" ),
					ct ) );
			},
			new McpToolAnnotations( "Odoo Create", ReadOnlyHint: false, OpenWorldHint: true ) );

		server.RegisterTool(
			"odoo_write",
			"Updates field values on one or more existing records of any Odoo model (subject to Odoo access rules).",
			McpSchema.Object()
				.Prop( "model", McpSchema.String( "Odoo model technical name." ), required: true )
				.Prop( "ids", McpSchema.IntegerArray( "Record IDs to update." ), required: true )
				.Prop( "values", McpSchema.AnyObject( "Field values JSON object to write." ), required: true )
				.Prop( "timeoutMs", McpSchema.Integer( "Optional RPC timeout in milliseconds.", 1000, 600000 ) )
				.Build(),
			async ( raw, ct ) =>
			{
				McpToolArgs args = new( raw );
				JsonElement vals = args.GetElement( "values" )
					?? throw new McpProtocolException( JsonRpcErrorCodes.InvalidParams, "Missing required 'values' object." );
				return McpToolResult.Ok( await api.OdooWriteAsync(
					args.RequireString( "model" ),
					args.GetIntList( "ids" ) ?? [],
					vals,
					args.GetInt( "timeoutMs" ),
					ct ) );
			},
			new McpToolAnnotations( "Odoo Write", ReadOnlyHint: false, OpenWorldHint: true ) );

		server.RegisterTool(
			"odoo_unlink",
			"Deletes records by ID on any Odoo model (subject to Odoo permissions). Requires confirm=true.",
			McpSchema.Object()
				.Prop( "model", McpSchema.String( "Odoo model technical name." ), required: true )
				.Prop( "ids", McpSchema.IntegerArray( "Record IDs to unlink/delete." ), required: true )
				.Prop( "confirm", McpSchema.Boolean( "Must be true to execute deletion." ), required: true )
				.Prop( "timeoutMs", McpSchema.Integer( "Optional RPC timeout in milliseconds.", 1000, 600000 ) )
				.Build(),
			async ( raw, ct ) =>
			{
				McpToolArgs args = new( raw );
				if( !args.GetBool( "confirm", false ) )
					return McpToolResult.Error( "odoo_unlink aborted: pass confirm=true to proceed." );
				return McpToolResult.Ok( await api.OdooUnlinkAsync(
					args.RequireString( "model" ),
					args.GetIntList( "ids" ) ?? [],
					args.GetInt( "timeoutMs" ),
					ct ) );
			},
			new McpToolAnnotations( "Odoo Unlink", ReadOnlyHint: false, DestructiveHint: true, OpenWorldHint: true ) );

		server.RegisterTool(
			"odoo_execute",
			"Invokes any arbitrary Odoo model method (via object.execute or object.execute_kw when kwargs/context are provided) or any raw Odoo JSON-RPC service call (when 'service' is set, e.g. 'common' / 'db'), giving complete access to every server endpoint.",
			McpSchema.Object()
				.Prop( "model", McpSchema.String( "Odoo model name for object.execute / object.execute_kw calls (e.g. 'hp.entry', 'hp.version', 'hp.pdm.commit', 'hp.release')." ) )
				.Prop( "method", McpSchema.String( "Method name to invoke on the model or service." ), required: true )
				.Prop( "args", McpSchema.AnyArray( "Positional JSON arguments to pass to the method." ) )
				.Prop( "kwargs", McpSchema.AnyObject( "Optional keyword arguments dictionary (automatically uses object.execute_kw)." ) )
				.Prop( "context", McpSchema.AnyObject( "Optional Odoo context dictionary merged into kwargs.context." ) )
				.Prop( "service", McpSchema.String( "Optional raw JSON-RPC service name (e.g. 'common', 'db', 'object', 'object.execute_kw'); omit for authenticated model calls." ) )
				.Prop( "timeoutMs", McpSchema.Integer( "Optional RPC timeout in milliseconds.", 1000, 1200000 ) )
				.Build(),
			async ( raw, ct ) =>
			{
				McpToolArgs args = new( raw );
				return McpToolResult.Ok( await api.OdooExecuteAsync(
					args.GetString( "service" ),
					args.GetString( "model" ),
					args.RequireString( "method" ),
					args.GetElement( "args" ),
					args.GetInt( "timeoutMs" ),
					ct,
					kwargsJson: args.GetElement( "kwargs" ),
					contextJson: args.GetElement( "context" ) ) );
			},
			new McpToolAnnotations( "Odoo Execute / Raw RPC", ReadOnlyHint: false, OpenWorldHint: true ) );
	}
}

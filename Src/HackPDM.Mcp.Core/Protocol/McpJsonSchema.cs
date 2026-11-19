using System.Text.Json.Nodes;

namespace HackPDM.Mcp.Protocol;

/// <summary>Fluent JSON Schema builder for MCP tool input definitions.</summary>
public static class McpSchema
{
	public static JsonObject EmptyObject() => new()
	{
		[ "type" ] = "object",
		[ "properties" ] = new JsonObject(),
		[ "additionalProperties" ] = false,
	};

	public static SchemaObjectBuilder Object( bool additionalProperties = false ) => new( additionalProperties );

	public static JsonObject String( string description, string? defaultValue = null )
	{
		JsonObject o = new()
		{
			[ "type" ] = "string",
			[ "description" ] = description,
		};
		if( defaultValue is not null )
			o[ "default" ] = defaultValue;
		return o;
	}

	public static JsonObject Enum( string description, params string[] values )
	{
		JsonArray arr = [];
		foreach( string v in values )
			arr.Add( v );
		return new JsonObject
		{
			[ "type" ] = "string",
			[ "description" ] = description,
			[ "enum" ] = arr,
		};
	}

	public static JsonObject Integer( string description, int? minimum = null, int? maximum = null, int? defaultValue = null )
	{
		JsonObject o = new()
		{
			[ "type" ] = "integer",
			[ "description" ] = description,
		};
		if( minimum is int min )
			o[ "minimum" ] = min;
		if( maximum is int max )
			o[ "maximum" ] = max;
		if( defaultValue is int def )
			o[ "default" ] = def;
		return o;
	}

	public static JsonObject Number( string description, double? defaultValue = null )
	{
		JsonObject o = new()
		{
			[ "type" ] = "number",
			[ "description" ] = description,
		};
		if( defaultValue is double def )
			o[ "default" ] = def;
		return o;
	}

	public static JsonObject Boolean( string description, bool? defaultValue = null )
	{
		JsonObject o = new()
		{
			[ "type" ] = "boolean",
			[ "description" ] = description,
		};
		if( defaultValue is bool def )
			o[ "default" ] = def;
		return o;
	}

	public static JsonObject Array( string description, JsonObject items ) => new()
	{
		[ "type" ] = "array",
		[ "description" ] = description,
		[ "items" ] = items,
	};

	public static JsonObject StringArray( string description )
		=> Array( description, new JsonObject { [ "type" ] = "string" } );

	public static JsonObject IntegerArray( string description )
		=> Array( description, new JsonObject { [ "type" ] = "integer" } );

	public static JsonObject Any( string description ) => new()
	{
		[ "description" ] = description,
	};

	public static JsonObject AnyObject( string description ) => new()
	{
		[ "type" ] = "object",
		[ "description" ] = description,
		[ "additionalProperties" ] = true,
	};

	public static JsonObject AnyArray( string description ) => new()
	{
		[ "type" ] = "array",
		[ "description" ] = description,
	};

	public sealed class SchemaObjectBuilder( bool additionalProperties )
	{
		private readonly JsonObject _properties = [];
		private readonly List<string> _required = [];

		public SchemaObjectBuilder Prop( string name, JsonObject schema, bool required = false )
		{
			_properties[ name ] = schema;
			if( required )
				_required.Add( name );
			return this;
		}

		public JsonObject Build()
		{
			JsonObject obj = new()
			{
				[ "type" ] = "object",
				[ "properties" ] = _properties,
				[ "additionalProperties" ] = additionalProperties,
			};
			if( _required.Count > 0 )
			{
				JsonArray req = [];
				foreach( string r in _required )
					req.Add( r );
				obj[ "required" ] = req;
			}
			return obj;
		}
	}
}

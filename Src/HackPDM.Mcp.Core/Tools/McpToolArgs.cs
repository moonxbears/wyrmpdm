using System.Globalization;
using System.Text.Json;

using HackPDM.Mcp.Protocol;

namespace HackPDM.Mcp.Tools;

/// <summary>Safe, typed argument extraction over an MCP tool call's JSON <c>arguments</c> object.</summary>
public readonly struct McpToolArgs( JsonElement? root )
{
	public JsonElement? Raw => root;

	public bool TryGetProperty( string name, out JsonElement value )
	{
		if( root is { ValueKind: JsonValueKind.Object } obj && obj.TryGetProperty( name, out JsonElement found )
			&& found.ValueKind is not ( JsonValueKind.Null or JsonValueKind.Undefined ) )
		{
			value = found;
			return true;
		}
		value = default;
		return false;
	}

	public JsonElement? GetElement( string name )
		=> TryGetProperty( name, out JsonElement el ) ? el : null;

	public string? GetString( string name, string? defaultValue = null )
	{
		if( !TryGetProperty( name, out JsonElement el ) )
			return defaultValue;
		return el.ValueKind == JsonValueKind.String ? el.GetString() : el.GetRawText();
	}

	public string RequireString( string name )
	{
		string? value = GetString( name );
		if( string.IsNullOrWhiteSpace( value ) )
			throw new McpProtocolException( JsonRpcErrorCodes.InvalidParams, $"Missing required string parameter '{name}'." );
		return value;
	}

	public int? GetInt( string name, int? defaultValue = null )
	{
		if( !TryGetProperty( name, out JsonElement el ) )
			return defaultValue;
		if( el.ValueKind == JsonValueKind.Number && el.TryGetInt32( out int i ) )
			return i;
		if( el.ValueKind == JsonValueKind.String && int.TryParse( el.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed ) )
			return parsed;
		throw new McpProtocolException( JsonRpcErrorCodes.InvalidParams, $"Parameter '{name}' must be an integer." );
	}

	public bool HasProperty( string name )
		=> root is { ValueKind: JsonValueKind.Object } obj && obj.TryGetProperty( name, out JsonElement found )
			&& found.ValueKind != JsonValueKind.Undefined;

	public bool IsNull( string name )
		=> root is { ValueKind: JsonValueKind.Object } obj && obj.TryGetProperty( name, out JsonElement found )
			&& found.ValueKind == JsonValueKind.Null;

	public long? GetLong( string name, long? defaultValue = null )
	{
		if( !TryGetProperty( name, out JsonElement el ) )
			return defaultValue;
		if( el.ValueKind == JsonValueKind.Number && el.TryGetInt64( out long l ) )
			return l;
		if( el.ValueKind == JsonValueKind.String && long.TryParse( el.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed ) )
			return parsed;
		throw new McpProtocolException( JsonRpcErrorCodes.InvalidParams, $"Parameter '{name}' must be an integer." );
	}

	public int RequireInt( string name )
		=> GetInt( name ) ?? throw new McpProtocolException( JsonRpcErrorCodes.InvalidParams, $"Missing required integer parameter '{name}'." );

	public bool? GetNullableBool( string name )
	{
		if( !TryGetProperty( name, out JsonElement el ) )
			return null;
		if( el.ValueKind == JsonValueKind.True )
			return true;
		if( el.ValueKind == JsonValueKind.False )
			return false;
		if( el.ValueKind == JsonValueKind.String && bool.TryParse( el.GetString(), out bool parsed ) )
			return parsed;
		throw new McpProtocolException( JsonRpcErrorCodes.InvalidParams, $"Parameter '{name}' must be a boolean." );
	}

	public bool GetBool( string name, bool defaultValue = false )
		=> GetNullableBool( name ) ?? defaultValue;

	public decimal? GetDecimal( string name )
	{
		if( !TryGetProperty( name, out JsonElement el ) )
			return null;
		if( el.ValueKind == JsonValueKind.Number && el.TryGetDecimal( out decimal d ) )
			return d;
		if( el.ValueKind == JsonValueKind.String && decimal.TryParse( el.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out decimal parsed ) )
			return parsed;
		throw new McpProtocolException( JsonRpcErrorCodes.InvalidParams, $"Parameter '{name}' must be a decimal number." );
	}

	public double? GetDouble( string name )
	{
		if( !TryGetProperty( name, out JsonElement el ) )
			return null;
		if( el.ValueKind == JsonValueKind.Number && el.TryGetDouble( out double d ) )
			return d;
		if( el.ValueKind == JsonValueKind.String && double.TryParse( el.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed ) )
			return parsed;
		throw new McpProtocolException( JsonRpcErrorCodes.InvalidParams, $"Parameter '{name}' must be a number." );
	}

	public List<string>? GetStringList( string name )
	{
		if( !TryGetProperty( name, out JsonElement el ) )
			return null;
		if( el.ValueKind == JsonValueKind.String )
			return [ el.GetString()! ];
		if( el.ValueKind != JsonValueKind.Array )
			throw new McpProtocolException( JsonRpcErrorCodes.InvalidParams, $"Parameter '{name}' must be an array of strings." );

		List<string> list = [];
		foreach( JsonElement item in el.EnumerateArray() )
		{
			if( item.ValueKind == JsonValueKind.String && item.GetString() is { } s )
				list.Add( s );
			else if( item.ValueKind is not ( JsonValueKind.Null or JsonValueKind.Undefined ) )
				list.Add( item.GetRawText() );
		}
		return list;
	}

	public List<int>? GetIntList( string name )
	{
		if( !TryGetProperty( name, out JsonElement el ) )
			return null;
		if( el.ValueKind == JsonValueKind.Number && el.TryGetInt32( out int single ) )
			return [ single ];
		if( el.ValueKind != JsonValueKind.Array )
			throw new McpProtocolException( JsonRpcErrorCodes.InvalidParams, $"Parameter '{name}' must be an array of integers." );

		List<int> list = [];
		foreach( JsonElement item in el.EnumerateArray() )
		{
			if( item.ValueKind == JsonValueKind.Number && item.TryGetInt32( out int i ) )
				list.Add( i );
			else if( item.ValueKind == JsonValueKind.String && int.TryParse( item.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed ) )
				list.Add( parsed );
			else
				throw new McpProtocolException( JsonRpcErrorCodes.InvalidParams, $"Parameter '{name}' must contain only integers." );
		}
		return list;
	}
}

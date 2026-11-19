using System.Reflection;
using System.Text.Json;

using HackPDM.Core.Hack;
using HackPDM.Domain.OdooModels;
using HackPDM.Domain.OdooModels.Models;
using HackPDM.Infrastructure.Odoo;
using HackPDM.Infrastructure.Odoo.Models;
using HackPDM.Shared.GlobalData;
using HackPDM.Shared.OdooAttributes;

namespace HackPDM.Mcp.Services;

/// <summary>Schema metadata for one [OdooProp] field on a HackPDM Odoo model class.</summary>
public sealed record OdooFieldSchema( string FieldName, string OdooType, string ClrPropertyName, string ClrType );

/// <summary>Schema metadata for one [OdooModel] class compiled into HackPDM.Infrastructure.</summary>
public sealed record OdooModelSchema( string Model, string InternalName, string ClrClassName, IReadOnlyList<OdooFieldSchema> Fields );

/// <summary>
/// Helpers converting between JSON inputs/outputs, Odoo JSON-RPC values/domains, and HackPDM models.
/// </summary>
public static class OdooJsonHelper
{
	private static readonly Lazy<IReadOnlyList<OdooModelSchema>> ModelCatalogLazy = new( BuildModelCatalog );

	/// <summary>All Odoo models defined with <c>[OdooModel]</c> in <c>HackPDM.Infrastructure</c>.</summary>
	public static IReadOnlyList<OdooModelSchema> ModelCatalog => ModelCatalogLazy.Value;

	public static OdooModelSchema? FindModel( string modelName )
		=> ModelCatalog.FirstOrDefault( m =>
			string.Equals( m.Model, modelName, StringComparison.OrdinalIgnoreCase )
			|| string.Equals( m.InternalName, modelName, StringComparison.OrdinalIgnoreCase )
			|| string.Equals( m.ClrClassName, modelName, StringComparison.OrdinalIgnoreCase ) );

	private static IReadOnlyList<OdooModelSchema> BuildModelCatalog()
	{
		Assembly assembly = typeof( HpEntry ).Assembly;
		List<OdooModelSchema> list = [];
		foreach( Type type in assembly.GetTypes() )
		{
			if( type.IsAbstract || !type.IsClass )
				continue;
			OdooModelAttribute? attr = type.GetCustomAttribute<OdooModelAttribute>();
			if( attr is null )
				continue;

			List<OdooFieldSchema> fields = [];
			foreach( PropertyInfo prop in type.GetProperties( BindingFlags.Public | BindingFlags.Instance ) )
			{
				foreach( OdooPropAttribute propAttr in prop.GetCustomAttributes<OdooPropAttribute>() )
				{
					fields.Add( new OdooFieldSchema(
						FieldName: propAttr.OdooFieldName,
						OdooType: propAttr.OdooType.ToString(),
						ClrPropertyName: prop.Name,
						ClrType: FormatTypeName( prop.PropertyType ) ) );
				}
			}

			list.Add( new OdooModelSchema(
				Model: attr.DBName,
				InternalName: attr.OdooName,
				ClrClassName: type.Name,
				Fields: [ .. fields.OrderBy( f => f.FieldName, StringComparer.Ordinal ) ] ) );
		}
		return [ .. list.OrderBy( m => m.Model, StringComparer.Ordinal ) ];
	}

	private static string FormatTypeName( Type t )
	{
		if( Nullable.GetUnderlyingType( t ) is { } underlying )
			return $"{FormatTypeName( underlying )}?";
		if( !t.IsGenericType )
			return t.Name;
		string name = t.Name[ ..t.Name.IndexOf( '`' ) ];
		return $"{name}<{string.Join( ", ", t.GetGenericArguments().Select( FormatTypeName ) )}>";
	}

	/// <summary>Converts a <see cref="JsonElement"/> into plain .NET objects suitable for <see cref="OdooRpc"/>.</summary>
	public static object? ToRpcValue( JsonElement element )
		=> OdooValues.FromJson( element );

	/// <summary>Converts a JSON object into a string-to-object dictionary for Odoo create/write calls.</summary>
	public static Dictionary<string, object?> ToDictionary( JsonElement element )
	{
		if( element.ValueKind != JsonValueKind.Object )
			throw new ArgumentException( "Expected a JSON object for record field values." );
		Dictionary<string, object?> map = new( StringComparer.Ordinal );
		foreach( JsonProperty prop in element.EnumerateObject() )
			map[ prop.Name ] = ToRpcValue( prop.Value );
		return map;
	}

	/// <summary>
	/// Parses a JSON array in standard Odoo domain syntax (supporting prefix operators "&amp;", "|", "!"
	/// and 3-element term arrays <c>[field, op, value]</c>) into an <see cref="OdooDomain"/> or raw RPC list.
	/// </summary>
	public static List<object?> ParseRawDomain( JsonElement? element )
	{
		if( element is null or { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } )
			return [];
		if( element.Value.ValueKind != JsonValueKind.Array )
			throw new ArgumentException( "Odoo domain must be a JSON array, e.g. [[\"name\", \"ilike\", \"%.sldprt\"]]." );

		List<object?> result = [];
		foreach( JsonElement item in element.Value.EnumerateArray() )
		{
			if( item.ValueKind == JsonValueKind.String )
			{
				string op = item.GetString() ?? "";
				if( op is not ( "&" or "|" or "!" ) )
					throw new ArgumentException( $"Invalid domain prefix operator '{op}'. Expected '&', '|', or '!'." );
				result.Add( op );
			}
			else if( item.ValueKind == JsonValueKind.Array )
			{
				List<JsonElement> parts = [ .. item.EnumerateArray() ];
				if( parts.Count != 3 || parts[ 0 ].ValueKind != JsonValueKind.String || parts[ 1 ].ValueKind != JsonValueKind.String )
					throw new ArgumentException( "Each domain term must be a 3-element array: [fieldName, operator, value]." );
				result.Add( new object?[]
				{
					parts[ 0 ].GetString(),
					parts[ 1 ].GetString(),
					ToRpcValue( parts[ 2 ] ),
				} );
			}
			else
			{
				throw new ArgumentException( $"Unexpected item kind '{item.ValueKind}' inside Odoo domain array." );
			}
		}
		return result;
	}

	/// <summary>Serializes a <see cref="DirectoryNode"/> subtree into a clean JSON-friendly object.</summary>
	public static object SerializeDirectoryNode( DirectoryNode node, string? parentPath = null, int depth = 0, int maxDepth = int.MaxValue )
	{
		string fullPath = string.IsNullOrEmpty( parentPath ) ? node.Name : $"{parentPath}\\{node.Name}";
		string origin = node.IsRemote && node.ExistsLocally
			? "Synced"
			: node.IsRemote
				? "RemoteOnly"
				: "LocalOnly";

		List<object>? children = depth < maxDepth
			? [ .. node.Children.Select( c => SerializeDirectoryNode( c, fullPath, depth + 1, maxDepth ) ) ]
			: null;

		return new
		{
			name = node.Name,
			directoryId = node.DirectoryId,
			fullPath,
			origin,
			existsRemotely = node.IsRemote,
			existsLocally = node.ExistsLocally,
			childCount = node.Children.Count,
			truncatedChildren = depth >= maxDepth && node.Children.Count > 0 ? true : (bool?)null,
			children,
		};
	}

	/// <summary>Serializes a <see cref="SubdirectoryDto"/> (from <c>get_children_directories_by_id</c>) into JSON.</summary>
	public static object SerializeSubdirectoryDto( SubdirectoryDto dto, string? parentPath = null, int depth = 0, int maxDepth = int.MaxValue, bool includeEntries = false )
	{
		string fullPath = string.IsNullOrEmpty( parentPath ) ? dto.Name : $"{parentPath}\\{dto.Name}";
		return new
		{
			name = dto.Name,
			directoryId = dto.Id,
			fullPath,
			childCount = dto.Directories.Count,
			entryCount = dto.Entries.Count,
			entries = includeEntries
				? dto.Entries.Select( e => new
				{
					id = e.Id,
					name = e.Name,
					type = e.Type,
					category = e.Category,
					fullName = e.FullName,
					size = e.Size,
					latestVersionId = e.LatestVersionId,
					latestReleaseId = e.LatestReleaseId,
					latestChecksum = e.LatestChecksum,
					latestDate = e.LatestDate,
					checkoutUserId = e.CheckoutUserId,
					deleted = e.Deleted,
				} ).ToList()
				: null,
			children = depth < maxDepth
				? dto.Directories.Select( c => SerializeSubdirectoryDto( c, fullPath, depth + 1, maxDepth, includeEntries ) ).ToList()
				: null,
			truncatedChildren = depth >= maxDepth && dto.Directories.Count > 0 ? true : (bool?)null,
		};
	}

	public static object? SerializeMany2One( IMany2One? m2o )
		=> m2o is null ? null : new { id = m2o.id, name = m2o.name };

	public static string DetectImageMimeType( ReadOnlySpan<byte> bytes )
	{
		if( bytes.Length >= 8 && bytes[ 0 ] == 0x89 && bytes[ 1 ] == 0x50 && bytes[ 2 ] == 0x4E && bytes[ 3 ] == 0x47 )
			return "image/png";
		if( bytes.Length >= 3 && bytes[ 0 ] == 0xFF && bytes[ 1 ] == 0xD8 && bytes[ 2 ] == 0xFF )
			return "image/jpeg";
		if( bytes.Length >= 6 && bytes[ 0 ] == 'G' && bytes[ 1 ] == 'I' && bytes[ 2 ] == 'F' )
			return "image/gif";
		if( bytes.Length >= 2 && bytes[ 0 ] == 'B' && bytes[ 1 ] == 'M' )
			return "image/bmp";
		if( bytes.Length >= 12 && bytes[ 0 ] == 'R' && bytes[ 1 ] == 'I' && bytes[ 2 ] == 'F' && bytes[ 3 ] == 'F'
			&& bytes[ 8 ] == 'W' && bytes[ 9 ] == 'E' && bytes[ 10 ] == 'B' && bytes[ 11 ] == 'P' )
			return "image/webp";
		return "application/octet-stream";
	}
}

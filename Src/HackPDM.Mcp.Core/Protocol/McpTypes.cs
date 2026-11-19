using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace HackPDM.Mcp.Protocol;

/// <summary>Shared JSON serialization options for MCP JSON-RPC 2.0 envelopes and payloads.</summary>
public static class McpJson
{
	public static JsonSerializerOptions Options { get; } = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		WriteIndented = false,
	};

	public static JsonSerializerOptions IndentedOptions { get; } = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		WriteIndented = true,
	};

	public static string SerializeResult( object? value )
		=> JsonSerializer.Serialize( value, IndentedOptions );
}

/// <summary>Standard JSON-RPC 2.0 error codes used by MCP.</summary>
public static class JsonRpcErrorCodes
{
	public const int ParseError = -32700;
	public const int InvalidRequest = -32600;
	public const int MethodNotFound = -32601;
	public const int InvalidParams = -32602;
	public const int InternalError = -32603;
	public const int ResourceNotFound = -32002;
}

public sealed record JsonRpcError(
	[property: JsonPropertyName( "code" )] int Code,
	[property: JsonPropertyName( "message" )] string Message,
	[property: JsonPropertyName( "data" )] object? Data = null );

public sealed record McpServerInfo(
	[property: JsonPropertyName( "name" )] string Name,
	[property: JsonPropertyName( "version" )] string Version,
	[property: JsonPropertyName( "title" )] string? Title = null );

public sealed record McpToolAnnotations(
	[property: JsonPropertyName( "title" )] string? Title = null,
	[property: JsonPropertyName( "readOnlyHint" )] bool? ReadOnlyHint = null,
	[property: JsonPropertyName( "destructiveHint" )] bool? DestructiveHint = null,
	[property: JsonPropertyName( "idempotentHint" )] bool? IdempotentHint = null,
	[property: JsonPropertyName( "openWorldHint" )] bool? OpenWorldHint = null );

public sealed record McpToolDefinition(
	[property: JsonPropertyName( "name" )] string Name,
	[property: JsonPropertyName( "description" )] string Description,
	[property: JsonPropertyName( "inputSchema" )] JsonObject InputSchema,
	[property: JsonPropertyName( "annotations" )] McpToolAnnotations? Annotations = null );

public sealed record McpContent
{
	[JsonPropertyName( "type" )]
	public required string Type { get; init; }

	[JsonPropertyName( "text" )]
	public string? Text { get; init; }

	[JsonPropertyName( "data" )]
	public string? Data { get; init; }

	[JsonPropertyName( "mimeType" )]
	public string? MimeType { get; init; }

	public static McpContent TextContent( string text ) => new() { Type = "text", Text = text };

	public static McpContent JsonContent( object? value ) => new()
	{
		Type = "text",
		Text = value is string s ? s : McpJson.SerializeResult( value ),
	};

	public static McpContent ImageContent( byte[] bytes, string mimeType = "image/png" ) => new()
	{
		Type = "image",
		Data = Convert.ToBase64String( bytes ),
		MimeType = mimeType,
	};
}

public sealed record McpToolResult(
	[property: JsonPropertyName( "content" )] IReadOnlyList<McpContent> Content,
	[property: JsonPropertyName( "isError" )] bool? IsError = null )
{
	public static McpToolResult Ok( object? payload )
		=> new( [ McpContent.JsonContent( payload ) ] );

	public static McpToolResult OkText( string text )
		=> new( [ McpContent.TextContent( text ) ] );

	public static McpToolResult OkImage( byte[] imageBytes, string mimeType, object? metadata = null )
	{
		List<McpContent> items = [ McpContent.ImageContent( imageBytes, mimeType ) ];
		if( metadata is not null )
			items.Add( McpContent.JsonContent( metadata ) );
		return new( items );
	}

	public static McpToolResult Error( string message, object? details = null )
	{
		object payload = details is null
			? new { error = message }
			: new { error = message, details };
		return new( [ McpContent.JsonContent( payload ) ], IsError: true );
	}
}

public sealed record McpResourceDefinition(
	[property: JsonPropertyName( "uri" )] string Uri,
	[property: JsonPropertyName( "name" )] string Name,
	[property: JsonPropertyName( "description" )] string? Description = null,
	[property: JsonPropertyName( "mimeType" )] string? MimeType = "application/json" );

public sealed record McpResourceTemplate(
	[property: JsonPropertyName( "uriTemplate" )] string UriTemplate,
	[property: JsonPropertyName( "name" )] string Name,
	[property: JsonPropertyName( "description" )] string? Description = null,
	[property: JsonPropertyName( "mimeType" )] string? MimeType = "application/json" );

public sealed record McpResourceContent(
	[property: JsonPropertyName( "uri" )] string Uri,
	[property: JsonPropertyName( "mimeType" )] string? MimeType = "application/json",
	[property: JsonPropertyName( "text" )] string? Text = null,
	[property: JsonPropertyName( "blob" )] string? Blob = null );

public sealed record McpPromptArgument(
	[property: JsonPropertyName( "name" )] string Name,
	[property: JsonPropertyName( "description" )] string? Description = null,
	[property: JsonPropertyName( "required" )] bool? Required = null );

public sealed record McpPromptDefinition(
	[property: JsonPropertyName( "name" )] string Name,
	[property: JsonPropertyName( "description" )] string? Description = null,
	[property: JsonPropertyName( "arguments" )] IReadOnlyList<McpPromptArgument>? Arguments = null );

public sealed record McpPromptMessage(
	[property: JsonPropertyName( "role" )] string Role,
	[property: JsonPropertyName( "content" )] McpContent Content );

public sealed record McpPromptResult(
	[property: JsonPropertyName( "description" )] string? Description,
	[property: JsonPropertyName( "messages" )] IReadOnlyList<McpPromptMessage> Messages );

/// <summary>Delegate invoked when an MCP client calls a registered tool.</summary>
public delegate Task<McpToolResult> McpToolHandler( JsonElement? arguments, CancellationToken cancellationToken );

/// <summary>Delegate invoked when an MCP client reads a registered static resource.</summary>
public delegate Task<McpResourceContent> McpResourceHandler( string uri, CancellationToken cancellationToken );

/// <summary>Delegate invoked when an MCP client reads a URI matching a resource template.</summary>
public delegate Task<McpResourceContent> McpResourceTemplateHandler( string uri, IReadOnlyDictionary<string, string> variables, CancellationToken cancellationToken );

/// <summary>Delegate invoked when an MCP client requests a prompt.</summary>
public delegate Task<McpPromptResult> McpPromptHandler( IReadOnlyDictionary<string, string> arguments, CancellationToken cancellationToken );

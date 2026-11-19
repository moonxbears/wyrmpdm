using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace HackPDM.Mcp.Protocol;

/// <summary>
/// Authenticated HTTP client that forwards MCP tool calls and resource reads to a live
/// HackPDM GUI client's embedded <see cref="McpHttpServer"/>.
/// </summary>
public sealed class McpRemoteProxyClient : IDisposable
{
	private readonly HttpClient _http;
	private long _nextId;

	public string BaseUrl { get; }
	public string McpEndpointUrl { get; }
	public string? AuthToken { get; }
	public int? RemotePid { get; }

	public McpRemoteProxyClient( string baseUrl, string? authToken = null, int? remotePid = null )
	{
		BaseUrl = baseUrl.TrimEnd( '/' );
		McpEndpointUrl = BaseUrl.EndsWith( "/mcp", StringComparison.OrdinalIgnoreCase )
			? BaseUrl
			: $"{BaseUrl}/mcp";
		AuthToken = string.IsNullOrEmpty( authToken ) ? null : authToken;
		RemotePid = remotePid;
		_http = new HttpClient { Timeout = TimeSpan.FromMinutes( 10 ) };
	}

	public async Task<JsonElement?> GetHealthAsync( CancellationToken cancellationToken = default )
	{
		string healthUrl = BaseUrl.EndsWith( "/mcp", StringComparison.OrdinalIgnoreCase )
			? BaseUrl[ ..^4 ] + "/health"
			: BaseUrl + "/health";
		try
		{
			using HttpRequestMessage req = new( HttpMethod.Get, healthUrl );
			if( AuthToken is not null )
				req.Headers.Authorization = new AuthenticationHeaderValue( "Bearer", AuthToken );
			using HttpResponseMessage resp = await _http.SendAsync( req, cancellationToken );
			if( !resp.IsSuccessStatusCode )
				return null;
			string json = await resp.Content.ReadAsStringAsync( cancellationToken );
			using JsonDocument doc = JsonDocument.Parse( json );
			return doc.RootElement.Clone();
		}
		catch
		{
			return null;
		}
	}

	public async Task<McpToolResult> CallToolAsync( string toolName, JsonElement? arguments, CancellationToken cancellationToken = default )
	{
		object parameters = arguments is { } a
			? new { name = toolName, arguments = a }
			: new { name = toolName };

		JsonElement result = await SendRpcAsync( "tools/call", parameters, cancellationToken );
		McpToolResult? parsed = result.Deserialize<McpToolResult>( McpJson.Options );
		return parsed ?? McpToolResult.Error( $"Live HackPDM client returned an invalid tools/call response for '{toolName}'." );
	}

	public async Task<McpResourceContent> ReadResourceAsync( string uri, CancellationToken cancellationToken = default )
	{
		JsonElement result = await SendRpcAsync( "resources/read", new { uri }, cancellationToken );
		if( result.TryGetProperty( "contents", out JsonElement contents )
			&& contents.ValueKind == JsonValueKind.Array
			&& contents.GetArrayLength() > 0 )
		{
			McpResourceContent? first = contents[ 0 ].Deserialize<McpResourceContent>( McpJson.Options );
			if( first is not null )
				return first;
		}
		throw new McpProtocolException( JsonRpcErrorCodes.ResourceNotFound, $"Live HackPDM client returned no content for '{uri}'." );
	}

	private async Task<JsonElement> SendRpcAsync( string method, object parameters, CancellationToken cancellationToken )
	{
		long id = Interlocked.Increment( ref _nextId );
		string payload = JsonSerializer.Serialize( new
		{
			jsonrpc = "2.0",
			id,
			method,
			@params = parameters,
		}, McpJson.Options );

		using HttpRequestMessage req = new( HttpMethod.Post, McpEndpointUrl )
		{
			Content = new StringContent( payload, Encoding.UTF8, "application/json" ),
		};
		if( AuthToken is not null )
			req.Headers.Authorization = new AuthenticationHeaderValue( "Bearer", AuthToken );

		using HttpResponseMessage resp = await _http.SendAsync( req, cancellationToken );
		string responseBody = await resp.Content.ReadAsStringAsync( cancellationToken );
		if( !resp.IsSuccessStatusCode )
			throw new InvalidOperationException( $"Live HackPDM bridge HTTP {(int)resp.StatusCode}: {responseBody}" );

		using JsonDocument doc = JsonDocument.Parse( responseBody );
		JsonElement root = doc.RootElement;
		if( root.TryGetProperty( "error", out JsonElement err ) && err.ValueKind == JsonValueKind.Object )
		{
			int code = err.TryGetProperty( "code", out JsonElement codeEl ) && codeEl.TryGetInt32( out int c ) ? c : JsonRpcErrorCodes.InternalError;
			string message = err.TryGetProperty( "message", out JsonElement msgEl ) ? msgEl.GetString() ?? "Remote bridge error" : "Remote bridge error";
			throw new McpProtocolException( code, message );
		}

		if( root.TryGetProperty( "result", out JsonElement res ) )
			return res.Clone();

		throw new InvalidOperationException( "Live HackPDM bridge returned a JSON-RPC message with neither result nor error." );
	}

	public void Dispose() => _http.Dispose();
}

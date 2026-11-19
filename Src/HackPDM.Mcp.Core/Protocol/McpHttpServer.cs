using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HackPDM.Mcp.Protocol;

/// <summary>
/// Loopback-only HTTP/1.1 MCP Streamable HTTP server (<c>/mcp</c>, <c>/health</c>) built on
/// <see cref="TcpListener"/> so it runs identically on Windows, Linux, and macOS with zero
/// URLACL/elevation requirements and supports ephemeral port 0 allocation.
/// </summary>
public sealed class McpHttpServer : IAsyncDisposable, IDisposable
{
	private const int MaxHeaderBytes = 64 * 1024;
	private const int MaxBodyBytes = 32 * 1024 * 1024; // 32 MiB (accommodates preview/screenshot/file transfers)

	private readonly McpServer _server;
	private readonly Func<object>? _healthProvider;
	private readonly byte[]? _tokenBytes;
	private readonly TcpListener _listener;
	private CancellationTokenSource? _cts;
	private Task? _acceptLoop;
	private int _disposed;

	public int Port { get; private set; }
	public string BaseUrl => $"http://127.0.0.1:{Port}";
	public string McpEndpointUrl => $"{BaseUrl}/mcp";
	public string? AuthToken { get; }
	public bool IsRunning => _acceptLoop is { IsCompleted: false };

	public McpHttpServer( McpServer server, int port = 0, string? authToken = null, Func<object>? healthProvider = null )
	{
		_server = server;
		_healthProvider = healthProvider;
		AuthToken = string.IsNullOrEmpty( authToken ) ? null : authToken;
		_tokenBytes = AuthToken is null ? null : Encoding.UTF8.GetBytes( AuthToken );
		_listener = new TcpListener( IPAddress.Loopback, port );
	}

	public static string GenerateSecureToken()
		=> Convert.ToHexStringLower( RandomNumberGenerator.GetBytes( 32 ) );

	public void Start()
	{
		if( _acceptLoop is not null )
			return;
		_listener.Start();
		Port = ( (IPEndPoint)_listener.LocalEndpoint ).Port;
		_cts = new CancellationTokenSource();
		_acceptLoop = Task.Run( () => AcceptLoopAsync( _cts.Token ) );
	}

	private async Task AcceptLoopAsync( CancellationToken cancellationToken )
	{
		while( !cancellationToken.IsCancellationRequested )
		{
			TcpClient client;
			try
			{
				client = await _listener.AcceptTcpClientAsync( cancellationToken );
			}
			catch( OperationCanceledException )
			{
				break;
			}
			catch( ObjectDisposedException )
			{
				break;
			}
			catch( SocketException )
			{
				if( cancellationToken.IsCancellationRequested )
					break;
				await Task.Delay( 50, cancellationToken );
				continue;
			}

			_ = Task.Run( () => HandleClientAsync( client, cancellationToken ), CancellationToken.None );
		}
	}

	private async Task HandleClientAsync( TcpClient client, CancellationToken cancellationToken )
	{
		using( client )
		{
			client.NoDelay = true;
			using NetworkStream stream = client.GetStream();
			try
			{
				using CancellationTokenSource readCts = CancellationTokenSource.CreateLinkedTokenSource( cancellationToken );
				readCts.CancelAfter( TimeSpan.FromMinutes( 15 ) );

				HttpRequestData? request = await ReadHttpRequestAsync( stream, readCts.Token );
				if( request is null )
					return;

				await RouteRequestAsync( stream, request, readCts.Token );
			}
			catch( Exception ex ) when( ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException )
			{
				// Client disconnected or server shutting down
			}
			catch( Exception ex )
			{
				try
				{
					string body = JsonSerializer.Serialize( new { error = ex.Message }, McpJson.Options );
					await WriteResponseAsync( stream, 500, "Internal Server Error", "application/json", Encoding.UTF8.GetBytes( body ), CancellationToken.None );
				}
				catch
				{
					// Ignore write failure on broken socket
				}
			}
		}
	}

	private async Task RouteRequestAsync( NetworkStream stream, HttpRequestData request, CancellationToken cancellationToken )
	{
		// Protect against cross-site browser requests / DNS rebinding
		if( request.Headers.TryGetValue( "origin", out string? origin ) && !IsLoopbackOrigin( origin ) )
		{
			await WriteJsonAsync( stream, 403, "Forbidden", new { error = "Cross-origin requests from non-loopback origins are forbidden." }, cancellationToken );
			return;
		}

		string path = request.Path.Split( '?', 2 )[ 0 ];

		if( request.Method.Equals( "OPTIONS", StringComparison.OrdinalIgnoreCase ) )
		{
			await WriteResponseAsync( stream, 204, "No Content", null, [], cancellationToken );
			return;
		}

		if( path.Equals( "/health", StringComparison.OrdinalIgnoreCase ) && request.Method.Equals( "GET", StringComparison.OrdinalIgnoreCase ) )
		{
			object health = _healthProvider?.Invoke() ?? new { status = "ok", server = _server.ServerInfo.Name, version = _server.ServerInfo.Version };
			await WriteJsonAsync( stream, 200, "OK", health, cancellationToken );
			return;
		}

		if( !IsAuthorized( request.Headers ) )
		{
			await WriteJsonAsync( stream, 401, "Unauthorized", new { error = "Missing or invalid Bearer token." }, cancellationToken );
			return;
		}

		if( !path.Equals( "/mcp", StringComparison.OrdinalIgnoreCase ) && !path.Equals( "/", StringComparison.OrdinalIgnoreCase ) )
		{
			await WriteJsonAsync( stream, 404, "Not Found", new { error = $"Unknown path '{path}'." }, cancellationToken );
			return;
		}

		if( request.Method.Equals( "DELETE", StringComparison.OrdinalIgnoreCase ) )
		{
			await WriteResponseAsync( stream, 200, "OK", "application/json", Encoding.UTF8.GetBytes( "{\"closed\":true}" ), cancellationToken );
			return;
		}

		if( request.Method.Equals( "GET", StringComparison.OrdinalIgnoreCase ) )
		{
			await WriteJsonAsync( stream, 405, "Method Not Allowed", new { error = "Use POST /mcp with JSON-RPC 2.0 payloads." }, cancellationToken,
				extraHeaders: [ ("Allow", "POST, DELETE, OPTIONS") ] );
			return;
		}

		if( !request.Method.Equals( "POST", StringComparison.OrdinalIgnoreCase ) )
		{
			await WriteJsonAsync( stream, 405, "Method Not Allowed", new { error = $"Unsupported HTTP method '{request.Method}'." }, cancellationToken );
			return;
		}

		string bodyText = Encoding.UTF8.GetString( request.Body );
		string? responseJson = await _server.HandleMessageAsync( bodyText, cancellationToken );
		if( responseJson is null )
		{
			await WriteResponseAsync( stream, 202, "Accepted", null, [], cancellationToken );
			return;
		}

		await WriteResponseAsync( stream, 200, "OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes( responseJson ), cancellationToken );
	}

	private bool IsAuthorized( Dictionary<string, string> headers )
	{
		if( _tokenBytes is null )
			return true;

		string? provided = null;
		if( headers.TryGetValue( "authorization", out string? auth ) && auth.StartsWith( "Bearer ", StringComparison.OrdinalIgnoreCase ) )
			provided = auth[ "Bearer ".Length.. ].Trim();
		else if( headers.TryGetValue( "x-hackpdm-token", out string? custom ) )
			provided = custom.Trim();

		if( string.IsNullOrEmpty( provided ) )
			return false;

		byte[] providedBytes = Encoding.UTF8.GetBytes( provided );
		return providedBytes.Length == _tokenBytes.Length
			&& CryptographicOperations.FixedTimeEquals( providedBytes, _tokenBytes );
	}

	internal static bool IsLoopbackOrigin( string origin )
	{
		if( string.IsNullOrWhiteSpace( origin ) || origin.Equals( "null", StringComparison.OrdinalIgnoreCase ) )
			return true;
		if( !Uri.TryCreate( origin, UriKind.Absolute, out Uri? uri ) )
			return false;
		return uri.IsLoopback || uri.Host.Equals( "localhost", StringComparison.OrdinalIgnoreCase ) || uri.Host == "127.0.0.1" || uri.Host == "::1";
	}

	private static async Task<HttpRequestData?> ReadHttpRequestAsync( Stream stream, CancellationToken cancellationToken )
	{
		using MemoryStream headerBuf = new();
		byte[] single = new byte[ 1 ];
		int matchState = 0;

		while( headerBuf.Length < MaxHeaderBytes )
		{
			int read = await stream.ReadAsync( single.AsMemory( 0, 1 ), cancellationToken );
			if( read == 0 )
				return null;

			byte b = single[ 0 ];
			headerBuf.WriteByte( b );
			matchState = ( matchState, b ) switch
			{
				( 0, (byte)'\r' ) => 1,
				( 1, (byte)'\n' ) => 2,
				( 2, (byte)'\r' ) => 3,
				( 3, (byte)'\n' ) => 4,
				( _, (byte)'\r' ) => 1,
				_ => 0,
			};
			if( matchState == 4 )
				break;
		}

		if( matchState != 4 )
			return null;

		string headerText = Encoding.ASCII.GetString( headerBuf.GetBuffer(), 0, (int)headerBuf.Length - 4 );
		string[] lines = headerText.Split( "\r\n" );
		if( lines.Length == 0 )
			return null;

		string[] requestLine = lines[ 0 ].Split( ' ', StringSplitOptions.RemoveEmptyEntries );
		if( requestLine.Length < 2 )
			return null;

		string method = requestLine[ 0 ];
		string path = requestLine[ 1 ];
		Dictionary<string, string> headers = new( StringComparer.OrdinalIgnoreCase );
		for( int i = 1; i < lines.Length; i++ )
		{
			int colon = lines[ i ].IndexOf( ':' );
			if( colon <= 0 )
				continue;
			string name = lines[ i ][ ..colon ].Trim();
			string value = lines[ i ][ ( colon + 1 ).. ].Trim();
			headers[ name ] = value;
		}

		byte[] body = [];
		if( headers.TryGetValue( "transfer-encoding", out string? te ) && te.Contains( "chunked", StringComparison.OrdinalIgnoreCase ) )
		{
			body = await ReadChunkedBodyAsync( stream, cancellationToken );
		}
		else if( headers.TryGetValue( "content-length", out string? lenText )
			&& int.TryParse( lenText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int contentLength )
			&& contentLength > 0 )
		{
			if( contentLength > MaxBodyBytes )
				throw new InvalidOperationException( $"Request body exceeds limit of {MaxBodyBytes} bytes." );
			body = new byte[ contentLength ];
			await ReadExactAsync( stream, body, cancellationToken );
		}

		return new HttpRequestData( method, path, headers, body );
	}

	private static async Task<byte[]> ReadChunkedBodyAsync( Stream stream, CancellationToken cancellationToken )
	{
		using MemoryStream result = new();
		while( true )
		{
			string? sizeLine = await ReadAsciiLineAsync( stream, cancellationToken );
			if( sizeLine is null )
				break;
			int semi = sizeLine.IndexOf( ';' );
			string hex = ( semi >= 0 ? sizeLine[ ..semi ] : sizeLine ).Trim();
			if( !int.TryParse( hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int chunkSize ) || chunkSize < 0 )
				throw new InvalidOperationException( "Invalid chunked transfer encoding size." );
			if( chunkSize == 0 )
			{
				// Consume trailing CRLF
				_ = await ReadAsciiLineAsync( stream, cancellationToken );
				break;
			}
			if( result.Length + chunkSize > MaxBodyBytes )
				throw new InvalidOperationException( $"Chunked body exceeds limit of {MaxBodyBytes} bytes." );
			byte[] chunk = new byte[ chunkSize ];
			await ReadExactAsync( stream, chunk, cancellationToken );
			result.Write( chunk, 0, chunkSize );
			_ = await ReadAsciiLineAsync( stream, cancellationToken );
		}
		return result.ToArray();
	}

	private static async Task<string?> ReadAsciiLineAsync( Stream stream, CancellationToken cancellationToken )
	{
		using MemoryStream line = new();
		byte[] b = new byte[ 1 ];
		while( line.Length < 4096 )
		{
			int read = await stream.ReadAsync( b.AsMemory( 0, 1 ), cancellationToken );
			if( read == 0 )
				return line.Length == 0 ? null : Encoding.ASCII.GetString( line.ToArray() );
			if( b[ 0 ] == (byte)'\n' )
				break;
			if( b[ 0 ] != (byte)'\r' )
				line.WriteByte( b[ 0 ] );
		}
		return Encoding.ASCII.GetString( line.ToArray() );
	}

	private static async Task ReadExactAsync( Stream stream, byte[] buffer, CancellationToken cancellationToken )
	{
		int offset = 0;
		while( offset < buffer.Length )
		{
			int read = await stream.ReadAsync( buffer.AsMemory( offset, buffer.Length - offset ), cancellationToken );
			if( read == 0 )
				throw new EndOfStreamException( "Unexpected end of HTTP request body." );
			offset += read;
		}
	}

	private static Task WriteJsonAsync(
		Stream stream,
		int statusCode,
		string reason,
		object payload,
		CancellationToken cancellationToken,
		(string Name, string Value)[]? extraHeaders = null )
	{
		byte[] body = Encoding.UTF8.GetBytes( JsonSerializer.Serialize( payload, McpJson.Options ) );
		return WriteResponseAsync( stream, statusCode, reason, "application/json; charset=utf-8", body, cancellationToken, extraHeaders );
	}

	private static async Task WriteResponseAsync(
		Stream stream,
		int statusCode,
		string reason,
		string? contentType,
		byte[] body,
		CancellationToken cancellationToken,
		(string Name, string Value)[]? extraHeaders = null )
	{
		StringBuilder sb = new();
		sb.Append( "HTTP/1.1 " ).Append( statusCode ).Append( ' ' ).Append( reason ).Append( "\r\n" );
		sb.Append( "Connection: close\r\n" );
		sb.Append( "Cache-Control: no-store\r\n" );
		if( contentType is not null )
			sb.Append( "Content-Type: " ).Append( contentType ).Append( "\r\n" );
		sb.Append( "Content-Length: " ).Append( body.Length ).Append( "\r\n" );
		if( extraHeaders is not null )
		{
			foreach( var (name, value) in extraHeaders )
				sb.Append( name ).Append( ": " ).Append( value ).Append( "\r\n" );
		}
		sb.Append( "\r\n" );

		byte[] headerBytes = Encoding.ASCII.GetBytes( sb.ToString() );
		await stream.WriteAsync( headerBytes, cancellationToken );
		if( body.Length > 0 )
			await stream.WriteAsync( body, cancellationToken );
		await stream.FlushAsync( cancellationToken );
	}

	public void Dispose()
	{
		if( Interlocked.Exchange( ref _disposed, 1 ) != 0 )
			return;
		try { _cts?.Cancel(); } catch { }
		try { _listener.Stop(); } catch { }
		_cts?.Dispose();
	}

	public async ValueTask DisposeAsync()
	{
		Dispose();
		if( _acceptLoop is { } loop )
		{
			try { await loop.WaitAsync( TimeSpan.FromSeconds( 2 ) ); }
			catch { }
		}
	}

	private sealed record HttpRequestData( string Method, string Path, Dictionary<string, string> Headers, byte[] Body );
}

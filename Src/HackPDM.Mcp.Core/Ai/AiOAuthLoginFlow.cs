using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HackPDM.Mcp.Ai;

/// <summary>Result of a browser PKCE OAuth login flow.</summary>
public sealed record AiOAuthLoginResult(
	bool Success,
	string? ApiKey,
	string? AccountLabel,
	string Message );

/// <summary>
/// Implements cross-platform browser sign-in flows so users can log in to their AI provider
/// without manually generating or copy-pasting API keys:
/// <list type="bullet">
/// <item><b>OpenRouter PKCE OAuth</b>: launches <c>https://openrouter.ai/auth</c> with a SHA-256
/// PKCE challenge and loopback redirect (<c>http://127.0.0.1:&lt;port&gt;/oauth/callback</c>),
/// automatically exchanges the authorization code for an API key, and returns it to HackPDM.</item>
/// <item><b>System Browser Launcher</b>: opens provider sign-in / key creation pages on Windows,
/// macOS, and Linux.</item>
/// </list>
/// </summary>
public static class AiOAuthLoginFlow
{
	private const string OpenRouterAuthEndpoint = "https://openrouter.ai/auth";
	private const string OpenRouterExchangeEndpoint = "https://openrouter.ai/api/v1/auth/keys";

	/// <summary>Generates a cryptographically random RFC 7636 PKCE verifier and S256 challenge.</summary>
	public static (string Verifier, string Challenge) CreatePkcePair()
	{
		byte[] random = RandomNumberGenerator.GetBytes( 32 );
		string verifier = Base64UrlEncode( random );
		byte[] hash = SHA256.HashData( Encoding.ASCII.GetBytes( verifier ) );
		string challenge = Base64UrlEncode( hash );
		return ( verifier, challenge );
	}

	/// <summary>Builds the OpenRouter OAuth PKCE authorize URL for a given loopback callback URL.</summary>
	public static string BuildOpenRouterAuthUrl( string callbackUrl, string codeChallenge )
		=> $"{OpenRouterAuthEndpoint}?callback_url={Uri.EscapeDataString( callbackUrl )}&code_challenge={Uri.EscapeDataString( codeChallenge )}&code_challenge_method=S256";

	/// <summary>
	/// Runs the full OpenRouter PKCE browser sign-in flow: opens a loopback listener on <c>127.0.0.1</c>,
	/// launches the user's default browser to sign in (Google / GitHub / email), receives the callback code,
	/// exchanges it for a scoped OpenRouter key, and shows a confirmation page in the browser.
	/// </summary>
	public static async Task<AiOAuthLoginResult> SignInWithOpenRouterAsync(
		TimeSpan? timeout = null,
		Action<string>? onAuthUrlReady = null,
		CancellationToken cancellationToken = default )
	{
		int port = FindFreeLoopbackPort();
		string callbackPrefix = $"http://127.0.0.1:{port}/";
		string callbackUrl = $"http://127.0.0.1:{port}/oauth/callback";
		var (verifier, challenge) = CreatePkcePair();
		string authUrl = BuildOpenRouterAuthUrl( callbackUrl, challenge );

		using HttpListener listener = new();
		listener.Prefixes.Add( callbackPrefix );
		listener.Start();

		onAuthUrlReady?.Invoke( authUrl );
		if( !TryOpenBrowser( authUrl, out string? openErr ) )
			return new AiOAuthLoginResult( false, null, null, $"Could not launch browser ({openErr}). Open manually: {authUrl}" );

		using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource( cancellationToken );
		timeoutCts.CancelAfter( timeout ?? TimeSpan.FromMinutes( 3 ) );

		string? authCode = null;
		try
		{
			while( authCode is null )
			{
				Task<HttpListenerContext> ctxTask = listener.GetContextAsync();
				using CancellationTokenRegistration reg = timeoutCts.Token.Register( () => { try { listener.Stop(); } catch { } } );
				HttpListenerContext ctx = await ctxTask;

				string? code = ctx.Request.QueryString[ "code" ];
				string? err = ctx.Request.QueryString[ "error" ];

				if( !string.IsNullOrWhiteSpace( err ) )
				{
					await WriteHtmlResponseAsync( ctx.Response, 400, "HackPDM Sign-In Cancelled", $"Authorization returned error: {WebUtility.HtmlEncode( err )}. You can close this tab." );
					return new AiOAuthLoginResult( false, null, null, $"Browser sign-in cancelled ({err})." );
				}

				if( !string.IsNullOrWhiteSpace( code ) )
				{
					authCode = code.Trim();
					await WriteHtmlResponseAsync(
						ctx.Response,
						200,
						"Connected to HackPDM",
						"Your OpenRouter AI account is now connected to HackPDM! You can close this browser tab and return to the HackPDM Command Palette." );
				}
				else
				{
					await WriteHtmlResponseAsync( ctx.Response, 200, "HackPDM OAuth", "Waiting for authorization callback..." );
				}
			}
		}
		catch( OperationCanceledException )
		{
			return new AiOAuthLoginResult( false, null, null, "Browser sign-in timed out or was cancelled." );
		}
		catch( HttpListenerException ) when( timeoutCts.IsCancellationRequested )
		{
			return new AiOAuthLoginResult( false, null, null, "Browser sign-in timed out or was cancelled." );
		}

		// Exchange PKCE code for API key
		using HttpClient http = new() { Timeout = TimeSpan.FromSeconds( 20 ) };
		string reqBody = JsonSerializer.Serialize( new
		{
			code = authCode,
			code_verifier = verifier,
			code_challenge_method = "S256",
		} );
		using StringContent content = new( reqBody, Encoding.UTF8, "application/json" );
		using HttpResponseMessage resp = await http.PostAsync( OpenRouterExchangeEndpoint, content, cancellationToken );
		string respText = await resp.Content.ReadAsStringAsync( cancellationToken );
		if( !resp.IsSuccessStatusCode )
			return new AiOAuthLoginResult( false, null, null, $"OpenRouter key exchange failed (HTTP {(int)resp.StatusCode}): {respText}" );

		try
		{
			using JsonDocument doc = JsonDocument.Parse( respText );
			if( doc.RootElement.TryGetProperty( "key", out JsonElement keyEl ) && keyEl.ValueKind == JsonValueKind.String
				&& keyEl.GetString() is { Length: > 0 } apiKey )
			{
				return new AiOAuthLoginResult( true, apiKey, "OpenRouter (Browser OAuth)", "Signed in via OpenRouter OAuth!" );
			}
		}
		catch( Exception ex )
		{
			return new AiOAuthLoginResult( false, null, null, $"Invalid OpenRouter key exchange response: {ex.Message}" );
		}

		return new AiOAuthLoginResult( false, null, null, "OpenRouter response did not include an API key." );
	}

	/// <summary>Opens a URL in the user's default system browser on Windows, Linux, or macOS.</summary>
	public static bool TryOpenBrowser( string url, out string? error )
	{
		error = null;
		try
		{
			if( RuntimeInformation.IsOSPlatform( OSPlatform.Windows ) )
			{
				Process.Start( new ProcessStartInfo( url ) { UseShellExecute = true } );
				return true;
			}
			if( RuntimeInformation.IsOSPlatform( OSPlatform.OSX ) )
			{
				Process.Start( new ProcessStartInfo( "open", url ) { UseShellExecute = false } );
				return true;
			}
			Process.Start( new ProcessStartInfo( "xdg-open", url ) { UseShellExecute = false } );
			return true;
		}
		catch( Exception ex )
		{
			error = ex.Message;
			return false;
		}
	}

	private static int FindFreeLoopbackPort()
	{
		using TcpListener l = new( IPAddress.Loopback, 0 );
		l.Start();
		return ( (IPEndPoint)l.LocalEndpoint ).Port;
	}

	private static string Base64UrlEncode( byte[] data )
		=> Convert.ToBase64String( data ).TrimEnd( '=' ).Replace( '+', '-' ).Replace( '/', '_' );

	private static async Task WriteHtmlResponseAsync( HttpListenerResponse resp, int statusCode, string title, string message )
	{
		string html =
			"<!doctype html><html><head><meta charset='utf-8'><title>" + WebUtility.HtmlEncode( title ) + "</title>" +
			"<style>body{font-family:system-ui,-apple-system,sans-serif;background:#11151d;color:#f3f5f9;display:flex;align-items:center;justify-content:center;height:100vh;margin:0}" +
			".card{background:#1b2230;border:1px solid #2e3b52;border-radius:12px;padding:28px 34px;max-width:460px;text-align:center;box-shadow:0 16px 40px rgba(0,0,0,.45)}" +
			"h1{margin:0 0 10px;font-size:20px;color:#60cdff}p{margin:0;line-height:1.5;color:#cbd5e1;font-size:14px}</style></head>" +
			"<body><div class='card'><h1>" + WebUtility.HtmlEncode( title ) + "</h1><p>" + WebUtility.HtmlEncode( message ) + "</p></div></body></html>";
		byte[] bytes = Encoding.UTF8.GetBytes( html );
		resp.StatusCode = statusCode;
		resp.ContentType = "text/html; charset=utf-8";
		resp.ContentLength64 = bytes.Length;
		await resp.OutputStream.WriteAsync( bytes );
		resp.Close();
	}
}

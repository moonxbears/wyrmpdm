using System.Text.Json;

using HackPDM.Mcp.Protocol;

namespace HackPDM.Mcp.Bridge;

public enum McpServerMode
{
	/// <summary>Embedded inside the running HackPDM desktop GUI process.</summary>
	EmbeddedGui,
	/// <summary>Standalone server that automatically attaches to a live GUI bridge when available, falling back to headless API mode.</summary>
	Auto,
	/// <summary>Standalone server that only proxies to a live GUI bridge.</summary>
	LiveOnly,
	/// <summary>Standalone server running strictly headless without attaching to any GUI bridge.</summary>
	Headless,
}

/// <summary>
/// Coordinates whether tool/resource calls execute in-process (headless or embedded GUI)
/// or are proxied to a live HackPDM desktop client's embedded MCP HTTP bridge.
/// </summary>
public sealed class HackPdmBridgeCoordinator : IDisposable
{
	/// <summary>Tools that always execute locally on the standalone MCP server (never proxied).</summary>
	private static readonly HashSet<string> LocalOnlyTools = new( StringComparer.Ordinal )
	{
		"client_status",
		"client_attach",
		"client_detach",
		"client_launch",
	};

	private readonly Lock _gate = new();
	private McpRemoteProxyClient? _proxy;
	private McpBridgeEndpointInfo? _endpoint;
	private bool _explicitlyDetached;

	public McpServerMode Mode { get; }
	public IHackPdmUiBridge? LocalUiBridge { get; }
	public string? DataDirOverride { get; }

	public HackPdmBridgeCoordinator(
		McpServerMode mode,
		IHackPdmUiBridge? localUiBridge = null,
		string? explicitClientUrl = null,
		string? explicitClientToken = null,
		string? dataDirOverride = null )
	{
		Mode = mode;
		LocalUiBridge = localUiBridge;
		DataDirOverride = dataDirOverride;

		if( !string.IsNullOrWhiteSpace( explicitClientUrl ) && mode is McpServerMode.Auto or McpServerMode.LiveOnly )
		{
			_proxy = new McpRemoteProxyClient( explicitClientUrl, explicitClientToken );
			_endpoint = new McpBridgeEndpointInfo(
				Pid: 0,
				Port: Uri.TryCreate( explicitClientUrl, UriKind.Absolute, out Uri? u ) ? u.Port : 0,
				BaseUrl: explicitClientUrl.TrimEnd( '/' ),
				McpUrl: _proxy.McpEndpointUrl,
				Token: explicitClientToken ?? "",
				StartedAtUtc: DateTimeOffset.UtcNow,
				AppVersion: "external",
				Platform: "external" );
		}
	}

	public bool HasLiveLocalUi => LocalUiBridge is not null;

	public bool IsProxiedAttached
	{
		get
		{
			lock( _gate )
				return _proxy is not null;
		}
	}

	public McpBridgeEndpointInfo? AttachedEndpoint
	{
		get
		{
			lock( _gate )
				return _endpoint;
		}
	}

	public string ActiveModeLabel
	{
		get
		{
			if( LocalUiBridge is not null )
				return "live_gui";
			if( IsProxiedAttached )
				return "proxied_to_live_gui";
			return "headless";
		}
	}

	public async Task<(bool Attached, McpBridgeEndpointInfo? Endpoint, string? Error)> TryAttachAsync(
		string? explicitUrl = null,
		string? explicitToken = null,
		CancellationToken cancellationToken = default )
	{
		if( LocalUiBridge is not null )
			return ( true, null, null );

		McpBridgeEndpointInfo? target;
		if( !string.IsNullOrWhiteSpace( explicitUrl ) )
		{
			string trimmed = explicitUrl.TrimEnd( '/' );
			string mcpUrl = trimmed.EndsWith( "/mcp", StringComparison.OrdinalIgnoreCase ) ? trimmed : $"{trimmed}/mcp";
			string baseUrl = trimmed.EndsWith( "/mcp", StringComparison.OrdinalIgnoreCase ) ? trimmed[ ..^4 ] : trimmed;
			int port = Uri.TryCreate( baseUrl, UriKind.Absolute, out Uri? uri ) ? uri.Port : 0;
			target = new McpBridgeEndpointInfo( 0, port, baseUrl, mcpUrl, explicitToken ?? "", DateTimeOffset.UtcNow, "unknown", "unknown" );
		}
		else
		{
			target = McpBridgeDiscovery.TryReadLiveBridge( DataDirOverride );
			if( target is null )
				return ( false, null, $"No running HackPDM desktop client bridge found at '{McpBridgeDiscovery.GetDiscoveryFilePath( DataDirOverride )}'." );
		}

		McpRemoteProxyClient candidate = new( target.BaseUrl, target.Token, target.Pid > 0 ? target.Pid : null );
		JsonElement? health = await candidate.GetHealthAsync( cancellationToken );
		if( health is null )
		{
			candidate.Dispose();
			return ( false, null, $"Could not reach HackPDM bridge at {target.BaseUrl}/health." );
		}

		lock( _gate )
		{
			_proxy?.Dispose();
			_proxy = candidate;
			_endpoint = target;
			_explicitlyDetached = false;
		}

		return ( true, target, null );
	}

	public void Detach()
	{
		lock( _gate )
		{
			_proxy?.Dispose();
			_proxy = null;
			_endpoint = null;
			_explicitlyDetached = true;
		}
	}

	public async Task<McpToolResult?> InterceptToolCallAsync( string toolName, JsonElement? arguments, CancellationToken cancellationToken )
	{
		if( LocalUiBridge is not null || Mode == McpServerMode.Headless || LocalOnlyTools.Contains( toolName ) )
			return null;

		McpRemoteProxyClient? proxy = await EnsureProxyIfAutoAsync( cancellationToken );
		if( proxy is null )
		{
			if( Mode == McpServerMode.LiveOnly )
				return McpToolResult.Error( "HackPDM MCP server is in live-only mode, but no live HackPDM desktop client is attached. Launch HackPDM or call client_launch / client_attach." );
			return null;
		}

		try
		{
			McpToolResult result = await proxy.CallToolAsync( toolName, arguments, cancellationToken );

			// If a live GUI tool (ui_app_restart or ui_theme_control with autoApplyRestart) scheduled
			// a full OS process restart, wait for the new process to publish its bridge and seamlessly re-attach!
			if( result.IsError != true
				&& toolName is "ui_app_restart" or "ui_theme_control"
				&& TryExtractScheduledRestart( result, out int? oldPid ) )
			{
				int waitSec = 30;
				if( arguments is { ValueKind: JsonValueKind.Object } obj
					&& obj.TryGetProperty( "waitTimeoutSeconds", out JsonElement wt )
					&& wt.ValueKind == JsonValueKind.Number
					&& wt.TryGetInt32( out int parsedWait ) )
				{
					waitSec = Math.Clamp( parsedWait, 3, 120 );
				}

				var (reattached, newEndpoint, reattachErr) = await WaitForRestartedBridgeAndReattachAsync( oldPid, waitSec, cancellationToken );
				return McpToolResult.Ok( new
				{
					restarted = true,
					mode = "full",
					oldPid,
					newPid = newEndpoint?.Pid,
					reattached,
					reattachError = reattachErr,
					endpoint = newEndpoint is null ? null : new { newEndpoint.Port, newEndpoint.BaseUrl, newEndpoint.McpUrl, newEndpoint.AppVersion },
				} );
			}

			return result;
		}
		catch( Exception ex ) when( ex is HttpRequestException or TaskCanceledException or InvalidOperationException )
		{
			// Live GUI closed or restarted — clear stale proxy
			ClearDeadProxy( proxy );
			if( Mode == McpServerMode.LiveOnly || toolName.StartsWith( "ui_", StringComparison.Ordinal ) )
				return McpToolResult.Error( $"Lost connection to live HackPDM client: {ex.Message}. Use client_attach or client_launch to reconnect." );
			// In Auto mode, non-UI API tools gracefully fall back to headless execution
			return null;
		}
	}

	/// <summary>
	/// Detaches from an exiting GUI PID and polls <c>mcp-bridge.json</c> until the restarted HackPDM
	/// process comes online with a new PID, then automatically re-attaches the proxy.
	/// </summary>
	public async Task<(bool Reattached, McpBridgeEndpointInfo? Endpoint, string? Error)> WaitForRestartedBridgeAndReattachAsync(
		int? oldPid,
		int waitTimeoutSeconds = 30,
		CancellationToken cancellationToken = default )
	{
		lock( _gate )
		{
			_proxy?.Dispose();
			_proxy = null;
			_endpoint = null;
			_explicitlyDetached = false;
		}

		DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds( Math.Clamp( waitTimeoutSeconds, 3, 120 ) );
		// Brief initial pause so the exiting process begins teardown
		await Task.Delay( 500, cancellationToken );

		while( DateTimeOffset.UtcNow < deadline && !cancellationToken.IsCancellationRequested )
		{
			McpBridgeEndpointInfo? candidate = McpBridgeDiscovery.TryReadLiveBridge( DataDirOverride );
			if( candidate is not null && ( oldPid is null || candidate.Pid != oldPid.Value ) )
			{
				var (attached, _, _) = await TryAttachAsync( candidate.BaseUrl, candidate.Token, cancellationToken );
				if( attached )
				{
					lock( _gate )
						_endpoint = candidate;
					return ( true, candidate, null );
				}
			}
			await Task.Delay( 250, cancellationToken );
		}

		return ( false, null, $"Timed out after {waitTimeoutSeconds}s waiting for restarted HackPDM GUI bridge to come online." );
	}

	private static bool TryExtractScheduledRestart( McpToolResult result, out int? oldPid )
	{
		oldPid = null;
		string? text = result.Content.FirstOrDefault( c => c.Type == "text" )?.Text;
		if( string.IsNullOrWhiteSpace( text ) )
			return false;
		try
		{
			using JsonDocument doc = JsonDocument.Parse( text );
			JsonElement root = doc.RootElement;
			if( root.ValueKind != JsonValueKind.Object )
				return false;

			bool scheduled = root.TryGetProperty( "scheduledFullRestart", out JsonElement s ) && s.ValueKind == JsonValueKind.True;
			if( !scheduled
				&& root.TryGetProperty( "restart", out JsonElement r )
				&& r.ValueKind == JsonValueKind.Object
				&& r.TryGetProperty( "scheduledFullRestart", out JsonElement rs )
				&& rs.ValueKind == JsonValueKind.True )
			{
				scheduled = true;
				if( r.TryGetProperty( "oldPid", out JsonElement rp ) && rp.TryGetInt32( out int p1 ) )
					oldPid = p1;
			}
			else if( scheduled && root.TryGetProperty( "oldPid", out JsonElement p ) && p.TryGetInt32( out int p2 ) )
			{
				oldPid = p2;
			}

			return scheduled;
		}
		catch
		{
			return false;
		}
	}

	public async Task<McpResourceContent?> InterceptResourceReadAsync( string uri, CancellationToken cancellationToken )
	{
		if( LocalUiBridge is not null || Mode == McpServerMode.Headless || uri.Equals( "hackpdm://status", StringComparison.OrdinalIgnoreCase ) )
			return null;

		McpRemoteProxyClient? proxy = await EnsureProxyIfAutoAsync( cancellationToken );
		if( proxy is null )
			return null;

		try
		{
			return await proxy.ReadResourceAsync( uri, cancellationToken );
		}
		catch( Exception ex ) when( ex is HttpRequestException or TaskCanceledException or InvalidOperationException )
		{
			ClearDeadProxy( proxy );
			return null;
		}
	}

	private async Task<McpRemoteProxyClient?> EnsureProxyIfAutoAsync( CancellationToken cancellationToken )
	{
		lock( _gate )
		{
			if( _proxy is not null )
				return _proxy;
			if( _explicitlyDetached )
				return null;
		}

		if( Mode is not ( McpServerMode.Auto or McpServerMode.LiveOnly ) )
			return null;

		var (attached, _, _) = await TryAttachAsync( cancellationToken: cancellationToken );
		if( !attached )
			return null;

		lock( _gate )
			return _proxy;
	}

	private void ClearDeadProxy( McpRemoteProxyClient dead )
	{
		lock( _gate )
		{
			if( ReferenceEquals( _proxy, dead ) )
			{
				_proxy.Dispose();
				_proxy = null;
				_endpoint = null;
			}
		}
	}

	public void Dispose()
	{
		lock( _gate )
		{
			_proxy?.Dispose();
			_proxy = null;
			_endpoint = null;
		}
	}
}

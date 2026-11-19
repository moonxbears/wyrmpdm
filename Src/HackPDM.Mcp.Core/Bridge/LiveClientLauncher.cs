using System.Diagnostics;

using HackPDM.Mcp.Protocol;

namespace HackPDM.Mcp.Bridge;

/// <summary>
/// Locates and launches the HackPDM desktop client with its embedded MCP bridge enabled,
/// then waits until the bridge's discovery file and <c>/health</c> endpoint report ready.
/// </summary>
public static class LiveClientLauncher
{
	public sealed record LaunchResult(
		bool Launched,
		int? Pid,
		string? CommandLine,
		McpBridgeEndpointInfo? Endpoint,
		string? Error );

	public static async Task<LaunchResult> LaunchAndWaitAsync(
		string? explicitExecutablePath = null,
		int waitTimeoutSeconds = 30,
		int? preferredPort = null,
		string? dataDirOverride = null,
		CancellationToken cancellationToken = default )
	{
		(string fileName, string arguments)? launchSpec = ResolveLaunchSpec( explicitExecutablePath );
		if( launchSpec is null )
		{
			return new LaunchResult(
				Launched: false,
				Pid: null,
				CommandLine: null,
				Endpoint: null,
				Error: "Could not locate the HackPDM desktop executable. Pass 'executablePath' or set HACKPDM_CLIENT_EXE." );
		}

		var (fileName, arguments) = launchSpec.Value;
		ProcessStartInfo psi = new()
		{
			FileName = fileName,
			Arguments = arguments,
			UseShellExecute = false,
			CreateNoWindow = false,
		};
		psi.Environment[ "HACKPDM_MCP_ENABLED" ] = "1";
		if( preferredPort is > 0 )
			psi.Environment[ "HACKPDM_MCP_PORT" ] = preferredPort.Value.ToString();
		if( !string.IsNullOrEmpty( dataDirOverride ) )
			psi.Environment[ "HACKPDM_DATA_DIR" ] = dataDirOverride;

		Process? process;
		try
		{
			process = Process.Start( psi );
		}
		catch( Exception ex )
		{
			return new LaunchResult( false, null, $"{fileName} {arguments}".Trim(), null, $"Failed to start HackPDM: {ex.Message}" );
		}

		if( process is null )
			return new LaunchResult( false, null, $"{fileName} {arguments}".Trim(), null, "Process.Start returned null." );

		int launchedPid = process.Id;
		Stopwatch sw = Stopwatch.StartNew();
		TimeSpan timeout = TimeSpan.FromSeconds( Math.Clamp( waitTimeoutSeconds, 3, 120 ) );

		while( sw.Elapsed < timeout )
		{
			cancellationToken.ThrowIfCancellationRequested();
			if( process.HasExited )
			{
				return new LaunchResult( false, launchedPid, $"{fileName} {arguments}".Trim(), null,
					$"HackPDM exited early with exit code {process.ExitCode}." );
			}

			McpBridgeEndpointInfo? info = McpBridgeDiscovery.TryReadLiveBridge( dataDirOverride );
			if( info is not null && info.Pid == launchedPid )
			{
				using McpRemoteProxyClient probe = new( info.BaseUrl, info.Token, info.Pid );
				if( await probe.GetHealthAsync( cancellationToken ) is not null )
					return new LaunchResult( true, launchedPid, $"{fileName} {arguments}".Trim(), info, null );
			}

			await Task.Delay( 250, cancellationToken );
		}

		return new LaunchResult( true, launchedPid, $"{fileName} {arguments}".Trim(), null,
			$"HackPDM started (pid {launchedPid}), but the MCP bridge did not become ready within {timeout.TotalSeconds:0}s." );
	}

	internal static (string FileName, string Arguments)? ResolveLaunchSpec( string? explicitPath )
	{
		if( !string.IsNullOrWhiteSpace( explicitPath ) )
			return BuildSpecForPath( explicitPath );

		if( Environment.GetEnvironmentVariable( "HACKPDM_CLIENT_EXE" ) is { Length: > 0 } envExe )
			return BuildSpecForPath( envExe );

		string baseDir = AppContext.BaseDirectory;
		List<string> candidates = [];

		// Sibling executable in published/installed directory
		candidates.Add( Path.Combine( baseDir, OperatingSystem.IsWindows() ? "HackPDM.exe" : "HackPDM" ) );
		candidates.Add( Path.Combine( baseDir, "HackPDM.dll" ) );

		// Development workspace build outputs relative to Src/HackPDM.McpServer/bin/... or current directory
		foreach( string root in EnumerateSearchRoots( baseDir ) )
		{
			string srcHack = Path.Combine( root, "Src", "HackPDM", "bin" );
			if( !Directory.Exists( srcHack ) )
				continue;

			if( OperatingSystem.IsWindows() )
			{
				candidates.Add( Path.Combine( srcHack, "x64", "Debug", "net10.0-windows10.0.26100.0", "HackPDM.exe" ) );
				candidates.Add( Path.Combine( srcHack, "x64", "Release", "net10.0-windows10.0.26100.0", "HackPDM.exe" ) );
			}
			candidates.Add( Path.Combine( srcHack, "x64", "Debug", "net10.0-desktop", "HackPDM.dll" ) );
			candidates.Add( Path.Combine( srcHack, "x64", "Release", "net10.0-desktop", "HackPDM.dll" ) );
			candidates.Add( Path.Combine( srcHack, "Debug", "net10.0-desktop", "HackPDM.dll" ) );
			candidates.Add( Path.Combine( srcHack, "Release", "net10.0-desktop", "HackPDM.dll" ) );
		}

		foreach( string candidate in candidates )
		{
			if( File.Exists( candidate ) )
				return BuildSpecForPath( candidate );
		}

		return null;
	}

	private static (string FileName, string Arguments) BuildSpecForPath( string path )
	{
		string full = Path.GetFullPath( path );
		if( full.EndsWith( ".dll", StringComparison.OrdinalIgnoreCase ) )
			return ( "dotnet", $"\"{full}\"" );
		return ( full, "" );
	}

	private static IEnumerable<string> EnumerateSearchRoots( string startDir )
	{
		HashSet<string> seen = new( StringComparer.OrdinalIgnoreCase );
		foreach( string seed in new[] { Environment.CurrentDirectory, startDir } )
		{
			DirectoryInfo? current = new( seed );
			for( int depth = 0; current is not null && depth < 7; depth++, current = current.Parent )
			{
				if( seen.Add( current.FullName ) )
					yield return current.FullName;
			}
		}
	}
}

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

using HackPDM.Shared.GlobalData;

namespace HackPDM.Mcp.Protocol;

/// <summary>Connection metadata published by a running HackPDM GUI client's embedded MCP bridge.</summary>
public sealed record McpBridgeEndpointInfo(
	int Pid,
	int Port,
	string BaseUrl,
	string McpUrl,
	string Token,
	DateTimeOffset StartedAtUtc,
	string AppVersion,
	string Platform );

/// <summary>
/// Reads and writes the per-user bridge discovery file (<c>AppDataPaths.Data/mcp-bridge.json</c>)
/// so the standalone stdio <c>HackPDM.McpServer</c> can automatically attach to a running HackPDM desktop client.
/// </summary>
public static class McpBridgeDiscovery
{
	public const string BridgeFileName = "mcp-bridge.json";

	public static string GetDiscoveryFilePath( string? dataDirOverride = null )
		=> Path.Combine( dataDirOverride ?? AppDataPaths.Data, BridgeFileName );

	public static void WriteDiscoveryFile( McpBridgeEndpointInfo info, string? dataDirOverride = null )
	{
		string path = GetDiscoveryFilePath( dataDirOverride );
		string? dir = Path.GetDirectoryName( path );
		if( !string.IsNullOrEmpty( dir ) )
			Directory.CreateDirectory( dir );

		string json = JsonSerializer.Serialize( info, McpJson.IndentedOptions );
		string temp = $"{path}.{Environment.ProcessId}.tmp";
		File.WriteAllText( temp, json );
		TryRestrictFilePermissions( temp );
		File.Move( temp, path, overwrite: true );
		TryRestrictFilePermissions( path );
	}

	public static void RemoveDiscoveryFile( int ownerPid, string? dataDirOverride = null )
	{
		string path = GetDiscoveryFilePath( dataDirOverride );
		try
		{
			if( !File.Exists( path ) )
				return;
			McpBridgeEndpointInfo? existing = TryReadRaw( path );
			if( existing is not null && existing.Pid != ownerPid )
				return;
			File.Delete( path );
		}
		catch
		{
			// Best-effort cleanup on shutdown
		}
	}

	public static McpBridgeEndpointInfo? TryReadLiveBridge( string? dataDirOverride = null )
	{
		string path = GetDiscoveryFilePath( dataDirOverride );
		McpBridgeEndpointInfo? info = TryReadRaw( path );
		if( info is null || info.Port <= 0 || string.IsNullOrWhiteSpace( info.McpUrl ) )
			return null;

		if( !IsProcessAlive( info.Pid ) )
		{
			RemoveDiscoveryFile( info.Pid, dataDirOverride );
			return null;
		}

		return info;
	}

	private static McpBridgeEndpointInfo? TryReadRaw( string path )
	{
		try
		{
			if( !File.Exists( path ) )
				return null;
			string json = File.ReadAllText( path );
			return JsonSerializer.Deserialize<McpBridgeEndpointInfo>( json, McpJson.Options );
		}
		catch
		{
			return null;
		}
	}

	private static bool IsProcessAlive( int pid )
	{
		if( pid <= 0 )
			return false;
		try
		{
			using Process p = Process.GetProcessById( pid );
			return !p.HasExited;
		}
		catch
		{
			return false;
		}
	}

	private static void TryRestrictFilePermissions( string path )
	{
		if( OperatingSystem.IsWindows() )
			return;
		try
		{
			File.SetUnixFileMode( path, UnixFileMode.UserRead | UnixFileMode.UserWrite );
		}
		catch
		{
			// Ignore filesystems that do not support POSIX file modes
		}
	}
}

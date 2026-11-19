using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HackPDM.Mcp.Ai;

/// <summary>Status of an external AI desktop/CLI client's MCP registration for HackPDM.</summary>
public sealed record ExternalAiClientStatus(
	string ClientId,
	string DisplayName,
	string Description,
	string ConfigPath,
	string RootJsonKey,
	bool IsDetected,
	bool IsConnected,
	string StatusLabel );

/// <summary>Result of connecting or disconnecting HackPDM in an external AI client's config.</summary>
public sealed record ExternalAiClientActionResult(
	bool Success,
	string ClientId,
	string ConfigPath,
	string? BackupPath,
	string Message );

/// <summary>
/// Cross-platform one-click auto-configurator that detects installed external AI clients
/// (Claude Desktop, Claude Code CLI, Cursor, VS Code, Windsurf, Gemini CLI / Antigravity)
/// and safely merges or removes ONLY the <c>"hackpdm"</c> MCP server entry while backing up
/// existing config files and preserving all other servers and settings untouched.
/// </summary>
public static class McpClientAutoConfigurator
{
	private sealed record ClientDef(
		string Id,
		string DisplayName,
		string Description,
		string RootKey,
		bool IsVsCodeStyle,
		Func<string, string, string> ResolveConfigPath );

	private static readonly IReadOnlyList<ClientDef> Clients =
	[
		new(
			"claude-desktop",
			"Claude Desktop",
			"Anthropic Claude Desktop app (claude_desktop_config.json)",
			"mcpServers",
			IsVsCodeStyle: false,
			ResolveConfigPath: ( home, appData ) =>
				RuntimeInformation.IsOSPlatform( OSPlatform.Windows )
					? Path.Combine( appData, "Claude", "claude_desktop_config.json" )
					: RuntimeInformation.IsOSPlatform( OSPlatform.OSX )
						? Path.Combine( home, "Library", "Application Support", "Claude", "claude_desktop_config.json" )
						: Path.Combine( home, ".config", "Claude", "claude_desktop_config.json" ) ),

		new(
			"claude-code",
			"Claude Code CLI",
			"Anthropic Claude Code CLI (~/.claude.json)",
			"mcpServers",
			IsVsCodeStyle: false,
			ResolveConfigPath: ( home, _ ) => Path.Combine( home, ".claude.json" ) ),

		new(
			"cursor",
			"Cursor",
			"Cursor AI IDE (~/.cursor/mcp.json)",
			"mcpServers",
			IsVsCodeStyle: false,
			ResolveConfigPath: ( home, _ ) => Path.Combine( home, ".cursor", "mcp.json" ) ),

		new(
			"vscode",
			"VS Code (GitHub Copilot MCP)",
			"VS Code user MCP config (Code/User/mcp.json)",
			"servers",
			IsVsCodeStyle: true,
			ResolveConfigPath: ( home, appData ) =>
				RuntimeInformation.IsOSPlatform( OSPlatform.Windows )
					? Path.Combine( appData, "Code", "User", "mcp.json" )
					: RuntimeInformation.IsOSPlatform( OSPlatform.OSX )
						? Path.Combine( home, "Library", "Application Support", "Code", "User", "mcp.json" )
						: Path.Combine( home, ".config", "Code", "User", "mcp.json" ) ),

		new(
			"windsurf",
			"Windsurf",
			"Codeium Windsurf IDE (~/.codeium/windsurf/mcp_config.json)",
			"mcpServers",
			IsVsCodeStyle: false,
			ResolveConfigPath: ( home, _ ) => Path.Combine( home, ".codeium", "windsurf", "mcp_config.json" ) ),

		new(
			"gemini-cli",
			"Gemini CLI / Antigravity",
			"Google Gemini CLI & Antigravity (~/.gemini/settings.json)",
			"mcpServers",
			IsVsCodeStyle: false,
			ResolveConfigPath: ( home, _ ) => Path.Combine( home, ".gemini", "settings.json" ) ),
	];

	public static IReadOnlyList<ExternalAiClientStatus> DetectClients(
		string? homeDirOverride = null,
		string? appDataDirOverride = null )
	{
		string home = homeDirOverride ?? Environment.GetFolderPath( Environment.SpecialFolder.UserProfile );
		string appData = appDataDirOverride ?? Environment.GetFolderPath( Environment.SpecialFolder.ApplicationData );
		List<ExternalAiClientStatus> list = [];

		foreach( ClientDef def in Clients )
		{
			string cfgPath = def.ResolveConfigPath( home, appData );
			string? parentDir = Path.GetDirectoryName( cfgPath );
			bool fileExists = File.Exists( cfgPath );
			bool dirExists = !string.IsNullOrWhiteSpace( parentDir ) && Directory.Exists( parentDir );
			bool detected = fileExists || ( dirExists && !string.Equals( parentDir, home, StringComparison.OrdinalIgnoreCase ) );
			bool connected = fileExists && HasHackPdmEntry( cfgPath, def.RootKey );

			string status = connected
				? "Connected"
				: ( detected ? "Detected" : "Ready to setup" );

			list.Add( new ExternalAiClientStatus(
				def.Id,
				def.DisplayName,
				def.Description,
				cfgPath,
				def.RootKey,
				detected,
				connected,
				status ) );
		}

		return list;
	}

	/// <summary>Safely connects HackPDM MCP Server into the specified external AI client's JSON config.</summary>
	public static ExternalAiClientActionResult ConnectClient(
		string clientId,
		string serverCommand,
		IReadOnlyList<string> serverArgs,
		string? homeDirOverride = null,
		string? appDataDirOverride = null )
	{
		ClientDef? def = Clients.FirstOrDefault( c => string.Equals( c.Id, clientId, StringComparison.OrdinalIgnoreCase ) );
		if( def is null )
			return new ExternalAiClientActionResult( false, clientId, "", null, $"Unknown client '{clientId}'." );

		string home = homeDirOverride ?? Environment.GetFolderPath( Environment.SpecialFolder.UserProfile );
		string appData = appDataDirOverride ?? Environment.GetFolderPath( Environment.SpecialFolder.ApplicationData );
		string cfgPath = def.ResolveConfigPath( home, appData );

		try
		{
			if( Path.GetDirectoryName( cfgPath ) is { Length: > 0 } dir )
				Directory.CreateDirectory( dir );

			string? backupPath = null;
			JsonObject root;
			if( File.Exists( cfgPath ) )
			{
				string existing = File.ReadAllText( cfgPath );
				backupPath = cfgPath + ".hackpdm.bak";
				File.WriteAllText( backupPath, existing );

				root = string.IsNullOrWhiteSpace( existing )
					? new JsonObject()
					: ( JsonNode.Parse( existing, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true } ) as JsonObject
						?? new JsonObject() );
			}
			else
			{
				root = new JsonObject();
			}

			if( root[ def.RootKey ] is not JsonObject serversObj )
			{
				serversObj = new JsonObject();
				root[ def.RootKey ] = serversObj;
			}

			JsonArray argsArr = [];
			foreach( string a in serverArgs )
				argsArr.Add( a );

			JsonObject entry = new()
			{
				[ "command" ] = serverCommand,
				[ "args" ] = argsArr,
			};
			if( def.IsVsCodeStyle )
				entry[ "type" ] = "stdio";

			serversObj[ "hackpdm" ] = entry;

			WriteAtomically( cfgPath, root.ToJsonString( new JsonSerializerOptions { WriteIndented = true } ) );
			return new ExternalAiClientActionResult(
				true,
				def.Id,
				cfgPath,
				backupPath,
				$"Connected {def.DisplayName}! Restart {def.DisplayName} to pick up HackPDM's MCP tools." );
		}
		catch( Exception ex )
		{
			return new ExternalAiClientActionResult( false, def.Id, cfgPath, null, $"Could not update {def.DisplayName} config: {ex.Message}" );
		}
	}

	/// <summary>Cleanly removes only the <c>"hackpdm"</c> entry from the external AI client's JSON config.</summary>
	public static ExternalAiClientActionResult DisconnectClient(
		string clientId,
		string? homeDirOverride = null,
		string? appDataDirOverride = null )
	{
		ClientDef? def = Clients.FirstOrDefault( c => string.Equals( c.Id, clientId, StringComparison.OrdinalIgnoreCase ) );
		if( def is null )
			return new ExternalAiClientActionResult( false, clientId, "", null, $"Unknown client '{clientId}'." );

		string home = homeDirOverride ?? Environment.GetFolderPath( Environment.SpecialFolder.UserProfile );
		string appData = appDataDirOverride ?? Environment.GetFolderPath( Environment.SpecialFolder.ApplicationData );
		string cfgPath = def.ResolveConfigPath( home, appData );

		if( !File.Exists( cfgPath ) )
			return new ExternalAiClientActionResult( true, def.Id, cfgPath, null, $"{def.DisplayName} is not configured." );

		try
		{
			string existing = File.ReadAllText( cfgPath );
			string backupPath = cfgPath + ".hackpdm.bak";
			File.WriteAllText( backupPath, existing );

			if( JsonNode.Parse( existing, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true } ) is JsonObject root
				&& root[ def.RootKey ] is JsonObject serversObj
				&& serversObj.Remove( "hackpdm" ) )
			{
				WriteAtomically( cfgPath, root.ToJsonString( new JsonSerializerOptions { WriteIndented = true } ) );
				return new ExternalAiClientActionResult( true, def.Id, cfgPath, backupPath, $"Disconnected HackPDM from {def.DisplayName}." );
			}

			return new ExternalAiClientActionResult( true, def.Id, cfgPath, null, $"HackPDM was not registered in {def.DisplayName}." );
		}
		catch( Exception ex )
		{
			return new ExternalAiClientActionResult( false, def.Id, cfgPath, null, $"Could not update {def.DisplayName} config: {ex.Message}" );
		}
	}

	private static bool HasHackPdmEntry( string path, string rootKey )
	{
		try
		{
			using JsonDocument doc = JsonDocument.Parse(
				File.ReadAllText( path ),
				new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true } );
			return doc.RootElement.ValueKind == JsonValueKind.Object
				&& doc.RootElement.TryGetProperty( rootKey, out JsonElement servers )
				&& servers.ValueKind == JsonValueKind.Object
				&& servers.TryGetProperty( "hackpdm", out _ );
		}
		catch
		{
			return false;
		}
	}

	private static void WriteAtomically( string path, string content )
	{
		string tmp = path + ".tmp";
		File.WriteAllText( tmp, content );
		File.Move( tmp, path, overwrite: true );
	}
}

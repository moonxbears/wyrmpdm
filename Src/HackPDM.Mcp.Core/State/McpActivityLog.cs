using System.Text;
using System.Text.Json;

using HackPDM.Mcp.Protocol;
using HackPDM.Shared.GlobalData;

namespace HackPDM.Mcp.State;

/// <summary>One recorded MCP tool execution entry.</summary>
public sealed record McpActivityEntry(
	string Id,
	DateTimeOffset TimestampUtc,
	string ToolName,
	bool Success,
	long DurationMs,
	string Mode,
	string? ArgsSummary,
	string? ResultSummary,
	string? ErrorMessage,
	string? RestorePointId );

/// <summary>
/// Persistent, thread-safe audit log of MCP tool invocations stored as JSONL in
/// <c>&lt;AppDataPaths.Data&gt;/mcp/activity.jsonl</c> with automatic secret redaction
/// and live change notifications for the HackPDM desktop GUI.
/// </summary>
public sealed class McpActivityLog
{
	private const int MaxInMemoryEntries = 2000;
	private const int MaxDiskLinesBeforeCompact = 5000;

	private static readonly string[] SecretKeySubstrings =
	[
		"password", "passwd", "secret", "token", "swkey", "apikey", "authorization", "credential",
	];

	private readonly Lock _gate = new();
	private readonly List<McpActivityEntry> _entries = [];
	private int _diskWriteCount;

	public string LogFilePath { get; }

	public event Action<McpActivityEntry>? EntryAdded;
	public event Action? Cleared;

	public McpActivityLog( string? dataDirOverride = null )
	{
		string baseDir = dataDirOverride ?? AppDataPaths.Data;
		LogFilePath = Path.Combine( baseDir, "mcp", "activity.jsonl" );
		LoadFromDisk();
	}

	public int Count
	{
		get
		{
			lock( _gate )
				return _entries.Count;
		}
	}

	public McpActivityEntry Record(
		string toolName,
		bool success,
		long durationMs,
		string mode,
		JsonElement? rawArguments,
		McpToolResult? result,
		string? errorMessage = null,
		string? restorePointId = null )
	{
		string? argsSummary = SummarizeArguments( rawArguments );
		string? resultSummary = SummarizeResult( result, success, errorMessage );
		string? err = success ? null : ( errorMessage ?? ExtractErrorText( result ) );

		McpActivityEntry entry = new(
			Id: Guid.NewGuid().ToString( "N" )[ ..12 ],
			TimestampUtc: DateTimeOffset.UtcNow,
			ToolName: toolName,
			Success: success,
			DurationMs: durationMs,
			Mode: mode,
			ArgsSummary: argsSummary,
			ResultSummary: resultSummary,
			ErrorMessage: err,
			RestorePointId: restorePointId );

		lock( _gate )
		{
			_entries.Add( entry );
			if( _entries.Count > MaxInMemoryEntries )
				_entries.RemoveRange( 0, _entries.Count - MaxInMemoryEntries );
			PersistEntryLocked( entry );
		}

		try { EntryAdded?.Invoke( entry ); } catch { }
		return entry;
	}

	public IReadOnlyList<McpActivityEntry> GetEntries(
		int limit = 100,
		string? toolFilter = null,
		bool errorsOnly = false,
		DateTimeOffset? sinceUtc = null,
		string? search = null )
	{
		int cap = Math.Clamp( limit, 1, MaxInMemoryEntries );
		lock( _gate )
		{
			IEnumerable<McpActivityEntry> query = _entries;
			if( !string.IsNullOrWhiteSpace( toolFilter ) )
				query = query.Where( e => e.ToolName.Contains( toolFilter.Trim(), StringComparison.OrdinalIgnoreCase ) );
			if( errorsOnly )
				query = query.Where( e => !e.Success );
			if( sinceUtc is DateTimeOffset since )
				query = query.Where( e => e.TimestampUtc >= since );
			if( !string.IsNullOrWhiteSpace( search ) )
			{
				string q = search.Trim();
				query = query.Where( e =>
					e.ToolName.Contains( q, StringComparison.OrdinalIgnoreCase )
					|| ( e.ArgsSummary?.Contains( q, StringComparison.OrdinalIgnoreCase ) ?? false )
					|| ( e.ResultSummary?.Contains( q, StringComparison.OrdinalIgnoreCase ) ?? false )
					|| ( e.ErrorMessage?.Contains( q, StringComparison.OrdinalIgnoreCase ) ?? false )
					|| ( e.RestorePointId?.Contains( q, StringComparison.OrdinalIgnoreCase ) ?? false ) );
			}

			// Newest first
			return query.Reverse().Take( cap ).ToList();
		}
	}

	public void Clear()
	{
		lock( _gate )
		{
			_entries.Clear();
			_diskWriteCount = 0;
			try
			{
				if( File.Exists( LogFilePath ) )
					File.Delete( LogFilePath );
			}
			catch
			{
				// Best effort
			}
		}

		try { Cleared?.Invoke(); } catch { }
	}

	private void LoadFromDisk()
	{
		try
		{
			if( !File.Exists( LogFilePath ) )
				return;
			string[] lines = File.ReadAllLines( LogFilePath, Encoding.UTF8 );
			int start = Math.Max( 0, lines.Length - MaxInMemoryEntries );
			for( int i = start; i < lines.Length; i++ )
			{
				string line = lines[ i ].Trim();
				if( line.Length == 0 )
					continue;
				try
				{
					McpActivityEntry? entry = JsonSerializer.Deserialize<McpActivityEntry>( line, McpJson.Options );
					if( entry is not null )
						_entries.Add( entry );
				}
				catch
				{
					// Skip corrupted log line
				}
			}
			_diskWriteCount = lines.Length;
		}
		catch
		{
			// Best effort on startup
		}
	}

	private void PersistEntryLocked( McpActivityEntry entry )
	{
		try
		{
			string? dir = Path.GetDirectoryName( LogFilePath );
			if( !string.IsNullOrEmpty( dir ) )
				Directory.CreateDirectory( dir );

			string line = JsonSerializer.Serialize( entry, McpJson.Options ) + "\n";
			File.AppendAllText( LogFilePath, line, Encoding.UTF8 );
			_diskWriteCount++;

			if( _diskWriteCount > MaxDiskLinesBeforeCompact )
			{
				string temp = $"{LogFilePath}.tmp";
				File.WriteAllLines( temp, _entries.Select( e => JsonSerializer.Serialize( e, McpJson.Options ) ), Encoding.UTF8 );
				File.Move( temp, LogFilePath, overwrite: true );
				_diskWriteCount = _entries.Count;
			}
		}
		catch
		{
			// Never let logging I/O fail a tool call
		}
	}

	public static string? SummarizeArguments( JsonElement? args )
	{
		if( args is not { ValueKind: JsonValueKind.Object } obj )
			return null;

		using MemoryStream ms = new();
		using( Utf8JsonWriter writer = new( ms ) )
		{
			WriteRedacted( writer, obj, depth: 0 );
		}
		string json = Encoding.UTF8.GetString( ms.ToArray() );
		if( json == "{}" )
			return null;
		return json.Length > 400 ? json[ ..400 ] + "…" : json;
	}

	private static void WriteRedacted( Utf8JsonWriter writer, JsonElement element, int depth )
	{
		if( depth > 5 )
		{
			writer.WriteStringValue( "..." );
			return;
		}

		switch( element.ValueKind )
		{
			case JsonValueKind.Object:
				writer.WriteStartObject();
				foreach( JsonProperty prop in element.EnumerateObject() )
				{
					writer.WritePropertyName( prop.Name );
					if( IsSecretKey( prop.Name ) )
					{
						writer.WriteStringValue( "***" );
					}
					else if( string.Equals( prop.Name, "content", StringComparison.OrdinalIgnoreCase )
						|| string.Equals( prop.Name, "base64Data", StringComparison.OrdinalIgnoreCase ) )
					{
						string raw = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() ?? "" : prop.Value.GetRawText();
						writer.WriteStringValue( raw.Length > 80 ? $"<{raw.Length} chars>" : raw );
					}
					else
					{
						WriteRedacted( writer, prop.Value, depth + 1 );
					}
				}
				writer.WriteEndObject();
				break;

			case JsonValueKind.Array:
				writer.WriteStartArray();
				int count = 0;
				foreach( JsonElement item in element.EnumerateArray() )
				{
					if( count >= 10 )
					{
						writer.WriteStringValue( $"...(+{element.GetArrayLength() - count})" );
						break;
					}
					WriteRedacted( writer, item, depth + 1 );
					count++;
				}
				writer.WriteEndArray();
				break;

			case JsonValueKind.String:
				string s = element.GetString() ?? "";
				writer.WriteStringValue( s.Length > 140 ? s[ ..140 ] + "…" : s );
				break;

			default:
				element.WriteTo( writer );
				break;
		}
	}

	private static bool IsSecretKey( string name )
	{
		foreach( string sub in SecretKeySubstrings )
			if( name.Contains( sub, StringComparison.OrdinalIgnoreCase ) )
				return true;
		return false;
	}

	private static string? SummarizeResult( McpToolResult? result, bool success, string? errorMessage )
	{
		if( !success )
			return Truncate( errorMessage ?? ExtractErrorText( result ), 240 );
		if( result is null || result.Content.Count == 0 )
			return "ok";

		McpContent first = result.Content[ 0 ];
		if( first.Type == "image" )
			return $"image ({first.MimeType}, {first.Data?.Length ?? 0} b64 chars)";
		return Truncate( first.Text?.Replace( "\r", " " ).Replace( "\n", " " ), 240 );
	}

	private static string? ExtractErrorText( McpToolResult? result )
	{
		if( result is null )
			return null;
		string? text = result.Content.FirstOrDefault( c => c.Type == "text" )?.Text;
		return Truncate( text, 300 );
	}

	private static string? Truncate( string? value, int max )
	{
		if( string.IsNullOrWhiteSpace( value ) )
			return null;
		string trimmed = value.Trim();
		return trimmed.Length > max ? trimmed[ ..max ] + "…" : trimmed;
	}
}

using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace HackPDM.Infrastructure.SldWrks.Format;

/// <summary>
/// Cross-platform writer for SolidWorks files supporting both container formats:
/// <list type="bullet">
/// <item>SolidWorks 2015+ (<see cref="SwStreamStore.ContainerFormat.Modern"/> chunked DEFLATE streams)</item>
/// <item>SolidWorks 2014 and earlier (<see cref="SwStreamStore.ContainerFormat.Ole"/> MS-CFB v3/v4 compound files, including <c>__ZLB</c> compressed streams and DIFAT chains)</item>
/// </list>
/// </summary>
public static class SwStreamWriter
{
	private static ReadOnlySpan<byte> ChunkMarker => [ 0x14, 0x00, 0x06, 0x00, 0x08, 0x00 ];
	private const int ChunkHeaderSize = 26;
	private const int MaxNameSize = 512;

	private const uint EndOfChain = 0xFFFFFFFE;
	private const uint FreeSector = 0xFFFFFFFF;
	private const uint FatSectorTag = 0xFFFFFFFD;
	private const uint DifatSectorTag = 0xFFFFFFFC;
	private const uint NoStream = 0xFFFFFFFF;

	/// <summary>
	/// Rewrites a SolidWorks file (in-memory bytes) replacing or adding the specified streams.
	/// For OLE <c>__ZLB</c> streams, callers pass the logical stream name without <c>__ZLB</c> (matching <see cref="SwStreamStore"/>)
	/// and uncompressed bytes; the writer transparently preserves the 24-byte ZLB header &amp; trailer and re-compresses with zlib.
	/// </summary>
	public static byte[] Rewrite( byte[] original, IReadOnlyDictionary<string, byte[]> updatedStreams )
	{
		if( updatedStreams.Count == 0 )
			return original;

		return SwStreamStore.DetectFormat( original ) switch
		{
			SwStreamStore.ContainerFormat.Modern => RewriteModern( original, updatedStreams ),
			SwStreamStore.ContainerFormat.Ole => RewriteOle( original, updatedStreams ),
			_ => throw new InvalidDataException( "Unrecognised SolidWorks container format." ),
		};
	}

	/// <summary>Atomically rewrites a SolidWorks file on disk (writes to a sibling temp file, then replaces).</summary>
	public static void RewriteFile( string filePath, IReadOnlyDictionary<string, byte[]> updatedStreams )
	{
		byte[] original = File.ReadAllBytes( filePath );
		byte[] updated = Rewrite( original, updatedStreams );
		WriteBytesAtomically( filePath, updated );
	}

	internal static void WriteBytesAtomically( string filePath, byte[] content )
	{
		string fullPath = Path.GetFullPath( filePath );
		string dir = Path.GetDirectoryName( fullPath ) ?? ".";
		string temp = Path.Combine( dir, $".{Path.GetFileName( fullPath )}.{Guid.NewGuid():N}.tmp" );
		try
		{
			File.WriteAllBytes( temp, content );
			File.Move( temp, fullPath, overwrite: true );
		}
		catch
		{
			try { if( File.Exists( temp ) ) File.Delete( temp ); } catch { }
			throw;
		}
	}

	// =========================================================================
	// Modern SolidWorks 2015+ Container Rewriter
	// =========================================================================

	private readonly record struct ModernChunkSpan( int Start, int End, string Name, uint Flags, bool IsIntact );

	private static byte[] RewriteModern( byte[] original, IReadOnlyDictionary<string, byte[]> updatedStreams )
	{
		if( original.Length < 8 )
			throw new InvalidDataException( "Modern SolidWorks container is too short." );

		int key = original[ 7 ] & 7;
		List<ModernChunkSpan> chunks = ScanModernChunks( original, key );

		// Pick the first intact chunk for each updated stream (or first chunk if none was intact).
		Dictionary<string, int> primaryChunkIndex = new( StringComparer.Ordinal );
		for( int i = 0; i < chunks.Count; i++ )
		{
			ModernChunkSpan c = chunks[ i ];
			if( !updatedStreams.ContainsKey( c.Name ) )
				continue;
			if( !primaryChunkIndex.TryGetValue( c.Name, out int existingIdx ) || ( !chunks[ existingIdx ].IsIntact && c.IsIntact ) )
				primaryChunkIndex[ c.Name ] = i;
		}

		HashSet<string> written = new( StringComparer.Ordinal );
		using MemoryStream output = new( original.Length + 4096 );
		int cursor = 0;

		for( int i = 0; i < chunks.Count; i++ )
		{
			ModernChunkSpan c = chunks[ i ];
			if( c.Start < cursor )
				continue; // defensive against overlapping false markers

			// Copy untouched bytes before this chunk
			if( c.Start > cursor )
				output.Write( original, cursor, c.Start - cursor );

			if( updatedStreams.TryGetValue( c.Name, out byte[]? newContent ) )
			{
				if( primaryChunkIndex.TryGetValue( c.Name, out int primaryIdx ) && primaryIdx == i )
				{
					byte[] encodedChunk = EncodeModernChunk( c.Name, newContent, key, c.Flags );
					output.Write( encodedChunk );
					written.Add( c.Name );
				}
				// Duplicate/stale copies of the replaced stream are intentionally dropped so readers always see the new copy.
			}
			else
			{
				output.Write( original, c.Start, c.End - c.Start );
			}

			cursor = c.End;
		}

		// Copy any trailing bytes after the last chunk
		if( cursor < original.Length )
			output.Write( original, cursor, original.Length - cursor );

		// Append brand-new streams that didn't previously exist
		foreach( var (name, content) in updatedStreams )
		{
			if( !written.Contains( name ) )
				output.Write( EncodeModernChunk( name, content, key, 0x7F667FDFu ) );
		}

		return output.ToArray();
	}

	private static List<ModernChunkSpan> ScanModernChunks( byte[] data, int key )
	{
		List<ModernChunkSpan> result = [];
		ReadOnlySpan<byte> span = data;
		int pos = 0;

		while( pos < span.Length )
		{
			int found = span[ pos.. ].IndexOf( ChunkMarker );
			if( found < 0 )
				break;
			int marker = pos + found;
			if( marker + ChunkHeaderSize > span.Length )
				break;

			uint flags = BinaryPrimitives.ReadUInt32LittleEndian( span[ ( marker + 6 ).. ] );
			uint crc = BinaryPrimitives.ReadUInt32LittleEndian( span[ ( marker + 10 ).. ] );
			uint compressed = BinaryPrimitives.ReadUInt32LittleEndian( span[ ( marker + 14 ).. ] );
			uint uncompressed = BinaryPrimitives.ReadUInt32LittleEndian( span[ ( marker + 18 ).. ] );
			uint nameSize = BinaryPrimitives.ReadUInt32LittleEndian( span[ ( marker + 22 ).. ] );

			if( nameSize is 0 or > MaxNameSize )
			{
				pos = marker + ChunkMarker.Length;
				continue;
			}

			long nameStart = marker + ChunkHeaderSize;
			long payloadStart = nameStart + nameSize;
			long chunkEnd = payloadStart + compressed;
			if( chunkEnd > span.Length )
			{
				pos = marker + ChunkMarker.Length;
				continue;
			}

			string? name = DecodeModernName( span.Slice( (int)nameStart, (int)nameSize ), key );
			if( name is null )
			{
				pos = marker + ChunkMarker.Length;
				continue;
			}

			bool intact = VerifyModernPayload( data, (int)payloadStart, (int)compressed, uncompressed, crc );
			result.Add( new ModernChunkSpan( marker, (int)chunkEnd, name, flags, intact ) );
			pos = (int)chunkEnd;
		}

		return result;
	}

	private static string? DecodeModernName( ReadOnlySpan<byte> encoded, int key )
	{
		char[] chars = new char[ encoded.Length ];
		for( int i = 0; i < encoded.Length; i++ )
		{
			byte b = (byte)( ( encoded[ i ] << key ) | ( encoded[ i ] >> ( 8 - key ) ) );
			if( b is < 0x20 or >= 0x7F )
				return null;
			chars[ i ] = (char)b;
		}
		return new string( chars );
	}

	private static byte[] EncodeModernName( string name, int key )
	{
		byte[] ascii = Encoding.ASCII.GetBytes( name );
		byte[] encoded = new byte[ ascii.Length ];
		for( int i = 0; i < ascii.Length; i++ )
			encoded[ i ] = (byte)( ( ascii[ i ] >> key ) | ( ascii[ i ] << ( 8 - key ) ) );
		return encoded;
	}

	private static bool VerifyModernPayload( byte[] data, int start, int length, uint expectedSize, uint expectedCrc )
	{
		if( expectedSize > 512 * 1024 * 1024 )
			return false;
		try
		{
			using MemoryStream input = new( data, start, length, writable: false );
			using DeflateStream inflater = new( input, CompressionMode.Decompress );
			byte[] output = new byte[ expectedSize ];
			int read = 0;
			while( read < output.Length )
			{
				int n = inflater.Read( output, read, output.Length - read );
				if( n == 0 )
					return false;
				read += n;
			}
			if( inflater.ReadByte() >= 0 )
				return false;
			return Crc32.Compute( output ) == expectedCrc;
		}
		catch
		{
			return false;
		}
	}

	public static byte[] EncodeModernChunk( string name, byte[] content, int key = 4, uint flags = 0x7F667FDFu )
	{
		using MemoryStream deflated = new();
		using( DeflateStream deflater = new( deflated, CompressionLevel.Optimal, leaveOpen: true ) )
			deflater.Write( content );
		byte[] payload = deflated.ToArray();
		byte[] encodedName = EncodeModernName( name, key );

		byte[] chunk = new byte[ ChunkHeaderSize + encodedName.Length + payload.Length ];
		ChunkMarker.CopyTo( chunk );
		BinaryPrimitives.WriteUInt32LittleEndian( chunk.AsSpan( 6 ), flags );
		BinaryPrimitives.WriteUInt32LittleEndian( chunk.AsSpan( 10 ), Crc32.Compute( content ) );
		BinaryPrimitives.WriteUInt32LittleEndian( chunk.AsSpan( 14 ), (uint)payload.Length );
		BinaryPrimitives.WriteUInt32LittleEndian( chunk.AsSpan( 18 ), (uint)content.Length );
		BinaryPrimitives.WriteUInt32LittleEndian( chunk.AsSpan( 22 ), (uint)encodedName.Length );
		encodedName.CopyTo( chunk, ChunkHeaderSize );
		payload.CopyTo( chunk, ChunkHeaderSize + encodedName.Length );
		return chunk;
	}

	// =========================================================================
	// Legacy SolidWorks <=2014 OLE Compound File (MS-CFB v3/v4) Rewriter
	// =========================================================================

	private sealed class OleDirRecord
	{
		public required byte[] Raw128 { get; init; }
		public required string Name { get; init; }
		public byte Type => Raw128[ 0x42 ];
		public uint Left { get => BinaryPrimitives.ReadUInt32LittleEndian( Raw128.AsSpan( 0x44 ) ); set => BinaryPrimitives.WriteUInt32LittleEndian( Raw128.AsSpan( 0x44 ), value ); }
		public uint Right { get => BinaryPrimitives.ReadUInt32LittleEndian( Raw128.AsSpan( 0x48 ) ); set => BinaryPrimitives.WriteUInt32LittleEndian( Raw128.AsSpan( 0x48 ), value ); }
		public uint Child { get => BinaryPrimitives.ReadUInt32LittleEndian( Raw128.AsSpan( 0x4C ) ); set => BinaryPrimitives.WriteUInt32LittleEndian( Raw128.AsSpan( 0x4C ), value ); }
		public uint Start { get => BinaryPrimitives.ReadUInt32LittleEndian( Raw128.AsSpan( 0x74 ) ); set => BinaryPrimitives.WriteUInt32LittleEndian( Raw128.AsSpan( 0x74 ), value ); }
		public ulong Size { get => BinaryPrimitives.ReadUInt64LittleEndian( Raw128.AsSpan( 0x78 ) ); set => BinaryPrimitives.WriteUInt64LittleEndian( Raw128.AsSpan( 0x78 ), value ); }
		public string? FullPath { get; set; }
		public byte[]? StreamBytes { get; set; }
	}

	private static byte[] RewriteOle( byte[] original, IReadOnlyDictionary<string, byte[]> updatedStreams )
	{
		if( original.Length < 512 )
			throw new InvalidDataException( "OLE file is shorter than 512-byte header." );

		byte[] header = original.AsSpan( 0, 512 ).ToArray();
		ushort majorVersion = BinaryPrimitives.ReadUInt16LittleEndian( header.AsSpan( 0x1A ) );
		int sectorSize = 1 << BinaryPrimitives.ReadUInt16LittleEndian( header.AsSpan( 0x1E ) );
		int miniSectorSize = 1 << BinaryPrimitives.ReadUInt16LittleEndian( header.AsSpan( 0x20 ) );
		uint origFatSectors = BinaryPrimitives.ReadUInt32LittleEndian( header.AsSpan( 0x2C ) );
		uint origDirStart = BinaryPrimitives.ReadUInt32LittleEndian( header.AsSpan( 0x30 ) );
		uint miniCutoff = BinaryPrimitives.ReadUInt32LittleEndian( header.AsSpan( 0x38 ) );
		uint origMiniFatStart = BinaryPrimitives.ReadUInt32LittleEndian( header.AsSpan( 0x3C ) );
		uint origMiniFatSectors = BinaryPrimitives.ReadUInt32LittleEndian( header.AsSpan( 0x40 ) );
		uint origDifatStart = BinaryPrimitives.ReadUInt32LittleEndian( header.AsSpan( 0x44 ) );
		uint origDifatSectors = BinaryPrimitives.ReadUInt32LittleEndian( header.AsSpan( 0x48 ) );

		ReadOnlySpan<byte> ReadSector( uint sid )
		{
			long offset = ( (long)sid + 1 ) * sectorSize;
			if( offset + sectorSize > original.Length )
				throw new InvalidDataException( $"OLE sector {sid} extends past EOF." );
			return original.AsSpan( (int)offset, sectorSize );
		}

		// Read existing FAT
		List<uint> fatIds = [];
		for( int i = 0; i < 109; i++ )
			fatIds.Add( BinaryPrimitives.ReadUInt32LittleEndian( header.AsSpan( 0x4C + i * 4 ) ) );
		uint difat = origDifatStart;
		for( uint n = 0; n < origDifatSectors && difat < EndOfChain; n++ )
		{
			ReadOnlySpan<byte> sec = ReadSector( difat );
			int perSec = sectorSize / 4;
			for( int i = 0; i < perSec - 1; i++ )
				fatIds.Add( BinaryPrimitives.ReadUInt32LittleEndian( sec[ ( i * 4 ).. ] ) );
			difat = BinaryPrimitives.ReadUInt32LittleEndian( sec[ ( ( perSec - 1 ) * 4 ).. ] );
		}

		List<uint> fatList = [];
		foreach( uint fid in fatIds.Take( (int)origFatSectors ) )
		{
			ReadOnlySpan<byte> sec = ReadSector( fid );
			for( int i = 0; i + 4 <= sec.Length; i += 4 )
				fatList.Add( BinaryPrimitives.ReadUInt32LittleEndian( sec[ i.. ] ) );
		}
		uint[] fat = [ .. fatList ];

		byte[] ReadChain( uint start )
		{
			using MemoryStream ms = new();
			uint cur = start;
			for( int guard = 0; cur < EndOfChain && cur != FreeSector && guard <= fat.Length; guard++ )
			{
				ms.Write( ReadSector( cur ) );
				cur = cur < fat.Length ? fat[ cur ] : EndOfChain;
			}
			return ms.ToArray();
		}

		uint[] miniFat = [];
		if( origMiniFatSectors > 0 && origMiniFatStart < EndOfChain )
		{
			byte[] mfRaw = ReadChain( origMiniFatStart );
			miniFat = new uint[ mfRaw.Length / 4 ];
			for( int i = 0; i < miniFat.Length; i++ )
				miniFat[ i ] = BinaryPrimitives.ReadUInt32LittleEndian( mfRaw.AsSpan( i * 4 ) );
		}

		byte[] dirBytes = ReadChain( origDirStart );
		List<OleDirRecord> entries = [];
		for( int off = 0; off + 128 <= dirBytes.Length; off += 128 )
		{
			byte[] raw128 = dirBytes.AsSpan( off, 128 ).ToArray();
			int nameLen = Math.Max( BinaryPrimitives.ReadUInt16LittleEndian( raw128.AsSpan( 0x40 ) ) - 2, 0 );
			string name = Encoding.Unicode.GetString( raw128, 0, Math.Min( nameLen, 64 ) );
			entries.Add( new OleDirRecord { Raw128 = raw128, Name = name } );
		}
		if( entries.Count == 0 )
			throw new InvalidDataException( "OLE file has no root directory entry." );

		OleDirRecord root = entries[ 0 ];
		ulong rootSize = majorVersion <= 3 ? (uint)root.Size : root.Size;
		byte[] origMiniStream = [];
		if( root.Start < EndOfChain && rootSize > 0 )
		{
			byte[] chain = ReadChain( root.Start );
			origMiniStream = chain.Length > (long)rootSize ? chain[ ..(int)rootSize ] : chain;
		}

		byte[] ReadStreamBytes( OleDirRecord rec )
		{
			ulong sz = majorVersion <= 3 ? (uint)rec.Size : rec.Size;
			if( sz == 0 )
				return [];
			if( sz < miniCutoff )
			{
				byte[] output = new byte[ sz ];
				int written = 0;
				uint sec = rec.Start;
				for( int guard = 0; sec < EndOfChain && written < output.Length && guard <= miniFat.Length; guard++ )
				{
					int off = (int)sec * miniSectorSize;
					int n = Math.Min( miniSectorSize, output.Length - written );
					if( off + n > origMiniStream.Length )
						throw new InvalidDataException( $"MiniStream overflow reading '{rec.FullPath}'." );
					origMiniStream.AsSpan( off, n ).CopyTo( output.AsSpan( written ) );
					written += n;
					sec = sec < miniFat.Length ? miniFat[ sec ] : EndOfChain;
				}
				return output;
			}
			byte[] regular = ReadChain( rec.Start );
			return regular.Length >= (long)sz ? regular[ ..(int)sz ] : regular;
		}

		// Walk existing storages & streams to resolve paths and load every stream's bytes
		HashSet<uint> visited = [];
		void Walk( uint idx, string prefix )
		{
			if( idx >= entries.Count || !visited.Add( idx ) )
				return;
			OleDirRecord rec = entries[ (int)idx ];
			Walk( rec.Left, prefix );
			string full = prefix + rec.Name;
			rec.FullPath = full;
			if( rec.Type == 1 )
				Walk( rec.Child, full + "/" );
			else if( rec.Type == 2 )
				rec.StreamBytes = ReadStreamBytes( rec );
			Walk( rec.Right, prefix );
		}
		Walk( root.Child, "" );

		// Apply updates to matching streams (supporting transparent __ZLB recompression!)
		const string zlbSuffix = "__ZLB";
		HashSet<string> applied = new( StringComparer.Ordinal );
		foreach( OleDirRecord rec in entries )
		{
			if( rec.Type != 2 || rec.FullPath is null )
				continue;

			if( updatedStreams.TryGetValue( rec.FullPath, out byte[]? directNew ) )
			{
				rec.StreamBytes = directNew;
				applied.Add( rec.FullPath );
			}
			else if( rec.FullPath.EndsWith( zlbSuffix, StringComparison.Ordinal ) )
			{
				string logicalName = rec.FullPath[ ..^zlbSuffix.Length ];
				if( updatedStreams.TryGetValue( logicalName, out byte[]? logicalNew ) )
				{
					rec.StreamBytes = RepackZlb( rec.StreamBytes, logicalNew );
					applied.Add( logicalName );
				}
			}
		}

		// Add any brand-new root-level streams (no '/' in path) that did not previously exist
		foreach( var (name, bytes) in updatedStreams )
		{
			if( applied.Contains( name ) || name.Contains( '/' ) )
				continue;
			OleDirRecord added = CreateNewStreamDirRecord( name, bytes );
			uint newIdx = (uint)entries.Count;
			entries.Add( added );
			InsertChildBst( entries, root, newIdx );
		}

		// ---- Rebuild MiniStream + MiniFAT for streams < miniCutoff ----
		using MemoryStream newMiniStream = new();
		List<uint> newMiniFat = [];
		foreach( OleDirRecord rec in entries )
		{
			if( rec.Type != 2 || rec.StreamBytes is null )
				continue;
			byte[] data = rec.StreamBytes;
			if( data.Length == 0 )
			{
				rec.Start = EndOfChain;
				rec.Size = 0;
				continue;
			}
			if( (uint)data.Length < miniCutoff )
			{
				int sectorsNeeded = ( data.Length + miniSectorSize - 1 ) / miniSectorSize;
				uint firstMini = (uint)newMiniFat.Count;
				for( int i = 0; i < sectorsNeeded; i++ )
					newMiniFat.Add( i + 1 < sectorsNeeded ? firstMini + (uint)i + 1 : EndOfChain );

				newMiniStream.Write( data );
				int rem = data.Length % miniSectorSize;
				if( rem != 0 )
					newMiniStream.Write( new byte[ miniSectorSize - rem ] );

				rec.Start = firstMini;
				rec.Size = (ulong)data.Length;
			}
		}

		byte[] miniStreamBytes = newMiniStream.ToArray();
		byte[] miniFatBytes = new byte[ newMiniFat.Count * 4 ];
		for( int i = 0; i < newMiniFat.Count; i++ )
			BinaryPrimitives.WriteUInt32LittleEndian( miniFatBytes.AsSpan( i * 4 ), newMiniFat[ i ] );

		// ---- Allocate regular sectors for regular streams, MiniStream, MiniFAT, and Directory ----
		List<byte[]> dataSectors = [];
		List<uint> newFat = [];

		uint WriteRegularChain( byte[] bytes )
		{
			if( bytes.Length == 0 )
				return EndOfChain;
			int count = ( bytes.Length + sectorSize - 1 ) / sectorSize;
			uint first = (uint)dataSectors.Count;
			for( int i = 0; i < count; i++ )
			{
				byte[] sec = new byte[ sectorSize ];
				int offset = i * sectorSize;
				int copy = Math.Min( sectorSize, bytes.Length - offset );
				bytes.AsSpan( offset, copy ).CopyTo( sec );
				dataSectors.Add( sec );
				newFat.Add( i + 1 < count ? first + (uint)i + 1 : EndOfChain );
			}
			return first;
		}

		// 1. Regular streams (>= miniCutoff)
		foreach( OleDirRecord rec in entries )
		{
			if( rec.Type != 2 || rec.StreamBytes is null )
				continue;
			if( (uint)rec.StreamBytes.Length >= miniCutoff )
			{
				rec.Start = WriteRegularChain( rec.StreamBytes );
				rec.Size = (ulong)rec.StreamBytes.Length;
			}
		}

		// 2. Root Entry MiniStream
		if( miniStreamBytes.Length > 0 )
		{
			root.Start = WriteRegularChain( miniStreamBytes );
			root.Size = (ulong)miniStreamBytes.Length;
		}
		else
		{
			root.Start = EndOfChain;
			root.Size = 0;
		}

		// 3. MiniFAT sectors
		uint newMiniFatStart = EndOfChain;
		uint newMiniFatSectorCount = 0;
		if( miniFatBytes.Length > 0 )
		{
			// Pad final MiniFAT sector with FreeSector (0xFFFFFFFF)
			int mfSectors = ( miniFatBytes.Length + sectorSize - 1 ) / sectorSize;
			byte[] paddedMf = new byte[ mfSectors * sectorSize ];
			Array.Fill( paddedMf, (byte)0xFF );
			miniFatBytes.CopyTo( paddedMf, 0 );
			newMiniFatStart = WriteRegularChain( paddedMf );
			newMiniFatSectorCount = (uint)mfSectors;
		}

		// 4. Directory sectors (pad final sector with empty 128-byte slots)
		int entriesPerDirSector = sectorSize / 128;
		int dirSectorCount = ( entries.Count + entriesPerDirSector - 1 ) / entriesPerDirSector;
		byte[] newDirBytes = new byte[ dirSectorCount * sectorSize ];
		for( int i = 0; i < entries.Count; i++ )
			entries[ i ].Raw128.CopyTo( newDirBytes, i * 128 );
		// Empty trailing directory entries must have Left/Right/Child = NoStream (0xFFFFFFFF)
		for( int i = entries.Count; i < dirSectorCount * entriesPerDirSector; i++ )
		{
			Span<byte> slot = newDirBytes.AsSpan( i * 128, 128 );
			BinaryPrimitives.WriteUInt32LittleEndian( slot[ 0x44.. ], NoStream );
			BinaryPrimitives.WriteUInt32LittleEndian( slot[ 0x48.. ], NoStream );
			BinaryPrimitives.WriteUInt32LittleEndian( slot[ 0x4C.. ], NoStream );
		}
		uint newDirStart = WriteRegularChain( newDirBytes );

		// 5. Compute FAT + DIFAT sector counts (fixed-point convergence)
		int fatEntriesPerSector = sectorSize / 4;
		int difatEntriesPerSector = fatEntriesPerSector - 1;
		int baseSectorCount = dataSectors.Count;
		int fatSectorCount = 1;
		int difatSectorCount = 0;
		while( true )
		{
			int totalSectors = baseSectorCount + fatSectorCount + difatSectorCount;
			int neededFat = ( totalSectors + fatEntriesPerSector - 1 ) / fatEntriesPerSector;
			int neededDifat = neededFat <= 109 ? 0 : ( neededFat - 109 + difatEntriesPerSector - 1 ) / difatEntriesPerSector;
			if( neededFat == fatSectorCount && neededDifat == difatSectorCount )
				break;
			fatSectorCount = neededFat;
			difatSectorCount = neededDifat;
		}

		uint[] difatSectorIds = new uint[ difatSectorCount ];
		for( int i = 0; i < difatSectorCount; i++ )
		{
			difatSectorIds[ i ] = (uint)( baseSectorCount + i );
			newFat.Add( DifatSectorTag );
		}

		uint[] fatSectorIds = new uint[ fatSectorCount ];
		for( int i = 0; i < fatSectorCount; i++ )
		{
			fatSectorIds[ i ] = (uint)( baseSectorCount + difatSectorCount + i );
			newFat.Add( FatSectorTag );
		}

		// Build DIFAT sector buffers
		List<byte[]> difatBuffers = [];
		int fatIdxCursor = 109;
		for( int d = 0; d < difatSectorCount; d++ )
		{
			byte[] sec = new byte[ sectorSize ];
			for( int i = 0; i < difatEntriesPerSector; i++ )
			{
				uint val = fatIdxCursor < fatSectorIds.Length ? fatSectorIds[ fatIdxCursor++ ] : FreeSector;
				BinaryPrimitives.WriteUInt32LittleEndian( sec.AsSpan( i * 4 ), val );
			}
			uint nextDifat = d + 1 < difatSectorCount ? difatSectorIds[ d + 1 ] : EndOfChain;
			BinaryPrimitives.WriteUInt32LittleEndian( sec.AsSpan( difatEntriesPerSector * 4 ), nextDifat );
			difatBuffers.Add( sec );
		}

		// Build FAT sector buffers
		List<byte[]> fatBuffers = [];
		for( int f = 0; f < fatSectorCount; f++ )
		{
			byte[] sec = new byte[ sectorSize ];
			for( int i = 0; i < fatEntriesPerSector; i++ )
			{
				int entryIdx = f * fatEntriesPerSector + i;
				uint val = entryIdx < newFat.Count ? newFat[ entryIdx ] : FreeSector;
				BinaryPrimitives.WriteUInt32LittleEndian( sec.AsSpan( i * 4 ), val );
			}
			fatBuffers.Add( sec );
		}

		// 6. Patch 512-byte CFB header
		BinaryPrimitives.WriteUInt32LittleEndian( header.AsSpan( 0x28 ), majorVersion >= 4 ? (uint)dirSectorCount : 0u );
		BinaryPrimitives.WriteUInt32LittleEndian( header.AsSpan( 0x2C ), (uint)fatSectorCount );
		BinaryPrimitives.WriteUInt32LittleEndian( header.AsSpan( 0x30 ), newDirStart );
		BinaryPrimitives.WriteUInt32LittleEndian( header.AsSpan( 0x3C ), newMiniFatStart );
		BinaryPrimitives.WriteUInt32LittleEndian( header.AsSpan( 0x40 ), newMiniFatSectorCount );
		BinaryPrimitives.WriteUInt32LittleEndian( header.AsSpan( 0x44 ), difatSectorCount > 0 ? difatSectorIds[ 0 ] : EndOfChain );
		BinaryPrimitives.WriteUInt32LittleEndian( header.AsSpan( 0x48 ), (uint)difatSectorCount );
		for( int i = 0; i < 109; i++ )
		{
			uint val = i < fatSectorIds.Length ? fatSectorIds[ i ] : FreeSector;
			BinaryPrimitives.WriteUInt32LittleEndian( header.AsSpan( 0x4C + i * 4 ), val );
		}

		// Assemble final CFB file
		int headerBlockSize = Math.Max( 512, sectorSize );
		using MemoryStream finalMs = new( headerBlockSize + ( dataSectors.Count + difatBuffers.Count + fatBuffers.Count ) * sectorSize );
		finalMs.Write( header );
		if( headerBlockSize > 512 )
			finalMs.Write( new byte[ headerBlockSize - 512 ] );
		foreach( byte[] s in dataSectors )
			finalMs.Write( s );
		foreach( byte[] s in difatBuffers )
			finalMs.Write( s );
		foreach( byte[] s in fatBuffers )
			finalMs.Write( s );

		return finalMs.ToArray();
	}

	private static byte[] RepackZlb( byte[]? existingZlb, byte[] newUncompressed )
	{
		byte[] prefix16 = new byte[ 16 ];
		byte[] trailer = new byte[ 8 ];
		if( existingZlb is { Length: >= 24 } )
		{
			existingZlb.AsSpan( 0, 16 ).CopyTo( prefix16 );
			uint oldComp = BinaryPrimitives.ReadUInt32LittleEndian( existingZlb.AsSpan( 20 ) );
			int trailerStart = 24 + (int)oldComp;
			if( trailerStart >= 24 && trailerStart <= existingZlb.Length )
				trailer = existingZlb.AsSpan( trailerStart ).ToArray();
		}

		using MemoryStream compressedMs = new();
		using( ZLibStream zlib = new( compressedMs, CompressionLevel.Optimal, leaveOpen: true ) )
			zlib.Write( newUncompressed );
		byte[] compressed = compressedMs.ToArray();

		byte[] result = new byte[ 24 + compressed.Length + trailer.Length ];
		prefix16.CopyTo( result, 0 );
		BinaryPrimitives.WriteUInt32LittleEndian( result.AsSpan( 16 ), (uint)newUncompressed.Length );
		BinaryPrimitives.WriteUInt32LittleEndian( result.AsSpan( 20 ), (uint)compressed.Length );
		compressed.CopyTo( result, 24 );
		trailer.CopyTo( result, 24 + compressed.Length );
		return result;
	}

	private static OleDirRecord CreateNewStreamDirRecord( string name, byte[] bytes )
	{
		if( name.Length > 31 )
			throw new ArgumentException( $"OLE stream name '{name}' exceeds 31 characters." );
		byte[] raw = new byte[ 128 ];
		byte[] utf16 = Encoding.Unicode.GetBytes( name + "\0" );
		utf16.CopyTo( raw, 0 );
		BinaryPrimitives.WriteUInt16LittleEndian( raw.AsSpan( 0x40 ), (ushort)utf16.Length );
		raw[ 0x42 ] = 2; // STGTY_STREAM
		raw[ 0x43 ] = 1; // Black
		BinaryPrimitives.WriteUInt32LittleEndian( raw.AsSpan( 0x44 ), NoStream );
		BinaryPrimitives.WriteUInt32LittleEndian( raw.AsSpan( 0x48 ), NoStream );
		BinaryPrimitives.WriteUInt32LittleEndian( raw.AsSpan( 0x4C ), NoStream );
		BinaryPrimitives.WriteUInt32LittleEndian( raw.AsSpan( 0x74 ), EndOfChain );
		BinaryPrimitives.WriteUInt64LittleEndian( raw.AsSpan( 0x78 ), (ulong)bytes.Length );
		return new OleDirRecord { Raw128 = raw, Name = name, FullPath = name, StreamBytes = bytes };
	}

	private static void InsertChildBst( List<OleDirRecord> entries, OleDirRecord parent, uint newIndex )
	{
		if( parent.Child == NoStream || parent.Child >= entries.Count )
		{
			parent.Child = newIndex;
			return;
		}
		string newName = entries[ (int)newIndex ].Name;
		uint cur = parent.Child;
		for( int guard = 0; guard <= entries.Count; guard++ )
		{
			OleDirRecord node = entries[ (int)cur ];
			int cmp = CompareCfbName( newName, node.Name );
			if( cmp < 0 )
			{
				if( node.Left == NoStream || node.Left >= entries.Count )
				{
					node.Left = newIndex;
					return;
				}
				cur = node.Left;
			}
			else
			{
				if( node.Right == NoStream || node.Right >= entries.Count )
				{
					node.Right = newIndex;
					return;
				}
				cur = node.Right;
			}
		}
	}

	private static int CompareCfbName( string a, string b )
	{
		int lenCmp = a.Length.CompareTo( b.Length );
		return lenCmp != 0 ? lenCmp : string.Compare( a, b, StringComparison.OrdinalIgnoreCase );
	}
}

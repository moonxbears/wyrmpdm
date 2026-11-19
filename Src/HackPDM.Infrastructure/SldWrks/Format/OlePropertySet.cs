using System.Buffers.Binary;
using System.Text;

namespace HackPDM.Infrastructure.SldWrks.Format;

/// <summary>
/// Reads the user-defined section of an OLE property set (MS-OLEPS), as stored in pre-2015
/// SolidWorks files: "\u0005DocumentSummaryInformation" for file properties and
/// "Config-N-Properties" for each configuration's properties.
/// </summary>
public static class OlePropertySet
{
	/// <summary>FMTID_UserDefinedProperties {D5CDD505-2E9C-101B-9397-08002B2CF9AE}.</summary>
	private static readonly Guid UserDefined = new( "D5CDD505-2E9C-101B-9397-08002B2CF9AE" );

	private const uint PidDictionary = 0, PidCodePage = 1;

	/// <summary>Reads the named properties of the user-defined section of an OLE property set stream.</summary>
	/// <param name="stream">The raw property set stream, e.g. "\u0005DocumentSummaryInformation" or "Config-N-Properties".</param>
	/// <returns>
	/// (name, VT type, value) for each named property, in dictionary order. The value is a short, int, uint, long, float,
	/// double, bool, <see cref="DateTime"/> or string for the types in <see cref="VarType"/>, and null for other types or a
	/// truncated value. Empty when the stream isn't a property set or has no user-defined section.
	/// </returns>
	/// <remarks>Strings use the section's code page; code pages other than UTF-8, UTF-16 and those built into .NET are read as Latin-1.</remarks>
	/// <exception cref="ArgumentException">A VT_DATE value is not a valid OLE Automation date.</exception>
	public static List<(string Name, ushort VarType, object? Value)> ReadUserDefined( byte[] stream )
	{
		var result = new List<(string, ushort, object?)>();
		var data = stream.AsSpan();
		if( data.Length < 28 || BinaryPrimitives.ReadUInt16LittleEndian( data ) != 0xFFFE )
			return result;

		uint sections = BinaryPrimitives.ReadUInt32LittleEndian( data[ 24.. ] );
		for( int s = 0; s < sections && 28 + s * 20 + 20 <= data.Length; s++ )
		{
			var fmtid = new Guid( data.Slice( 28 + s * 20, 16 ) );
			if( fmtid != UserDefined )
				continue;
			int sectionStart = (int)BinaryPrimitives.ReadUInt32LittleEndian( data[ ( 28 + s * 20 + 16 ).. ] );
			ReadSection( data, sectionStart, result );
		}
		return result;
	}

	private static void ReadSection( ReadOnlySpan<byte> data, int start, List<(string, ushort, object?)> result )
	{
		if( start + 8 > data.Length )
			return;
		uint count = BinaryPrimitives.ReadUInt32LittleEndian( data[ ( start + 4 ).. ] );
		var offsets = new Dictionary<uint, int>();
		for( int i = 0; i < count && start + 8 + i * 8 + 8 <= data.Length; i++ )
		{
			uint pid = BinaryPrimitives.ReadUInt32LittleEndian( data[ ( start + 8 + i * 8 ).. ] );
			int offset = (int)BinaryPrimitives.ReadUInt32LittleEndian( data[ ( start + 12 + i * 8 ).. ] );
			offsets[ pid ] = start + offset;
		}

		int codePage = 1252;
		if( offsets.TryGetValue( PidCodePage, out int cpAt ) && cpAt + 6 <= data.Length )
			codePage = BinaryPrimitives.ReadUInt16LittleEndian( data[ ( cpAt + 4 ).. ] );
		var encoding = EncodingFor( codePage );

		if( !offsets.TryGetValue( PidDictionary, out int dictAt ) )
			return;
		foreach( var (pid, name) in ReadDictionary( data, dictAt, codePage, encoding ) )
		{
			if( pid is PidDictionary or PidCodePage || !offsets.TryGetValue( pid, out int valueAt ) )
				continue;
			var (type, value) = ReadValue( data, valueAt, codePage, encoding );
			result.Add( ( name, type, value ) );
		}
	}

	private static List<(uint Pid, string Name)> ReadDictionary( ReadOnlySpan<byte> data, int at, int codePage, Encoding encoding )
	{
		var names = new List<(uint, string)>();
		if( at + 4 > data.Length )
			return names;
		uint count = BinaryPrimitives.ReadUInt32LittleEndian( data[ at.. ] );
		int pos = at + 4;
		for( int i = 0; i < count && pos + 8 <= data.Length; i++ )
		{
			uint pid = BinaryPrimitives.ReadUInt32LittleEndian( data[ pos.. ] );
			int length = (int)BinaryPrimitives.ReadUInt32LittleEndian( data[ ( pos + 4 ).. ] );
			pos += 8;
			int bytes = codePage == 1200 ? length * 2 : length;
			if( length < 0 || pos + bytes > data.Length )
				break;
			names.Add( ( pid, encoding.GetString( data.Slice( pos, bytes ) ).TrimEnd( '\0' ) ) );
			pos += bytes;
			if( codePage == 1200 )
				pos = ( pos + 3 ) & ~3; // Unicode entries are padded to 4 bytes
		}
		return names;
	}

	private static (ushort Type, object? Value) ReadValue( ReadOnlySpan<byte> data, int at, int codePage, Encoding encoding )
	{
		if( at + 4 > data.Length )
			return ( 0, null );
		ushort type = BinaryPrimitives.ReadUInt16LittleEndian( data[ at.. ] );
		var v = data[ ( at + 4 ).. ];
		try
		{
			object? value = type switch
			{
				VarType.I2 => BinaryPrimitives.ReadInt16LittleEndian( v ),
				VarType.I4 or VarType.Int => BinaryPrimitives.ReadInt32LittleEndian( v ),
				VarType.UI4 or VarType.UInt => BinaryPrimitives.ReadUInt32LittleEndian( v ),
				VarType.I8 => BinaryPrimitives.ReadInt64LittleEndian( v ),
				VarType.R4 => BinaryPrimitives.ReadSingleLittleEndian( v ),
				VarType.R8 => BinaryPrimitives.ReadDoubleLittleEndian( v ),
				VarType.Bool => BinaryPrimitives.ReadInt16LittleEndian( v ) != 0,
				VarType.Date => DateTime.FromOADate( BinaryPrimitives.ReadDoubleLittleEndian( v ) ),
				VarType.FileTime => DateTime.FromFileTimeUtc( BinaryPrimitives.ReadInt64LittleEndian( v ) ),
				VarType.LpStr => ReadLpStr( v, codePage, encoding ),
				VarType.LpWStr => ReadLpWStr( v ),
				_ => null,
			};
			return ( type, value );
		}
		catch( ArgumentOutOfRangeException )
		{
			return ( type, null );
		}
	}

	private static string ReadLpStr( ReadOnlySpan<byte> v, int codePage, Encoding encoding )
	{
		int length = (int)BinaryPrimitives.ReadUInt32LittleEndian( v );
		if( codePage == 1200 )
			return ReadLpWStr( v );
		return encoding.GetString( v.Slice( 4, length ) ).TrimEnd( '\0' );
	}

	private static string ReadLpWStr( ReadOnlySpan<byte> v )
	{
		int chars = (int)BinaryPrimitives.ReadUInt32LittleEndian( v );
		return Encoding.Unicode.GetString( v.Slice( 4, chars * 2 ) ).TrimEnd( '\0' );
	}

	private static Encoding EncodingFor( int codePage )
	{
		try
		{
			return codePage switch
			{
				65001 => Encoding.UTF8,
				1200 => Encoding.Unicode,
				_ => Encoding.GetEncoding( codePage ),
			};
		}
		catch( Exception e ) when( e is ArgumentException or NotSupportedException )
		{
			return Encoding.Latin1; // code pages beyond Latin-1/UTF need System.Text.Encoding.CodePages
		}
	}

	/// <summary>The VARENUM codes property sets use (also the TypeID in the 2015+ XML).</summary>
	public static class VarType
	{
		public const ushort I2 = 2, I4 = 3, R4 = 4, R8 = 5, Date = 7, Bool = 11, I8 = 20,
			UI4 = 19, Int = 22, UInt = 23, LpStr = 30, LpWStr = 31, FileTime = 64;
	}

	/// <summary>
	/// Rebuilds an MS-OLEPS property set stream with the given user-defined properties while preserving
	/// any other sections (such as <c>FMTID_DocSummaryInformation</c>) verbatim.
	/// </summary>
	public static byte[] WriteUserDefined(
		byte[]? existingStream,
		IReadOnlyList<(string Name, ushort VarType, object? Value)> properties )
	{
		List<(Guid FmtId, byte[] RawSection)> preservedSections = [];
		byte[] headerPrefix = new byte[ 24 ];
		BinaryPrimitives.WriteUInt16LittleEndian( headerPrefix.AsSpan( 0 ), 0xFFFE );
		BinaryPrimitives.WriteUInt16LittleEndian( headerPrefix.AsSpan( 2 ), 0x0000 );
		BinaryPrimitives.WriteUInt32LittleEndian( headerPrefix.AsSpan( 4 ), 0x00020105 ); // Windows OS version

		if( existingStream is { Length: >= 28 }
			&& BinaryPrimitives.ReadUInt16LittleEndian( existingStream ) == 0xFFFE )
		{
			existingStream.AsSpan( 0, 24 ).CopyTo( headerPrefix );
			uint existingCount = BinaryPrimitives.ReadUInt32LittleEndian( existingStream.AsSpan( 24 ) );
			for( int s = 0; s < existingCount && 28 + s * 20 + 20 <= existingStream.Length; s++ )
			{
				Guid fmtid = new( existingStream.AsSpan( 28 + s * 20, 16 ) );
				if( fmtid == UserDefined )
					continue;
				int secStart = (int)BinaryPrimitives.ReadUInt32LittleEndian( existingStream.AsSpan( 28 + s * 20 + 16 ) );
				if( secStart < 0 || secStart + 4 > existingStream.Length )
					continue;
				int secSize = (int)BinaryPrimitives.ReadUInt32LittleEndian( existingStream.AsSpan( secStart ) );
				if( secSize <= 0 || secStart + secSize > existingStream.Length )
					secSize = existingStream.Length - secStart;
				byte[] raw = existingStream.AsSpan( secStart, secSize ).ToArray();
				preservedSections.Add( ( fmtid, raw ) );
			}
		}

		byte[] userSection = BuildUserDefinedSection( properties );
		preservedSections.Add( ( UserDefined, userSection ) );

		int sectionCount = preservedSections.Count;
		int headerSize = 28 + sectionCount * 20;
		using MemoryStream ms = new();
		ms.Write( headerPrefix );
		WriteUInt32( ms, (uint)sectionCount );

		int[] offsets = new int[ sectionCount ];
		int cursor = Align4( headerSize );
		for( int i = 0; i < sectionCount; i++ )
		{
			offsets[ i ] = cursor;
			cursor = Align4( cursor + preservedSections[ i ].RawSection.Length );
		}

		for( int i = 0; i < sectionCount; i++ )
		{
			ms.Write( preservedSections[ i ].FmtId.ToByteArray() );
			WriteUInt32( ms, (uint)offsets[ i ] );
		}

		for( int i = 0; i < sectionCount; i++ )
		{
			PadTo( ms, offsets[ i ] );
			ms.Write( preservedSections[ i ].RawSection );
		}
		PadTo( ms, Align4( (int)ms.Length ) );
		return ms.ToArray();
	}

	private static byte[] BuildUserDefinedSection( IReadOnlyList<(string Name, ushort VarType, object? Value)> properties )
	{
		bool needsUnicode = properties.Any( p =>
			HasNonAscii( p.Name )
			|| ( p.Value is string sv && HasNonAscii( sv ) ) );

		ushort codePage = needsUnicode ? (ushort)1200 : (ushort)1252;
		Encoding textEnc = needsUnicode ? Encoding.Unicode : Encoding.Latin1;

		// Filter out empty names and assign PIDs starting at 2
		List<(uint Pid, string Name, ushort VarType, object? Value)> items = [];
		uint nextPid = 2;
		foreach( var p in properties )
		{
			if( string.IsNullOrWhiteSpace( p.Name ) )
				continue;
			items.Add( ( nextPid++, p.Name, p.VarType, p.Value ) );
		}

		// Serialize dictionary (PID 0)
		byte[] dictBytes;
		using( MemoryStream dictMs = new() )
		{
			WriteUInt32( dictMs, (uint)items.Count );
			foreach( var item in items )
			{
				WriteUInt32( dictMs, item.Pid );
				if( codePage == 1200 )
				{
					byte[] nameUtf16 = Encoding.Unicode.GetBytes( item.Name + "\0" );
					WriteUInt32( dictMs, (uint)( item.Name.Length + 1 ) );
					dictMs.Write( nameUtf16 );
					PadTo( dictMs, Align4( (int)dictMs.Length ) );
				}
				else
				{
					byte[] nameBytes = textEnc.GetBytes( item.Name + "\0" );
					WriteUInt32( dictMs, (uint)nameBytes.Length );
					dictMs.Write( nameBytes );
				}
			}
			PadTo( dictMs, Align4( (int)dictMs.Length ) );
			dictBytes = dictMs.ToArray();
		}

		// Serialize code page (PID 1)
		byte[] cpBytes = new byte[ 8 ];
		BinaryPrimitives.WriteUInt16LittleEndian( cpBytes.AsSpan( 0 ), VarType.I2 );
		BinaryPrimitives.WriteUInt16LittleEndian( cpBytes.AsSpan( 4 ), codePage );

		// Serialize each property value
		List<(uint Pid, byte[] Bytes)> serialized =
		[
			( PidDictionary, dictBytes ),
			( PidCodePage, cpBytes ),
		];
		foreach( var item in items )
			serialized.Add( ( item.Pid, SerializePropertyValue( item.VarType, item.Value, codePage, textEnc ) ) );

		int tableOffset = 8;
		int dataStart = tableOffset + serialized.Count * 8;
		int[] propOffsets = new int[ serialized.Count ];
		int relCursor = dataStart;
		for( int i = 0; i < serialized.Count; i++ )
		{
			propOffsets[ i ] = relCursor;
			relCursor = Align4( relCursor + serialized[ i ].Bytes.Length );
		}

		using MemoryStream secMs = new();
		WriteUInt32( secMs, (uint)relCursor ); // cbSection
		WriteUInt32( secMs, (uint)serialized.Count ); // cProperties
		for( int i = 0; i < serialized.Count; i++ )
		{
			WriteUInt32( secMs, serialized[ i ].Pid );
			WriteUInt32( secMs, (uint)propOffsets[ i ] );
		}
		for( int i = 0; i < serialized.Count; i++ )
		{
			PadTo( secMs, propOffsets[ i ] );
			secMs.Write( serialized[ i ].Bytes );
		}
		PadTo( secMs, relCursor );
		return secMs.ToArray();
	}

	private static byte[] SerializePropertyValue( ushort varType, object? value, ushort codePage, Encoding textEnc )
	{
		using MemoryStream ms = new();
		switch( varType )
		{
			case VarType.Bool:
			{
				bool b = value switch
				{
					bool bv => bv,
					string sv => sv.Trim().ToLowerInvariant() is "yes" or "true" or "1" or "-1",
					_ => Convert.ToBoolean( value ?? false, System.Globalization.CultureInfo.InvariantCulture ),
				};
				WriteUInt16( ms, VarType.Bool );
				WriteUInt16( ms, 0 );
				WriteInt16( ms, b ? (short)-1 : (short)0 );
				WriteUInt16( ms, 0 );
				break;
			}
			case VarType.I4 or VarType.Int:
			{
				int iv = Convert.ToInt32( value ?? 0, System.Globalization.CultureInfo.InvariantCulture );
				WriteUInt16( ms, VarType.I4 );
				WriteUInt16( ms, 0 );
				WriteInt32( ms, iv );
				break;
			}
			case VarType.R8:
			{
				double dv = Convert.ToDouble( value ?? 0d, System.Globalization.CultureInfo.InvariantCulture );
				WriteUInt16( ms, VarType.R8 );
				WriteUInt16( ms, 0 );
				byte[] buf = new byte[ 8 ];
				BinaryPrimitives.WriteDoubleLittleEndian( buf, dv );
				ms.Write( buf );
				break;
			}
			case VarType.FileTime or VarType.Date:
			{
				DateTime dt = value switch
				{
					DateTime d => d.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind( d, DateTimeKind.Utc ) : d.ToUniversalTime(),
					DateTimeOffset dto => dto.UtcDateTime,
					string s when DateTime.TryParse( s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed ) => parsed,
					_ => DateTime.UtcNow,
				};
				WriteUInt16( ms, VarType.FileTime );
				WriteUInt16( ms, 0 );
				byte[] buf = new byte[ 8 ];
				BinaryPrimitives.WriteInt64LittleEndian( buf, dt.ToFileTimeUtc() );
				ms.Write( buf );
				break;
			}
			default:
			{
				string text = value?.ToString() ?? string.Empty;
				if( codePage == 1200 || varType == VarType.LpWStr )
				{
					WriteUInt16( ms, VarType.LpWStr );
					WriteUInt16( ms, 0 );
					WriteUInt32( ms, (uint)( text.Length + 1 ) );
					ms.Write( Encoding.Unicode.GetBytes( text + "\0" ) );
				}
				else
				{
					WriteUInt16( ms, VarType.LpStr );
					WriteUInt16( ms, 0 );
					byte[] raw = textEnc.GetBytes( text + "\0" );
					WriteUInt32( ms, (uint)raw.Length );
					ms.Write( raw );
				}
				break;
			}
		}
		PadTo( ms, Align4( (int)ms.Length ) );
		return ms.ToArray();
	}

	private static bool HasNonAscii( string text )
	{
		foreach( char c in text )
			if( c > 0x7F )
				return true;
		return false;
	}

	private static int Align4( int n ) => ( n + 3 ) & ~3;

	private static void PadTo( MemoryStream ms, int target )
	{
		while( ms.Length < target )
			ms.WriteByte( 0 );
	}

	private static void WriteUInt16( MemoryStream ms, ushort v )
	{
		Span<byte> b = stackalloc byte[ 2 ];
		BinaryPrimitives.WriteUInt16LittleEndian( b, v );
		ms.Write( b );
	}

	private static void WriteInt16( MemoryStream ms, short v )
	{
		Span<byte> b = stackalloc byte[ 2 ];
		BinaryPrimitives.WriteInt16LittleEndian( b, v );
		ms.Write( b );
	}

	private static void WriteUInt32( MemoryStream ms, uint v )
	{
		Span<byte> b = stackalloc byte[ 4 ];
		BinaryPrimitives.WriteUInt32LittleEndian( b, v );
		ms.Write( b );
	}

	private static void WriteInt32( MemoryStream ms, int v )
	{
		Span<byte> b = stackalloc byte[ 4 ];
		BinaryPrimitives.WriteInt32LittleEndian( b, v );
		ms.Write( b );
	}
}

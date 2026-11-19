using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

using HackPDM.Infrastructure.SldWrks.Format;

namespace HackPDM.Infrastructure.SldWrks;

/// <summary>One custom property edit (add, update, or remove) for a SolidWorks document.</summary>
public sealed record SwPropertyEdit(
	string Configuration,
	string Name,
	string? Type = null,
	object? Value = null,
	bool Remove = false );

/// <summary>One dependency path replacement rule for a SolidWorks document.</summary>
public sealed record SwDependencyReplacement(
	string OldPath,
	string NewPath );

/// <summary>Result of updating SolidWorks custom properties.</summary>
public sealed record SwPropertyWriteResult(
	string FilePath,
	string ContainerFormat,
	string Engine,
	int AppliedCount,
	IReadOnlyList<string> ModifiedStreams,
	IReadOnlyList<SwFileReader.SwProperty> CurrentProperties );

/// <summary>One dependency path rewrite entry (planned or applied).</summary>
public sealed record SwDependencyRewriteEntry(
	string FileName,
	string OldPath,
	string NewPath,
	bool Changed );

/// <summary>Result of rewriting SolidWorks dependency paths.</summary>
public sealed record SwDependencyWriteResult(
	string FilePath,
	string ContainerFormat,
	string Engine,
	bool DryRun,
	int RewrittenCount,
	IReadOnlyList<SwDependencyRewriteEntry> Rewrites,
	IReadOnlyList<string> ModifiedStreams,
	IReadOnlyList<string> VerifiedDependencies );

/// <summary>
/// Cross-platform editor for SolidWorks files (<c>.SLDPRT</c>, <c>.SLDASM</c>, <c>.SLDDRW</c>):
/// edits file-level and configuration-level custom properties and rewrites stored dependency paths
/// across both SolidWorks 2015+ (Modern chunked) and SolidWorks 2014-and-earlier (OLE / MS-CFB) files
/// on Windows, Linux, and macOS without requiring SolidWorks or its COM Document Manager.
/// </summary>
public static partial class SwFileWriter
{
	[GeneratedRegex( @"^docProps/Config-(\d+)-Properties\.xml$" )]
	private static partial Regex ConfigXmlRegex();

	[GeneratedRegex( @"^Config-(\d+)-Properties$" )]
	private static partial Regex ConfigOleRegex();

	[GeneratedRegex( @"^Contents/Config-\d+$" )]
	private static partial Regex ContentsConfigRegex();

	/// <summary>
	/// Normalizes a dependency path string to SolidWorks' expected backslash separator style
	/// while preserving POSIX absolute paths when explicitly requested.
	/// </summary>
	public static string NormalizeSolidWorksPath( string path, bool forceBackslashes = true )
	{
		if( string.IsNullOrWhiteSpace( path ) )
			return string.Empty;
		string trimmed = path.Trim();
		return forceBackslashes ? trimmed.Replace( '/', '\\' ) : trimmed;
	}

	// =========================================================================
	// Custom Properties Editing
	// =========================================================================

	/// <summary>Adds, updates, or removes custom properties in a SolidWorks document.</summary>
	public static SwPropertyWriteResult SetProperties(
		string filePath,
		IReadOnlyList<SwPropertyEdit> edits )
	{
		if( string.IsNullOrWhiteSpace( filePath ) )
			throw new ArgumentException( "filePath is required.", nameof( filePath ) );
		if( !SwDocumentTypes.IsSolidWorks( filePath ) )
			throw new ArgumentException( $"File '{filePath}' is not a SolidWorks document (.sldprt, .sldasm, .slddrw).", nameof( filePath ) );
		if( !File.Exists( filePath ) )
			throw new FileNotFoundException( $"SolidWorks file not found: '{filePath}'.", filePath );
		if( edits is null || edits.Count == 0 )
			throw new ArgumentException( "At least one property edit is required.", nameof( edits ) );

		byte[] originalBytes = File.ReadAllBytes( filePath );
		SwStreamStore store = SwStreamStore.FromBytes( originalBytes );
		if( store.Format == SwStreamStore.ContainerFormat.Unknown )
			throw new InvalidDataException( $"Unrecognised SolidWorks container format: '{filePath}'." );

		// Build configuration name -> index lookup
		SortedDictionary<int, (string StreamName, bool IsXml)> configStreams = new();
		foreach( string name in store.Names )
		{
			if( ConfigXmlRegex().Match( name ) is { Success: true } x )
				configStreams[ int.Parse( x.Groups[ 1 ].Value, CultureInfo.InvariantCulture ) ] = ( name, true );
			else if( ConfigOleRegex().Match( name ) is { Success: true } o )
				configStreams.TryAdd( int.Parse( o.Groups[ 1 ].Value, CultureInfo.InvariantCulture ), ( name, false ) );
		}

		Dictionary<int, string> indexToConfigName = configStreams.Count > 0
			? SwFileReader.ConfigurationNames( store, configStreams.Keys )
			: new();

		Dictionary<string, int> configNameToIndex = new( StringComparer.OrdinalIgnoreCase );
		foreach( var (idx, cfgName) in indexToConfigName )
			configNameToIndex.TryAdd( cfgName, idx );

		// Group edits by target configuration ("" = file-level)
		var grouped = edits
			.GroupBy( e => e.Configuration?.Trim() ?? string.Empty, StringComparer.OrdinalIgnoreCase )
			.ToList();

		Dictionary<string, byte[]> updatedStreams = new( StringComparer.Ordinal );
		int appliedCount = 0;

		foreach( var group in grouped )
		{
			string config = group.Key;
			List<SwPropertyEdit> groupEdits = [ .. group ];

			if( string.IsNullOrEmpty( config ) )
			{
				// File-level properties
				if( store.Format == SwStreamStore.ContainerFormat.Modern || store.Contains( "docProps/custom.xml" ) )
				{
					string? existingXml = store.GetText( "docProps/custom.xml" );
					string updatedXml = ApplyEditsToPropertyXml( existingXml, groupEdits, out int count );
					updatedStreams[ "docProps/custom.xml" ] = Encoding.UTF8.GetBytes( updatedXml );
					appliedCount += count;
				}
				else
				{
					byte[]? existingPropSet = store.Get( "\u0005DocumentSummaryInformation" );
					byte[] updatedPropSet = ApplyEditsToOlePropertySet( existingPropSet, groupEdits, out int count );
					updatedStreams[ "\u0005DocumentSummaryInformation" ] = updatedPropSet;
					appliedCount += count;
				}
			}
			else
			{
				// Configuration-level properties
				int configIndex;
				if( configNameToIndex.TryGetValue( config, out int resolvedIdx ) )
				{
					configIndex = resolvedIdx;
				}
				else if( config.StartsWith( "Config-", StringComparison.OrdinalIgnoreCase )
					&& int.TryParse( config[ "Config-".Length.. ], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedIdx ) )
				{
					configIndex = parsedIdx;
				}
				else if( configStreams.Count == 0 && store.Format == SwStreamStore.ContainerFormat.Modern )
				{
					// Modern file with no prior config property stream yet — create Config-0
					configIndex = 0;
				}
				else
				{
					string known = indexToConfigName.Count > 0
						? string.Join( ", ", indexToConfigName.Values )
						: "(none)";
					throw new InvalidOperationException( $"Configuration '{config}' not found in '{Path.GetFileName( filePath )}'. Known configurations: {known}." );
				}

				bool useXml = ( configStreams.TryGetValue( configIndex, out var entry ) && entry.IsXml )
					|| store.Format == SwStreamStore.ContainerFormat.Modern;

				if( useXml )
				{
					string streamName = entry.StreamName ?? $"docProps/Config-{configIndex}-Properties.xml";
					string? existingXml = store.GetText( streamName );
					string updatedXml = ApplyEditsToPropertyXml( existingXml, groupEdits, out int count );
					updatedStreams[ streamName ] = Encoding.UTF8.GetBytes( updatedXml );
					appliedCount += count;
				}
				else
				{
					string streamName = entry.StreamName ?? $"Config-{configIndex}-Properties";
					byte[]? existingPropSet = store.Get( streamName );
					byte[] updatedPropSet = ApplyEditsToOlePropertySet( existingPropSet, groupEdits, out int count );
					updatedStreams[ streamName ] = updatedPropSet;
					appliedCount += count;
				}
			}
		}

		byte[] rewrittenBytes = SwStreamWriter.Rewrite( originalBytes, updatedStreams );
		SwStreamWriter.WriteBytesAtomically( filePath, rewrittenBytes );

		// Re-read to verify exact state on disk
		SwStreamStore verifiedStore = SwStreamStore.FromBytes( rewrittenBytes );
		List<SwFileReader.SwProperty> verifiedProps = SwFileReader.ReadProperties( verifiedStore );

		return new SwPropertyWriteResult(
			FilePath: Path.GetFullPath( filePath ),
			ContainerFormat: store.Format.ToString(),
			Engine: "NativeCrossPlatform",
			AppliedCount: appliedCount,
			ModifiedStreams: [ .. updatedStreams.Keys ],
			CurrentProperties: verifiedProps );
	}

	private static string ApplyEditsToPropertyXml( string? existingXml, IReadOnlyList<SwPropertyEdit> edits, out int applied )
	{
		XDocument doc;
		if( !string.IsNullOrWhiteSpace( existingXml ) )
		{
			try { doc = XDocument.Parse( existingXml, LoadOptions.PreserveWhitespace ); }
			catch { doc = new XDocument( new XElement( "Properties" ) ); }
		}
		else
		{
			doc = new XDocument( new XElement( "Properties" ) );
		}

		XElement root = doc.Root ?? new XElement( "Properties" );
		if( doc.Root is null )
			doc.Add( root );

		XNamespace defaultNs = root.GetDefaultNamespace();
		XElement? section = root.Descendants()
			.FirstOrDefault( e => e.Name.LocalName == "propertySection" && (string?)e.Attribute( "name" ) == "UserDefinedProperties" );
		if( section is null )
		{
			section = new XElement( defaultNs + "propertySection", new XAttribute( "name", "UserDefinedProperties" ) );
			root.Add( section );
		}

		// Detect namespace used by existing value elements (e.g. vt:lpstr or plain lpstr)
		XNamespace propNs = section.Name.Namespace;
		XNamespace valNs = section.Elements()
			.SelectMany( p => p.Elements() )
			.Where( v => v.Name.LocalName != "FPVals" )
			.Select( v => v.Name.Namespace )
			.FirstOrDefault() ?? propNs;

		applied = 0;
		foreach( SwPropertyEdit edit in edits )
		{
			if( string.IsNullOrWhiteSpace( edit.Name ) )
				continue;

			XElement? existingProp = section.Elements()
				.FirstOrDefault( e => e.Name.LocalName == "property"
					&& string.Equals( (string?)e.Attribute( "name" ), edit.Name, StringComparison.OrdinalIgnoreCase ) );

			if( edit.Remove )
			{
				if( existingProp is not null )
				{
					existingProp.Remove();
					applied++;
				}
				continue;
			}

			var (vtName, serializedText) = FormatXmlPropertyValue( edit.Type, edit.Value );
			if( existingProp is null )
			{
				existingProp = new XElement( propNs + "property", new XAttribute( "name", edit.Name ) );
				section.Add( existingProp );
			}
			else
			{
				existingProp.RemoveNodes();
			}

			existingProp.Add( new XElement( valNs + vtName, serializedText ) );
			applied++;
		}

		return doc.ToString( SaveOptions.DisableFormatting );
	}

	private static (string VtLocalName, string Text) FormatXmlPropertyValue( string? declaredType, object? value )
	{
		string kind = NormalizePropertyKind( declaredType, value );
		return kind switch
		{
			"yesno" => ( "bool", ParseBool( value ) ? "true" : "false" ),
			"number" => ( "r8", FormatNumberForXml( value ) ),
			"date" => ( "filetime", ParseUtcDate( value ).ToString( "yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture ) ),
			_ => ( "lpstr", value?.ToString() ?? string.Empty ),
		};
	}

	private static string FormatNumberForXml( object? value ) => value switch
	{
		decimal dec => dec.ToString( CultureInfo.InvariantCulture ),
		int i => i.ToString( CultureInfo.InvariantCulture ),
		long l => l.ToString( CultureInfo.InvariantCulture ),
		double d => d.ToString( "G15", CultureInfo.InvariantCulture ),
		float f => f.ToString( "G7", CultureInfo.InvariantCulture ),
		string s when decimal.TryParse( s, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal dec ) => dec.ToString( CultureInfo.InvariantCulture ),
		_ => Convert.ToDouble( value ?? 0d, CultureInfo.InvariantCulture ).ToString( "G15", CultureInfo.InvariantCulture ),
	};

	private static byte[] ApplyEditsToOlePropertySet( byte[]? existingStream, IReadOnlyList<SwPropertyEdit> edits, out int applied )
	{
		List<(string Name, ushort VarType, object? Value)> props = existingStream is { Length: > 0 }
			? OlePropertySet.ReadUserDefined( existingStream )
			: [];

		applied = 0;
		foreach( SwPropertyEdit edit in edits )
		{
			if( string.IsNullOrWhiteSpace( edit.Name ) )
				continue;

			int idx = props.FindIndex( p => string.Equals( p.Name, edit.Name, StringComparison.OrdinalIgnoreCase ) );
			if( edit.Remove )
			{
				if( idx >= 0 )
				{
					props.RemoveAt( idx );
					applied++;
				}
				continue;
			}

			string kind = NormalizePropertyKind( edit.Type, edit.Value );
			(ushort vt, object? typedVal) = kind switch
			{
				"yesno" => ( OlePropertySet.VarType.Bool, (object)ParseBool( edit.Value ) ),
				"number" => ( OlePropertySet.VarType.R8, (object)Convert.ToDouble( edit.Value ?? 0d, CultureInfo.InvariantCulture ) ),
				"date" => ( OlePropertySet.VarType.FileTime, (object)ParseUtcDate( edit.Value ) ),
				_ => ( OlePropertySet.VarType.LpStr, (object)( edit.Value?.ToString() ?? string.Empty ) ),
			};

			if( idx >= 0 )
				props[ idx ] = ( props[ idx ].Name, vt, typedVal );
			else
				props.Add( ( edit.Name, vt, typedVal ) );
			applied++;
		}

		return OlePropertySet.WriteUserDefined( existingStream, props );
	}

	private static string NormalizePropertyKind( string? declaredType, object? value )
	{
		if( !string.IsNullOrWhiteSpace( declaredType ) )
		{
			return declaredType.Trim().ToLowerInvariant() switch
			{
				"bool" or "boolean" or "yesno" or "yes/no" => "yesno",
				"num" or "number" or "int" or "integer" or "float" or "double" or "decimal" => "number",
				"date" or "datetime" or "filetime" => "date",
				_ => "text",
			};
		}

		return value switch
		{
			bool => "yesno",
			int or long or short or float or double or decimal => "number",
			DateTime or DateTimeOffset => "date",
			_ => "text",
		};
	}

	private static bool ParseBool( object? value ) => value switch
	{
		bool b => b,
		string s => s.Trim().ToLowerInvariant() is "yes" or "true" or "1" or "-1",
		_ => Convert.ToBoolean( value ?? false, CultureInfo.InvariantCulture ),
	};

	private static DateTime ParseUtcDate( object? value ) => value switch
	{
		DateTime dt => dt.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind( dt, DateTimeKind.Utc ) : dt.ToUniversalTime(),
		DateTimeOffset dto => dto.UtcDateTime,
		string s when DateTime.TryParse( s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime parsed ) => parsed,
		_ => DateTime.UtcNow,
	};

	// =========================================================================
	// Dependency Path Rewriting
	// =========================================================================

	/// <summary>
	/// Rewrites stored dependency paths inside a SolidWorks document (<c>.SLDPRT</c>, <c>.SLDASM</c>, <c>.SLDDRW</c>)
	/// across <c>SwDocContentMgr/SwDocContentMgrInfo</c>, <c>swXmlContents/COMPINSTANCETREE</c>, <c>Header2</c>,
	/// and OLE <c>Contents/Config-*</c> streams.
	/// </summary>
	public static SwDependencyWriteResult RewriteDependencies(
		string filePath,
		IReadOnlyList<SwDependencyReplacement>? replacements = null,
		string? oldPrefix = null,
		string? newPrefix = null,
		string? remapToDirectory = null,
		bool dryRun = false )
	{
		if( string.IsNullOrWhiteSpace( filePath ) )
			throw new ArgumentException( "filePath is required.", nameof( filePath ) );
		if( !SwDocumentTypes.IsSolidWorks( filePath ) )
			throw new ArgumentException( $"File '{filePath}' is not a SolidWorks document (.sldprt, .sldasm, .slddrw).", nameof( filePath ) );
		if( !File.Exists( filePath ) )
			throw new FileNotFoundException( $"SolidWorks file not found: '{filePath}'.", filePath );

		byte[] originalBytes = File.ReadAllBytes( filePath );
		SwStreamStore store = SwStreamStore.FromBytes( originalBytes );
		if( store.Format == SwStreamStore.ContainerFormat.Unknown )
			throw new InvalidDataException( $"Unrecognised SolidWorks container format: '{filePath}'." );

		string ownName = Path.GetFileName( filePath );
		List<string> currentDeps = SwFileReader.GetReferences( store, ownName );

		// Build concrete oldStoredPath -> newStoredPath map
		Dictionary<string, string> exactMap = new( StringComparer.OrdinalIgnoreCase );
		List<SwDependencyRewriteEntry> plan = [];

		string? normOldPrefix = string.IsNullOrWhiteSpace( oldPrefix ) ? null : NormalizeSolidWorksPath( oldPrefix ).TrimEnd( '\\' );
		string? normNewPrefix = newPrefix is null ? null : NormalizeSolidWorksPath( newPrefix ).TrimEnd( '\\' );
		string? normRemapDir = string.IsNullOrWhiteSpace( remapToDirectory ) ? null : NormalizeSolidWorksPath( remapToDirectory ).TrimEnd( '\\' );

		foreach( string stored in currentDeps )
		{
			string fileName = FileNameOf( stored );
			string? target = ResolveTargetPath( stored, fileName, replacements, normOldPrefix, normNewPrefix, normRemapDir );
			bool changed = target is not null && !string.Equals( stored, target, StringComparison.Ordinal );
			string finalTarget = changed ? target! : stored;
			plan.Add( new SwDependencyRewriteEntry( fileName, stored, finalTarget, changed ) );
			if( changed )
				exactMap[ stored ] = finalTarget;
		}

		// Also allow explicit replacement rules that match additional paths present in COMPINSTANCETREE
		// (e.g. grandchildren) even if not in direct GetReferences
		if( replacements is not null )
		{
			foreach( SwDependencyReplacement r in replacements )
			{
				if( string.IsNullOrWhiteSpace( r.OldPath ) || string.IsNullOrWhiteSpace( r.NewPath ) )
					continue;
				if( r.OldPath.Contains( '\\' ) || r.OldPath.Contains( '/' ) )
				{
					string normOld = NormalizeSolidWorksPath( r.OldPath );
					string normNew = NormalizeSolidWorksPath( r.NewPath );
					if( !string.Equals( normOld, normNew, StringComparison.Ordinal ) )
						exactMap.TryAdd( normOld, normNew );
				}
			}
		}

		if( dryRun || exactMap.Count == 0 )
		{
			return new SwDependencyWriteResult(
				FilePath: Path.GetFullPath( filePath ),
				ContainerFormat: store.Format.ToString(),
				Engine: "NativeCrossPlatform",
				DryRun: dryRun,
				RewrittenCount: plan.Count( p => p.Changed ),
				Rewrites: plan,
				ModifiedStreams: [],
				VerifiedDependencies: currentDeps );
		}

		Dictionary<string, byte[]> updatedStreams = new( StringComparer.Ordinal );

		// 1. swXmlContents/COMPINSTANCETREE (XML swFile[@swPath])
		if( store.GetText( "swXmlContents/COMPINSTANCETREE" ) is { } treeXml )
		{
			string newTreeXml = RewriteCompInstanceTreeXml( treeXml, exactMap, replacements, normOldPrefix, normNewPrefix, normRemapDir, out bool changed );
			if( changed )
				updatedStreams[ "swXmlContents/COMPINSTANCETREE" ] = Encoding.UTF8.GetBytes( newTreeXml );
		}

		// 2. SwDocContentMgr/SwDocContentMgrInfo (2015+ primary reference stream)
		if( store.Get( "SwDocContentMgr/SwDocContentMgrInfo" ) is { } contentMgr )
		{
			byte[] newBytes = RewriteBinaryUtf16Paths( contentMgr, exactMap, out bool changed );
			if( changed )
				updatedStreams[ "SwDocContentMgr/SwDocContentMgrInfo" ] = newBytes;
		}

		// 3. Header2 (OLE primary reference stream, also present in some modern files)
		if( store.Get( "Header2" ) is { } header2 )
		{
			byte[] newBytes = RewriteBinaryUtf16Paths( header2, exactMap, out bool changed );
			if( changed )
				updatedStreams[ "Header2" ] = newBytes;
		}

		// 4. OLE Contents/Config-* streams (component archive entries)
		foreach( string name in store.Names )
		{
			if( !ContentsConfigRegex().IsMatch( name ) )
				continue;
			if( store.Get( name ) is { } cfgBytes )
			{
				byte[] newBytes = RewriteBinaryUtf16Paths( cfgBytes, exactMap, out bool changed );
				if( changed )
					updatedStreams[ name ] = newBytes;
			}
		}

		if( updatedStreams.Count > 0 )
		{
			byte[] rewrittenBytes = SwStreamWriter.Rewrite( originalBytes, updatedStreams );
			SwStreamWriter.WriteBytesAtomically( filePath, rewrittenBytes );
			originalBytes = rewrittenBytes;
		}

		SwStreamStore verifiedStore = SwStreamStore.FromBytes( originalBytes );
		List<string> verifiedDeps = SwFileReader.GetReferences( verifiedStore, ownName );

		return new SwDependencyWriteResult(
			FilePath: Path.GetFullPath( filePath ),
			ContainerFormat: store.Format.ToString(),
			Engine: "NativeCrossPlatform",
			DryRun: false,
			RewrittenCount: plan.Count( p => p.Changed ),
			Rewrites: plan,
			ModifiedStreams: [ .. updatedStreams.Keys ],
			VerifiedDependencies: verifiedDeps );
	}

	private static string? ResolveTargetPath(
		string storedPath,
		string fileName,
		IReadOnlyList<SwDependencyReplacement>? replacements,
		string? normOldPrefix,
		string? normNewPrefix,
		string? normRemapDir )
	{
		string storedNorm = NormalizeSolidWorksPath( storedPath );

		if( replacements is not null )
		{
			// Exact full-path match first
			foreach( SwDependencyReplacement r in replacements )
			{
				if( string.IsNullOrWhiteSpace( r.OldPath ) || string.IsNullOrWhiteSpace( r.NewPath ) )
					continue;
				if( string.Equals( storedNorm, NormalizeSolidWorksPath( r.OldPath ), StringComparison.OrdinalIgnoreCase ) )
					return NormalizeSolidWorksPath( r.NewPath );
			}
			// Filename-only match second (e.g. OldPath = "Part1.SLDPRT")
			foreach( SwDependencyReplacement r in replacements )
			{
				if( string.IsNullOrWhiteSpace( r.OldPath ) || string.IsNullOrWhiteSpace( r.NewPath ) )
					continue;
				if( !r.OldPath.Contains( '\\' ) && !r.OldPath.Contains( '/' )
					&& string.Equals( fileName, r.OldPath.Trim(), StringComparison.OrdinalIgnoreCase ) )
				{
					return NormalizeSolidWorksPath( r.NewPath );
				}
			}
		}

		if( normOldPrefix is not null && normNewPrefix is not null )
		{
			if( storedNorm.StartsWith( normOldPrefix + "\\", StringComparison.OrdinalIgnoreCase ) )
				return normNewPrefix + storedNorm[ normOldPrefix.Length.. ];
			if( string.Equals( storedNorm, normOldPrefix, StringComparison.OrdinalIgnoreCase ) )
				return normNewPrefix;
		}

		if( normRemapDir is not null )
			return $"{normRemapDir}\\{fileName}";

		return null;
	}

	private static string RewriteCompInstanceTreeXml(
		string xml,
		Dictionary<string, string> exactMap,
		IReadOnlyList<SwDependencyReplacement>? replacements,
		string? normOldPrefix,
		string? normNewPrefix,
		string? normRemapDir,
		out bool changed )
	{
		changed = false;
		XDocument doc;
		try { doc = XDocument.Parse( xml, LoadOptions.PreserveWhitespace ); }
		catch { return xml; }

		foreach( XElement fileEl in doc.Descendants().Where( e => e.Name.LocalName == "swFile" ) )
		{
			XAttribute? attr = fileEl.Attribute( "swPath" );
			if( attr is null || string.IsNullOrWhiteSpace( attr.Value ) )
				continue;

			string current = attr.Value;
			if( exactMap.TryGetValue( current, out string? mapped )
				|| exactMap.TryGetValue( NormalizeSolidWorksPath( current ), out mapped ) )
			{
				if( !string.Equals( current, mapped, StringComparison.Ordinal ) )
				{
					attr.Value = mapped;
					changed = true;
				}
				continue;
			}

			// Also apply prefix/filename rules to transitive swFile nodes inside COMPINSTANCETREE
			string? computed = ResolveTargetPath( current, FileNameOf( current ), replacements, normOldPrefix, normNewPrefix, normRemapDir );
			if( computed is not null && !string.Equals( current, computed, StringComparison.Ordinal ) )
			{
				// Avoid rewriting the root assembly's bare self-name (no directory separator) when using remapToDirectory
				if( normRemapDir is not null && !current.Contains( '\\' ) && !current.Contains( '/' ) && replacements is null )
					continue;
				attr.Value = computed;
				changed = true;
			}
		}

		if( !changed )
			return xml;

		// Preserve XML declaration if present
		using StringWriter sw = new();
		if( doc.Declaration is not null )
			doc.Save( sw, SaveOptions.DisableFormatting );
		else
			sw.Write( doc.ToString( SaveOptions.DisableFormatting ) );
		return sw.ToString();
	}

	/// <summary>
	/// Replaces UTF-16LE path occurrences in a SolidWorks binary stream (<c>Header2</c>,
	/// <c>SwDocContentMgrInfo</c>, <c>Contents/Config-*</c>).
	/// Properly updates MFC <c>CArchive</c> Unicode length prefixes (<c>FF FE FF &lt;len&gt;</c>)
	/// as well as 32-bit length-prefixed or null-terminated UTF-16LE strings.
	/// </summary>
	internal static byte[] RewriteBinaryUtf16Paths(
		byte[] stream,
		IReadOnlyDictionary<string, string> exactMap,
		out bool changed )
	{
		changed = false;
		if( exactMap.Count == 0 || stream.Length == 0 )
			return stream;

		// Sort longest-first to avoid partial substring matches
		var ordered = exactMap
			.Where( kv => kv.Key.Length > 0 && !string.Equals( kv.Key, kv.Value, StringComparison.Ordinal ) )
			.OrderByDescending( kv => kv.Key.Length )
			.ToList();

		byte[] current = stream;
		foreach( var (oldPath, newPath) in ordered )
		{
			byte[] next = ReplaceSingleUtf16Path( current, oldPath, newPath, out bool replaced );
			if( replaced )
			{
				current = next;
				changed = true;
			}
		}
		return current;
	}

	private static byte[] ReplaceSingleUtf16Path( byte[] data, string oldPath, string newPath, out bool replaced )
	{
		replaced = false;
		byte[] oldUtf16 = Encoding.Unicode.GetBytes( oldPath );
		byte[] newUtf16 = Encoding.Unicode.GetBytes( newPath );
		if( oldUtf16.Length == 0 )
			return data;

		// Fast check (case-insensitive UTF-16LE ASCII search)
		List<int> matchOffsets = FindUtf16CaseInsensitive( data, oldPath );
		if( matchOffsets.Count == 0 )
			return data;

		using MemoryStream output = new( data.Length + Math.Max( 0, newUtf16.Length - oldUtf16.Length ) * matchOffsets.Count );
		int cursor = 0;
		Span<byte> lenBuf = stackalloc byte[ 4 ];

		foreach( int pos in matchOffsets )
		{
			if( pos < cursor )
				continue;

			// Case 1: MFC CArchive Unicode string prefix: FF FE FF <len>
			//   Short (<255 chars): [pos-4]=FF, [pos-3]=FE, [pos-2]=FF, [pos-1]=(byte)oldPath.Length
			//   Medium (255..65534): [pos-6]=FF, [pos-5]=FE, [pos-4]=FF, [pos-3]=FF, [pos-2..pos]=ushort(oldPath.Length)
			if( TryMatchMfcUnicodePrefix( data, pos, oldPath.Length, out int mfcPrefixStart ) && mfcPrefixStart >= cursor )
			{
				output.Write( data, cursor, mfcPrefixStart - cursor );
				WriteMfcUnicodePrefix( output, newPath.Length );
				output.Write( newUtf16 );
				cursor = pos + oldUtf16.Length;
				replaced = true;
				continue;
			}

			// Case 2: 4-byte little-endian char-count prefix immediately preceding UTF-16LE (common in SwDocContentMgrInfo)
			if( pos >= cursor + 4 )
			{
				uint preU32 = BinaryPrimitives.ReadUInt32LittleEndian( data.AsSpan( pos - 4 ) );
				if( preU32 == (uint)oldPath.Length || preU32 == (uint)( oldPath.Length + 1 ) )
				{
					bool hasNull = preU32 == (uint)( oldPath.Length + 1 )
						&& pos + oldUtf16.Length + 2 <= data.Length
						&& data[ pos + oldUtf16.Length ] == 0
						&& data[ pos + oldUtf16.Length + 1 ] == 0;
					if( preU32 == (uint)oldPath.Length || hasNull )
					{
						output.Write( data, cursor, ( pos - 4 ) - cursor );
						BinaryPrimitives.WriteUInt32LittleEndian( lenBuf, (uint)( newPath.Length + ( hasNull ? 1 : 0 ) ) );
						output.Write( lenBuf );
						output.Write( newUtf16 );
						cursor = pos + oldUtf16.Length;
						replaced = true;
						continue;
					}
				}
			}

			// Case 3: Plain / null-terminated UTF-16LE string (e.g. SwDocContentMgrInfo plain buffer)
			output.Write( data, cursor, pos - cursor );
			output.Write( newUtf16 );
			cursor = pos + oldUtf16.Length;
			replaced = true;
		}

		if( cursor < data.Length )
			output.Write( data, cursor, data.Length - cursor );

		return replaced ? output.ToArray() : data;
	}

	private static bool TryMatchMfcUnicodePrefix( byte[] data, int strStart, int charLen, out int prefixStart )
	{
		prefixStart = -1;
		if( charLen < 0xFF && strStart >= 4 )
		{
			if( data[ strStart - 4 ] == 0xFF
				&& data[ strStart - 3 ] == 0xFE
				&& data[ strStart - 2 ] == 0xFF
				&& data[ strStart - 1 ] == (byte)charLen )
			{
				prefixStart = strStart - 4;
				return true;
			}
		}
		if( charLen is >= 0xFF and < 0xFFFE && strStart >= 6 )
		{
			if( data[ strStart - 6 ] == 0xFF
				&& data[ strStart - 5 ] == 0xFE
				&& data[ strStart - 4 ] == 0xFF
				&& data[ strStart - 3 ] == 0xFF
				&& BinaryPrimitives.ReadUInt16LittleEndian( data.AsSpan( strStart - 2 ) ) == (ushort)charLen )
			{
				prefixStart = strStart - 6;
				return true;
			}
		}
		return false;
	}

	private static void WriteMfcUnicodePrefix( MemoryStream output, int charLen )
	{
		output.WriteByte( 0xFF );
		output.WriteByte( 0xFE );
		output.WriteByte( 0xFF );
		if( charLen < 0xFF )
		{
			output.WriteByte( (byte)charLen );
		}
		else if( charLen < 0xFFFE )
		{
			output.WriteByte( 0xFF );
			Span<byte> buf = stackalloc byte[ 2 ];
			BinaryPrimitives.WriteUInt16LittleEndian( buf, (ushort)charLen );
			output.Write( buf );
		}
		else
		{
			output.WriteByte( 0xFF );
			output.WriteByte( 0xFF );
			output.WriteByte( 0xFF );
			Span<byte> buf = stackalloc byte[ 4 ];
			BinaryPrimitives.WriteUInt32LittleEndian( buf, (uint)charLen );
			output.Write( buf );
		}
	}

	private static List<int> FindUtf16CaseInsensitive( byte[] data, string needle )
	{
		List<int> hits = [];
		int needleByteLen = needle.Length * 2;
		if( needleByteLen == 0 || data.Length < needleByteLen )
			return hits;

		char[] lower = needle.ToLowerInvariant().ToCharArray();
		char[] upper = needle.ToUpperInvariant().ToCharArray();
		int limit = data.Length - needleByteLen;

		for( int i = 0; i <= limit; i++ )
		{
			char c0 = (char)( data[ i ] | ( data[ i + 1 ] << 8 ) );
			if( c0 != lower[ 0 ] && c0 != upper[ 0 ] )
				continue;

			bool ok = true;
			for( int k = 1; k < lower.Length; k++ )
			{
				int off = i + k * 2;
				char ck = (char)( data[ off ] | ( data[ off + 1 ] << 8 ) );
				if( ck != lower[ k ] && ck != upper[ k ] )
				{
					ok = false;
					break;
				}
			}

			if( ok )
			{
				hits.Add( i );
				i += needleByteLen - 1;
			}
		}
		return hits;
	}

	private static string FileNameOf( string storedPath )
		=> storedPath[ ( storedPath.LastIndexOfAny( [ '\\', '/' ] ) + 1 ).. ];
}

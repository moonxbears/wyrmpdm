using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

using HackPDM.Core.Hack;

namespace HackPDM.Core.Graph;

/// <summary>
/// Declarative view, traversal depth, per-node offspring/parent branch limits, and filtering rules
/// applied to any HackPDM node graph (both in the live GUI and through the MCP server).
/// </summary>
public sealed record GraphFilterCriteria
{
	public static GraphFilterCriteria Default { get; } = new();

	/// <summary>How the graph is constructed (Dependencies, FolderStructure, FoldersAndFiles, ByProperty, SearchFilter).</summary>
	public GraphViewMode ViewMode { get; init; } = GraphViewMode.Dependencies;

	/// <summary>Dependency traversal direction (Children, Parents, Both, FullNetwork).</summary>
	public GraphDirection Direction { get; init; } = GraphDirection.Children;

	/// <summary>Overall maximum hop/level depth from root nodes (0 = unlimited).</summary>
	public int MaxDepth { get; init; }

	/// <summary>
	/// Optional maximum depth for offspring (child) branches (null = inherit <see cref="MaxDepth"/>; 0 = none).
	/// </summary>
	public int? MaxChildDepth { get; init; }

	/// <summary>
	/// Optional maximum depth for parent (where-used) branches (null = inherit <see cref="MaxDepth"/>; 0 = none).
	/// </summary>
	public int? MaxParentDepth { get; init; }

	/// <summary>
	/// Maximum offspring/child branches expanded per node (null = unlimited; 0 = disallow child branches; N &gt; 0 = cap at N).
	/// </summary>
	public int? MaxChildBranches { get; init; }

	/// <summary>
	/// Maximum parent branches expanded per node (null = unlimited; 0 = disallow parent branches; N &gt; 0 = cap at N).
	/// </summary>
	public int? MaxParentBranches { get; init; }

	/// <summary>Maximum total nodes returned in the graph (0 = unlimited; default 250).</summary>
	public int MaxNodes { get; init; } = 250;

	/// <summary>Whitelist of file extensions or aliases (e.g. "sldprt", "sldasm", "slddrw", "cad", "part").</summary>
	public IReadOnlyList<string>? Extensions { get; init; }

	/// <summary>Blacklist of file extensions or aliases to exclude.</summary>
	public IReadOnlyList<string>? ExcludeExtensions { get; init; }

	/// <summary>Case-insensitive substring or wildcard (*, ?) match on node name.</summary>
	public string? NameFilter { get; init; }

	/// <summary>Case-insensitive substring or wildcard (*, ?) match on node path.</summary>
	public string? PathFilter { get; init; }

	/// <summary>Whitelist of file categories (case-insensitive).</summary>
	public IReadOnlyList<string>? Categories { get; init; }

	/// <summary>Filter by checkout state (true = checked out only, false = not checked out only, null = any).</summary>
	public bool? CheckedOutOnly { get; init; }

	/// <summary>Filter by checkout user substring (case-insensitive).</summary>
	public string? CheckoutUser { get; init; }

	/// <summary>Whether logically deleted entries are kept (default true).</summary>
	public bool IncludeDeleted { get; init; } = true;

	/// <summary>Optional minimum file size in bytes.</summary>
	public long? MinSizeBytes { get; init; }

	/// <summary>Optional maximum file size in bytes.</summary>
	public long? MaxSizeBytes { get; init; }

	/// <summary>Optional custom property or built-in attribute name to filter by (e.g. "Material", "Revision", "Category").</summary>
	public string? PropertyName { get; init; }

	/// <summary>Operator for <see cref="PropertyName"/> ("contains", "=", "!=", "starts_with", "ends_with", "exists", "not_exists", "&gt;", "&gt;=", "&lt;", "&lt;=").</summary>
	public string? PropertyOperator { get; init; }

	/// <summary>Target comparison value for <see cref="PropertyName"/>.</summary>
	public string? PropertyValue { get; init; }

	/// <summary>Property or attribute name used to cluster nodes in <see cref="GraphViewMode.ByProperty"/> mode.</summary>
	public string? GroupByProperty { get; init; }

	/// <summary>When true in Folder/Property/Search modes, also overlays file-to-file dependency links between included files.</summary>
	public bool IncludeDependencyLinks { get; init; }

	/// <summary>Keep selected root nodes even when attribute filters do not match them (default true).</summary>
	public bool KeepRoots { get; init; } = true;

	/// <summary>
	/// When attribute filters hide intermediate nodes (e.g. filtering to SLDPRT only hides a sub-assembly),
	/// transitively bridges remaining ancestors to remaining descendants so dependency lineage stays visible (default true).
	/// </summary>
	public bool BridgeFilteredEdges { get; init; } = true;

	/// <summary>True if any attribute filter (extension, name, path, category, checkout, deleted, size, or property) is active.</summary>
	public bool HasAttributeFilters =>
		( Extensions is { Count: > 0 } )
		|| ( ExcludeExtensions is { Count: > 0 } )
		|| !string.IsNullOrWhiteSpace( NameFilter )
		|| !string.IsNullOrWhiteSpace( PathFilter )
		|| ( Categories is { Count: > 0 } )
		|| CheckedOutOnly.HasValue
		|| !string.IsNullOrWhiteSpace( CheckoutUser )
		|| !IncludeDeleted
		|| MinSizeBytes.HasValue
		|| MaxSizeBytes.HasValue
		|| !string.IsNullOrWhiteSpace( PropertyName );

	/// <summary>True if any depth, per-node branch count, or node cap is active.</summary>
	public bool HasTraversalLimits =>
		MaxDepth > 0
		|| MaxChildDepth.HasValue
		|| MaxParentDepth.HasValue
		|| MaxChildBranches.HasValue
		|| MaxParentBranches.HasValue
		|| MaxNodes > 0;

	/// <summary>True if custom Odoo <c>hp.version.property</c> rows should be loaded for this query.</summary>
	public bool NeedsCustomProperties =>
		( !string.IsNullOrWhiteSpace( PropertyName ) && !GraphFilterEngine.IsBuiltInField( PropertyName ) )
		|| ( ViewMode == GraphViewMode.ByProperty
			&& !string.IsNullOrWhiteSpace( GroupByProperty )
			&& !GraphFilterEngine.IsBuiltInField( GroupByProperty ) );
}

/// <summary>Telemetry explaining how depth limits, branch caps, and filters reshaped a graph.</summary>
public sealed record GraphFilterStats(
	int TotalNodesBeforeFilter,
	int TotalEdgesBeforeFilter,
	int FilteredNodes,
	int FilteredEdges,
	int BridgedEdgesAdded,
	int NodesTrimmedByLimits,
	int EdgesTrimmedByChildBranchLimit,
	int EdgesTrimmedByParentBranchLimit,
	bool TruncatedByLimits )
{
	public static GraphFilterStats Empty { get; } = new( 0, 0, 0, 0, 0, 0, 0, 0, false );
}

/// <summary>A filtered/constructed <see cref="DependencyGraph"/> paired with its <see cref="GraphFilterStats"/>.</summary>
public sealed record GraphFilterResult( DependencyGraph Graph, GraphFilterStats Stats )
{
	public static GraphFilterResult Empty { get; } = new( DependencyGraph.Empty, GraphFilterStats.Empty );
}

/// <summary>
/// Pure, cross-platform graph shaping engine: depth trimming, separate offspring/parent branch limiting,
/// multi-criteria attribute &amp; property filtering with transitive edge bridging, graph merging, and
/// builders for folder-structure, folders+files, and property-grouped node graphs.
/// </summary>
public static class GraphFilterEngine
{
	private static readonly char[] ListSeparators = [ ',', ';', ' ', '\t', '\r', '\n' ];
	private static readonly char[] PathSeparators = [ '\\', '/' ];

	/// <summary>Returns true when <paramref name="fieldName"/> resolves from built-in <see cref="GraphNode"/> metadata without querying custom properties.</summary>
	public static bool IsBuiltInField( string? fieldName )
	{
		if( string.IsNullOrWhiteSpace( fieldName ) )
			return true;
		return fieldName.Trim().ToLowerInvariant() switch
		{
			"ext" or "extension" or "filetype" or "file_type" or "type" or "typename" or "type_name"
				or "category" or "cat"
				or "checkout" or "checkoutuser" or "checkout_user" or "checkedout" or "checked_out"
				or "folder" or "directory" or "path"
				or "status" or "state" or "deleted"
				or "name" or "filename"
				or "size" or "versions" or "modified" => true,
			_ => false,
		};
	}

	/// <summary>Parses a comma/space/semicolon-separated extension string into normalized extensions.</summary>
	public static IReadOnlyList<string> ParseExtensionList( string? raw )
	{
		if( string.IsNullOrWhiteSpace( raw ) )
			return [];
		return NormalizeExtensions( raw.Split( ListSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries ) );
	}

	/// <summary>Normalizes extension names (strips dots, lowercases, expands aliases like 'part', 'assembly', 'drawing', 'cad').</summary>
	public static IReadOnlyList<string> NormalizeExtensions( IEnumerable<string>? raw )
	{
		if( raw is null )
			return [];
		HashSet<string> result = new( StringComparer.OrdinalIgnoreCase );
		foreach( string item in raw )
		{
			if( string.IsNullOrWhiteSpace( item ) )
				continue;
			foreach( string piece in item.Split( ListSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries ) )
			{
				string clean = piece.Trim().TrimStart( '*', '.' ).ToLowerInvariant();
				if( clean.Length == 0 )
					continue;
				switch( clean )
				{
					case "part" or "parts" or "swpart":
						result.Add( "sldprt" );
						break;
					case "assembly" or "assemblies" or "swasm":
						result.Add( "sldasm" );
						break;
					case "drawing" or "drawings" or "swdrw":
						result.Add( "slddrw" );
						break;
					case "cad" or "solidworks" or "sw":
						result.Add( "sldprt" );
						result.Add( "sldasm" );
						result.Add( "slddrw" );
						break;
					default:
						result.Add( clean );
						break;
				}
			}
		}
		return [ .. result.Order( StringComparer.OrdinalIgnoreCase ) ];
	}

	/// <summary>Matches <paramref name="text"/> against a case-insensitive substring or wildcard (<c>*</c>, <c>?</c>) pattern.</summary>
	public static bool MatchesWildcardOrSubstring( string? text, string? pattern )
	{
		if( string.IsNullOrWhiteSpace( pattern ) )
			return true;
		string hay = text ?? "";
		string needle = pattern.Trim();
		if( needle.Contains( '*' ) || needle.Contains( '?' ) )
		{
			string regex = "^" + Regex.Escape( needle ).Replace( "\\*", ".*" ).Replace( "\\?", "." ) + "$";
			return Regex.IsMatch( hay, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant );
		}
		return hay.Contains( needle, StringComparison.OrdinalIgnoreCase );
	}

	/// <summary>Resolves a built-in attribute or custom property value from <paramref name="node"/>.</summary>
	public static string? ResolveFieldValue( GraphNode node, string fieldName )
	{
		string key = fieldName.Trim();
		if( key.Length == 0 )
			return null;

		switch( key.ToLowerInvariant() )
		{
			case "ext" or "extension" or "filetype" or "file_type":
			{
				string ext = EffectiveExtension( node );
				return ext.Length > 0 ? ext.ToUpperInvariant() : "(None)";
			}
			case "type" or "typename" or "type_name":
				return node.TypeName.Length > 0 ? node.TypeName : ( node.Ext.Length > 0 ? node.Ext.ToUpperInvariant() : "(Unknown)" );
			case "category" or "cat":
				return node.Category.Length > 0 ? node.Category : null;
			case "checkout" or "checkoutuser" or "checkout_user":
				return node.CheckedOut ? ( node.CheckoutUser.Length > 0 ? node.CheckoutUser : "Checked Out" ) : "Available";
			case "checkedout" or "checked_out":
				return node.CheckedOut ? "true" : "false";
			case "folder" or "directory":
				return ExtractFolder( node.Path );
			case "path":
				return node.Path;
			case "status" or "state":
				return node.Deleted ? "Deleted" : node.CheckedOut ? "Checked Out" : "Active";
			case "deleted":
				return node.Deleted ? "true" : "false";
			case "name" or "filename":
				return node.Name;
			case "size":
				return node.Size.ToString( CultureInfo.InvariantCulture );
			case "versions":
				return node.Versions.ToString( CultureInfo.InvariantCulture );
			case "modified":
				return node.Modified.Length > 0 ? node.Modified : null;
		}

		if( node.Properties is not null )
		{
			if( node.Properties.TryGetValue( key, out string? direct ) )
				return direct;
			foreach( var kv in node.Properties )
			{
				if( string.Equals( kv.Key, key, StringComparison.OrdinalIgnoreCase ) )
					return kv.Value;
			}
		}
		return null;
	}

	/// <summary>Evaluates whether <paramref name="node"/> satisfies a property/attribute predicate.</summary>
	public static bool MatchesProperty( GraphNode node, string propertyName, string? op, string? expectedValue )
	{
		string? actual = ResolveFieldValue( node, propertyName );
		string normalizedOp = string.IsNullOrWhiteSpace( op ) ? "contains" : op.Trim().ToLowerInvariant();
		string expected = expectedValue?.Trim() ?? "";

		if( normalizedOp is "exists" or "has" or "not_empty" )
			return !string.IsNullOrWhiteSpace( actual );
		if( normalizedOp is "not_exists" or "missing" or "empty" )
			return string.IsNullOrWhiteSpace( actual );

		if( string.IsNullOrEmpty( expected ) )
			return !string.IsNullOrWhiteSpace( actual );
		if( actual is null )
			return normalizedOp is "!=" or "not_equals" or "neq" or "not_contains";

		switch( normalizedOp )
		{
			case "=" or "==" or "eq" or "equals":
				return string.Equals( actual.Trim(), expected, StringComparison.OrdinalIgnoreCase );
			case "!=" or "<>" or "neq" or "not_equals":
				return !string.Equals( actual.Trim(), expected, StringComparison.OrdinalIgnoreCase );
			case "starts_with" or "startswith":
				return actual.StartsWith( expected, StringComparison.OrdinalIgnoreCase );
			case "ends_with" or "endswith":
				return actual.EndsWith( expected, StringComparison.OrdinalIgnoreCase );
			case "not_contains":
				return !MatchesWildcardOrSubstring( actual, expected );
			case ">" or "gt":
				return TryCompareNumbers( actual, expected, out int cmpGt ) && cmpGt > 0;
			case ">=" or "gte":
				return TryCompareNumbers( actual, expected, out int cmpGte ) && cmpGte >= 0;
			case "<" or "lt":
				return TryCompareNumbers( actual, expected, out int cmpLt ) && cmpLt < 0;
			case "<=" or "lte":
				return TryCompareNumbers( actual, expected, out int cmpLte ) && cmpLte <= 0;
			default:
				return MatchesWildcardOrSubstring( actual, expected );
		}
	}

	/// <summary>Tests whether a file node matches all attribute/property criteria in <paramref name="criteria"/>.</summary>
	public static bool MatchesNodeAttributes( GraphNode node, GraphFilterCriteria criteria )
	{
		if( node.Role != GraphNodeRole.File )
		{
			if( !string.IsNullOrWhiteSpace( criteria.NameFilter ) && !MatchesWildcardOrSubstring( node.Name, criteria.NameFilter ) )
				return false;
			if( !string.IsNullOrWhiteSpace( criteria.PathFilter ) && !MatchesWildcardOrSubstring( node.Path, criteria.PathFilter ) )
				return false;
			return true;
		}

		if( !criteria.IncludeDeleted && node.Deleted )
			return false;

		IReadOnlyList<string> includeExt = NormalizeExtensions( criteria.Extensions );
		if( includeExt.Count > 0 )
		{
			string nodeExt = EffectiveExtension( node );
			if( !includeExt.Contains( nodeExt, StringComparer.OrdinalIgnoreCase ) )
				return false;
		}

		IReadOnlyList<string> excludeExt = NormalizeExtensions( criteria.ExcludeExtensions );
		if( excludeExt.Count > 0 )
		{
			string nodeExt = EffectiveExtension( node );
			if( excludeExt.Contains( nodeExt, StringComparer.OrdinalIgnoreCase ) )
				return false;
		}

		if( !string.IsNullOrWhiteSpace( criteria.NameFilter ) && !MatchesWildcardOrSubstring( node.Name, criteria.NameFilter ) )
			return false;

		if( !string.IsNullOrWhiteSpace( criteria.PathFilter ) && !MatchesWildcardOrSubstring( node.Path, criteria.PathFilter ) )
			return false;

		if( criteria.Categories is { Count: > 0 } cats )
		{
			if( !cats.Any( c => !string.IsNullOrWhiteSpace( c ) && string.Equals( node.Category, c.Trim(), StringComparison.OrdinalIgnoreCase ) ) )
				return false;
		}

		if( criteria.CheckedOutOnly is bool co && node.CheckedOut != co )
			return false;

		if( !string.IsNullOrWhiteSpace( criteria.CheckoutUser ) )
		{
			if( !node.CheckedOut || !MatchesWildcardOrSubstring( node.CheckoutUser, criteria.CheckoutUser ) )
				return false;
		}

		if( criteria.MinSizeBytes is long minSz && node.Size < minSz )
			return false;

		if( criteria.MaxSizeBytes is long maxSz && node.Size > maxSz )
			return false;

		if( !string.IsNullOrWhiteSpace( criteria.PropertyName ) )
		{
			if( !MatchesProperty( node, criteria.PropertyName, criteria.PropertyOperator, criteria.PropertyValue ) )
				return false;
		}

		return true;
	}

	/// <summary>Merges two graphs (union of nodes and edges), preferring richer metadata and merging custom properties.</summary>
	public static DependencyGraph MergeGraphs( DependencyGraph primary, DependencyGraph addition, IReadOnlyList<int>? rootsOverride = null )
	{
		Dictionary<int, GraphNode> nodes = [];
		foreach( GraphNode n in primary.Nodes )
			nodes[ n.Id ] = n;
		foreach( GraphNode n in addition.Nodes )
		{
			if( !nodes.TryGetValue( n.Id, out GraphNode? existing ) )
			{
				nodes[ n.Id ] = n;
			}
			else
			{
				IReadOnlyDictionary<string, string>? mergedProps = MergeProperties( existing.Properties, n.Properties );
				nodes[ n.Id ] = existing with
				{
					Name = existing.Name.Length > 0 ? existing.Name : n.Name,
					Ext = existing.Ext.Length > 0 ? existing.Ext : n.Ext,
					Size = existing.Size > 0 ? existing.Size : n.Size,
					Category = existing.Category.Length > 0 ? existing.Category : n.Category,
					VersionId = existing.VersionId > 0 ? existing.VersionId : n.VersionId,
					Path = existing.Path.Length > 0 ? existing.Path : n.Path,
					CheckoutUser = existing.CheckoutUser.Length > 0 ? existing.CheckoutUser : n.CheckoutUser,
					TypeName = existing.TypeName.Length > 0 ? existing.TypeName : n.TypeName,
					Modified = existing.Modified.Length > 0 ? existing.Modified : n.Modified,
					Versions = Math.Max( existing.Versions, n.Versions ),
					Subtitle = existing.Subtitle.Length > 0 ? existing.Subtitle : n.Subtitle,
					ItemCount = Math.Max( existing.ItemCount, n.ItemCount ),
					Properties = mergedProps,
				};
			}
		}

		HashSet<GraphEdge> edges = [];
		foreach( GraphEdge e in primary.Edges )
			if( e.Parent != e.Child && nodes.ContainsKey( e.Parent ) && nodes.ContainsKey( e.Child ) )
				edges.Add( e );
		foreach( GraphEdge e in addition.Edges )
			if( e.Parent != e.Child && nodes.ContainsKey( e.Parent ) && nodes.ContainsKey( e.Child ) )
				edges.Add( e );

		IReadOnlyList<int> roots = rootsOverride is not null
			? [ .. rootsOverride.Where( nodes.ContainsKey ).Distinct() ]
			: [ .. primary.Roots.Concat( primary.Roots.Count == 0 ? addition.Roots : [] ).Where( nodes.ContainsKey ).Distinct() ];

		return new DependencyGraph( roots, [ .. nodes.Values ], [ .. edges ], primary.Truncated || addition.Truncated );
	}

	/// <summary>
	/// Applies depth limits (overall, offspring-specific, parent-specific), attribute &amp; property filters
	/// (with optional transitive edge bridging across hidden nodes), and per-node offspring/parent branch limits
	/// plus node cap.
	/// </summary>
	public static GraphFilterResult ApplyFilters( DependencyGraph graph, GraphFilterCriteria? criteria )
	{
		criteria ??= GraphFilterCriteria.Default;
		int origNodes = graph.Nodes.Count;
		int origEdges = graph.Edges.Count;
		if( origNodes == 0 )
			return GraphFilterResult.Empty;

		// Stage 1: Depth trimming (on topological dependency distance before attribute hiding).
		DependencyGraph working = ApplyDepthLimitsOnly( graph, criteria, out int trimmedByDepth, out bool depthTruncated );

		// Stage 2: Attribute & property filtering + optional transitive edge bridging.
		int bridgedAdded = 0;
		if( criteria.HasAttributeFilters )
			working = ApplyAttributeFilterAndBridge( working, criteria, out bridgedAdded );

		// Stage 3: Per-node offspring/parent branch caps & MaxNodes cap.
		working = ApplyBranchAndNodeLimits(
			working,
			criteria,
			out int trimmedByBranchesOrCap,
			out int edgesTrimmedChild,
			out int edgesTrimmedParent,
			out bool branchTruncated );

		bool truncated = graph.Truncated || depthTruncated || branchTruncated;
		DependencyGraph finalGraph = working with { Truncated = truncated };
		GraphFilterStats stats = new(
			TotalNodesBeforeFilter: origNodes,
			TotalEdgesBeforeFilter: origEdges,
			FilteredNodes: finalGraph.Nodes.Count,
			FilteredEdges: finalGraph.Edges.Count,
			BridgedEdgesAdded: bridgedAdded,
			NodesTrimmedByLimits: trimmedByDepth + trimmedByBranchesOrCap,
			EdgesTrimmedByChildBranchLimit: edgesTrimmedChild,
			EdgesTrimmedByParentBranchLimit: edgesTrimmedParent,
			TruncatedByLimits: depthTruncated || branchTruncated );
		return new GraphFilterResult( finalGraph, stats );
	}

	/// <summary>Builds a pure folder-hierarchy node graph from <paramref name="root"/>.</summary>
	public static GraphFilterResult BuildFolderStructureGraph( DirectoryNode root, string? startPath, GraphFilterCriteria? criteria )
	{
		criteria ??= GraphFilterCriteria.Default;
		DirectoryNode start = ResolveStartDirectory( root, startPath, out string startServerPath );

		int maxDepth = ResolveFolderDepth( criteria );
		int maxChildBranches = criteria.MaxChildBranches ?? int.MaxValue;
		int maxNodes = criteria.MaxNodes > 0 ? criteria.MaxNodes : int.MaxValue;

		int seq = 0;
		int AllocFolderId( DirectoryNode dir ) => dir.DirectoryId > 0 ? -( 1_000_000 + dir.DirectoryId ) : -( 1_500_000 + ++seq );

		List<GraphNode> nodes = [];
		List<GraphEdge> edges = [];
		Dictionary<int, int> omittedChildMap = [];
		int totalSeenFolders = 0;
		int edgesTrimmedChild = 0;
		bool truncated = false;

		int rootId = AllocFolderId( start );

		void Walk( DirectoryNode dir, int dirNodeId, string fullPath, int depth )
		{
			totalSeenFolders++;
			IReadOnlyList<DirectoryNode> children = dir.Children
				.OrderBy( c => c.Name, StringComparer.OrdinalIgnoreCase )
				.ToList();

			string subtitle = children.Count == 1 ? "1 subfolder" : $"{children.Count} subfolders";
			if( !dir.IsRemote )
				subtitle += " · local only";

			nodes.Add( new GraphNode(
				Id: dirNodeId,
				Name: dir.Name,
				Ext: "",
				Size: 0,
				Category: dir.IsRemote ? "Vault Folder" : "Local Folder",
				VersionId: 0,
				Deleted: false,
				CheckedOut: false,
				Path: fullPath )
			{
				Role = GraphNodeRole.Folder,
				TypeName = dir.IsRemote ? "Vault Folder" : "Local Folder",
				Subtitle = subtitle,
				ItemCount = children.Count,
			} );

			if( children.Count == 0 )
				return;

			if( depth >= maxDepth )
			{
				omittedChildMap[ dirNodeId ] = children.Count;
				truncated = true;
				return;
			}

			int emitted = 0;
			int omitted = 0;
			foreach( DirectoryNode child in children )
			{
				string childPath = $"{fullPath}\\{child.Name}";
				if( emitted >= maxChildBranches )
				{
					omitted++;
					edgesTrimmedChild++;
					truncated = true;
					continue;
				}
				if( nodes.Count >= maxNodes )
				{
					omitted++;
					truncated = true;
					continue;
				}
				int childId = AllocFolderId( child );
				edges.Add( new GraphEdge( dirNodeId, childId ) );
				emitted++;
				Walk( child, childId, childPath, depth + 1 );
			}
			if( omitted > 0 )
				omittedChildMap[ dirNodeId ] = omitted;
		}

		Walk( start, rootId, startServerPath, 0 );

		// Apply optional NameFilter / PathFilter on folders (keeping ancestors of matching folders).
		List<GraphNode> finalizedNodes = [ .. nodes.Select( n => omittedChildMap.TryGetValue( n.Id, out int om ) ? n with { OmittedChildBranches = om } : n ) ];
		DependencyGraph raw = new( [ rootId ], finalizedNodes, edges, truncated );
		if( !string.IsNullOrWhiteSpace( criteria.NameFilter ) || !string.IsNullOrWhiteSpace( criteria.PathFilter ) )
			raw = FilterFolderTreeKeepingAncestors( raw, criteria );

		GraphFilterStats stats = new(
			TotalNodesBeforeFilter: totalSeenFolders,
			TotalEdgesBeforeFilter: edges.Count,
			FilteredNodes: raw.Nodes.Count,
			FilteredEdges: raw.Edges.Count,
			BridgedEdgesAdded: 0,
			NodesTrimmedByLimits: Math.Max( 0, totalSeenFolders - raw.Nodes.Count ),
			EdgesTrimmedByChildBranchLimit: edgesTrimmedChild,
			EdgesTrimmedByParentBranchLimit: 0,
			TruncatedByLimits: truncated );
		return new GraphFilterResult( raw, stats );
	}

	/// <summary>Builds a combined folder-hierarchy + files node graph, optionally overlaying file dependency links.</summary>
	public static GraphFilterResult BuildFoldersAndFilesGraph(
		DirectoryNode root,
		IReadOnlyDictionary<int, IReadOnlyList<GraphNode>> filesByDirId,
		DependencyGraph? dependencyOverlay,
		string? startPath,
		GraphFilterCriteria? criteria )
	{
		criteria ??= GraphFilterCriteria.Default;
		DirectoryNode start = ResolveStartDirectory( root, startPath, out string startServerPath );

		int maxDepth = ResolveFolderDepth( criteria );
		int maxChildBranches = criteria.MaxChildBranches ?? int.MaxValue;
		int maxNodes = criteria.MaxNodes > 0 ? criteria.MaxNodes : int.MaxValue;

		int seq = 0;
		int AllocFolderId( DirectoryNode dir ) => dir.DirectoryId > 0 ? -( 1_000_000 + dir.DirectoryId ) : -( 1_500_000 + ++seq );

		Dictionary<int, GraphNode> nodeMap = [];
		HashSet<GraphEdge> edgeSet = [];
		HashSet<int> keptFileIds = [];
		int totalCandidates = 0;
		int edgesTrimmedChild = 0;
		bool truncated = false;
		bool hasFileFilters = criteria.HasAttributeFilters;

		int rootId = AllocFolderId( start );

		bool Walk( DirectoryNode dir, int dirNodeId, string fullPath, int depth )
		{
			totalCandidates++;
			IReadOnlyList<DirectoryNode> childDirs = [ .. dir.Children.OrderBy( c => c.Name, StringComparer.OrdinalIgnoreCase ) ];
			IReadOnlyList<GraphNode> allDirFiles = dir.DirectoryId > 0 && filesByDirId.TryGetValue( dir.DirectoryId, out var found )
				? found
				: [];
			totalCandidates += allDirFiles.Count;

			List<GraphNode> matchingFiles = [ .. allDirFiles
				.Where( f => MatchesNodeAttributes( f, criteria ) )
				.OrderBy( f => f.Name, StringComparer.OrdinalIgnoreCase ) ];

			List<(DirectoryNode Child, int ChildId, string ChildPath)> keptSubdirs = [];
			List<GraphNode> keptFiles = [];
			int emitted = 0;
			int omitted = 0;

			if( depth < maxDepth )
			{
				// Reserve space: process subdirectories first, then files in this folder
				foreach( DirectoryNode sub in childDirs )
				{
					string subPath = $"{fullPath}\\{sub.Name}";
					if( emitted >= maxChildBranches )
					{
						omitted++;
						edgesTrimmedChild++;
						truncated = true;
						continue;
					}
					if( nodeMap.Count + keptFiles.Count + 1 >= maxNodes )
					{
						omitted++;
						truncated = true;
						continue;
					}
					int subId = AllocFolderId( sub );
					if( Walk( sub, subId, subPath, depth + 1 ) )
					{
						keptSubdirs.Add( ( sub, subId, subPath ) );
						emitted++;
					}
				}

				foreach( GraphNode file in matchingFiles )
				{
					if( emitted >= maxChildBranches )
					{
						omitted++;
						edgesTrimmedChild++;
						truncated = true;
						continue;
					}
					if( nodeMap.Count + keptFiles.Count + 1 >= maxNodes )
					{
						omitted++;
						truncated = true;
						continue;
					}
					keptFiles.Add( file );
					emitted++;
				}
			}
			else if( childDirs.Count > 0 || matchingFiles.Count > 0 )
			{
				omitted = childDirs.Count + matchingFiles.Count;
				truncated = true;
			}

			bool folderItselfMatches = !hasFileFilters
				|| depth == 0
				|| keptSubdirs.Count > 0
				|| keptFiles.Count > 0
				|| ( !string.IsNullOrWhiteSpace( criteria.NameFilter ) && MatchesWildcardOrSubstring( dir.Name, criteria.NameFilter ) );
			if( !folderItselfMatches )
				return false;

			string subText = $"{childDirs.Count} folder{( childDirs.Count == 1 ? "" : "s" )} · {matchingFiles.Count} file{( matchingFiles.Count == 1 ? "" : "s" )}";
			nodeMap[ dirNodeId ] = new GraphNode(
				Id: dirNodeId,
				Name: dir.Name,
				Ext: "",
				Size: 0,
				Category: dir.IsRemote ? "Vault Folder" : "Local Folder",
				VersionId: 0,
				Deleted: false,
				CheckedOut: false,
				Path: fullPath )
			{
				Role = GraphNodeRole.Folder,
				TypeName = dir.IsRemote ? "Vault Folder" : "Local Folder",
				Subtitle = subText,
				ItemCount = childDirs.Count + matchingFiles.Count,
				OmittedChildBranches = omitted,
			};

			foreach( var (_, subId, _) in keptSubdirs )
				edgeSet.Add( new GraphEdge( dirNodeId, subId ) );

			foreach( GraphNode file in keptFiles )
			{
				nodeMap[ file.Id ] = file;
				keptFileIds.Add( file.Id );
				edgeSet.Add( new GraphEdge( dirNodeId, file.Id ) );
			}

			return true;
		}

		Walk( start, rootId, startServerPath, 0 );

		if( criteria.IncludeDependencyLinks && dependencyOverlay is not null )
		{
			foreach( GraphEdge e in dependencyOverlay.Edges )
			{
				if( e.Parent != e.Child && keptFileIds.Contains( e.Parent ) && keptFileIds.Contains( e.Child ) )
					edgeSet.Add( e );
			}
		}

		DependencyGraph built = new( nodeMap.ContainsKey( rootId ) ? [ rootId ] : [], [ .. nodeMap.Values ], [ .. edgeSet ], truncated );
		GraphFilterStats stats = new(
			TotalNodesBeforeFilter: totalCandidates,
			TotalEdgesBeforeFilter: edgeSet.Count + edgesTrimmedChild,
			FilteredNodes: built.Nodes.Count,
			FilteredEdges: built.Edges.Count,
			BridgedEdgesAdded: 0,
			NodesTrimmedByLimits: Math.Max( 0, totalCandidates - built.Nodes.Count ),
			EdgesTrimmedByChildBranchLimit: edgesTrimmedChild,
			EdgesTrimmedByParentBranchLimit: 0,
			TruncatedByLimits: truncated );
		return new GraphFilterResult( built, stats );
	}

	/// <summary>
	/// Groups <paramref name="candidateFiles"/> under synthetic <see cref="GraphNodeRole.PropertyGroup"/> hub
	/// nodes for any custom property or built-in attribute (Category, Extension, CheckoutUser, Folder, Material, Revision, etc.).
	/// </summary>
	public static GraphFilterResult BuildPropertyGroupGraph(
		IReadOnlyList<GraphNode> candidateFiles,
		DependencyGraph? dependencyOverlay,
		GraphFilterCriteria? criteria )
	{
		criteria ??= GraphFilterCriteria.Default;
		string groupProp = string.IsNullOrWhiteSpace( criteria.GroupByProperty )
			? ( !string.IsNullOrWhiteSpace( criteria.PropertyName ) ? criteria.PropertyName!.Trim() : "Category" )
			: criteria.GroupByProperty!.Trim();

		List<GraphNode> filtered = [ .. candidateFiles
			.Where( n => n.Role == GraphNodeRole.File )
			.DistinctBy( n => n.Id )
			.Where( n => MatchesNodeAttributes( n, criteria ) )
			.OrderBy( n => n.Name, StringComparer.OrdinalIgnoreCase ) ];

		if( filtered.Count == 0 )
		{
			return new GraphFilterResult( DependencyGraph.Empty, new GraphFilterStats(
				candidateFiles.Count, 0, 0, 0, 0, 0, 0, 0, false ) );
		}

		int maxChildBranches = criteria.MaxChildBranches ?? int.MaxValue;
		int maxNodes = criteria.MaxNodes > 0 ? criteria.MaxNodes : int.MaxValue;

		var groups = filtered
			.GroupBy( n =>
			{
				string? val = ResolveFieldValue( n, groupProp );
				return string.IsNullOrWhiteSpace( val ) ? "(Unassigned)" : val.Trim();
			}, StringComparer.OrdinalIgnoreCase )
			.OrderByDescending( g => g.Count() )
			.ThenBy( g => g.Key, StringComparer.OrdinalIgnoreCase )
			.ToList();

		List<int> roots = [];
		Dictionary<int, GraphNode> nodeMap = [];
		HashSet<GraphEdge> edges = [];
		HashSet<int> keptFileIds = [];
		int groupIdx = 0;
		int edgesTrimmedChild = 0;
		bool truncated = false;

		foreach( var grp in groups )
		{
			if( nodeMap.Count >= maxNodes )
			{
				truncated = true;
				break;
			}
			int groupId = -( 2_000_000 + ++groupIdx );
			List<GraphNode> members = [ .. grp.OrderBy( f => f.Name, StringComparer.OrdinalIgnoreCase ) ];
			int emitted = 0;
			int omitted = 0;

			List<GraphNode> keptMembers = [];
			foreach( GraphNode member in members )
			{
				if( emitted >= maxChildBranches )
				{
					omitted++;
					edgesTrimmedChild++;
					truncated = true;
					continue;
				}
				if( nodeMap.Count + 1 + keptMembers.Count >= maxNodes )
				{
					omitted++;
					truncated = true;
					continue;
				}
				keptMembers.Add( member );
				emitted++;
			}

			GraphNode hub = new(
				Id: groupId,
				Name: $"{groupProp}: {grp.Key}",
				Ext: "",
				Size: 0,
				Category: groupProp,
				VersionId: 0,
				Deleted: false,
				CheckedOut: false,
				Path: $"{groupProp} = {grp.Key}" )
			{
				Role = GraphNodeRole.PropertyGroup,
				TypeName = $"Property ({groupProp})",
				Subtitle = members.Count == 1 ? "1 file" : $"{members.Count} files",
				ItemCount = members.Count,
				OmittedChildBranches = omitted,
			};

			roots.Add( groupId );
			nodeMap[ groupId ] = hub;
			foreach( GraphNode member in keptMembers )
			{
				nodeMap[ member.Id ] = member;
				keptFileIds.Add( member.Id );
				edges.Add( new GraphEdge( groupId, member.Id ) );
			}
		}

		if( criteria.IncludeDependencyLinks && dependencyOverlay is not null )
		{
			foreach( GraphEdge e in dependencyOverlay.Edges )
			{
				if( e.Parent != e.Child && keptFileIds.Contains( e.Parent ) && keptFileIds.Contains( e.Child ) )
					edges.Add( e );
			}
		}

		DependencyGraph built = new( roots, [ .. nodeMap.Values ], [ .. edges ], truncated );
		GraphFilterStats stats = new(
			TotalNodesBeforeFilter: candidateFiles.Count,
			TotalEdgesBeforeFilter: filtered.Count,
			FilteredNodes: built.Nodes.Count,
			FilteredEdges: built.Edges.Count,
			BridgedEdgesAdded: 0,
			NodesTrimmedByLimits: Math.Max( 0, filtered.Count - keptFileIds.Count ),
			EdgesTrimmedByChildBranchLimit: edgesTrimmedChild,
			EdgesTrimmedByParentBranchLimit: 0,
			TruncatedByLimits: truncated );
		return new GraphFilterResult( built, stats );
	}

	// ------------------------------------------------------------------ Internal Stages

	private static DependencyGraph ApplyDepthLimitsOnly(
		DependencyGraph graph,
		GraphFilterCriteria criteria,
		out int trimmedCount,
		out bool truncated )
	{
		trimmedCount = 0;
		truncated = false;

		bool hasDepthLimit = criteria.MaxDepth > 0 || criteria.MaxChildDepth.HasValue || criteria.MaxParentDepth.HasValue;
		if( !hasDepthLimit )
			return graph;

		Dictionary<int, GraphNode> nodeMap = graph.Nodes.DistinctBy( n => n.Id ).ToDictionary( n => n.Id );
		List<GraphEdge> validEdges = [ .. graph.Edges.Where( e => e.Parent != e.Child && nodeMap.ContainsKey( e.Parent ) && nodeMap.ContainsKey( e.Child ) ).Distinct() ];
		ILookup<int, int> childrenOf = validEdges.ToLookup( e => e.Parent, e => e.Child );
		ILookup<int, int> parentsOf = validEdges.ToLookup( e => e.Child, e => e.Parent );

		List<int> roots = ResolveEffectiveRoots( graph, nodeMap, parentsOf );
		int overallMax = criteria.MaxDepth > 0 ? criteria.MaxDepth : int.MaxValue;
		int childMax = criteria.MaxChildDepth ?? overallMax;
		int parentMax = criteria.MaxParentDepth ?? overallMax;

		HashSet<int> kept = [ .. roots ];
		Dictionary<int, int> omittedChild = [];
		Dictionary<int, int> omittedParent = [];

		if( criteria.Direction == GraphDirection.FullNetwork )
		{
			Queue<(int Id, int Hops, int ChildSteps, int ParentSteps)> q = new();
			foreach( int r in roots )
				q.Enqueue( ( r, 0, 0, 0 ) );

			while( q.TryDequeue( out var cur ) )
			{
				bool canExpandChild = cur.Hops < overallMax && cur.ChildSteps < childMax;
				bool canExpandParent = cur.Hops < overallMax && cur.ParentSteps < parentMax;

				if( canExpandChild )
				{
					foreach( int c in childrenOf[ cur.Id ] )
					{
						if( kept.Add( c ) )
							q.Enqueue( ( c, cur.Hops + 1, cur.ChildSteps + 1, cur.ParentSteps ) );
					}
				}
				else
				{
					int missed = childrenOf[ cur.Id ].Count( c => !kept.Contains( c ) );
					if( missed > 0 )
					{
						omittedChild[ cur.Id ] = Math.Max( omittedChild.GetValueOrDefault( cur.Id ), missed );
						truncated = true;
					}
				}

				if( canExpandParent )
				{
					foreach( int p in parentsOf[ cur.Id ] )
					{
						if( kept.Add( p ) )
							q.Enqueue( ( p, cur.Hops + 1, cur.ChildSteps, cur.ParentSteps + 1 ) );
					}
				}
				else
				{
					int missed = parentsOf[ cur.Id ].Count( p => !kept.Contains( p ) );
					if( missed > 0 )
					{
						omittedParent[ cur.Id ] = Math.Max( omittedParent.GetValueOrDefault( cur.Id ), missed );
						truncated = true;
					}
				}
			}
		}
		else
		{
			if( criteria.Direction != GraphDirection.Parents )
			{
				HashSet<int> seenDown = [ .. roots ];
				Queue<(int Id, int Depth)> q = new();
				foreach( int r in roots )
					q.Enqueue( ( r, 0 ) );
				while( q.TryDequeue( out var cur ) )
				{
					if( cur.Depth >= childMax )
					{
						int missed = childrenOf[ cur.Id ].Count( c => !seenDown.Contains( c ) );
						if( missed > 0 )
						{
							omittedChild[ cur.Id ] = missed;
							truncated = true;
						}
						continue;
					}
					foreach( int c in childrenOf[ cur.Id ] )
					{
						if( seenDown.Add( c ) )
						{
							kept.Add( c );
							q.Enqueue( ( c, cur.Depth + 1 ) );
						}
					}
				}
			}

			if( criteria.Direction != GraphDirection.Children )
			{
				HashSet<int> seenUp = [ .. roots ];
				Queue<(int Id, int Depth)> q = new();
				foreach( int r in roots )
					q.Enqueue( ( r, 0 ) );
				while( q.TryDequeue( out var cur ) )
				{
					if( cur.Depth >= parentMax )
					{
						int missed = parentsOf[ cur.Id ].Count( p => !seenUp.Contains( p ) );
						if( missed > 0 )
						{
							omittedParent[ cur.Id ] = missed;
							truncated = true;
						}
						continue;
					}
					foreach( int p in parentsOf[ cur.Id ] )
					{
						if( seenUp.Add( p ) )
						{
							kept.Add( p );
							q.Enqueue( ( p, cur.Depth + 1 ) );
						}
					}
				}
			}
		}

		trimmedCount = Math.Max( 0, nodeMap.Count - kept.Count );
		if( trimmedCount > 0 )
			truncated = true;

		List<GraphNode> keptNodes = [ .. graph.Nodes
			.Where( n => kept.Contains( n.Id ) )
			.Select( n => n with
			{
				OmittedChildBranches = n.OmittedChildBranches + omittedChild.GetValueOrDefault( n.Id ),
				OmittedParentBranches = n.OmittedParentBranches + omittedParent.GetValueOrDefault( n.Id ),
			} ) ];
		List<GraphEdge> keptEdges = [ .. validEdges.Where( e => kept.Contains( e.Parent ) && kept.Contains( e.Child ) ) ];
		return new DependencyGraph( [ .. graph.Roots.Where( kept.Contains ) ], keptNodes, keptEdges, graph.Truncated || truncated );
	}

	private static DependencyGraph ApplyAttributeFilterAndBridge(
		DependencyGraph graph,
		GraphFilterCriteria criteria,
		out int bridgedAdded )
	{
		bridgedAdded = 0;
		Dictionary<int, GraphNode> nodeMap = graph.Nodes.DistinctBy( n => n.Id ).ToDictionary( n => n.Id );
		HashSet<int> rootSet = [ .. graph.Roots.Where( nodeMap.ContainsKey ) ];

		HashSet<int> kept = [];
		foreach( GraphNode node in nodeMap.Values )
		{
			if( ( criteria.KeepRoots && rootSet.Contains( node.Id ) ) || MatchesNodeAttributes( node, criteria ) )
				kept.Add( node.Id );
		}

		List<GraphEdge> validEdges = [ .. graph.Edges.Where( e => e.Parent != e.Child && nodeMap.ContainsKey( e.Parent ) && nodeMap.ContainsKey( e.Child ) ).Distinct() ];
		HashSet<GraphEdge> newEdges = [];
		foreach( GraphEdge e in validEdges )
		{
			if( kept.Contains( e.Parent ) && kept.Contains( e.Child ) )
				newEdges.Add( e );
		}

		if( criteria.BridgeFilteredEdges && kept.Count > 1 && kept.Count < nodeMap.Count )
		{
			ILookup<int, int> childrenOf = validEdges.ToLookup( e => e.Parent, e => e.Child );
			foreach( int start in kept )
			{
				// Walk ONLY through hidden intermediate nodes until reaching the first surviving descendant(s).
				HashSet<int> visitedHidden = [];
				Queue<int> q = new();
				foreach( int first in childrenOf[ start ] )
				{
					if( !kept.Contains( first ) && visitedHidden.Add( first ) )
						q.Enqueue( first );
				}
				while( q.TryDequeue( out int hidden ) )
				{
					foreach( int next in childrenOf[ hidden ] )
					{
						if( next == start )
							continue;
						if( kept.Contains( next ) )
						{
							if( newEdges.Add( new GraphEdge( start, next ) ) )
								bridgedAdded++;
						}
						else if( visitedHidden.Add( next ) )
						{
							q.Enqueue( next );
						}
					}
				}
			}
		}

		List<GraphNode> keptNodes = [ .. graph.Nodes.Where( n => kept.Contains( n.Id ) ) ];
		List<int> keptRoots = [ .. graph.Roots.Where( kept.Contains ) ];
		return new DependencyGraph( keptRoots, keptNodes, [ .. newEdges ], graph.Truncated );
	}

	private static DependencyGraph ApplyBranchAndNodeLimits(
		DependencyGraph graph,
		GraphFilterCriteria criteria,
		out int trimmedNodes,
		out int edgesTrimmedChild,
		out int edgesTrimmedParent,
		out bool truncated )
	{
		trimmedNodes = 0;
		edgesTrimmedChild = 0;
		edgesTrimmedParent = 0;
		truncated = false;

		bool hasChildBranchCap = criteria.MaxChildBranches.HasValue;
		bool hasParentBranchCap = criteria.MaxParentBranches.HasValue;
		bool hasNodeCap = criteria.MaxNodes > 0 && graph.Nodes.Count > criteria.MaxNodes;
		if( !hasChildBranchCap && !hasParentBranchCap && !hasNodeCap )
			return graph;

		Dictionary<int, GraphNode> nodeMap = graph.Nodes.DistinctBy( n => n.Id ).ToDictionary( n => n.Id );
		List<GraphEdge> validEdges = [ .. graph.Edges.Where( e => e.Parent != e.Child && nodeMap.ContainsKey( e.Parent ) && nodeMap.ContainsKey( e.Child ) ).Distinct() ];
		ILookup<int, int> childrenOf = validEdges.ToLookup( e => e.Parent, e => e.Child );
		ILookup<int, int> parentsOf = validEdges.ToLookup( e => e.Child, e => e.Parent );

		List<int> roots = ResolveEffectiveRoots( graph, nodeMap, parentsOf );
		HashSet<int> rootSet = [ .. roots ];
		int maxNodes = criteria.MaxNodes > 0 ? criteria.MaxNodes : int.MaxValue;
		int maxChildBranches = criteria.MaxChildBranches ?? int.MaxValue;
		int maxParentBranches = criteria.MaxParentBranches ?? int.MaxValue;

		HashSet<int> keptNodes = [];
		HashSet<GraphEdge> keptEdges = [];
		Dictionary<int, int> omittedChild = [];
		Dictionary<int, int> omittedParent = [];
		Queue<int> queue = new();

		foreach( int r in roots )
		{
			if( keptNodes.Count >= maxNodes )
			{
				truncated = true;
				break;
			}
			if( keptNodes.Add( r ) )
				queue.Enqueue( r );
		}

		// Deterministic ordering: roots first, already-included nodes next (so paths to roots stay unbroken), then Name, then Id.
		List<int> SortCandidates( IEnumerable<int> ids ) =>
		[
			.. ids.Distinct()
				.OrderBy( id => rootSet.Contains( id ) ? 0 : keptNodes.Contains( id ) ? 1 : 2 )
				.ThenBy( id => nodeMap[ id ].Name, StringComparer.OrdinalIgnoreCase )
				.ThenBy( id => id ),
		];

		bool allowChildren = criteria.Direction != GraphDirection.Parents;
		bool allowParents = criteria.Direction != GraphDirection.Children;

		if( criteria.Direction == GraphDirection.Both )
		{
			// Separate downward and upward passes from roots so Both never leaks into sibling/co-parent expansion.
			HashSet<int> downVisited = [ .. keptNodes ];
			Queue<int> downQ = new( keptNodes );
			while( downQ.TryDequeue( out int u ) )
			{
				List<int> kids = SortCandidates( childrenOf[ u ] );
				int taken = 0;
				int omitted = 0;
				foreach( int c in kids )
				{
					if( taken >= maxChildBranches )
					{
						omitted++;
						edgesTrimmedChild++;
						truncated = true;
						continue;
					}
					if( !keptNodes.Contains( c ) && keptNodes.Count >= maxNodes )
					{
						omitted++;
						truncated = true;
						continue;
					}
					keptEdges.Add( new GraphEdge( u, c ) );
					taken++;
					if( keptNodes.Add( c ) )
					{
						downVisited.Add( c );
						downQ.Enqueue( c );
					}
					else if( downVisited.Add( c ) )
					{
						downQ.Enqueue( c );
					}
				}
				if( omitted > 0 )
					omittedChild[ u ] = omitted;
			}

			HashSet<int> upVisited = [ .. roots.Where( keptNodes.Contains ) ];
			Queue<int> upQ = new( upVisited );
			while( upQ.TryDequeue( out int u ) )
			{
				List<int> pars = SortCandidates( parentsOf[ u ] );
				int taken = 0;
				int omitted = 0;
				foreach( int p in pars )
				{
					if( taken >= maxParentBranches )
					{
						omitted++;
						edgesTrimmedParent++;
						truncated = true;
						continue;
					}
					if( !keptNodes.Contains( p ) && keptNodes.Count >= maxNodes )
					{
						omitted++;
						truncated = true;
						continue;
					}
					keptEdges.Add( new GraphEdge( p, u ) );
					taken++;
					if( keptNodes.Add( p ) )
					{
						upVisited.Add( p );
						upQ.Enqueue( p );
					}
					else if( upVisited.Add( p ) )
					{
						upQ.Enqueue( p );
					}
				}
				if( omitted > 0 )
					omittedParent[ u ] = omitted;
			}
		}
		else
		{
			// Children, Parents, or FullNetwork BFS with independent per-node offspring & parent branch limits.
			while( queue.TryDequeue( out int u ) )
			{
				if( allowChildren )
				{
					List<int> kids = SortCandidates( childrenOf[ u ] );
					int existingOut = keptEdges.Count( e => e.Parent == u );
					int omitted = 0;
					foreach( int c in kids )
					{
						GraphEdge edge = new( u, c );
						if( keptEdges.Contains( edge ) )
							continue;
						if( existingOut >= maxChildBranches )
						{
							omitted++;
							edgesTrimmedChild++;
							truncated = true;
							continue;
						}
						if( !keptNodes.Contains( c ) && keptNodes.Count >= maxNodes )
						{
							omitted++;
							truncated = true;
							continue;
						}
						keptEdges.Add( edge );
						existingOut++;
						if( keptNodes.Add( c ) )
							queue.Enqueue( c );
					}
					if( omitted > 0 )
						omittedChild[ u ] = omitted;
				}

				if( allowParents )
				{
					List<int> pars = SortCandidates( parentsOf[ u ] );
					int existingIn = keptEdges.Count( e => e.Child == u );
					int omitted = 0;
					foreach( int p in pars )
					{
						GraphEdge edge = new( p, u );
						if( keptEdges.Contains( edge ) )
							continue;
						if( existingIn >= maxParentBranches )
						{
							omitted++;
							edgesTrimmedParent++;
							truncated = true;
							continue;
						}
						if( !keptNodes.Contains( p ) && keptNodes.Count >= maxNodes )
						{
							omitted++;
							truncated = true;
							continue;
						}
						keptEdges.Add( edge );
						existingIn++;
						if( keptNodes.Add( p ) )
							queue.Enqueue( p );
					}
					if( omitted > 0 )
						omittedParent[ u ] = omitted;
				}
			}
		}

		// Preserve any disconnected nodes up to maxNodes if no branch cap excluded them (e.g. SearchFilter flat results).
		if( validEdges.Count == 0 )
		{
			foreach( int id in nodeMap.Keys.Order() )
			{
				if( keptNodes.Contains( id ) )
					continue;
				if( keptNodes.Count >= maxNodes )
				{
					truncated = true;
					break;
				}
				keptNodes.Add( id );
			}
		}

		trimmedNodes = Math.Max( 0, nodeMap.Count - keptNodes.Count );
		if( trimmedNodes > 0 )
			truncated = true;

		List<GraphNode> outNodes = [ .. graph.Nodes
			.Where( n => keptNodes.Contains( n.Id ) )
			.Select( n => n with
			{
				OmittedChildBranches = n.OmittedChildBranches + omittedChild.GetValueOrDefault( n.Id ),
				OmittedParentBranches = n.OmittedParentBranches + omittedParent.GetValueOrDefault( n.Id ),
			} ) ];

		return new DependencyGraph(
			[ .. graph.Roots.Where( keptNodes.Contains ) ],
			outNodes,
			[ .. keptEdges ],
			graph.Truncated || truncated );
	}

	private static List<int> ResolveEffectiveRoots(
		DependencyGraph graph,
		Dictionary<int, GraphNode> nodeMap,
		ILookup<int, int> parentsOf )
	{
		List<int> roots = [ .. graph.Roots.Where( nodeMap.ContainsKey ).Distinct() ];
		if( roots.Count > 0 )
			return roots;
		List<int> sources = [ .. nodeMap.Keys.Where( id => !parentsOf[ id ].Any() ).Order() ];
		return sources.Count > 0 ? sources : [ .. nodeMap.Keys.Order() ];
	}

	private static int ResolveFolderDepth( GraphFilterCriteria criteria )
	{
		if( criteria.MaxChildDepth.HasValue )
			return Math.Max( 0, criteria.MaxChildDepth.Value );
		return criteria.MaxDepth > 0 ? criteria.MaxDepth : int.MaxValue;
	}

	private static DirectoryNode ResolveStartDirectory( DirectoryNode root, string? startPath, out string fullServerPath )
	{
		fullServerPath = root.Name;
		if( string.IsNullOrWhiteSpace( startPath ) )
			return root;

		string[] segments = startPath.Split( PathSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries );
		if( segments.Length == 0 )
			return root;

		int idx = string.Equals( segments[ 0 ], root.Name, StringComparison.OrdinalIgnoreCase ) ? 1 : 0;
		DirectoryNode cur = root;
		List<string> pathParts = [ root.Name ];
		for( int i = idx; i < segments.Length; i++ )
		{
			DirectoryNode? next = cur.Children.FirstOrDefault( c => string.Equals( c.Name, segments[ i ], StringComparison.OrdinalIgnoreCase ) );
			if( next is null )
				break;
			cur = next;
			pathParts.Add( cur.Name );
		}
		fullServerPath = string.Join( "\\", pathParts );
		return cur;
	}

	private static DependencyGraph FilterFolderTreeKeepingAncestors( DependencyGraph graph, GraphFilterCriteria criteria )
	{
		Dictionary<int, GraphNode> nodeMap = graph.Nodes.ToDictionary( n => n.Id );
		ILookup<int, int> parentOf = graph.Edges.ToLookup( e => e.Child, e => e.Parent );
		HashSet<int> kept = [];

		foreach( GraphNode n in graph.Nodes )
		{
			bool matches = MatchesWildcardOrSubstring( n.Name, criteria.NameFilter )
				&& MatchesWildcardOrSubstring( n.Path, criteria.PathFilter );
			if( !matches )
				continue;
			int cur = n.Id;
			while( kept.Add( cur ) )
			{
				int? par = parentOf[ cur ].Cast<int?>().FirstOrDefault();
				if( par is null )
					break;
				cur = par.Value;
			}
		}

		if( kept.Count == 0 && graph.Roots.Count > 0 && criteria.KeepRoots )
			kept.Add( graph.Roots[ 0 ] );

		return new DependencyGraph(
			[ .. graph.Roots.Where( kept.Contains ) ],
			[ .. graph.Nodes.Where( n => kept.Contains( n.Id ) ) ],
			[ .. graph.Edges.Where( e => kept.Contains( e.Parent ) && kept.Contains( e.Child ) ) ],
			graph.Truncated );
	}

	private static string EffectiveExtension( GraphNode node )
	{
		if( node.Ext.Length > 0 )
			return node.Ext.TrimStart( '.' ).ToLowerInvariant();
		return System.IO.Path.GetExtension( node.Name ).TrimStart( '.' ).ToLowerInvariant();
	}

	private static string ExtractFolder( string path )
	{
		if( string.IsNullOrWhiteSpace( path ) )
			return "(Root)";
		string norm = path.Replace( '/', '\\' );
		int cut = norm.LastIndexOf( '\\' );
		return cut > 0 ? norm[ ..cut ] : "(Root)";
	}

	private static bool TryCompareNumbers( string actual, string expected, out int cmp )
	{
		cmp = 0;
		if( double.TryParse( actual, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out double a )
			&& double.TryParse( expected, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out double b ) )
		{
			cmp = a.CompareTo( b );
			return true;
		}
		return false;
	}

	private static IReadOnlyDictionary<string, string>? MergeProperties(
		IReadOnlyDictionary<string, string>? a,
		IReadOnlyDictionary<string, string>? b )
	{
		if( a is null or { Count: 0 } )
			return b;
		if( b is null or { Count: 0 } )
			return a;
		Dictionary<string, string> merged = new( a, StringComparer.OrdinalIgnoreCase );
		foreach( var kv in b )
			merged[ kv.Key ] = kv.Value;
		return merged;
	}
}

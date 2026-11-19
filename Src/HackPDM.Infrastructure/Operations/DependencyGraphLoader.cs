using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using HackPDM.Core;
using HackPDM.Core.Graph;
using HackPDM.Core.Hack;
using HackPDM.Domain.OdooModels.Models;
using HackPDM.Infrastructure.Odoo;
using HackPDM.Infrastructure.Odoo.Models;

namespace HackPDM.Infrastructure.Operations;

/// <summary>Unified request for building any HackPDM node graph view mode (Dependencies, FolderStructure, FoldersAndFiles, ByProperty, SearchFilter).</summary>
public sealed record GraphBuildRequest
{
	public IReadOnlyList<int>? EntryIds { get; init; }
	public int? DirectoryId { get; init; }
	public string? FolderPath { get; init; }
	public bool RecursiveFolders { get; init; } = true;
	public GraphFilterCriteria Criteria { get; init; } = GraphFilterCriteria.Default;
	public bool LoadProperties { get; init; }
	public DirectoryNode? CachedDirectoryTree { get; init; }
}

/// <summary>
/// Loads dependency graphs (<c>hp.entry.dependency_graph</c>), performs multi-hop <see cref="GraphDirection.FullNetwork"/>
/// expansion (each parent's and child's parents &amp; children), attaches custom properties, and builds
/// folder-structure, folders-and-files, property-grouped, and search-filtered node graphs.
/// </summary>
public static class DependencyGraphLoader
{
	private const int RpcBatchSize = 80;
	private const int PropertyBatchSize = 200;

	/// <summary>The files the selected files use, are used by, both, or full multi-hop network.</summary>
	/// <param name="entryIds">The selected files (hp.entry ids).</param>
	/// <param name="direction">Which dependencies to follow.</param>
	/// <param name="token">Cancels the call.</param>
	/// <returns>The graph (empty for no files); or the server's error.</returns>
	/// <exception cref="OperationCanceledException"><paramref name="token"/> was cancelled.</exception>
	public static async Task<Result<DependencyGraph>> LoadAsync( IReadOnlyList<int> entryIds, GraphDirection direction, CancellationToken token = default )
	{
		if( entryIds.Count == 0 )
			return Result<DependencyGraph>.Success( DependencyGraph.Empty );

		if( direction == GraphDirection.FullNetwork )
		{
			Result<DependencyGraph> full = await LoadFullNetworkRawAsync( entryIds, maxRounds: 2, nodeBudget: 600, token );
			return full;
		}

		return await FetchSingleDependencyGraphAsync( entryIds, direction, token );
	}

	/// <summary>Loads a dependency graph and applies depth, per-node branch limits, and attribute/property filters.</summary>
	public static async Task<Result<GraphFilterResult>> LoadFilteredAsync(
		IReadOnlyList<int> entryIds,
		GraphFilterCriteria? criteria,
		bool loadProperties = false,
		CancellationToken token = default )
	{
		criteria ??= GraphFilterCriteria.Default;
		if( entryIds.Count == 0 )
			return Result<GraphFilterResult>.Success( GraphFilterResult.Empty );

		Result<DependencyGraph> rawResult;
		if( criteria.Direction == GraphDirection.FullNetwork )
		{
			int rounds = ResolveFullNetworkRounds( criteria );
			int budget = criteria.MaxNodes > 0 ? Math.Clamp( criteria.MaxNodes * 3, 250, 2000 ) : 1000;
			rawResult = await LoadFullNetworkRawAsync( entryIds, rounds, budget, token );
		}
		else
		{
			rawResult = await FetchSingleDependencyGraphAsync( entryIds, criteria.Direction, token );
		}

		if( !rawResult.Ok || rawResult.Value is not { } raw )
			return Result<GraphFilterResult>.Fail( rawResult.Error ?? "Failed to load dependency graph." );

		if( loadProperties || criteria.NeedsCustomProperties )
			raw = await AttachPropertiesAsync( raw, criteria.PropertyName, token );

		return Result<GraphFilterResult>.Success( GraphFilterEngine.ApplyFilters( raw, criteria ) );
	}

	/// <summary>
	/// Fetches the parents and/or children of a single <paramref name="nodeId"/> and merges them into
	/// <paramref name="current"/>, enabling interactive step-by-step neighborhood expansion.
	/// </summary>
	public static async Task<Result<DependencyGraph>> ExpandNodeNeighborsAsync(
		DependencyGraph current,
		int nodeId,
		GraphDirection direction = GraphDirection.Both,
		CancellationToken token = default )
	{
		if( nodeId <= 0 )
			return Result<DependencyGraph>.Success( current );

		Result<DependencyGraph> delta = direction == GraphDirection.FullNetwork
			? await LoadFullNetworkRawAsync( [ nodeId ], maxRounds: 2, nodeBudget: 400, token )
			: await FetchSingleDependencyGraphAsync( [ nodeId ], direction, token );

		if( !delta.Ok || delta.Value is not { } added )
			return Result<DependencyGraph>.Fail( delta.Error ?? "Failed to expand node neighbors." );

		IReadOnlyList<int> roots = current.Roots.Count > 0 ? current.Roots : [ nodeId ];
		return Result<DependencyGraph>.Success( GraphFilterEngine.MergeGraphs( current, added, roots ) );
	}

	/// <summary>
	/// Unified builder supporting all 5 <see cref="GraphViewMode"/> modes:
	/// <c>Dependencies</c>, <c>FolderStructure</c>, <c>FoldersAndFiles</c>, <c>ByProperty</c>, and <c>SearchFilter</c>.
	/// </summary>
	public static async Task<Result<GraphFilterResult>> BuildGraphAsync( GraphBuildRequest request, CancellationToken token = default )
	{
		GraphFilterCriteria criteria = request.Criteria ?? GraphFilterCriteria.Default;
		return criteria.ViewMode switch
		{
			GraphViewMode.FolderStructure => await BuildFolderStructureAsync( request, criteria, token ),
			GraphViewMode.FoldersAndFiles => await BuildFoldersAndFilesAsync( request, criteria, token ),
			GraphViewMode.ByProperty => await BuildByPropertyAsync( request, criteria, token ),
			GraphViewMode.SearchFilter => await BuildSearchFilterAsync( request, criteria, token ),
			_ => await LoadFilteredAsync( request.EntryIds ?? [], criteria, request.LoadProperties, token ),
		};
	}

	/// <summary>
	/// Queries <c>hp.version.property</c> for every file node in <paramref name="graph"/> that has a positive
	/// <see cref="GraphNode.VersionId"/> and attaches a case-insensitive custom property map to each node.
	/// </summary>
	public static async Task<DependencyGraph> AttachPropertiesAsync(
		DependencyGraph graph,
		string? propertyNameFilter = null,
		CancellationToken token = default )
	{
		List<int> versionIds = [ .. graph.Nodes
			.Where( n => n.Role == GraphNodeRole.File && n.VersionId > 0 )
			.Select( n => n.VersionId )
			.Distinct() ];
		if( versionIds.Count == 0 )
			return graph;

		Dictionary<int, Dictionary<string, string>> propsByVersion = await FetchPropertiesByVersionIdsAsync( versionIds, propertyNameFilter, token );
		if( propsByVersion.Count == 0 )
			return graph;

		List<GraphNode> enriched = [ .. graph.Nodes.Select( n =>
		{
			if( n.VersionId <= 0 || !propsByVersion.TryGetValue( n.VersionId, out Dictionary<string, string>? found ) )
				return n;
			Dictionary<string, string> merged = n.Properties is { Count: > 0 }
				? new Dictionary<string, string>( n.Properties, StringComparer.OrdinalIgnoreCase )
				: new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
			foreach( var kv in found )
				merged[ kv.Key ] = kv.Value;
			return n with { Properties = merged };
		} ) ];

		return graph with { Nodes = enriched };
	}

	/// <summary>Builds a <see cref="DependencyGraph"/> from the server's reply; missing or malformed parts are left out.</summary>
	public static DependencyGraph Parse( IReadOnlyDictionary<string, object?> reply )
	{
		List<GraphNode> nodes = [];
		foreach( var row in reply.GetValueOrDefault( "nodes" ) as IEnumerable<object?> ?? [] )
		{
			if( row is not IReadOnlyDictionary<string, object?> n || n.Int( "id" ) is not int id )
				continue;
			string name = n.Text( "name" ) ?? "";
			string ext = n.Text( "ext" ) ?? "";

			Dictionary<string, string>? inlineProps = null;
			if( n.GetValueOrDefault( "properties" ) is IReadOnlyDictionary<string, object?> propObj && propObj.Count > 0 )
			{
				inlineProps = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
				foreach( var kv in propObj )
				{
					if( kv.Value is not null && kv.Value is not false )
						inlineProps[ kv.Key ] = Convert.ToString( kv.Value, CultureInfo.InvariantCulture ) ?? "";
				}
			}

			nodes.Add( new GraphNode( id, name, ext, Long( n.GetValueOrDefault( "size" ) ),
				n.Text( "category" ) ?? "", n.Int( "version_id" ) ?? 0, n.Bool( "deleted" ), n.Bool( "checked_out" ), n.Text( "path" ) ?? "" )
			{
				CheckoutUser = n.Text( "checkout_user" ) ?? "",
				TypeName = n.Text( "type" ) ?? "",
				Modified = n.Text( "modified" ) ?? "",
				Versions = n.Int( "versions" ) ?? 0,
				Properties = inlineProps,
			} );
		}
		List<GraphEdge> edges = [];
		foreach( var pair in reply.GetValueOrDefault( "edges" ) as IEnumerable<object?> ?? [] )
			if( pair is IList<object?> { Count: 2 } p && OdooValues.AsInt( p[ 0 ] ) is int parent && OdooValues.AsInt( p[ 1 ] ) is int child )
				edges.Add( new GraphEdge( parent, child ) );
		return new DependencyGraph( OdooValues.Ids( reply.GetValueOrDefault( "roots" ) ), nodes, edges, reply.GetValueOrDefault( "truncated" ) is true );
	}

	// ------------------------------------------------------------------ Multi-Hop FullNetwork Loader

	private static async Task<Result<DependencyGraph>> FetchSingleDependencyGraphAsync(
		IReadOnlyList<int> entryIds,
		GraphDirection direction,
		CancellationToken token )
	{
		if( entryIds.Count == 0 )
			return Result<DependencyGraph>.Success( DependencyGraph.Empty );

		string wireDirection = direction switch
		{
			GraphDirection.Parents => "parents",
			GraphDirection.Both or GraphDirection.FullNetwork => "both",
			_ => "children",
		};

		DependencyGraph merged = DependencyGraph.Empty;
		foreach( int[] chunk in entryIds.Distinct().Chunk( RpcBatchSize ) )
		{
			token.ThrowIfCancellationRequested();
			Result<OdooRow> reply = await OdooRpc.ExecuteAsync<OdooRow>(
				"hp.entry",
				"dependency_graph",
				[ chunk.ToList(), wireDirection ],
				OdooRpc.LongTimeout * 3,
				token );
			if( !reply.Ok )
				return Result<DependencyGraph>.Fail( reply.Error );
			DependencyGraph part = Parse( reply.Value! );
			merged = merged.Nodes.Count == 0 ? part : GraphFilterEngine.MergeGraphs( merged, part, entryIds );
		}

		return Result<DependencyGraph>.Success( merged with { Roots = [ .. entryIds.Where( id => merged.Nodes.Any( n => n.Id == id ) ).Distinct() ] } );
	}

	private static async Task<Result<DependencyGraph>> LoadFullNetworkRawAsync(
		IReadOnlyList<int> entryIds,
		int maxRounds,
		int nodeBudget,
		CancellationToken token )
	{
		List<int> roots = [ .. entryIds.Where( id => id > 0 ).Distinct() ];
		if( roots.Count == 0 )
			return Result<DependencyGraph>.Success( DependencyGraph.Empty );

		HashSet<int> queried = [];
		List<int> frontier = [ .. roots ];
		DependencyGraph accumulated = DependencyGraph.Empty;
		bool truncated = false;

		int rounds = Math.Clamp( maxRounds, 1, 8 );
		for( int round = 0; round < rounds && frontier.Count > 0; round++ )
		{
			token.ThrowIfCancellationRequested();
			List<int> toQuery = [ .. frontier.Where( id => id > 0 && queried.Add( id ) ) ];
			if( toQuery.Count == 0 )
				break;

			if( accumulated.Nodes.Count >= nodeBudget )
			{
				truncated = true;
				break;
			}

			int remainingSlots = Math.Max( 1, nodeBudget - accumulated.Nodes.Count );
			if( toQuery.Count > remainingSlots && round > 0 )
			{
				toQuery = [ .. toQuery.Take( remainingSlots ) ];
				truncated = true;
			}

			Result<DependencyGraph> step = await FetchSingleDependencyGraphAsync( toQuery, GraphDirection.Both, token );
			if( !step.Ok )
			{
				if( round == 0 )
					return step;
				truncated = true;
				break;
			}

			DependencyGraph prev = accumulated;
			accumulated = prev.Nodes.Count == 0
				? step.Value! with { Roots = roots }
				: GraphFilterEngine.MergeGraphs( prev, step.Value!, roots );

			if( step.Value!.Truncated )
				truncated = true;

			// Next frontier: every discovered neighbor not yet queried with "both",
			// so each parent's other children & parents and each child's other parents & children are loaded.
			frontier = [ .. accumulated.Nodes
				.Select( n => n.Id )
				.Where( id => id > 0 && !queried.Contains( id ) ) ];
		}

		return Result<DependencyGraph>.Success( accumulated with { Roots = [ .. roots.Where( r => accumulated.Nodes.Any( n => n.Id == r ) ) ], Truncated = accumulated.Truncated || truncated } );
	}

	private static int ResolveFullNetworkRounds( GraphFilterCriteria criteria )
	{
		if( criteria.MaxDepth > 0 )
			return Math.Clamp( criteria.MaxDepth, 1, 8 );
		if( criteria.MaxChildDepth.HasValue || criteria.MaxParentDepth.HasValue )
		{
			int maxDir = Math.Max( criteria.MaxChildDepth ?? 1, criteria.MaxParentDepth ?? 1 );
			return Math.Clamp( maxDir + 1, 2, 6 );
		}
		// Default FullNetwork = 2 rounds: (1) roots' parents & children, (2) each parent's & child's parents & children.
		return 2;
	}

	// ------------------------------------------------------------------ Multi-Mode Builders

	private static async Task<Result<GraphFilterResult>> BuildFolderStructureAsync(
		GraphBuildRequest request,
		GraphFilterCriteria criteria,
		CancellationToken token )
	{
		DirectoryNode? tree = request.CachedDirectoryTree;
		if( tree is null )
		{
			Result<(DirectoryNode Tree, Dictionary<int, IReadOnlyList<GraphNode>> _)> loaded =
				await LoadSubdirectoryTreeAsync( request.DirectoryId, request.FolderPath, withEntries: false, includeDeleted: criteria.IncludeDeleted, token );
			if( !loaded.Ok || loaded.Value.Tree is null )
				return Result<GraphFilterResult>.Fail( loaded.Error ?? "Could not load directory tree." );
			tree = loaded.Value.Tree;
		}

		string? startPath = request.CachedDirectoryTree is not null ? request.FolderPath : null;
		return Result<GraphFilterResult>.Success( GraphFilterEngine.BuildFolderStructureGraph( tree, startPath, criteria ) );
	}

	private static async Task<Result<GraphFilterResult>> BuildFoldersAndFilesAsync(
		GraphBuildRequest request,
		GraphFilterCriteria criteria,
		CancellationToken token )
	{
		Result<(DirectoryNode Tree, Dictionary<int, IReadOnlyList<GraphNode>> FilesByDir)> loaded =
			await LoadSubdirectoryTreeAsync( request.DirectoryId, request.FolderPath, withEntries: true, includeDeleted: criteria.IncludeDeleted, token );
		if( !loaded.Ok || loaded.Value.Tree is null )
			return Result<GraphFilterResult>.Fail( loaded.Error ?? "Could not load folders and files." );

		(DirectoryNode tree, Dictionary<int, IReadOnlyList<GraphNode>> filesByDir) = loaded.Value;

		// Attach custom properties when filtering by custom property or explicitly requested
		if( request.LoadProperties || criteria.NeedsCustomProperties )
			filesByDir = await AttachPropertiesToFolderMapAsync( filesByDir, criteria.PropertyName, token );

		DependencyGraph? depOverlay = null;
		if( criteria.IncludeDependencyLinks )
		{
			List<int> fileIds = [ .. filesByDir.Values.SelectMany( l => l ).Select( f => f.Id ).Distinct().Take( 150 ) ];
			if( fileIds.Count > 0 )
			{
				Result<DependencyGraph> deps = await FetchSingleDependencyGraphAsync( fileIds, GraphDirection.Children, token );
				if( deps.Ok )
					depOverlay = deps.Value;
			}
		}

		return Result<GraphFilterResult>.Success(
			GraphFilterEngine.BuildFoldersAndFilesGraph( tree, filesByDir, depOverlay, startPath: null, criteria ) );
	}

	private static async Task<Result<GraphFilterResult>> BuildByPropertyAsync(
		GraphBuildRequest request,
		GraphFilterCriteria criteria,
		CancellationToken token )
	{
		List<GraphNode> candidates = [];
		DependencyGraph? depOverlay = null;

		if( request.EntryIds is { Count: > 0 } entryIds )
		{
			// Collect candidate files from the dependency neighborhood of the selected entries
			GraphFilterCriteria loadCriteria = criteria with
			{
				ViewMode = GraphViewMode.Dependencies,
				Extensions = null,
				ExcludeExtensions = null,
				NameFilter = null,
				PathFilter = null,
				Categories = null,
				CheckedOutOnly = null,
				CheckoutUser = null,
				MinSizeBytes = null,
				MaxSizeBytes = null,
				PropertyName = null,
				PropertyValue = null,
			};
			Result<GraphFilterResult> loaded = await LoadFilteredAsync( entryIds, loadCriteria, loadProperties: false, token );
			if( !loaded.Ok )
				return loaded;
			depOverlay = loaded.Value!.Graph;
			candidates.AddRange( depOverlay.Nodes.Where( n => n.Role == GraphNodeRole.File ) );
		}
		else if( request.RecursiveFolders )
		{
			Result<(DirectoryNode _, Dictionary<int, IReadOnlyList<GraphNode>> FilesByDir)> loaded =
				await LoadSubdirectoryTreeAsync( request.DirectoryId, request.FolderPath, withEntries: true, includeDeleted: criteria.IncludeDeleted, token );
			if( !loaded.Ok )
				return Result<GraphFilterResult>.Fail( loaded.Error );
			candidates.AddRange( loaded.Value.FilesByDir.Values.SelectMany( l => l ) );
		}
		else
		{
			int dirId = await ResolveDirectoryIdAsync( request.DirectoryId, request.FolderPath, token );
			Result<IReadOnlyList<RemoteEntryDto>> entries = await HpDirectory.GetEntriesAsync( dirId, criteria.IncludeDeleted );
			if( !entries.Ok )
				return Result<GraphFilterResult>.Fail( entries.Error );
			string folderPrefix = request.FolderPath ?? PwaPaths.Root;
			candidates.AddRange( entries.Value!.Select( e => RemoteEntryToGraphNode( e, folderPrefix ) ) );
		}

		if( candidates.Count == 0 )
			return Result<GraphFilterResult>.Success( GraphFilterResult.Empty );

		if( request.LoadProperties || criteria.NeedsCustomProperties )
		{
			DependencyGraph temp = new( [], candidates, [], false );
			temp = await AttachPropertiesAsync( temp, propertyNameFilter: null, token );
			candidates = [ .. temp.Nodes ];
		}

		if( criteria.IncludeDependencyLinks && depOverlay is null )
		{
			List<int> ids = [ .. candidates.Select( c => c.Id ).Distinct().Take( 150 ) ];
			if( ids.Count > 0 )
			{
				Result<DependencyGraph> deps = await FetchSingleDependencyGraphAsync( ids, GraphDirection.Children, token );
				if( deps.Ok )
					depOverlay = deps.Value;
			}
		}

		return Result<GraphFilterResult>.Success( GraphFilterEngine.BuildPropertyGroupGraph( candidates, depOverlay, criteria ) );
	}

	private static async Task<Result<GraphFilterResult>> BuildSearchFilterAsync(
		GraphBuildRequest request,
		GraphFilterCriteria criteria,
		CancellationToken token )
	{
		OdooDomain domain = OdooDomain.All;
		if( !criteria.IncludeDeleted )
			domain = domain.And( "deleted", false );
		if( criteria.CheckedOutOnly is true )
			domain = domain.And( "is_checked_out", true );
		else if( criteria.CheckedOutOnly is false )
			domain = domain.And( "is_checked_out", false );

		if( !string.IsNullOrWhiteSpace( criteria.NameFilter ) && !criteria.NameFilter.Contains( '*' ) && !criteria.NameFilter.Contains( '?' ) )
			domain = domain.And( "name", "ilike", criteria.NameFilter.Trim() );

		if( !string.IsNullOrWhiteSpace( request.FolderPath ) )
		{
			string normalizedDir = PwaPaths.ToServerDirectory( request.FolderPath ) ?? request.FolderPath.Trim();
			if( normalizedDir.Length > 0 && !string.Equals( normalizedDir, PwaPaths.Root, StringComparison.OrdinalIgnoreCase ) )
				domain = domain.And( "directory_complete_name", "ilike", normalizedDir );
		}
		else if( request.DirectoryId is int dirId and > 0 )
		{
			domain = domain.And( "dir_id", dirId );
		}

		// When filtering by a single exact extension, let Odoo narrow server-side if possible
		IReadOnlyList<string> normalizedExts = GraphFilterEngine.NormalizeExtensions( criteria.Extensions );
		if( normalizedExts.Count == 1 )
			domain = domain.And( "name", "ilike", $".{normalizedExts[ 0 ]}" );

		int fetchLimit = criteria.MaxNodes > 0 ? Math.Clamp( criteria.MaxNodes * 3, 100, 1000 ) : 500;
		Result<IReadOnlyList<OdooRow>> rowsRes = await OdooRpc.SearchReadAsync(
			"hp.entry",
			domain,
			[ "id", "name", "windows_complete_name", "directory_complete_name", "deleted", "is_checked_out", "checkout_user", "latest_version_id", "type_id", "cat_id" ],
			timeoutMs: OdooRpc.LongTimeout,
			token: token );

		if( !rowsRes.Ok )
			return Result<GraphFilterResult>.Fail( rowsRes.Error );

		List<GraphNode> matchedNodes = [];
		foreach( OdooRow row in ( rowsRes.Value ?? [] ).Take( fetchLimit ) )
		{
			if( row.Int( "id" ) is not int id || id <= 0 )
				continue;
			string name = row.Text( "name" ) ?? $"entry_{id}";
			string ext = Path.GetExtension( name ).TrimStart( '.' ).ToLowerInvariant();
			string path = row.Text( "windows_complete_name" ) ?? "";
			if( path.Length == 0 && row.Text( "directory_complete_name" ) is { Length: > 0 } dirComplete )
				path = PwaPaths.ToServerName( [ .. PwaPaths.Segments( dirComplete ), name ] );
			string category = row.RefName( "cat_id" ) ?? "";
			string typeName = row.RefName( "type_id" ) ?? "";
			int versionId = row.RefId( "latest_version_id" ) ?? 0;
			string checkoutUser = row.RefName( "checkout_user" ) ?? "";
			bool checkedOut = row.Bool( "is_checked_out" ) || checkoutUser.Length > 0;
			bool deleted = row.Bool( "deleted" );

			matchedNodes.Add( new GraphNode( id, name, ext, 0, category, versionId, deleted, checkedOut, path )
			{
				CheckoutUser = checkoutUser,
				TypeName = typeName,
			} );
		}

		if( matchedNodes.Count == 0 )
			return Result<GraphFilterResult>.Success( GraphFilterResult.Empty );

		DependencyGraph baseGraph = new( [ .. matchedNodes.Select( n => n.Id ) ], matchedNodes, [], rowsRes.Value!.Count > fetchLimit );

		// Attach custom properties first if filtering by a custom property
		if( request.LoadProperties || criteria.NeedsCustomProperties )
			baseGraph = await AttachPropertiesAsync( baseGraph, criteria.PropertyName, token );

		// Pre-filter candidate roots before optional dependency expansion so we don't expand irrelevant files
		if( criteria.HasAttributeFilters )
		{
			List<GraphNode> matchingSeeds = [ .. baseGraph.Nodes.Where( n => GraphFilterEngine.MatchesNodeAttributes( n, criteria ) ) ];
			baseGraph = new DependencyGraph( [ .. matchingSeeds.Select( n => n.Id ) ], matchingSeeds, [], baseGraph.Truncated );
		}

		if( baseGraph.Nodes.Count == 0 )
			return Result<GraphFilterResult>.Success( GraphFilterResult.Empty );

		// Optionally expand matched entries' dependency links when requested
		if( criteria.IncludeDependencyLinks )
		{
			List<int> seedIds = [ .. baseGraph.Roots.Take( 60 ) ];
			Result<DependencyGraph> deps = criteria.Direction == GraphDirection.FullNetwork
				? await LoadFullNetworkRawAsync( seedIds, ResolveFullNetworkRounds( criteria ), Math.Clamp( criteria.MaxNodes * 3, 250, 1500 ), token )
				: await FetchSingleDependencyGraphAsync( seedIds, criteria.Direction, token );

			if( deps.Ok && deps.Value is { } depGraph )
			{
				baseGraph = GraphFilterEngine.MergeGraphs( depGraph, baseGraph, seedIds );
				if( request.LoadProperties || criteria.NeedsCustomProperties )
					baseGraph = await AttachPropertiesAsync( baseGraph, criteria.PropertyName, token );
			}
		}

		return Result<GraphFilterResult>.Success( GraphFilterEngine.ApplyFilters( baseGraph, criteria ) );
	}

	// ------------------------------------------------------------------ Helpers

	private static async Task<Result<(DirectoryNode Tree, Dictionary<int, IReadOnlyList<GraphNode>> FilesByDir)>> LoadSubdirectoryTreeAsync(
		int? directoryId,
		string? folderPath,
		bool withEntries,
		bool includeDeleted,
		CancellationToken token )
	{
		int dirId = await ResolveDirectoryIdAsync( directoryId, folderPath, token );
		Result<OdooRow> rawTree = await OdooRpc.ExecuteAsync<OdooRow>(
			"hp.directory",
			"get_children_directories_by_id",
			[ dirId, withEntries ],
			OdooRpc.LongTimeout,
			token );

		if( !rawTree.Ok || rawTree.Value is null )
			return Result<(DirectoryNode, Dictionary<int, IReadOnlyList<GraphNode>>)>.Fail( rawTree.Error ?? "Directory tree could not be loaded." );

		SubdirectoryDto dto = SubdirectoryDto.FromRow( rawTree.Value );
		Dictionary<int, IReadOnlyList<GraphNode>> filesByDir = [];
		string rootPrefix = !string.IsNullOrWhiteSpace( folderPath )
			? PwaPaths.ToServerName( PwaPaths.Segments( folderPath ) )
			: dto.Name;
		if( string.IsNullOrWhiteSpace( rootPrefix ) )
			rootPrefix = PwaPaths.Root;

		DirectoryNode Convert( SubdirectoryDto node, string serverPath )
		{
			DirectoryNode dirNode = new( node.Name.Length > 0 ? node.Name : PwaPaths.Root, node.Id );
			if( withEntries && node.Entries.Count > 0 )
			{
				List<GraphNode> files = [ .. node.Entries
					.Where( e => includeDeleted || !e.Deleted )
					.Select( e => RemoteEntryToGraphNode( e, serverPath ) ) ];
				filesByDir[ node.Id ] = files;
			}
			foreach( SubdirectoryDto child in node.Directories )
			{
				string childPath = $"{serverPath}\\{child.Name}";
				dirNode.Children.Add( Convert( child, childPath ) );
			}
			return dirNode;
		}

		DirectoryNode rootNode = Convert( dto, rootPrefix );
		rootNode.SortRecursive();
		return Result<(DirectoryNode, Dictionary<int, IReadOnlyList<GraphNode>>)>.Success( ( rootNode, filesByDir ) );
	}

	private static async Task<int> ResolveDirectoryIdAsync( int? directoryId, string? folderPath, CancellationToken token )
	{
		if( directoryId is int id and > 0 )
			return id;
		if( !string.IsNullOrWhiteSpace( folderPath ) )
		{
			string[] segs = [ PwaPaths.Root, .. PwaPaths.Segments( folderPath ) ];
			Result<LastAvailableDirectory> last = await HpDirectory.LastAvailableDirectory( segs );
			token.ThrowIfCancellationRequested();
			if( last.Ok && last.Value.DirectoryId > 0 )
				return last.Value.DirectoryId;
		}
		return OdooDefaults.Instance.HpDirectoryRoot?.id ?? 1;
	}

	private static GraphNode RemoteEntryToGraphNode( RemoteEntryDto entry, string folderServerPath )
	{
		string ext = Path.GetExtension( entry.Name ).TrimStart( '.' ).ToLowerInvariant();
		string checkoutUser = entry.CheckoutUserId is int uid && OdooDefaults.Instance.IdToUser.TryGetValue( uid, out IHpUserModel? u )
			? u.name ?? ""
			: "";
		string path = !string.IsNullOrWhiteSpace( entry.FullName )
			? entry.FullName!
			: $"{folderServerPath}\\{entry.Name}";

		return new GraphNode(
			Id: entry.Id,
			Name: entry.Name,
			Ext: ext,
			Size: entry.Size,
			Category: entry.Category ?? "",
			VersionId: entry.LatestVersionId ?? 0,
			Deleted: entry.Deleted,
			CheckedOut: entry.CheckoutUserId is > 0,
			Path: path )
		{
			CheckoutUser = checkoutUser,
			TypeName = entry.Type ?? "",
			Modified = entry.LatestDate is DateTime dt ? dt.ToString( "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture ) : "",
		};
	}

	private static async Task<Dictionary<int, IReadOnlyList<GraphNode>>> AttachPropertiesToFolderMapAsync(
		Dictionary<int, IReadOnlyList<GraphNode>> filesByDir,
		string? propertyNameFilter,
		CancellationToken token )
	{
		List<int> versionIds = [ .. filesByDir.Values.SelectMany( l => l ).Where( f => f.VersionId > 0 ).Select( f => f.VersionId ).Distinct() ];
		if( versionIds.Count == 0 )
			return filesByDir;

		Dictionary<int, Dictionary<string, string>> propsByVersion = await FetchPropertiesByVersionIdsAsync( versionIds, propertyNameFilter, token );
		if( propsByVersion.Count == 0 )
			return filesByDir;

		Dictionary<int, IReadOnlyList<GraphNode>> updated = [];
		foreach( var (dirId, list) in filesByDir )
		{
			updated[ dirId ] = [ .. list.Select( n =>
				n.VersionId > 0 && propsByVersion.TryGetValue( n.VersionId, out var props )
					? n with { Properties = props }
					: n ) ];
		}
		return updated;
	}

	private static async Task<Dictionary<int, Dictionary<string, string>>> FetchPropertiesByVersionIdsAsync(
		IReadOnlyList<int> versionIds,
		string? propertyNameFilter,
		CancellationToken token )
	{
		Dictionary<int, Dictionary<string, string>> byVersion = [];
		string[] fields = [ "version_id", "prop_id", "prop_name", "sw_config_name", "text_value", "number_value", "yesno_value", "date_value" ];

		foreach( int[] chunk in versionIds.Chunk( PropertyBatchSize ) )
		{
			token.ThrowIfCancellationRequested();
			OdooDomain domain = OdooDomain.Where( "version_id", "in", chunk );
			if( !string.IsNullOrWhiteSpace( propertyNameFilter ) && !GraphFilterEngine.IsBuiltInField( propertyNameFilter ) )
			{
				if( OdooDefaults.Instance.ExtToProp.TryGetValue( propertyNameFilter.Trim(), out IHpPropertyModel? knownProp ) && knownProp.id is int pid )
					domain = domain.And( "prop_id", pid );
			}

			Result<IReadOnlyList<OdooRow>> res = await OdooRpc.SearchReadAsync(
				"hp.version.property",
				domain,
				fields,
				timeoutMs: OdooRpc.LongTimeout,
				token: token );

			if( !res.Ok || res.Value is not { Count: > 0 } rows )
				continue;

			foreach( OdooRow row in rows )
			{
				if( row.RefId( "version_id" ) is not int vid || vid <= 0 )
					continue;
				string? propName = row.Text( "prop_name" ) ?? row.RefName( "prop_id" );
				if( string.IsNullOrWhiteSpace( propName ) )
					continue;

				string? val = ExtractPropertyValue( row );
				if( val is null )
					continue;

				if( !byVersion.TryGetValue( vid, out Dictionary<string, string>? map ) )
					byVersion[ vid ] = map = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );

				// Prefer default/empty configuration value over config-specific overrides when both exist
				string? cfg = row.Text( "sw_config_name" );
				if( !map.ContainsKey( propName ) || string.IsNullOrWhiteSpace( cfg ) || string.Equals( cfg, "Default", StringComparison.OrdinalIgnoreCase ) )
					map[ propName ] = val;
			}
		}

		return byVersion;
	}

	private static string? ExtractPropertyValue( OdooRow row )
	{
		if( row.Text( "text_value" ) is { Length: > 0 } txt && !string.Equals( txt, "False", StringComparison.OrdinalIgnoreCase ) )
			return txt;
		if( row.Text( "date_value" ) is { Length: > 0 } dt && !string.Equals( dt, "False", StringComparison.OrdinalIgnoreCase ) )
			return dt;
		if( row.GetValueOrDefault( "number_value" ) is int ni && ni != 0 )
			return ni.ToString( CultureInfo.InvariantCulture );
		if( row.GetValueOrDefault( "number_value" ) is long nl && nl != 0 )
			return nl.ToString( CultureInfo.InvariantCulture );
		if( row.GetValueOrDefault( "number_value" ) is double nd && Math.Abs( nd ) > double.Epsilon )
			return nd.ToString( "G", CultureInfo.InvariantCulture );
		if( row.Bool( "yesno_value" ) )
			return "Yes";
		return null;
	}

	private static long Long( object? value ) => value switch
	{
		int i => i,
		long l => l,
		double d => (long)d,
		_ => 0,
	};
}

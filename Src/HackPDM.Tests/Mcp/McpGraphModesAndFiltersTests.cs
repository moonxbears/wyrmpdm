using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using HackPDM.Core.Configuration;
using HackPDM.Core.Graph;
using HackPDM.Core.Hack;
using HackPDM.Infrastructure.Odoo;
using HackPDM.Infrastructure.Operations;
using HackPDM.Mcp.Bridge;
using HackPDM.Mcp.Protocol;
using HackPDM.Mcp.Services;
using HackPDM.Mcp.Tools;

using Xunit;

namespace HackPDM.Tests.Mcp;

[Collection( "OdooMockRpc" )]
public sealed class McpGraphModesAndFiltersTests
{
	private static GraphNode MakeFile(
		int id,
		string name,
		string ext,
		long size = 50_000,
		string category = "CAD",
		bool checkedOut = false,
		string checkoutUser = "",
		bool deleted = false,
		string? path = null,
		IReadOnlyDictionary<string, string>? props = null )
		=> new( id, name, ext, size, category, id * 10, deleted, checkedOut, path ?? $@"root\Projects\{name}" )
		{
			CheckoutUser = checkoutUser,
			Properties = props,
		};

	// ------------------------------------------------------------------ Depth & Offspring/Parent Branch Limits

	[Fact]
	public void ApplyFilters_FullNetwork_DepthLimit_ControlsExpansionHops()
	{
		// GrandTop(10) -> Top(1) -> Sub(2, Root) -> Bolt(3) -> Washer(4)
		// Top(1) -> Sibling(5)
		// CoGrand(11) -> CoParent(6) -> Bolt(3)
		DependencyGraph full = new(
			[ 2 ],
			[
				MakeFile( 10, "GrandTop.sldasm", "sldasm" ),
				MakeFile( 1, "Top.sldasm", "sldasm" ),
				MakeFile( 2, "Sub.sldasm", "sldasm" ),
				MakeFile( 3, "Bolt.sldprt", "sldprt" ),
				MakeFile( 4, "Washer.sldprt", "sldprt" ),
				MakeFile( 5, "Sibling.sldprt", "sldprt" ),
				MakeFile( 6, "CoParent.sldasm", "sldasm" ),
				MakeFile( 11, "CoGrand.sldasm", "sldasm" ),
			],
			[
				new( 10, 1 ), new( 1, 2 ), new( 1, 5 ),
				new( 2, 3 ), new( 3, 4 ), new( 6, 3 ), new( 11, 6 ),
			],
			false );

		// Depth 1 from Sub(2): only Top(1), Sub(2), Bolt(3)
		GraphFilterResult d1 = GraphFilterEngine.ApplyFilters( full, new GraphFilterCriteria
		{
			Direction = GraphDirection.FullNetwork,
			MaxDepth = 1,
		} );
		Assert.Equal( [ 1, 2, 3 ], d1.Graph.Nodes.Select( n => n.Id ).Order().ToArray() );
		Assert.True( d1.Graph.Truncated );

		// Depth 2 from Sub(2): includes GrandTop(10), Sibling(5), CoParent(6), Washer(4), excludes CoGrand(11) at hop 3
		GraphFilterResult d2 = GraphFilterEngine.ApplyFilters( full, new GraphFilterCriteria
		{
			Direction = GraphDirection.FullNetwork,
			MaxDepth = 2,
		} );
		Assert.Equal( [ 1, 2, 3, 4, 5, 6, 10 ], d2.Graph.Nodes.Select( n => n.Id ).Order().ToArray() );
		Assert.DoesNotContain( d2.Graph.Nodes, n => n.Id == 11 );
	}

	[Fact]
	public void ApplyFilters_SeparateChildDepthAndParentDepth_WorkIndependently()
	{
		// GrandTop(10) -> Top(1) -> Sub(2, Root) -> Bolt(3) -> Washer(4)
		DependencyGraph chain = new(
			[ 2 ],
			[
				MakeFile( 10, "GrandTop.sldasm", "sldasm" ),
				MakeFile( 1, "Top.sldasm", "sldasm" ),
				MakeFile( 2, "Sub.sldasm", "sldasm" ),
				MakeFile( 3, "Bolt.sldprt", "sldprt" ),
				MakeFile( 4, "Washer.sldprt", "sldprt" ),
			],
			[ new( 10, 1 ), new( 1, 2 ), new( 2, 3 ), new( 3, 4 ) ],
			false );

		// 1 parent level up, 2 child levels down
		GraphFilterResult asym = GraphFilterEngine.ApplyFilters( chain, new GraphFilterCriteria
		{
			Direction = GraphDirection.Both,
			MaxParentDepth = 1,
			MaxChildDepth = 2,
		} );
		Assert.Equal( [ 1, 2, 3, 4 ], asym.Graph.Nodes.Select( n => n.Id ).Order().ToArray() );
		Assert.Equal( 1, asym.Graph.Nodes.Single( n => n.Id == 1 ).OmittedParentBranches );

		// 0 parent depth, 2 child depth -> strictly offspring only
		GraphFilterResult kidsOnly = GraphFilterEngine.ApplyFilters( chain, new GraphFilterCriteria
		{
			Direction = GraphDirection.FullNetwork,
			MaxParentDepth = 0,
			MaxChildDepth = 2,
		} );
		Assert.Equal( [ 2, 3, 4 ], kidsOnly.Graph.Nodes.Select( n => n.Id ).Order().ToArray() );

		// 2 parent depth, 0 child depth -> strictly parents only
		GraphFilterResult parentsOnly = GraphFilterEngine.ApplyFilters( chain, new GraphFilterCriteria
		{
			Direction = GraphDirection.FullNetwork,
			MaxParentDepth = 2,
			MaxChildDepth = 0,
		} );
		Assert.Equal( [ 1, 2, 10 ], parentsOnly.Graph.Nodes.Select( n => n.Id ).Order().ToArray() );
	}

	[Fact]
	public void ApplyFilters_MaxChildBranchesAndMaxParentBranches_CapEachNodeIndependently()
	{
		// Top(1) has 5 children: Sub(2, Root), SibA(11), SibB(12), SibC(13), SibD(14)
		// Bolt(3) has 5 parents: Sub(2, Root), CoA(21), CoB(22), CoC(23), CoD(24)
		DependencyGraph fan = new(
			[ 2 ],
			[
				MakeFile( 1, "Top.sldasm", "sldasm" ),
				MakeFile( 2, "Sub.sldasm", "sldasm" ),
				MakeFile( 3, "Bolt.sldprt", "sldprt" ),
				MakeFile( 11, "SibA.sldprt", "sldprt" ),
				MakeFile( 12, "SibB.sldprt", "sldprt" ),
				MakeFile( 13, "SibC.sldprt", "sldprt" ),
				MakeFile( 14, "SibD.sldprt", "sldprt" ),
				MakeFile( 21, "CoA.sldasm", "sldasm" ),
				MakeFile( 22, "CoB.sldasm", "sldasm" ),
				MakeFile( 23, "CoC.sldasm", "sldasm" ),
				MakeFile( 24, "CoD.sldasm", "sldasm" ),
			],
			[
				new( 1, 2 ), new( 1, 11 ), new( 1, 12 ), new( 1, 13 ), new( 1, 14 ),
				new( 2, 3 ), new( 21, 3 ), new( 22, 3 ), new( 23, 3 ), new( 24, 3 ),
			],
			false );

		GraphFilterResult capped = GraphFilterEngine.ApplyFilters( fan, new GraphFilterCriteria
		{
			Direction = GraphDirection.FullNetwork,
			MaxChildBranches = 2,
			MaxParentBranches = 3,
		} );

		// Top(1) keeps Sub(2) + 1 sibling (SibA) = 2 outgoing, omitting 3 siblings
		Assert.Equal( 2, capped.Graph.Edges.Count( e => e.Parent == 1 ) );
		Assert.Contains( capped.Graph.Edges, e => e.Parent == 1 && e.Child == 2 );
		Assert.Equal( 3, capped.Graph.Nodes.Single( n => n.Id == 1 ).OmittedChildBranches );
		Assert.Equal( 3, capped.Stats.EdgesTrimmedByChildBranchLimit );

		// Bolt(3) keeps Sub(2) + 2 co-parents (CoA, CoB) = 3 incoming, omitting 2 co-parents
		Assert.Equal( 3, capped.Graph.Edges.Count( e => e.Child == 3 ) );
		Assert.Contains( capped.Graph.Edges, e => e.Parent == 2 && e.Child == 3 );
		Assert.Equal( 2, capped.Graph.Nodes.Single( n => n.Id == 3 ).OmittedParentBranches );
		Assert.Equal( 2, capped.Stats.EdgesTrimmedByParentBranchLimit );
		Assert.True( capped.Graph.Truncated );
	}

	[Fact]
	public void ApplyFilters_ZeroChildBranches_StopsOffspringWhileAllowingParents()
	{
		DependencyGraph fan = new(
			[ 2 ],
			[
				MakeFile( 1, "Top.sldasm", "sldasm" ),
				MakeFile( 2, "Sub.sldasm", "sldasm" ),
				MakeFile( 3, "Bolt.sldprt", "sldprt" ),
				MakeFile( 11, "Sibling.sldprt", "sldprt" ),
			],
			[ new( 1, 2 ), new( 1, 11 ), new( 2, 3 ) ],
			false );

		GraphFilterResult res = GraphFilterEngine.ApplyFilters( fan, new GraphFilterCriteria
		{
			Direction = GraphDirection.FullNetwork,
			MaxChildBranches = 0,
			MaxParentBranches = 2,
		} );

		Assert.Equal( [ 1, 2 ], res.Graph.Nodes.Select( n => n.Id ).Order().ToArray() );
		Assert.Single( res.Graph.Edges );
		Assert.Equal( new GraphEdge( 1, 2 ), res.Graph.Edges[ 0 ] );
	}

	// ------------------------------------------------------------------ File Type, Wildcard, Property Filters & Edge Bridging

	[Fact]
	public void ApplyFilters_SldprtOnly_BridgesHiddenSubAssemblies()
	{
		// Top.sldasm(1) -> Sub.sldasm(2) -> Bracket.sldprt(3) -> Insert.sldprt(4)
		// Top.sldasm(1) -> Drawing.slddrw(5)
		DependencyGraph asm = new(
			[ 1 ],
			[
				MakeFile( 1, "Top.sldasm", "sldasm" ),
				MakeFile( 2, "Sub.sldasm", "sldasm" ),
				MakeFile( 3, "Bracket.sldprt", "sldprt" ),
				MakeFile( 4, "Insert.sldprt", "sldprt" ),
				MakeFile( 5, "Drawing.slddrw", "slddrw" ),
			],
			[ new( 1, 2 ), new( 2, 3 ), new( 3, 4 ), new( 1, 5 ) ],
			false );

		// KeepRoots=true + BridgeFilteredEdges=true: Top(1) bridges directly to Bracket(3), skipping hidden Sub(2)
		GraphFilterResult bridged = GraphFilterEngine.ApplyFilters( asm, new GraphFilterCriteria
		{
			Direction = GraphDirection.Children,
			Extensions = [ "sldprt" ],
			KeepRoots = true,
			BridgeFilteredEdges = true,
		} );

		Assert.Equal( [ 1, 3, 4 ], bridged.Graph.Nodes.Select( n => n.Id ).Order().ToArray() );
		Assert.Contains( new GraphEdge( 1, 3 ), bridged.Graph.Edges );
		Assert.Contains( new GraphEdge( 3, 4 ), bridged.Graph.Edges );
		Assert.Equal( 1, bridged.Stats.BridgedEdgesAdded );

		// KeepRoots=false: strictly SLDPRT nodes only!
		GraphFilterResult strictParts = GraphFilterEngine.ApplyFilters( asm, new GraphFilterCriteria
		{
			Direction = GraphDirection.Children,
			Extensions = [ "part" ], // alias for sldprt
			KeepRoots = false,
			BridgeFilteredEdges = true,
		} );
		Assert.Equal( [ 3, 4 ], strictParts.Graph.Nodes.Select( n => n.Id ).Order().ToArray() );
		Assert.All( strictParts.Graph.Nodes, n => Assert.Equal( "sldprt", n.Ext ) );
	}

	[Fact]
	public void ApplyFilters_WildcardName_CheckedOut_AndCustomPropertyPredicates()
	{
		DependencyGraph g = new(
			[ 1 ],
			[
				MakeFile( 1, "Drive_Asm.sldasm", "sldasm", size: 500_000, checkedOut: true, checkoutUser: "Alice",
					path: @"root\Drive\Drive_Asm.sldasm",
					props: new Dictionary<string, string> { [ "Material" ] = "Assembly", [ "MassKg" ] = "12.5" } ),
				MakeFile( 2, "Mount_Bracket_L.sldprt", "sldprt", size: 120_000, checkedOut: true, checkoutUser: "Alice",
					path: @"root\Drive\Brackets\Mount_Bracket_L.sldprt",
					props: new Dictionary<string, string> { [ "Material" ] = "6061-T6 Aluminum", [ "MassKg" ] = "1.4" } ),
				MakeFile( 3, "Shaft_Main.sldprt", "sldprt", size: 80_000, checkedOut: false,
					path: @"root\Drive\Shafts\Shaft_Main.sldprt",
					props: new Dictionary<string, string> { [ "Material" ] = "4140 Steel", [ "MassKg" ] = "3.8" } ),
			],
			[ new( 1, 2 ), new( 1, 3 ) ],
			false );

		// Wildcard + CheckedOut + Material filter
		GraphFilterResult filtered = GraphFilterEngine.ApplyFilters( g, new GraphFilterCriteria
		{
			Direction = GraphDirection.Children,
			NameFilter = "*Bracket*",
			CheckedOutOnly = true,
			PropertyName = "Material",
			PropertyOperator = "contains",
			PropertyValue = "6061",
			KeepRoots = false,
		} );
		Assert.Single( filtered.Graph.Nodes );
		Assert.Equal( 2, filtered.Graph.Nodes[ 0 ].Id );

		// Numeric property comparison (> 2.0 kg)
		GraphFilterResult heavy = GraphFilterEngine.ApplyFilters( g, new GraphFilterCriteria
		{
			Direction = GraphDirection.Children,
			PropertyName = "MassKg",
			PropertyOperator = ">",
			PropertyValue = "2.0",
			KeepRoots = false,
		} );
		Assert.Equal( [ 1, 3 ], heavy.Graph.Nodes.Select( n => n.Id ).Order().ToArray() );
	}

	// ------------------------------------------------------------------ FolderStructure, FoldersAndFiles & ByProperty Builders

	[Fact]
	public void BuildFolderStructureGraph_RespectsDepthAndMaxChildBranches()
	{
		DirectoryNode root = new( "root", 1 );
		DirectoryNode proj = new( "Projects", 2 );
		proj.Children.Add( new DirectoryNode( "Alpha", 10 ) );
		proj.Children.Add( new DirectoryNode( "Beta", 11 ) );
		proj.Children.Add( new DirectoryNode( "Gamma", 12 ) );
		proj.Children[ 0 ].Children.Add( new DirectoryNode( "DeepLeaf", 20 ) );
		root.Children.Add( proj );
		root.Children.Add( new DirectoryNode( "Library", 3 ) );

		GraphFilterResult built = GraphFilterEngine.BuildFolderStructureGraph( root, "root", new GraphFilterCriteria
		{
			ViewMode = GraphViewMode.FolderStructure,
			MaxDepth = 2,
			MaxChildBranches = 2,
		} );

		// root -> Library, Projects; Projects -> Alpha, Beta (Gamma omitted by MaxChildBranches=2; DeepLeaf omitted by MaxDepth=2)
		Assert.All( built.Graph.Nodes, n => Assert.Equal( GraphNodeRole.Folder, n.Role ) );
		Assert.Contains( built.Graph.Nodes, n => n.Name == "Alpha" );
		Assert.Contains( built.Graph.Nodes, n => n.Name == "Beta" );
		Assert.DoesNotContain( built.Graph.Nodes, n => n.Name == "Gamma" );
		Assert.DoesNotContain( built.Graph.Nodes, n => n.Name == "DeepLeaf" );
		Assert.Equal( 1, built.Graph.Nodes.Single( n => n.Name == "Projects" ).OmittedChildBranches );
		Assert.True( built.Graph.Truncated );
	}

	[Fact]
	public void BuildFoldersAndFilesGraph_FiltersByTypeAndOverlaysDependencyLinks()
	{
		DirectoryNode root = new( "root", 1 );
		DirectoryNode drive = new( "Drive", 2 );
		root.Children.Add( drive );

		GraphNode asm = MakeFile( 100, "Drive.sldasm", "sldasm", path: @"root\Drive\Drive.sldasm" );
		GraphNode prt1 = MakeFile( 101, "Gear.sldprt", "sldprt", path: @"root\Drive\Gear.sldprt" );
		GraphNode prt2 = MakeFile( 102, "Pin.sldprt", "sldprt", path: @"root\Drive\Pin.sldprt" );
		GraphNode drw = MakeFile( 103, "Drive.slddrw", "slddrw", path: @"root\Drive\Drive.slddrw" );

		Dictionary<int, IReadOnlyList<GraphNode>> filesByDir = new()
		{
			[ 2 ] = [ asm, prt1, prt2, drw ],
		};
		DependencyGraph deps = new( [ 100 ], [ asm, prt1, prt2 ], [ new( 100, 101 ), new( 101, 102 ) ], false );

		GraphFilterResult res = GraphFilterEngine.BuildFoldersAndFilesGraph( root, filesByDir, deps, "root", new GraphFilterCriteria
		{
			ViewMode = GraphViewMode.FoldersAndFiles,
			Extensions = [ "sldprt" ],
			IncludeDependencyLinks = true,
		} );

		// Only SLDPRT files plus their parent folders, AND Gear -> Pin dependency link overlaid!
		List<GraphNode> fileNodes = [ .. res.Graph.Nodes.Where( n => n.Role == GraphNodeRole.File ) ];
		Assert.Equal( [ 101, 102 ], fileNodes.Select( f => f.Id ).Order().ToArray() );
		Assert.Contains( new GraphEdge( 101, 102 ), res.Graph.Edges );
		Assert.Contains( res.Graph.Nodes, n => n.Role == GraphNodeRole.Folder && n.Name == "Drive" );
	}

	[Fact]
	public void BuildPropertyGroupGraph_ClustersByCustomPropertyAndCapsGroupBranches()
	{
		List<GraphNode> files =
		[
			MakeFile( 1, "A.sldprt", "sldprt", props: new Dictionary<string, string> { [ "Material" ] = "6061-T6" } ),
			MakeFile( 2, "B.sldprt", "sldprt", props: new Dictionary<string, string> { [ "Material" ] = "6061-T6" } ),
			MakeFile( 3, "C.sldprt", "sldprt", props: new Dictionary<string, string> { [ "Material" ] = "6061-T6" } ),
			MakeFile( 4, "D.sldprt", "sldprt", props: new Dictionary<string, string> { [ "Material" ] = "304 Stainless" } ),
		];

		GraphFilterResult grouped = GraphFilterEngine.BuildPropertyGroupGraph( files, null, new GraphFilterCriteria
		{
			ViewMode = GraphViewMode.ByProperty,
			GroupByProperty = "Material",
			MaxChildBranches = 2,
		} );

		List<GraphNode> hubs = [ .. grouped.Graph.Nodes.Where( n => n.Role == GraphNodeRole.PropertyGroup ) ];
		Assert.Equal( 2, hubs.Count );
		GraphNode aluHub = hubs.Single( h => h.Name == "Material: 6061-T6" );
		Assert.Equal( 3, aluHub.ItemCount );
		Assert.Equal( 1, aluHub.OmittedChildBranches );
		Assert.Equal( 2, grouped.Graph.Edges.Count( e => e.Parent == aluHub.Id ) );
		Assert.True( grouped.Graph.Truncated );
	}

	// ------------------------------------------------------------------ End-to-End Mock Odoo Server + MCP Server

	[Fact]
	public async Task McpTool_DependencyGraphLoad_FullNetwork_Filters_And_AllViewModes_EndToEnd()
	{
		await using LoopbackGraphOdooServer mock = new();
		mock.Start();

		// Configure Odoo session pointing to loopback mock
		CoreSettings settings = HackPdmApiService.EnsureHeadlessDefaultsInitialized();
		OdooDefaults.Instance.OdooAddress = "127.0.0.1";
		OdooDefaults.Instance.OdooPort = mock.Port.ToString();
		OdooDefaults.Instance.OdooUseHttps = false;
		OdooDefaults.Instance.OdooUrl = $"http://127.0.0.1:{mock.Port}/jsonrpc";
		OdooDefaults.Instance.OdooDb = "test_graph_db";
		OdooDefaults.Instance.OdooUser = "engineer";
		OdooDefaults.Instance.OdooPass = "secret";
		OdooDefaults.Instance.OdooId = 42;

		HackPdmApiService api = new( settingsStore: settings );
		McpServer server = new( new McpServerInfo( "hackpdm-test", "1.0.0" ) );
		DirectoryAndEntryTools.Register( server, api );

		// 1. FullNetwork around Sub(2): must automatically fetch GrandTop(10), Sibling(5), CoParent(6), Washer(4) via multi-round RPC!
		JsonElement fullNet = await CallToolJsonAsync( server, "dependency_graph_load", new
		{
			entryIds = new[] { 2 },
			viewMode = "Dependencies",
			direction = "FullNetwork",
			maxDepth = 2,
			maxChildBranches = 2,
			maxParentBranches = 2,
			includeLayout = true,
		} );
		Assert.Equal( "FullNetwork", fullNet.GetProperty( "direction" ).GetString() );
		List<int> fullIds = [ .. fullNet.GetProperty( "nodes" ).EnumerateArray().Select( n => n.GetProperty( "id" ).GetInt32() ) ];
		Assert.Contains( 1, fullIds );  // Top (parent)
		Assert.Contains( 2, fullIds );  // Sub (root)
		Assert.Contains( 3, fullIds );  // Bolt (child)
		Assert.Contains( 5, fullIds );  // Sibling (parent's other child!)
		Assert.Contains( 6, fullIds );  // CoParent (child's other parent!)
		Assert.True( fullNet.GetProperty( "layout" ).GetProperty( "nodes" ).GetArrayLength() == fullIds.Count );

		// 2. SLDPRT-only filter with bridged links
		JsonElement sldprtOnly = await CallToolJsonAsync( server, "dependency_graph_load", new
		{
			entryIds = new[] { 1 },
			direction = "children",
			fileTypes = new[] { "sldprt" },
			keepRoots = false,
			bridgeFilteredEdges = true,
		} );
		Assert.All( sldprtOnly.GetProperty( "nodes" ).EnumerateArray(),
			n => Assert.Equal( "sldprt", n.GetProperty( "ext" ).GetString() ) );

		// 3. FolderStructure mode
		JsonElement folders = await CallToolJsonAsync( server, "dependency_graph_load", new
		{
			viewMode = "FolderStructure",
			maxDepth = 2,
		} );
		Assert.Equal( "FolderStructure", folders.GetProperty( "viewMode" ).GetString() );
		Assert.True( folders.GetProperty( "nodeCount" ).GetInt32() >= 3 );
		Assert.All( folders.GetProperty( "nodes" ).EnumerateArray(),
			n => Assert.Equal( "Folder", n.GetProperty( "role" ).GetString() ) );

		// 4. FoldersAndFiles mode filtered to sldprt only
		JsonElement foldersAndFiles = await CallToolJsonAsync( server, "dependency_graph_load", new
		{
			viewMode = "FoldersAndFiles",
			extensions = new[] { "sldprt" },
		} );
		Assert.Equal( "FoldersAndFiles", foldersAndFiles.GetProperty( "viewMode" ).GetString() );
		var ffNodes = foldersAndFiles.GetProperty( "nodes" ).EnumerateArray().ToList();
		Assert.Contains( ffNodes, n => n.GetProperty( "role" ).GetString() == "Folder" );
		Assert.Contains( ffNodes, n => n.GetProperty( "role" ).GetString() == "File" && n.GetProperty( "ext" ).GetString() == "sldprt" );
		Assert.DoesNotContain( ffNodes, n => n.GetProperty( "role" ).GetString() == "File" && n.GetProperty( "ext" ).GetString() == "sldasm" );

		// 5. ByProperty mode clustering by custom SolidWorks property "Material"
		JsonElement byMaterial = await CallToolJsonAsync( server, "dependency_graph_load", new
		{
			viewMode = "ByProperty",
			groupByProperty = "Material",
			includeDependencyLinks = true,
		} );
		Assert.Equal( "ByProperty", byMaterial.GetProperty( "viewMode" ).GetString() );
		var matNodes = byMaterial.GetProperty( "nodes" ).EnumerateArray().ToList();
		Assert.Contains( matNodes, n => n.GetProperty( "role" ).GetString() == "PropertyGroup"
			&& n.GetProperty( "name" ).GetString()!.Contains( "6061-T6", StringComparison.OrdinalIgnoreCase ) );

		// 6. SearchFilter mode
		JsonElement searchGraph = await CallToolJsonAsync( server, "dependency_graph_load", new
		{
			viewMode = "SearchFilter",
			extensions = new[] { "sldprt" },
			nameFilter = "Bolt",
			includeDependencyLinks = true,
			direction = "parents",
		} );
		Assert.Equal( "SearchFilter", searchGraph.GetProperty( "viewMode" ).GetString() );
		Assert.Contains( searchGraph.GetProperty( "nodes" ).EnumerateArray(),
			n => n.GetProperty( "name" ).GetString() == "Bolt.sldprt" );
	}

	private static async Task<JsonElement> CallToolJsonAsync( McpServer server, string toolName, object args )
	{
		using JsonDocument argsDoc = JsonDocument.Parse( JsonSerializer.Serialize( args ) );
		McpToolResult res = await server.CallToolAsync( toolName, argsDoc.RootElement.Clone(), CancellationToken.None );
		string text = res.Content[ 0 ].Text!;
		Assert.True( res.IsError != true, $"Tool '{toolName}' failed: {text}" );
		using JsonDocument payload = JsonDocument.Parse( text );
		return payload.RootElement.Clone();
	}

	/// <summary>Minimal loopback Odoo JSON-RPC mock serving dependency_graph, directory tree, hp.entry search, and hp.version.property.</summary>
	private sealed class LoopbackGraphOdooServer : IAsyncDisposable
	{
		private HttpListener? _listener;
		private readonly CancellationTokenSource _cts = new();
		private Task? _loop;

		// Topology:
		// GrandTop(10) -> Top(1) -> Sub(2) -> Bolt(3) -> Washer(4)
		// Top(1) -> Sibling(5)
		// CoParent(6) -> Bolt(3)
		private static readonly Dictionary<int, (string Name, string Ext, string Path, string Cat, int Ver, string Mat)> Entries = new()
		{
			[ 10 ] = ( "GrandTop.sldasm", "sldasm", @"root\Assemblies\GrandTop.sldasm", "Assemblies", 100, "Assembly" ),
			[ 1 ] = ( "Top.sldasm", "sldasm", @"root\Assemblies\Top.sldasm", "Assemblies", 101, "Assembly" ),
			[ 2 ] = ( "Sub.sldasm", "sldasm", @"root\Assemblies\Sub.sldasm", "Assemblies", 102, "Assembly" ),
			[ 3 ] = ( "Bolt.sldprt", "sldprt", @"root\Hardware\Bolt.sldprt", "Fasteners", 103, "304 Stainless" ),
			[ 4 ] = ( "Washer.sldprt", "sldprt", @"root\Hardware\Washer.sldprt", "Fasteners", 104, "304 Stainless" ),
			[ 5 ] = ( "Sibling.sldprt", "sldprt", @"root\Parts\Sibling.sldprt", "Parts", 105, "6061-T6 Aluminum" ),
			[ 6 ] = ( "CoParent.sldasm", "sldasm", @"root\Assemblies\CoParent.sldasm", "Assemblies", 106, "Assembly" ),
		};

		private static readonly (int Parent, int Child)[] AllEdges =
		[
			( 10, 1 ), ( 1, 2 ), ( 1, 5 ), ( 2, 3 ), ( 3, 4 ), ( 6, 3 ),
		];

		public int Port { get; private set; }

		public void Start()
		{
			using( TcpListener probe = new( IPAddress.Loopback, 0 ) )
			{
				probe.Start();
				Port = ( (IPEndPoint)probe.LocalEndpoint ).Port;
			}
			_listener = new HttpListener();
			_listener.Prefixes.Add( $"http://127.0.0.1:{Port}/" );
			_listener.Start();
			_loop = Task.Run( AcceptLoopAsync );
		}

		private async Task AcceptLoopAsync()
		{
			while( !_cts.IsCancellationRequested && _listener is { IsListening: true } )
			{
				HttpListenerContext ctx;
				try { ctx = await _listener.GetContextAsync(); }
				catch { break; }
				_ = HandleContextAsync( ctx );
			}
		}

		private async Task HandleContextAsync( HttpListenerContext ctx )
		{
			try
			{
				using System.IO.StreamReader sr = new( ctx.Request.InputStream, Encoding.UTF8 );
				string body = await sr.ReadToEndAsync( _cts.Token );
				object result = Dispatch( body );
				string respJson = JsonSerializer.Serialize( new { jsonrpc = "2.0", id = "1", result } );
				byte[] respBytes = Encoding.UTF8.GetBytes( respJson );
				ctx.Response.StatusCode = 200;
				ctx.Response.ContentType = "application/json";
				ctx.Response.ContentLength64 = respBytes.Length;
				await ctx.Response.OutputStream.WriteAsync( respBytes, _cts.Token );
				ctx.Response.OutputStream.Close();
			}
			catch( Exception ex )
			{
				try
				{
					byte[] err = Encoding.UTF8.GetBytes( JsonSerializer.Serialize( new { jsonrpc = "2.0", id = "1", error = new { code = -32000, message = ex.Message } } ) );
					ctx.Response.StatusCode = 200;
					ctx.Response.ContentType = "application/json";
					ctx.Response.ContentLength64 = err.Length;
					await ctx.Response.OutputStream.WriteAsync( err );
					ctx.Response.OutputStream.Close();
				}
				catch { }
			}
		}

		private static object Dispatch( string json )
		{
			using JsonDocument doc = JsonDocument.Parse( json );
			JsonElement rpcArgs = doc.RootElement.GetProperty( "params" ).GetProperty( "args" );
			string model = rpcArgs[ 3 ].GetString()!;
			string method = rpcArgs[ 4 ].GetString()!;

			if( model == "hp.entry" && method == "dependency_graph" )
			{
				List<int> ids = [ .. rpcArgs[ 5 ].EnumerateArray().Select( e => e.GetInt32() ) ];
				string dir = rpcArgs[ 6 ].GetString() ?? "children";
				return BuildDepGraphReply( ids, dir );
			}
			if( model == "hp.directory" && method == "get_children_directories_by_id" )
			{
				return BuildTreeReply();
			}
			if( model == "hp.version.property" && method == "search_read" )
			{
				return Entries.Select( kv => new Dictionary<string, object?>
				{
					[ "id" ] = kv.Key * 100,
					[ "version_id" ] = new object[] { kv.Value.Ver, kv.Value.Name },
					[ "prop_id" ] = new object[] { 1, "Material" },
					[ "prop_name" ] = "Material",
					[ "sw_config_name" ] = "Default",
					[ "text_value" ] = kv.Value.Mat,
				} ).ToList();
			}
			if( model == "hp.entry" && method == "search_read" )
			{
				return Entries.Select( kv => new Dictionary<string, object?>
				{
					[ "id" ] = kv.Key,
					[ "name" ] = kv.Value.Name,
					[ "windows_complete_name" ] = kv.Value.Path,
					[ "directory_complete_name" ] = "root / " + kv.Value.Path.Split( '\\' )[ 1 ],
					[ "deleted" ] = false,
					[ "is_checked_out" ] = false,
					[ "latest_version_id" ] = new object[] { kv.Value.Ver, kv.Value.Name },
					[ "cat_id" ] = new object[] { 1, kv.Value.Cat },
					[ "type_id" ] = new object[] { 1, kv.Value.Ext.ToUpperInvariant() },
				} ).ToList();
			}
			return Array.Empty<object>();
		}

		private static object BuildDepGraphReply( List<int> seeds, string dir )
		{
			HashSet<int> visited = [ .. seeds.Where( Entries.ContainsKey ) ];
			if( dir is "children" or "both" )
			{
				Queue<int> q = new( visited );
				while( q.TryDequeue( out int u ) )
					foreach( var (p, c) in AllEdges )
						if( p == u && visited.Add( c ) )
							q.Enqueue( c );
			}
			if( dir is "parents" or "both" )
			{
				Queue<int> q = new( seeds.Where( Entries.ContainsKey ) );
				HashSet<int> seenUp = [ .. q ];
				while( q.TryDequeue( out int u ) )
					foreach( var (p, c) in AllEdges )
						if( c == u && seenUp.Add( p ) )
						{
							visited.Add( p );
							q.Enqueue( p );
						}
			}

			var nodes = visited.Select( id =>
			{
				var e = Entries[ id ];
				return new Dictionary<string, object?>
				{
					[ "id" ] = id,
					[ "name" ] = e.Name,
					[ "ext" ] = e.Ext,
					[ "size" ] = 64_000,
					[ "category" ] = e.Cat,
					[ "version_id" ] = e.Ver,
					[ "deleted" ] = false,
					[ "checked_out" ] = false,
					[ "path" ] = e.Path,
				};
			} ).ToList();
			var edges = AllEdges
				.Where( e => visited.Contains( e.Parent ) && visited.Contains( e.Child ) )
				.Select( e => new[] { e.Parent, e.Child } )
				.ToList();

			return new Dictionary<string, object?>
			{
				[ "roots" ] = seeds,
				[ "nodes" ] = nodes,
				[ "edges" ] = edges,
				[ "truncated" ] = false,
			};
		}

		private static object BuildTreeReply()
		{
			Dictionary<string, object?> EntryMap( params int[] ids ) => ids.ToDictionary(
				id => Entries[ id ].Name,
				id => (object?)new Dictionary<string, object?>
				{
					[ "id" ] = id,
					[ "type" ] = Entries[ id ].Ext.ToUpperInvariant(),
					[ "category" ] = Entries[ id ].Cat,
					[ "fullname" ] = Entries[ id ].Path,
					[ "size" ] = 64_000,
					[ "latest" ] = Entries[ id ].Ver,
					[ "deleted" ] = false,
				} );

			return new Dictionary<string, object?>
			{
				[ "id" ] = 1,
				[ "name" ] = "root",
				[ "entries" ] = new Dictionary<string, object?>(),
				[ "directories" ] = new Dictionary<string, object?>
				{
					[ "Assemblies" ] = new Dictionary<string, object?>
					{
						[ "id" ] = 2,
						[ "name" ] = "Assemblies",
						[ "directories" ] = new Dictionary<string, object?>(),
						[ "entries" ] = EntryMap( 10, 1, 2, 6 ),
					},
					[ "Parts" ] = new Dictionary<string, object?>
					{
						[ "id" ] = 3,
						[ "name" ] = "Parts",
						[ "directories" ] = new Dictionary<string, object?>(),
						[ "entries" ] = EntryMap( 5 ),
					},
					[ "Hardware" ] = new Dictionary<string, object?>
					{
						[ "id" ] = 4,
						[ "name" ] = "Hardware",
						[ "directories" ] = new Dictionary<string, object?>(),
						[ "entries" ] = EntryMap( 3, 4 ),
					},
				},
			};
		}

		public async ValueTask DisposeAsync()
		{
			_cts.Cancel();
			try { _listener?.Stop(); _listener?.Close(); } catch { }
			if( _loop is not null )
				try { await _loop; } catch { }
			_cts.Dispose();
		}
	}
}

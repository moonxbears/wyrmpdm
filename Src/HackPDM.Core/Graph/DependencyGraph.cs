namespace HackPDM.Core.Graph;

/// <summary>Which dependencies a graph shows, from the selected files.</summary>
public enum GraphDirection
{
	/// <summary>The files the selected files use; the selected files are on the left.</summary>
	Children,
	/// <summary>The files that use the selected files; the selected files are on the right.</summary>
	Parents,
	/// <summary>Both; the selected files are in the middle.</summary>
	Both,
	/// <summary>
	/// Full bidirectional neighborhood: shows parents, children, and recursively each parent's and
	/// child's parents and children (siblings, co-parents, and multi-hop ancestors/descendants).
	/// </summary>
	FullNetwork,
}

/// <summary>How the node graph organizes and constructs its nodes and links.</summary>
public enum GraphViewMode
{
	/// <summary>File-to-file dependency network around selected entries.</summary>
	Dependencies,
	/// <summary>Vault directory hierarchy shown as folder nodes and parent-to-child folder links.</summary>
	FolderStructure,
	/// <summary>Folder hierarchy together with the files inside each folder (and optional dependency links).</summary>
	FoldersAndFiles,
	/// <summary>Files grouped under hub nodes for any custom property or built-in attribute (Category, Extension, Material, etc.).</summary>
	ByProperty,
	/// <summary>Files matching multi-criteria vault search/filter rules, with optional dependency expansion.</summary>
	SearchFilter,
}

/// <summary>Semantic role of a node rendered in a <see cref="DependencyGraph"/>.</summary>
public enum GraphNodeRole
{
	/// <summary>A vault file (<c>hp.entry</c>).</summary>
	File,
	/// <summary>A vault folder (<c>hp.directory</c> or local folder).</summary>
	Folder,
	/// <summary>A synthetic grouping hub representing a property or attribute value.</summary>
	PropertyGroup,
}

/// <summary>One node (file, folder, or property group) in a graph.</summary>
/// <param name="Id">The hp.entry id (positive for files; negative synthetic ids for folders and property groups).</param>
/// <param name="Name">The display name (file name with extension, folder name, or group title).</param>
/// <param name="Ext">The extension, lower case, without the dot.</param>
/// <param name="Size">The latest version's size in bytes.</param>
/// <param name="Category">The file type's category name; empty when none.</param>
/// <param name="VersionId">The latest hp.version id; 0 when none.</param>
/// <param name="Deleted">Whether the file is logically deleted.</param>
/// <param name="CheckedOut">Whether someone has it checked out.</param>
/// <param name="Path">The server path ("root\a\b.sldprt").</param>
public sealed record GraphNode( int Id, string Name, string Ext, long Size, string Category, int VersionId, bool Deleted, bool CheckedOut, string Path )
{
	/// <summary>Who has it checked out; empty when nobody.</summary>
	public string CheckoutUser { get; init; } = "";
	/// <summary>The file type's description ("SolidWorks part"); empty when unknown.</summary>
	public string TypeName { get; init; } = "";
	/// <summary>When the latest version's file was last changed, as the server writes it (UTC, "yyyy-MM-dd HH:mm:ss"); empty when unknown.</summary>
	public string Modified { get; init; } = "";
	/// <summary>How many versions the file has.</summary>
	public int Versions { get; init; }
	/// <summary>Whether this node represents a file, folder, or property group hub.</summary>
	public GraphNodeRole Role { get; init; } = GraphNodeRole.File;
	/// <summary>Optional secondary summary line for non-file nodes (e.g. subfolder/file count or property name).</summary>
	public string Subtitle { get; init; } = "";
	/// <summary>Number of direct items represented by a folder or property group node.</summary>
	public int ItemCount { get; init; }
	/// <summary>Number of direct offspring/child branches omitted from this node due to branch or depth limits.</summary>
	public int OmittedChildBranches { get; init; }
	/// <summary>Number of direct parent branches omitted from this node due to branch or depth limits.</summary>
	public int OmittedParentBranches { get; init; }
	/// <summary>Optional custom properties keyed by property name (case-insensitive).</summary>
	public IReadOnlyDictionary<string, string>? Properties { get; init; }
}

/// <summary>A dependency: <paramref name="Parent"/>'s latest version uses <paramref name="Child"/>.</summary>
public readonly record struct GraphEdge( int Parent, int Child );

/// <summary>The files around the selected ones (<see cref="Roots"/>) and how they depend on each other.</summary>
/// <param name="Roots">The selected files' ids, in selection order.</param>
/// <param name="Nodes">Every file in the graph, the roots included.</param>
/// <param name="Edges">Parent-to-child dependencies between those files.</param>
/// <param name="Truncated">Whether the server stopped early (too deep or too many files).</param>
public sealed record DependencyGraph( IReadOnlyList<int> Roots, IReadOnlyList<GraphNode> Nodes, IReadOnlyList<GraphEdge> Edges, bool Truncated )
{
	/// <summary>A graph with nothing in it.</summary>
	public static DependencyGraph Empty { get; } = new( [], [], [], false );
}

using System.Text;

namespace HackPDM.Mcp.Database;

/// <summary>Safety classification of a PostgreSQL SQL statement.</summary>
public enum SqlStatementKind
{
	Empty,
	ReadOnly,
	WriteDml,
	AdminDdl,
	Unknown,
}

/// <summary>Structured lexical analysis of a SQL payload before execution.</summary>
public sealed record SqlClassificationResult(
	SqlStatementKind Kind,
	string LeadingKeyword,
	int StatementCount,
	bool IsStrictlyReadOnly,
	string Reason );

/// <summary>
/// Lexical PostgreSQL statement classifier that strips comments, string literals, quoted identifiers,
/// and dollar-quoted blocks before inspecting tokens. Prevents multi-statement piggybacking and
/// write-CTE smuggling (e.g. <c>WITH d AS (DELETE FROM ... RETURNING *) SELECT * FROM d</c>) in
/// read-only queries.
/// </summary>
public static class PostgresSqlClassifier
{
	private static readonly HashSet<string> WriteDmlKeywords = new( StringComparer.OrdinalIgnoreCase )
	{
		"INSERT", "UPDATE", "DELETE", "MERGE", "COPY", "CALL", "DO",
	};

	private static readonly HashSet<string> AdminDdlKeywords = new( StringComparer.OrdinalIgnoreCase )
	{
		"DROP", "TRUNCATE", "ALTER", "CREATE", "GRANT", "REVOKE",
		"VACUUM", "REINDEX", "CLUSTER", "LOCK", "SET", "RESET",
		"DISCARD", "LOAD", "LISTEN", "NOTIFY", "UNLISTEN", "COMMENT", "SECURITY",
	};

	private static readonly HashSet<string> ReadOnlyLeadingKeywords = new( StringComparer.OrdinalIgnoreCase )
	{
		"SELECT", "TABLE", "VALUES", "SHOW",
	};

	public static SqlClassificationResult Analyze( string? sql )
	{
		if( string.IsNullOrWhiteSpace( sql ) )
			return new SqlClassificationResult( SqlStatementKind.Empty, string.Empty, 0, false, "SQL statement is empty." );

		List<List<string>> statements = TokenizeStatements( sql );
		if( statements.Count == 0 )
			return new SqlClassificationResult( SqlStatementKind.Empty, string.Empty, 0, false, "SQL statement contains only comments or whitespace." );

		if( statements.Count > 1 )
		{
			// Classify highest risk across statements; multi-statement is never strictly read-only.
			SqlStatementKind worst = SqlStatementKind.ReadOnly;
			foreach( List<string> stmt in statements )
			{
				SqlStatementKind k = ClassifySingle( stmt ).Kind;
				if( k == SqlStatementKind.AdminDdl )
					worst = SqlStatementKind.AdminDdl;
				else if( k == SqlStatementKind.WriteDml && worst != SqlStatementKind.AdminDdl )
					worst = SqlStatementKind.WriteDml;
				else if( k == SqlStatementKind.Unknown && worst == SqlStatementKind.ReadOnly )
					worst = SqlStatementKind.Unknown;
			}
			string lead = statements[ 0 ][ 0 ].ToUpperInvariant();
			return new SqlClassificationResult(
				worst == SqlStatementKind.ReadOnly ? SqlStatementKind.WriteDml : worst,
				lead,
				statements.Count,
				IsStrictlyReadOnly: false,
				Reason: $"Multi-statement SQL ({statements.Count} statements) is not allowed in read-only queries." );
		}

		return ClassifySingle( statements[ 0 ] );
	}

	private static SqlClassificationResult ClassifySingle( List<string> tokens )
	{
		if( tokens.Count == 0 )
			return new SqlClassificationResult( SqlStatementKind.Empty, string.Empty, 0, false, "Empty statement." );

		string lead = tokens[ 0 ].ToUpperInvariant();

		// EXPLAIN [ (options) | ANALYZE ] <inner statement>
		if( lead == "EXPLAIN" )
		{
			bool hasAnalyze = tokens.Any( t => string.Equals( t, "ANALYZE", StringComparison.OrdinalIgnoreCase ) );
			bool containsWrite = tokens.Skip( 1 ).Any( t => WriteDmlKeywords.Contains( t ) || AdminDdlKeywords.Contains( t ) );
			if( hasAnalyze && containsWrite )
			{
				return new SqlClassificationResult(
					SqlStatementKind.WriteDml,
					lead,
					1,
					IsStrictlyReadOnly: false,
					Reason: "EXPLAIN ANALYZE on a modifying statement executes the write and is not read-only." );
			}
			return new SqlClassificationResult( SqlStatementKind.ReadOnly, lead, 1, true, "Read-only EXPLAIN statement." );
		}

		// WITH [RECURSIVE] cte AS (...) <main query>
		if( lead == "WITH" )
		{
			// Check every keyword token for data-modifying CTEs or DDL
			foreach( string tok in tokens )
			{
				if( AdminDdlKeywords.Contains( tok ) )
				{
					return new SqlClassificationResult(
						SqlStatementKind.AdminDdl,
						lead,
						1,
						IsStrictlyReadOnly: false,
						Reason: $"WITH query contains administrative/DDL keyword '{tok.ToUpperInvariant()}'." );
				}
				if( WriteDmlKeywords.Contains( tok ) )
				{
					return new SqlClassificationResult(
						SqlStatementKind.WriteDml,
						lead,
						1,
						IsStrictlyReadOnly: false,
						Reason: $"WITH query contains data-modifying keyword '{tok.ToUpperInvariant()}'." );
				}
			}

			bool hasSelect = tokens.Any( t => ReadOnlyLeadingKeywords.Contains( t ) );
			return hasSelect
				? new SqlClassificationResult( SqlStatementKind.ReadOnly, lead, 1, true, "Read-only WITH/SELECT query." )
				: new SqlClassificationResult( SqlStatementKind.Unknown, lead, 1, false, "WITH statement does not resolve to a SELECT query." );
		}

		if( ReadOnlyLeadingKeywords.Contains( lead ) )
		{
			// Reject SELECT ... INTO table (which creates a table in PostgreSQL!)
			if( lead == "SELECT" && ContainsSelectIntoTable( tokens ) )
			{
				return new SqlClassificationResult(
					SqlStatementKind.WriteDml,
					lead,
					1,
					IsStrictlyReadOnly: false,
					Reason: "SELECT ... INTO creates a new table and is not read-only." );
			}
			return new SqlClassificationResult( SqlStatementKind.ReadOnly, lead, 1, true, $"Read-only {lead} query." );
		}

		if( WriteDmlKeywords.Contains( lead ) )
			return new SqlClassificationResult( SqlStatementKind.WriteDml, lead, 1, false, $"Modifying {lead} statement." );

		if( AdminDdlKeywords.Contains( lead ) )
			return new SqlClassificationResult( SqlStatementKind.AdminDdl, lead, 1, false, $"Administrative/DDL {lead} statement." );

		return new SqlClassificationResult( SqlStatementKind.Unknown, lead, 1, false, $"Unrecognized leading SQL keyword '{lead}'." );
	}

	private static bool ContainsSelectIntoTable( List<string> tokens )
	{
		// In PostgreSQL, SELECT ... INTO [TEMPORARY|TEMP|UNLOGGED] [TABLE] new_table ...
		// appears at top level (outside parentheses) before FROM.
		int depth = 0;
		for( int i = 1; i < tokens.Count; i++ )
		{
			string t = tokens[ i ];
			if( t == "(" ) { depth++; continue; }
			if( t == ")" ) { depth = Math.Max( 0, depth - 1 ); continue; }
			if( depth != 0 )
				continue;
			if( string.Equals( t, "FROM", StringComparison.OrdinalIgnoreCase ) )
				return false;
			if( string.Equals( t, "INTO", StringComparison.OrdinalIgnoreCase ) )
				return true;
		}
		return false;
	}

	/// <summary>
	/// Extracts SQL keyword/punctuation tokens grouped by top-level semicolon-delimited statements,
	/// discarding comments, string literals, quoted identifiers, and dollar-quoted bodies.
	/// </summary>
	private static List<List<string>> TokenizeStatements( string sql )
	{
		List<List<string>> statements = [];
		List<string> current = [];

		int i = 0;
		int len = sql.Length;
		while( i < len )
		{
			char ch = sql[ i ];

			// Whitespace
			if( char.IsWhiteSpace( ch ) )
			{
				i++;
				continue;
			}

			// Line comment: -- ... \n
			if( ch == '-' && i + 1 < len && sql[ i + 1 ] == '-' )
			{
				i += 2;
				while( i < len && sql[ i ] != '\n' )
					i++;
				continue;
			}

			// Nested block comment: /* ... */
			if( ch == '/' && i + 1 < len && sql[ i + 1 ] == '*' )
			{
				i += 2;
				int depth = 1;
				while( i < len && depth > 0 )
				{
					if( sql[ i ] == '/' && i + 1 < len && sql[ i + 1 ] == '*' )
					{
						depth++;
						i += 2;
					}
					else if( sql[ i ] == '*' && i + 1 < len && sql[ i + 1 ] == '/' )
					{
						depth--;
						i += 2;
					}
					else
					{
						i++;
					}
				}
				continue;
			}

			// Single-quoted string literal '...' (or E'...')
			if( ch == '\'' )
			{
				i++;
				while( i < len )
				{
					if( sql[ i ] == '\\' && i + 1 < len )
					{
						i += 2;
						continue;
					}
					if( sql[ i ] == '\'' )
					{
						i++;
						if( i < len && sql[ i ] == '\'' )
						{
							i++;
							continue;
						}
						break;
					}
					i++;
				}
				continue;
			}

			// Double-quoted identifier "..."
			if( ch == '"' )
			{
				i++;
				while( i < len )
				{
					if( sql[ i ] == '"' )
					{
						i++;
						if( i < len && sql[ i ] == '"' )
						{
							i++;
							continue;
						}
						break;
					}
					i++;
				}
				continue;
			}

			// Dollar-quoted string $tag$...$tag$
			if( ch == '$' && TryMatchDollarTag( sql, i, out string tag ) )
			{
				i += tag.Length;
				int end = sql.IndexOf( tag, i, StringComparison.Ordinal );
				i = end >= 0 ? end + tag.Length : len;
				continue;
			}

			// Statement separator
			if( ch == ';' )
			{
				if( current.Count > 0 )
				{
					statements.Add( current );
					current = [];
				}
				i++;
				continue;
			}

			if( ch is '(' or ')' )
			{
				current.Add( ch.ToString() );
				i++;
				continue;
			}

			// Unquoted keyword / identifier
			if( char.IsLetter( ch ) || ch == '_' )
			{
				StringBuilder sb = new();
				while( i < len && ( char.IsLetterOrDigit( sql[ i ] ) || sql[ i ] == '_' ) )
				{
					sb.Append( sql[ i ] );
					i++;
				}
				current.Add( sb.ToString() );
				continue;
			}

			i++;
		}

		if( current.Count > 0 )
			statements.Add( current );

		return statements;
	}

	private static bool TryMatchDollarTag( string sql, int start, out string tag )
	{
		tag = string.Empty;
		if( start >= sql.Length || sql[ start ] != '$' )
			return false;
		int j = start + 1;
		if( j < sql.Length && sql[ j ] == '$' )
		{
			tag = "$$";
			return true;
		}
		if( j < sql.Length && ( char.IsLetter( sql[ j ] ) || sql[ j ] == '_' ) )
		{
			j++;
			while( j < sql.Length && ( char.IsLetterOrDigit( sql[ j ] ) || sql[ j ] == '_' ) )
				j++;
			if( j < sql.Length && sql[ j ] == '$' )
			{
				tag = sql[ start..( j + 1 ) ];
				return true;
			}
		}
		return false;
	}
}

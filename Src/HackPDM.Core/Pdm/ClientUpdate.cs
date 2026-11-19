using System.Reflection;
using System.Security.Cryptography;

using HackPDM.Core.Configuration;
using HackPDM.Core.Odoo;

namespace HackPDM.Core.Pdm;

public readonly record struct VersionCheckResult( bool IsAllowed, string? Reason );

/// <summary>Checks client version against Odoo's client_version rule and downloads SHA-256 verified installers.</summary>
public static class ClientUpdate
{
	private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes( 30 ) };

	public static Version Current { get; } = typeof( ClientUpdate ).Assembly.GetName().Version ?? new Version( 0, 0 );

	public static string CurrentText { get; } =
		typeof( ClientUpdate ).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
		?? Current.ToString();

	public static bool IsOutdated( Version current, string? required )
		=> Version.TryParse( required?.Trim(), out Version? minimum ) && Normalize( current ) < Normalize( minimum );

	public static VersionCheckResult CheckVersion( Version current, string? requirement )
	{
		if( string.IsNullOrWhiteSpace( requirement ) )
			return new( true, null );

		string spec = requirement.Trim();
		Version normalizedCurrent = Normalize( current );

		int dashIdx = spec.IndexOf( " - ", StringComparison.Ordinal );
		if( dashIdx >= 0 )
		{
			string left = spec[ ..dashIdx ].Trim();
			string right = spec[ ( dashIdx + 3 ).. ].Trim();
			if( !Version.TryParse( left, out Version? minVer ) || !Version.TryParse( right, out Version? maxVer ) )
				return new( false, $"Invalid server client_version range '{spec}'." );
			if( Normalize( minVer ) > Normalize( maxVer ) )
				return new( false, $"Invalid server client_version range '{spec}' (minimum exceeds maximum)." );
			bool inRange = normalizedCurrent >= Normalize( minVer ) && normalizedCurrent <= Normalize( maxVer );
			return inRange
				? new( true, null )
				: new( false, $"Client version {FormatVersion( current )} is outside the allowed server version range '{spec}'." );
		}

		string[] rawTokens = spec.Split( [ ',', ';' ], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries );
		List<string> clauses = [];
		foreach( string token in rawTokens )
		{
			string[] parts = token.Split( ' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries );
			for( int i = 0; i < parts.Length; i++ )
			{
				if( parts[ i ] is ">=" or "<=" or "==" or ">" or "<" or "=" && i + 1 < parts.Length )
				{
					clauses.Add( parts[ i ] + parts[ i + 1 ] );
					i++;
				}
				else
				{
					clauses.Add( parts[ i ] );
				}
			}
		}

		foreach( string clause in clauses )
		{
			( string op, string verText ) = clause switch
			{
				_ when clause.StartsWith( ">=", StringComparison.Ordinal ) || clause.StartsWith( "<=", StringComparison.Ordinal ) || clause.StartsWith( "==", StringComparison.Ordinal )
					=> ( clause[ ..2 ], clause[ 2.. ].Trim() ),
				_ when clause.StartsWith( ">", StringComparison.Ordinal ) || clause.StartsWith( "<", StringComparison.Ordinal ) || clause.StartsWith( "=", StringComparison.Ordinal )
					=> ( clause[ ..1 ], clause[ 1.. ].Trim() ),
				_ => ( "==", clause ),
			};

			if( !Version.TryParse( verText, out Version? bound ) )
				return new( false, $"Invalid server client_version requirement '{spec}'." );

			Version normalizedBound = Normalize( bound );
			bool matches = op switch
			{
				">=" => normalizedCurrent >= normalizedBound,
				">" => normalizedCurrent > normalizedBound,
				"<=" => normalizedCurrent <= normalizedBound,
				"<" => normalizedCurrent < normalizedBound,
				"=" or "==" => normalizedCurrent == normalizedBound,
				_ => false,
			};
			if( !matches )
				return new( false, $"Client version {FormatVersion( current )} does not match the server's required version '{spec}'." );
		}
		return new( true, null );
	}

	public static bool IsVersionMismatch( Version current, string? requirement )
		=> !CheckVersion( current, requirement ).IsAllowed;

	private static Version Normalize( Version v )
		=> new( v.Major, Math.Max( 0, v.Minor ), Math.Max( 0, v.Build ), Math.Max( 0, v.Revision ) );

	private static string FormatVersion( Version v )
		=> v.Revision > 0
			? $"{v.Major}.{v.Minor}.{Math.Max( 0, v.Build )}.{v.Revision}"
			: $"{v.Major}.{v.Minor}.{Math.Max( 0, v.Build )}";

	public static async Task<Result<string>> DownloadInstallerAsync(
		string? source,
		string? sha256,
		string? targetDirectory = null,
		CancellationToken token = default )
	{
		if( string.IsNullOrWhiteSpace( source ) )
			return Result<string>.Fail( "the server does not say where to download the client (hp.settings client_download_url)" );
		string expected = ( sha256 ?? "" ).Trim().ToLowerInvariant();
		if( expected.Length != 64 || expected.Any( c => !Uri.IsHexDigit( c ) ) )
			return Result<string>.Fail( "the server gives no valid SHA-256 for the installer (hp.settings client_download_sha256), so it cannot be checked" );

		source = source.Trim();
		bool web = Uri.TryCreate( source, UriKind.Absolute, out Uri? uri ) && uri.Scheme is "http" or "https";
		string fileName = Path.GetFileName( web ? uri!.AbsolutePath : uri is { IsFile: true } ? uri.LocalPath : source );
		if( string.IsNullOrEmpty( fileName ) )
			fileName = "HackPDM-setup";
		targetDirectory ??= Path.Combine( Path.GetTempPath(), $"hackpdm-update-{Guid.NewGuid():N}" );
		string target = Path.Combine( targetDirectory, fileName );
		string partial = target + ".download";

		try
		{
			Directory.CreateDirectory( targetDirectory );
			await using( FileStream output = new( partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true ) )
			{
				if( web )
				{
					using HttpResponseMessage response = await Http.GetAsync( uri, HttpCompletionOption.ResponseHeadersRead, token );
					response.EnsureSuccessStatusCode();
					await using Stream input = await response.Content.ReadAsStreamAsync( token );
					await input.CopyToAsync( output, token );
				}
				else
				{
					await using FileStream input = new( uri is { IsFile: true } ? uri.LocalPath : source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true );
					await input.CopyToAsync( output, token );
				}
			}

			string actual;
			await using( FileStream check = File.OpenRead( partial ) )
				actual = Convert.ToHexStringLower( await SHA256.HashDataAsync( check, token ) );
			if( actual != expected )
			{
				File.Delete( partial );
				HackLogger.Error( "Updater", $"SHA-256 mismatch for installer from '{source}': expected={expected}, actual={actual}; file deleted." );
				return Result<string>.Fail( $"the downloaded installer does not match the server's SHA-256 (got {actual}); it was deleted" );
			}

			File.Move( partial, target, overwrite: true );
			HackLogger.Info( "Updater", $"Downloaded and verified installer '{fileName}' (sha256={actual})." );
			return Result<string>.Success( target );
		}
		catch( Exception e ) when( e is IOException or UnauthorizedAccessException or HttpRequestException or TaskCanceledException && !token.IsCancellationRequested )
		{
			TryDelete( partial );
			HackLogger.Error( "Updater", $"Installer download from '{source}' failed: {e.Message}", e );
			return Result<string>.Fail( $"could not download the installer from {source}: {e.Message}" );
		}
		catch
		{
			TryDelete( partial );
			throw;
		}
	}

	private static void TryDelete( string path )
	{
		try { File.Delete( path ); }
		catch( Exception e ) when( e is IOException or UnauthorizedAccessException ) { }
	}
}

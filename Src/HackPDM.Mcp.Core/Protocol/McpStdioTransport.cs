using System.Text;

namespace HackPDM.Mcp.Protocol;

/// <summary>
/// Newline-delimited JSON-RPC 2.0 MCP transport over standard input/output (or arbitrary streams for testing).
/// Never writes diagnostic text to the output stream; diagnostics go to <paramref name="errorLog"/> (stderr).
/// </summary>
public sealed class McpStdioTransport(
	McpServer server,
	Stream? input = null,
	Stream? output = null,
	TextWriter? errorLog = null )
{
	private static readonly UTF8Encoding Utf8NoBom = new( encoderShouldEmitUTF8Identifier: false );
	private readonly SemaphoreSlim _writeGate = new( 1, 1 );

	public async Task RunAsync( CancellationToken cancellationToken = default )
	{
		Stream inStream = input ?? Console.OpenStandardInput();
		Stream outStream = output ?? Console.OpenStandardOutput();
		TextWriter err = errorLog ?? Console.Error;

		using StreamReader reader = new( inStream, Utf8NoBom, detectEncodingFromByteOrderMarks: true, bufferSize: 64 * 1024, leaveOpen: true );
		using StreamWriter writer = new( outStream, Utf8NoBom, bufferSize: 64 * 1024, leaveOpen: true ) { NewLine = "\n" };

		while( !cancellationToken.IsCancellationRequested )
		{
			string? line;
			try
			{
				line = await reader.ReadLineAsync( cancellationToken );
			}
			catch( OperationCanceledException )
			{
				break;
			}
			catch( IOException ex )
			{
				await err.WriteLineAsync( $"[hackpdm-mcp] stdin read error: {ex.Message}" );
				break;
			}

			if( line is null )
				break;

			string trimmed = line.Trim();
			if( trimmed.Length == 0 )
				continue;

			string? response;
			try
			{
				response = await server.HandleMessageAsync( trimmed, cancellationToken );
			}
			catch( Exception ex )
			{
				await err.WriteLineAsync( $"[hackpdm-mcp] message handling error: {ex}" );
				continue;
			}

			if( response is null )
				continue;

			await _writeGate.WaitAsync( cancellationToken );
			try
			{
				// MCP stdio frames must be single-line JSON terminated by \n
				string singleLine = response.Replace( "\r", "" ).Replace( "\n", "" );
				await writer.WriteLineAsync( singleLine );
				await writer.FlushAsync( cancellationToken );
			}
			finally
			{
				_writeGate.Release();
			}
		}
	}
}

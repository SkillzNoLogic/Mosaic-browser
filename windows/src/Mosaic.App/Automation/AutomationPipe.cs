using System.IO;
using System.IO.Pipes;
using System.Text;
using Mosaic.Core.Browser;

// Not "Mosaic.App.Automation": a namespace of that name would hide Mosaic.Core.Browser.Automation
// from all code under Mosaic.App.
namespace Mosaic.App.AutomationSupport;

/// <summary>
/// The transport of the automation protocol: the named pipe <c>\\.\pipe\mosaic-automation</c>.
/// A client connects, writes one line of JSON, reads one line back and disconnects; what the
/// lines mean is <see cref="Automation"/>'s business.
/// </summary>
/// <remarks>
/// Everything here runs on the UI thread (the pipe is asynchronous, so nothing blocks it), which
/// is where the browser model has to be driven from. The pipe only accepts the current user.
/// </remarks>
internal sealed class AutomationPipe : IDisposable
{
    /// <summary>How long a connected client gets to send its request, and later to read the reply.</summary>
    private static readonly TimeSpan ClientPatience = TimeSpan.FromSeconds(10);
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly Automation automation;
    private readonly Action quit;
    private readonly CancellationTokenSource stopping = new();

    /// <param name="automation">Runs the requests.</param>
    /// <param name="quit">Exits the app; called after the reply to <c>quit</c> has been delivered.</param>
    public AutomationPipe(Automation automation, Action quit)
    {
        this.automation = automation;
        this.quit = quit;
    }

    /// <summary>Starts accepting clients. Call on the UI thread.</summary>
    public void Start() => _ = ListenAsync();

    /// <summary>Stops listening and abandons any client still being served.</summary>
    public void Dispose() => stopping.Cancel();

    private async Task ListenAsync()
    {
        var stop = stopping.Token;
        while (!stop.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(
                    Automation.PipeName, PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(stop);
            }
            catch (OperationCanceledException)
            {
                pipe?.Dispose();
                return;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // The pipe could not be created or the client vanished while connecting.
                // Automation is a test aid: keep trying quietly rather than take the app down.
                pipe?.Dispose();
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(500), stop);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                continue;
            }

            // Serve this client without waiting for it, so the next one can connect meanwhile.
            _ = ServeAsync(pipe, stop);
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken stop)
    {
        var quitRequested = false;
        try
        {
            using var reader = new StreamReader(pipe, Utf8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);

            if (await ReadLineAsync(reader, stop) is not { } request) return;
            var reply = await automation.HandleAsync(request);
            await pipe.WriteAsync(Utf8.GetBytes(reply.Json + "\n"), stop);
            await pipe.FlushAsync(stop);
            quitRequested = reply.QuitRequested;

            // Closing the pipe straight away could race the client's read. The client hangs up
            // once it has its line; wait for that (it shows up as the end of the stream).
            await ReadLineAsync(reader, stop);
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // The client went away or took too long. Its loss; the next one starts afresh.
        }
        finally
        {
            await pipe.DisposeAsync();
        }

        if (quitRequested) quit();
    }

    private static async Task<string?> ReadLineAsync(StreamReader reader, CancellationToken stop)
    {
        using var patience = CancellationTokenSource.CreateLinkedTokenSource(stop);
        patience.CancelAfter(ClientPatience);
        return await reader.ReadLineAsync(patience.Token);
    }
}

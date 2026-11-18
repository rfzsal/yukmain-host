using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Windows.Threading;

namespace YukMain.Host.Desktop;

/// <summary>
/// Receives newline-delimited controller snapshots from a separate
/// non-elevated process. No HTTP/WebSocket handling runs in this process.
/// </summary>
internal sealed class RemoteInputPipeServer : IDisposable
{
    public const string PipeName = "yukmain-controller-input-v1";

    private readonly Dispatcher _dispatcher;
    private readonly Action<string> _onMessage;
    private readonly Action _onDisconnect;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    public RemoteInputPipeServer(
        Dispatcher dispatcher, Action<string> onMessage, Action onDisconnect)
    {
        _dispatcher = dispatcher;
        _onMessage = onMessage;
        _onDisconnect = onDisconnect;
        _loop = Task.Run(RunAsync);
    }

    private async Task RunAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(
                    PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_stop.Token);
                using var reader = new StreamReader(pipe, new UTF8Encoding(false),
                    detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);

                while (!_stop.IsCancellationRequested && pipe.IsConnected)
                {
                    var line = await reader.ReadLineAsync(_stop.Token);
                    if (line is null) break;
                    if (line.Length > 4096) continue;
                    await _dispatcher.InvokeAsync(() => _onMessage(line));
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch (IOException) { /* Bridge disconnected. */ }
            catch (Exception) when (_stop.IsCancellationRequested) { break; }
            catch (Exception) { await Task.Delay(500, _stop.Token); }
            finally
            {
                if (!_stop.IsCancellationRequested)
                {
                    try { await _dispatcher.InvokeAsync(_onDisconnect); }
                    catch (TaskCanceledException) { /* Window closed. */ }
                }
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        // Do not synchronously wait for the background loop on the WPF UI thread.
        _ = _loop.ContinueWith(_ => _stop.Dispose(), TaskScheduler.Default);
    }
}

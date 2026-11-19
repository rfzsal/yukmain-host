using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
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
    private readonly Action<string> _onError;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    public RemoteInputPipeServer(
        Dispatcher dispatcher, Action<string> onMessage, Action onDisconnect, Action<string> onError)
    {
        _dispatcher = dispatcher;
        _onMessage = onMessage;
        _onDisconnect = onDisconnect;
        _onError = onError;
        _loop = Task.Run(RunAsync);
    }

    private async Task RunAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                var user = WindowsIdentity.GetCurrent().User
                    ?? throw new InvalidOperationException("Windows user identity is unavailable.");
                var security = new PipeSecurity();
                // Restrict access to this account, but permit its non-elevated process
                // to write to the elevated engine through a medium-integrity pipe.
                security.SetSecurityDescriptorSddlForm(
                    $"O:{user.Value}D:P(A;;GA;;;{user.Value})");
                using var pipe = NamedPipeServerStreamAcl.Create(
                    PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous, 0, 0, security);
                // Set only LABEL_SECURITY_INFORMATION. Passing a SACL during
                // creation requests SeSecurityPrivilege, which is not enabled.
                var label = new RawSecurityDescriptor("S:(ML;;NW;;;ME)");
                var descriptor = new byte[label.BinaryLength];
                label.GetBinaryForm(descriptor, 0);
                if (!SetKernelObjectSecurity(pipe.SafePipeHandle, 0x10, descriptor))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
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
            catch (Exception ex)
            {
                await _dispatcher.InvokeAsync(() => _onError($"Controller pipe failed: {ex.Message}"));
                await Task.Delay(500, _stop.Token);
            }
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

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetKernelObjectSecurity(
        SafePipeHandle handle, uint securityInformation, byte[] securityDescriptor);

    public void Dispose()
    {
        _stop.Cancel();
        // Do not synchronously wait for the background loop on the WPF UI thread.
        _ = _loop.ContinueWith(_ => _stop.Dispose(), TaskScheduler.Default);
    }
}

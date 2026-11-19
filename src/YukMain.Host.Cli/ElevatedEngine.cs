using System.Diagnostics;
using System.Security.Principal;
using System.Windows.Threading;
using YukMain.Host.Core.Controllers;
using YukMain.Host.HidMaestro;
using YukMain.Host.Desktop;

namespace YukMain.Host.Cli;

// The elevated mode runs in a separate process of YukMain.Host.exe.
// This process never handles HTTP or WebSocket connections.
internal static class ElevatedEngine
{
    public static string ReadyEvent(int pid) => $@"Local\YukMain-Engine-Ready-{pid}";
    public static string StopEvent(int pid) => $@"Local\YukMain-Engine-Stop-{pid}";

    public static int Run(int parentPid)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            return 1;

        try
        {
            using var ready = EventWaitHandle.OpenExisting(ReadyEvent(parentPid));
            using var stop = EventWaitHandle.OpenExisting(StopEvent(parentPid));
            using var parent = Process.GetProcessById(parentPid);

            // Keep the existing receiver's DispatcherTimer/watchdog active
            // without creating or showing the former WPF MainWindow.
            var dispatcher = Dispatcher.CurrentDispatcher;
            using var manager = new ControllerManager(new HidMaestroBackend());
            manager.Initialize();
            using var receiver = new EngineReceiver(manager);
            using var pipe = new RemoteInputPipeServer(
                dispatcher, receiver.Receive, receiver.ResetAll,
                message => Console.Error.WriteLine("Controller pipe: " + message));

            var shutdown = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(250)
            };
            shutdown.Tick += (_, _) =>
            {
                if (stop.WaitOne(0) || parent.HasExited)
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Normal);
            };
            shutdown.Start();

            ready.Set();
            Dispatcher.Run();
            shutdown.Stop();
            // Reset before disposing the HIDMaestro driver/context.
            receiver.ResetAll();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Elevated controller engine failed: " + ex.Message);
            return 1;
        }
    }
}

// Reuse the WPF-agent's existing controller mapping and stale-state watchdog.
// Only the UI callbacks are replaced with no-op callbacks.
internal sealed class EngineReceiver : IDisposable
{
    private readonly RemoteControllerReceiver _receiver;

    public EngineReceiver(ControllerManager manager)
    {
        _receiver = new RemoteControllerReceiver(manager, (_, _) => { }, _ => { });
    }

    public void Receive(string data)
    {
        try { _receiver.Receive(data); }
        catch (Exception ex) { Console.Error.WriteLine("Controller input rejected: " + ex.Message); }
    }

    public void ResetAll() => _receiver.ResetAll();
    public void Dispose() => _receiver.Stop();
}

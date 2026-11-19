using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Windows.Threading;
using YukMain.Host.Core.Controllers;
using YukMain.Host.HidMaestro;
using YukMain.Host.Desktop;

namespace YukMain.Host.Cli;

// The elevated mode is a separate process of the same single EXE.
// No HTTP/WebSocket code executes in this mode. The status pipe is one-way
// diagnostics only, not a privileged command interface.
internal static class ElevatedEngine
{
    public static string ReadyEvent(int pid) => $@"Local\YukMain-Engine-Ready-{pid}";
    public static string StopEvent(int pid) => $@"Local\YukMain-Engine-Stop-{pid}";

    public static int Run(int parentPid, string statusPipeName, bool selfTest)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            return 1;

        try
        {
            using var ready = EventWaitHandle.OpenExisting(ReadyEvent(parentPid));
            using var stop = EventWaitHandle.OpenExisting(StopEvent(parentPid));
            using var parent = Process.GetProcessById(parentPid);
            using var statusPipe = new NamedPipeClientStream(".", statusPipeName, PipeDirection.Out);
            statusPipe.Connect(10000);
            using var status = new StreamWriter(statusPipe, new UTF8Encoding(false), 1024, leaveOpen: true)
            {
                AutoFlush = true
            };
            void Report(string message)
            {
                try { status.WriteLine(message); }
                catch (IOException) { /* Parent launcher disconnected. */ }
            }

            var dispatcher = Dispatcher.CurrentDispatcher;
            // The original WPF Agent performs both initialization and Submit on
            // one STA dispatcher. Preserve that ownership for the CLI helper.
            using var manager = new ControllerManager(new HidMaestroBackend());
            manager.Initialize();
            Report("HIDMaestro driver initialized.");

            using var receiver = new EngineReceiver(manager, Report);
            using var pipe = selfTest ? null : new RemoteInputPipeServer(
                dispatcher, receiver.Receive, receiver.ResetAll,
                error => Report("ERROR " + error));

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

            if (selfTest)
            {
                // Prove native HIDMaestro Create/Submit independently of the
                // WebSocket bridge, browser, and input JSON parsing.
                var seconds = 0;
                var testTimer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher)
                {
                    Interval = TimeSpan.FromSeconds(1)
                };
                manager.Create(2);
                Report("P2 self-test controller CREATED. Open joy.cpl Properties > Test.");
                testTimer.Tick += (_, _) =>
                {
                    try
                    {
                        seconds++;
                        if (seconds < 5)
                            Report($"P2 self-test: Xbox A in {5 - seconds}s...");
                        else if (seconds == 5)
                        {
                            manager.Submit(2, new ControllerState { Buttons = ControllerButtons.A });
                            Report("P2 self-test: Xbox A PRESSED (hold 5 seconds).");
                        }
                        else if (seconds == 10)
                        {
                            manager.Reset(2);
                            Report("P2 self-test: Xbox A RELEASED. Test complete.");
                            testTimer.Stop();
                            dispatcher.BeginInvokeShutdown(DispatcherPriority.Normal);
                        }
                    }
                    catch (Exception ex)
                    {
                        Report("ERROR P2 self-test: " + ex.Message);
                        testTimer.Stop();
                        dispatcher.BeginInvokeShutdown(DispatcherPriority.Normal);
                    }
                };
                testTimer.Start();
            }
            else
                Report("Remote input pipe listening; waiting for P2/P3/P4 controller states.");

            ready.Set();
            Dispatcher.Run();
            shutdown.Stop();
            receiver.ResetAll();
            Report("Controller engine stopped; inputs neutralized.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Elevated controller engine failed: " + ex);
            return 1;
        }
    }
}

internal sealed class EngineReceiver : IDisposable
{
    private readonly ControllerManager _manager;
    private readonly RemoteControllerReceiver _receiver;
    private readonly Action<string> _report;
    private readonly HashSet<int> _reported = [];
    private readonly Dictionary<int, DateTime> _lastActiveLog = new();

    public EngineReceiver(ControllerManager manager, Action<string> report)
    {
        _manager = manager;
        _report = report;
        _receiver = new RemoteControllerReceiver(manager, OnApplied, message => _report(message));
    }

    private void OnApplied(int slot, ControllerState state)
    {
        if (_reported.Add(slot))
            _report($"P{slot} virtual Xbox controller CREATED and first state submitted.");

        var active = state.Buttons != ControllerButtons.None ||
            state.DPad != DPadDirection.None ||
            Math.Abs(state.LeftX) > 0.2f || Math.Abs(state.LeftY) > 0.2f ||
            Math.Abs(state.RightX) > 0.2f || Math.Abs(state.RightY) > 0.2f ||
            state.LeftTrigger > 0.2f || state.RightTrigger > 0.2f;
        if (!active) return;
        var now = DateTime.UtcNow;
        if (_lastActiveLog.TryGetValue(slot, out var previous) && now - previous < TimeSpan.FromSeconds(2))
            return;
        _lastActiveLog[slot] = now;
        _report($"P{slot} HIDMaestro Submit OK (buttons={state.Buttons}, left={state.LeftX:F2}/{state.LeftY:F2}).");
    }

    public void Receive(string data)
    {
        try { _receiver.Receive(data); }
        catch (Exception ex) { _report("ERROR Remote controller input rejected: " + ex.Message); }
    }

    public void ResetAll()
    {
        _receiver.ResetAll();
        _reported.Clear();
        _lastActiveLog.Clear();
    }

    public void Dispose() => _receiver.Stop();
}

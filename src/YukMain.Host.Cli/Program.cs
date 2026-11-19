using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace YukMain.Host.Cli;

internal static class Program
{
    private const string EngineArgument = "--engine";

    [STAThread]
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 6 && args[0] == EngineArgument &&
            args[1] == "--parent" && int.TryParse(args[2], out var parentPid) && parentPid > 0 &&
            args[3] == "--status-pipe" && IsValidPipeId(args[4]) &&
            args[5] is "host" or "test")
            return ElevatedEngine.Run(parentPid, args[4], args[5] == "test");

        if (args.Length == 1 && args[0] == "--licenses")
        {
            PrintEmbeddedLicenses();
            return 0;
        }
        if (args.Length == 1 && args[0] is "--help" or "-h")
        {
            Console.WriteLine("YukMain.Host.exe - single-download CLI remote gamepad host");
            Console.WriteLine("Run without arguments to pair your PC with a YukMain room.");
            Console.WriteLine("--test-controller: local P2 Xbox A press test, no network required.");
            Console.WriteLine("--licenses: print embedded third-party license notices.");
            Console.WriteLine("Only the local virtual-controller helper requests Windows elevation.");
            return 0;
        }
        var test = args.Length == 1 && args[0] == "--test-controller";
        if (args.Length != 0 && !test)
        {
            Console.Error.WriteLine("Unknown option. Use --help.");
            return 2;
        }

        using var identity = WindowsIdentity.GetCurrent();
        if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        {
            Console.Error.WriteLine("Start YukMain as a normal Windows user, not Run as Administrator.");
            Console.Error.WriteLine("The driver engine is elevated separately; the network bridge stays non-elevated.");
            return 1;
        }

        Console.WriteLine(test ? "YukMain Host - local controller self-test" : "YukMain Host - CLI");
        Console.WriteLine(test
            ? "Open joy.cpl > Properties > Test. P2 Xbox A will be held for 5 seconds after a 5-second countdown."
            : "Starting isolated virtual-controller engine. A Windows UAC prompt may appear.");
        Console.WriteLine();

        var pid = Environment.ProcessId;
        var statusName = $"yukmain-engine-status-{pid}-{Guid.NewGuid():N}";
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, ElevatedEngine.ReadyEvent(pid));
        using var stop = new EventWaitHandle(false, EventResetMode.ManualReset, ElevatedEngine.StopEvent(pid));
        using var statusCancel = new CancellationTokenSource();

        // A distinct, unguessable diagnostic channel. The low-privilege launcher
        // owns it and only this Windows account can access it.
        var security = new PipeSecurity();
        var sid = identity.User ?? throw new InvalidOperationException("Windows account SID is unavailable.");
        security.SetSecurityDescriptorSddlForm($"O:{sid.Value}D:P(A;;GA;;;{sid.Value})");
        using var statusPipe = NamedPipeServerStreamAcl.Create(
            statusName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, 0, 0, security);
        var statusTask = ReadEngineStatusAsync(statusPipe, statusCancel.Token);

        Process? helper = null;
        try
        {
            var executable = Environment.ProcessPath ??
                throw new InvalidOperationException("Cannot determine YukMain executable path.");
            var info = new ProcessStartInfo(executable)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = AppContext.BaseDirectory
            };
            info.ArgumentList.Add(EngineArgument);
            info.ArgumentList.Add("--parent");
            info.ArgumentList.Add(pid.ToString(System.Globalization.CultureInfo.InvariantCulture));
            info.ArgumentList.Add("--status-pipe");
            info.ArgumentList.Add(statusName);
            info.ArgumentList.Add(test ? "test" : "host");
            helper = Process.Start(info) ??
                throw new InvalidOperationException("Windows did not start the controller helper.");

            var started = Stopwatch.StartNew();
            while (!ready.WaitOne(250))
            {
                if (helper.HasExited)
                {
                    Console.Error.WriteLine($"Controller engine failed before ready (code {helper.ExitCode}).");
                    return 1;
                }
                if (started.Elapsed > TimeSpan.FromMinutes(2))
                {
                    Console.Error.WriteLine("Timed out waiting for controller engine readiness.");
                    return 1;
                }
            }

            Console.WriteLine("Controller engine READY.");
            if (test)
            {
                await helper.WaitForExitAsync();
                return helper.ExitCode;
            }

            Console.WriteLine("Bridge runs without administrator privileges.");
            Console.WriteLine();
            return await YukMain.Host.Bridge.Program.Main();
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            Console.Error.WriteLine("Windows administrator approval was cancelled.");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("YukMain startup failed: " + ex.Message);
            return 1;
        }
        finally
        {
            stop.Set();
            if (helper != null)
            {
                try
                {
                    if (!helper.HasExited && !helper.WaitForExit(5000))
                        Console.Error.WriteLine("Controller engine still exiting; its parent-process watchdog remains active.");
                }
                catch (InvalidOperationException) { /* Exited concurrently. */ }
                helper.Dispose();
            }
            statusCancel.Cancel();
            try { await statusTask; }
            catch (OperationCanceledException) { /* Expected after shutdown. */ }
        }
    }

    private static bool IsValidPipeId(string value) =>
        value.Length is > 10 and < 100 &&
        value.StartsWith("yukmain-engine-status-", StringComparison.Ordinal) &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    private static async Task ReadEngineStatusAsync(NamedPipeServerStream pipe, CancellationToken token)
    {
        try
        {
            await pipe.WaitForConnectionAsync(token);
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true);
            while (!token.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(token);
                if (line is null) break;
                if (line.StartsWith("ERROR ", StringComparison.Ordinal))
                    Console.Error.WriteLine("[Engine] " + line);
                else Console.WriteLine("[Engine] " + line);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (IOException) { /* Elevated helper exited/disconnected. */ }
    }

    private static void PrintEmbeddedLicenses()
    {
        foreach (var resource in new[] { "YukMain.HIDMaestro.LICENSE.txt", "YukMain.HIDMaestro.THIRD-PARTY-NOTICES.txt" })
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource);
            if (stream is null) continue;
            using var reader = new StreamReader(stream);
            Console.WriteLine("====== " + resource + " ======");
            Console.WriteLine(reader.ReadToEnd());
        }
    }
}

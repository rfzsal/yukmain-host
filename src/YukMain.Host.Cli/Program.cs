using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Security.Principal;

namespace YukMain.Host.Cli;

internal static class Program
{
    private const string EngineArgument = "--engine";

    [STAThread]
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 3 && args[0] == EngineArgument &&
            args[1] == "--parent" && int.TryParse(args[2], out var parentPid) && parentPid > 0)
            return ElevatedEngine.Run(parentPid);

        if (args.Length == 1 && args[0] == "--licenses")
        {
            PrintEmbeddedLicenses();
            return 0;
        }
        if (args.Length == 1 && args[0] is "--help" or "-h")
        {
            Console.WriteLine("YukMain.Host.exe - single-download CLI remote gamepad host");
            Console.WriteLine("Run without arguments to pair your PC with a YukMain room.");
            Console.WriteLine("--licenses prints the embedded HIDMaestro third-party license notices.");
            Console.WriteLine("Windows prompts for elevation of only the local controller helper.");
            return 0;
        }
        if (args.Length != 0)
        {
            Console.Error.WriteLine("Unknown option. Use --help.");
            return 2;
        }

        using var identity = WindowsIdentity.GetCurrent();
        if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        {
            Console.Error.WriteLine("Start YukMain as a normal Windows process, not Run as Administrator.");
            Console.Error.WriteLine("The local driver engine is elevated separately to keep the network bridge unprivileged.");
            return 1;
        }

        Console.WriteLine("YukMain Host - CLI");
        Console.WriteLine("Starting the local controller engine. A Windows UAC prompt may appear.");
        Console.WriteLine();

        var pid = Environment.ProcessId;
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, ElevatedEngine.ReadyEvent(pid));
        using var stop = new EventWaitHandle(false, EventResetMode.ManualReset, ElevatedEngine.StopEvent(pid));

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
            helper = Process.Start(info) ??
                throw new InvalidOperationException("Windows did not start the driver helper.");

            var started = Stopwatch.StartNew();
            while (!ready.WaitOne(250))
            {
                if (helper.HasExited)
                {
                    Console.Error.WriteLine($"Controller engine stopped unexpectedly (exit code {helper.ExitCode}).");
                    return 1;
                }
                if (started.Elapsed > TimeSpan.FromMinutes(2))
                {
                    Console.Error.WriteLine("Timed out waiting for controller initialization.");
                    return 1;
                }
            }
            Console.WriteLine("Controller engine READY");
            Console.WriteLine("Network bridge is running with normal-user permissions.");
            Console.WriteLine();
            // Link the proven Bridge's existing HTTPS + WSS + named-pipe flow
            // into this binary rather than shipping a second executable.
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
                        Console.Error.WriteLine("Elevated engine still shutting down. It also monitors this process and will exit when it ends.");
                }
                catch (InvalidOperationException) { /* Exited concurrently. */ }
                helper.Dispose();
            }
        }
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

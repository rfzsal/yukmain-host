using System.IO.Pipes;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace YukMain.Host.Bridge;

// Run this small process as a STANDARD Windows user. Networking is deliberately
// separated from the elevated WPF/HIDMaestro process. Do not add driver APIs here.
internal static class Program
{
    private const string PipeName = "yukmain-controller-input-v1";

    public static async Task<int> Main()
    {
        Console.WriteLine("YukMain · Remote Controller Bridge (ordinary-user process)");
        Console.Write("YukMain HTTPS URL: ");
        var address = Console.ReadLine()?.Trim().TrimEnd('/');
        Console.Write("6-digit room code: ");
        var room = Console.ReadLine()?.Trim();
        Console.Write("One-time 8-character pairing code: ");
        var pairingCode = Console.ReadLine()?.Trim();

        if (!Uri.TryCreate(address, UriKind.Absolute, out var site) ||
            (site.Scheme != Uri.UriSchemeHttps &&
             !(site.Scheme == Uri.UriSchemeHttp && site.IsLoopback)) ||
            room is null || room.Length != 6 || !room.All(char.IsAsciiDigit) ||
            pairingCode is null || pairingCode.Length != 8)
        {
            Console.Error.WriteLine("Invalid server URL, room code, or pairing code.");
            return 1;
        }

        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out,
                PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
            Console.WriteLine("Connecting to the elevated local controller engine...");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await pipe.ConnectAsync(timeout.Token);
            var owner = pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier));
            if (!Equals(owner, WindowsIdentity.GetCurrent().User))
                throw new UnauthorizedAccessException("Controller pipe belongs to another Windows account.");
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), bufferSize: 1024, leaveOpen: true)
            {
                AutoFlush = true
            };
            Console.WriteLine("Local controller engine connected.");

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var redeemUrl = new Uri(site, $"/api/rooms/{room}/pairing/redeem");
            using var response = await http.PostAsJsonAsync(redeemUrl,
                new { pairingCode });
            if (!response.IsSuccessStatusCode)
            {
                Console.Error.WriteLine($"Pairing rejected (HTTP {(int)response.StatusCode}). Generate a fresh code in the host studio.");
                return 1;
            }
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var token = document.RootElement.GetProperty("agentToken").GetString();
            if (string.IsNullOrWhiteSpace(token) || token.Length != 64)
                throw new InvalidOperationException("Server returned an invalid agent credential.");
            Console.WriteLine("Paired with YukMain room. Opening secure WebSocket...");

            var socketUrl = new UriBuilder(new Uri(site, $"/api/rooms/{room}/relay?role=agent"))
            {
                Scheme = site.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
                Port = site.IsDefaultPort ? -1 : site.Port
            }.Uri;

            using var socket = new ClientWebSocket();
            socket.Options.AddSubProtocol("yukmain.v1");
            socket.Options.AddSubProtocol("yukmain-agent." + token);
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            await socket.ConnectAsync(socketUrl, CancellationToken.None);
            Console.WriteLine("WSS connected. Awaiting player input (Ctrl+C to stop).");
            var buffer = new byte[4096];
            var lastControllerLog = DateTime.MinValue;

            try
            {
                while (socket.State == WebSocketState.Open)
                {
                    using var message = new MemoryStream();
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                        if (result.MessageType == WebSocketMessageType.Close) return 0;
                        if (result.MessageType != WebSocketMessageType.Text)
                            throw new InvalidDataException("Unexpected binary WebSocket message.");
                        if (message.Length + result.Count > 4096)
                            throw new InvalidDataException("Controller frame exceeds size limit.");
                        message.Write(buffer, 0, result.Count);
                    } while (!result.EndOfMessage);

                    var data = Encoding.UTF8.GetString(message.ToArray());
                    using var packet = JsonDocument.Parse(data);
                    var root = packet.RootElement;
                    if (!root.TryGetProperty("type", out var eventType)) continue;
                    var type = eventType.GetString();
                    if (type is not ("controller.state" or "controller.neutral" or "player.disconnected")) continue;
                    if (!root.TryGetProperty("slot", out var value) ||
                        !value.TryGetInt32(out var slot) || slot is < 2 or > 4) continue;
                    await writer.WriteLineAsync(data);
                    // Gamepad snapshots may arrive 60 times/sec per player.
                    // Forward every frame, but avoid flooding stdout and slowing the bridge.
                    if (type != "controller.state" ||
                        DateTime.UtcNow - lastControllerLog >= TimeSpan.FromSeconds(2))
                    {
                        Console.WriteLine($"P{slot}: {type}");
                        lastControllerLog = DateTime.UtcNow;
                    }
                }
            }
            finally
            {
                // Always neutralize even if the WebSocket disconnects unexpectedly.
                await writer.WriteLineAsync("{\"type\":\"controller.reset_all\"}");
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Bridge connection failed: " + ex.Message);
            return 1;
        }
    }
}

using System.Text.Json;
using System.Windows.Threading;
using YukMain.Host.Core.Controllers;

namespace YukMain.Host.Desktop;

internal sealed class RemoteControllerReceiver
{
    private readonly ControllerManager _controllers;
    private readonly Action<int, ControllerState> _onApplied;
    private readonly Action<string> _onStatus;
    private readonly Dictionary<int, ulong> _lastSequence = new();
    private readonly Dictionary<int, DateTime> _lastInput = new();
    private readonly DispatcherTimer _watchdog;

    public RemoteControllerReceiver(
        ControllerManager controllers, Action<int, ControllerState> onApplied,
        Action<string> onStatus)
    {
        _controllers = controllers;
        _onApplied = onApplied;
        _onStatus = onStatus;
        _watchdog = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _watchdog.Tick += (_, _) => CheckStaleInputs();
        _watchdog.Start();
    }

    public void Receive(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var body = doc.RootElement;
        if (!body.TryGetProperty("type", out var kind)) return;
        var type = kind.GetString();
        if (type == "controller.reset_all") { ResetAll(); return; }
        if (type is not ("controller.state" or "controller.neutral" or "player.disconnected")) return;
        if (!body.TryGetProperty("slot", out var slotValue) ||
            !slotValue.TryGetInt32(out var slot) || slot is < 2 or > 4) return;
        if (type != "controller.state") { Reset(slot); return; }

        if (!body.TryGetProperty("seq", out var sequenceValue) ||
            !sequenceValue.TryGetUInt64(out var sequence) ||
            sequence == 0 || (_lastSequence.TryGetValue(slot, out var prior) && sequence <= prior))
            return;

        var state = DecodeState(body);
        _lastSequence[slot] = sequence;
        _lastInput[slot] = DateTime.UtcNow;
        if (!_controllers.IsConnected(slot)) _controllers.Create(slot);
        _controllers.Submit(slot, state);
        _onApplied(slot, state);
    }

    private static ControllerState DecodeState(JsonElement value)
    {
        var buttons = ControllerButtons.None;
        if (value.TryGetProperty("buttons", out var buttonArray) &&
            buttonArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var button in buttonArray.EnumerateArray())
            {
                buttons |= button.GetString() switch
                {
                    "A" => ControllerButtons.A,
                    "B" => ControllerButtons.B,
                    "X" => ControllerButtons.X,
                    "Y" => ControllerButtons.Y,
                    "LB" => ControllerButtons.LeftBumper,
                    "RB" => ControllerButtons.RightBumper,
                    "Start" => ControllerButtons.Start,
                    "Back" => ControllerButtons.Back,
                    "LS" => ControllerButtons.LeftStick,
                    "RS" => ControllerButtons.RightStick,
                    "Guide" => ControllerButtons.Guide,
                    _ => ControllerButtons.None
                };
            }
        }
        var direction = value.TryGetProperty("dpad", out var dpad)
            ? dpad.GetString() : "none";
        static float Axis(JsonElement element, string name)
        {
            return element.TryGetProperty(name, out var number) && number.TryGetSingle(out var v) ? v : 0;
        }
        static (float x, float y) Stick(JsonElement element, string name)
        {
            if (!element.TryGetProperty(name, out var stick) ||
                stick.ValueKind != JsonValueKind.Object) return (0, 0);
            return (Axis(stick, "x"), Axis(stick, "y"));
        }
        var left = Stick(value, "leftStick");
        var right = Stick(value, "rightStick");
        return new ControllerState
        {
            Buttons = buttons,
            DPad = direction switch
            {
                "up" => DPadDirection.Up,
                "up-right" => DPadDirection.UpRight,
                "right" => DPadDirection.Right,
                "down-right" => DPadDirection.DownRight,
                "down" => DPadDirection.Down,
                "down-left" => DPadDirection.DownLeft,
                "left" => DPadDirection.Left,
                "up-left" => DPadDirection.UpLeft,
                _ => DPadDirection.None
            },
            LeftX = left.x, LeftY = left.y,
            RightX = right.x, RightY = right.y,
            LeftTrigger = Axis(value, "leftTrigger"),
            RightTrigger = Axis(value, "rightTrigger")
        }.Clamp();
    }

    private void CheckStaleInputs()
    {
        foreach (var pair in _lastInput.ToArray())
        {
            if (DateTime.UtcNow - pair.Value > TimeSpan.FromMilliseconds(1200))
            {
                // Fail safe: release held inputs if no fresh snapshot arrives.
                Reset(pair.Key);
                _onStatus($"P{pair.Key}: remote input timed out; released");
            }
        }
    }

    public void Reset(int slot)
    {
        _lastInput.Remove(slot);
        _lastSequence.Remove(slot);
        if (_controllers.IsConnected(slot)) _controllers.Reset(slot);
        _onApplied(slot, default);
    }

    public void ResetAll()
    {
        foreach (var slot in new[] { 2, 3, 4 }) Reset(slot);
        _onStatus("Bridge disconnected: all remote inputs released.");
    }

    public void Stop() => _watchdog.Stop();
}

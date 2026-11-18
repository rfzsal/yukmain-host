using HIDMaestro;
using YukMain.Host.Core.Controllers;

namespace YukMain.Host.HidMaestro;

internal sealed class HidMaestroSession(HMController controller) : IControllerSession
{
    private readonly HMController _controller = controller;
    private bool _disposed;

    public void Submit(in ControllerState state)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var frame = state.Clamp();
        var hmState = new HMGamepadState
        {
            Buttons = ConvertButtons(frame.Buttons),
            Hat = (HMHat)frame.DPad,
            Axes = HMGamepadStateHelpers.StandardAxes(
                _controller.Profile,
                leftStickX: ToHidAxis(frame.LeftX),
                leftStickY: ToHidAxis(frame.LeftY),
                rightStickX: ToHidAxis(frame.RightX),
                rightStickY: ToHidAxis(frame.RightY),
                leftTrigger: frame.LeftTrigger,
                rightTrigger: frame.RightTrigger)
        };
        _controller.SubmitState(in hmState);
    }

    public void Dispose()
    {
        if (_disposed) return;
        try { Submit(default); }
        finally
        {
            _disposed = true;
            _controller.Dispose();
        }
    }

    private static float ToHidAxis(float axis) => (Math.Clamp(axis, -1f, 1f) + 1f) / 2f;

    private static HMButton ConvertButtons(ControllerButtons buttons)
    {
        HMButton result = HMButton.None;
        if (buttons.HasFlag(ControllerButtons.A)) result |= HMButton.A;
        if (buttons.HasFlag(ControllerButtons.B)) result |= HMButton.B;
        if (buttons.HasFlag(ControllerButtons.X)) result |= HMButton.X;
        if (buttons.HasFlag(ControllerButtons.Y)) result |= HMButton.Y;
        if (buttons.HasFlag(ControllerButtons.LeftBumper)) result |= HMButton.LeftBumper;
        if (buttons.HasFlag(ControllerButtons.RightBumper)) result |= HMButton.RightBumper;
        if (buttons.HasFlag(ControllerButtons.Back)) result |= HMButton.Back;
        if (buttons.HasFlag(ControllerButtons.Start)) result |= HMButton.Start;
        if (buttons.HasFlag(ControllerButtons.LeftStick)) result |= HMButton.LeftStick;
        if (buttons.HasFlag(ControllerButtons.RightStick)) result |= HMButton.RightStick;
        if (buttons.HasFlag(ControllerButtons.Guide)) result |= HMButton.Guide;
        return result;
    }
}

namespace YukMain.Host.Core.Controllers;

/// <summary>
/// A complete, source-agnostic gamepad snapshot. Stick axes range -1..1
/// (X positive right; Y positive up), triggers range 0..1.
/// Passing the default state always releases all inputs.
/// </summary>
public readonly record struct ControllerState
{
    public ControllerButtons Buttons { get; init; }
    public DPadDirection DPad { get; init; }
    public float LeftX { get; init; }
    public float LeftY { get; init; }
    public float RightX { get; init; }
    public float RightY { get; init; }
    public float LeftTrigger { get; init; }
    public float RightTrigger { get; init; }

    public ControllerState Clamp() => this with
    {
        LeftX = ClampFinite(LeftX, -1, 1),
        LeftY = ClampFinite(LeftY, -1, 1),
        RightX = ClampFinite(RightX, -1, 1),
        RightY = ClampFinite(RightY, -1, 1),
        LeftTrigger = ClampFinite(LeftTrigger, 0, 1),
        RightTrigger = ClampFinite(RightTrigger, 0, 1)
    };

    private static float ClampFinite(float value, float minimum, float maximum) =>
        float.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : 0;
}

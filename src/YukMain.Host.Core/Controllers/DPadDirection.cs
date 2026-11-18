namespace YukMain.Host.Core.Controllers;

// Matches the HIDMaestro HMHat direction convention (0 = neutral, 1..8 clockwise).
public enum DPadDirection : byte
{
    None = 0,
    Up = 1,
    UpRight = 2,
    Right = 3,
    DownRight = 4,
    Down = 5,
    DownLeft = 6,
    Left = 7,
    UpLeft = 8
}

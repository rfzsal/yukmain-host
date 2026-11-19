using HIDMaestro;
using YukMain.Host.Core.Controllers;

namespace YukMain.Host.HidMaestro;

/// <summary>
/// The only project that references HIDMaestro. Keep an elevated process
/// boundary around this adapter when networking is introduced.
/// </summary>
public sealed class HidMaestroBackend : IControllerBackend
{
    private readonly HMContext _context = new();
    private readonly HMProfile _profile;
    private bool _ready;
    private bool _disposed;

    public HidMaestroBackend()
    {
        _context.LoadDefaultProfiles();
        _profile = _context.GetProfile("xbox-360-wired")
            ?? throw new InvalidOperationException("HIDMaestro Xbox 360 profile was not found.");
    }

    public string Name => "HIDMaestro · Xbox 360 (wired)";

    public void Initialize()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_ready) return;
        _context.InstallDriver();
        _ready = true;
    }

    public IControllerSession Create(int playerNumber)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_ready) throw new InvalidOperationException("Install/check the driver first.");
        var controller = _context.CreateController(_profile, $"yukmain-player-{playerNumber}");
        return new HidMaestroSession(controller);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _context.Dispose();
    }
}

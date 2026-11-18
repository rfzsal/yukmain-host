namespace YukMain.Host.Core.Controllers;

/// <summary>
/// Owns local slots P2-P4. All session state changes are serialized so a
/// future WebSocket input provider can reuse the same interface.
/// </summary>
public sealed class ControllerManager : IDisposable
{
    private readonly IControllerBackend _backend;
    private readonly Dictionary<int, IControllerSession> _sessions = new();
    private readonly object _gate = new();
    private bool _disposed;

    public ControllerManager(IControllerBackend backend) =>
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));

    public string BackendName => _backend.Name;

    public void Initialize()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            _backend.Initialize();
        }
    }

    public bool IsConnected(int player)
    {
        ValidatePlayer(player);
        lock (_gate)
        {
            ThrowIfDisposed();
            return _sessions.ContainsKey(player);
        }
    }

    public void Create(int player)
    {
        ValidatePlayer(player);
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_sessions.ContainsKey(player))
                throw new InvalidOperationException($"Player {player} already has a controller.");

            _sessions.Add(player, _backend.Create(player));
        }
    }

    public void Submit(int player, in ControllerState state)
    {
        ValidatePlayer(player);
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_sessions.TryGetValue(player, out var session))
                throw new InvalidOperationException($"Player {player} is not connected.");

            var normalized = state.Clamp();
            session.Submit(in normalized);
        }
    }

    public void Reset(int player) => Submit(player, default);

    public void Remove(int player)
    {
        ValidatePlayer(player);
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_sessions.Remove(player, out var session)) return;

            try { session.Submit(default); }
            finally { session.Dispose(); }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;

            foreach (var session in _sessions.Values)
            {
                try { session.Submit(default); }
                catch { /* Continue cleaning up other controllers. */ }
                try { session.Dispose(); }
                catch { /* Best-effort shutdown; driver owns process-exit cleanup too. */ }
            }

            _sessions.Clear();
            _backend.Dispose();
        }
    }

    private static void ValidatePlayer(int player)
    {
        if (player is < 2 or > 4)
            throw new ArgumentOutOfRangeException(nameof(player), "Only Player 2, 3 and 4 are supported.");
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(_disposed, this);
}

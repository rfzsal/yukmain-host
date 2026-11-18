using YukMain.Host.Core.Controllers;

var tests = new (string Name, Action Run)[]
{
    ("rejects invalid player slots", RejectInvalidSlots),
    ("creates three isolated controllers", CreateThreeIsolatedControllers),
    ("normalizes invalid axis values", NormalizeAxes),
    ("releases state before removing controller", RemoveReleasesState),
    ("cleans up every controller on shutdown", DisposesAll)
};

var failed = 0;
foreach (var (name, run) in tests)
{
    try
    {
        run();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.Error.WriteLine($"FAIL {name}: {ex}");
    }
}

return failed == 0 ? 0 : 1;

static void RejectInvalidSlots()
{
    using var manager = new ControllerManager(new FakeBackend());
    Expect<ArgumentOutOfRangeException>(() => manager.Create(1));
    Expect<ArgumentOutOfRangeException>(() => manager.IsConnected(5));
}

static void CreateThreeIsolatedControllers()
{
    var backend = new FakeBackend();
    using var manager = new ControllerManager(backend);
    manager.Initialize();
    foreach (var slot in new[] { 2, 3, 4 }) manager.Create(slot);

    manager.Submit(2, new ControllerState { Buttons = ControllerButtons.A });
    manager.Submit(3, new ControllerState { Buttons = ControllerButtons.B });
    Check(backend.Sessions[2].Last.Buttons == ControllerButtons.A, "P2 button");
    Check(backend.Sessions[3].Last.Buttons == ControllerButtons.B, "P3 button");
    Check(backend.Sessions[4].Last.Buttons == ControllerButtons.None, "P4 neutral");
    Expect<InvalidOperationException>(() => manager.Create(3));
}

static void NormalizeAxes()
{
    var backend = new FakeBackend();
    using var manager = new ControllerManager(backend);
    manager.Initialize();
    manager.Create(2);
    manager.Submit(2, new ControllerState { LeftX = 4, LeftY = float.NaN, RightTrigger = -1 });
    var s = backend.Sessions[2].Last;
    Check(s.LeftX == 1 && s.LeftY == 0 && s.RightTrigger == 0, "axes clamped");
}

static void RemoveReleasesState()
{
    var backend = new FakeBackend();
    using var manager = new ControllerManager(backend);
    manager.Initialize();
    manager.Create(2);
    manager.Submit(2, new ControllerState { Buttons = ControllerButtons.X });
    manager.Remove(2);
    var session = backend.Sessions[2];
    Check(session.Disposed && session.Last == default, "neutral snapshot before dispose");
    Check(!manager.IsConnected(2), "slot cleared");
    Expect<InvalidOperationException>(() => manager.Submit(2, default));
}

static void DisposesAll()
{
    var backend = new FakeBackend();
    var manager = new ControllerManager(backend);
    manager.Initialize();
    manager.Create(2);
    manager.Create(4);
    manager.Dispose();
    Check(backend.Disposed, "backend disposed");
    Check(backend.Sessions.Values.All(s => s.Disposed && s.Last == default), "sessions neutral");
}

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

static void Expect<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new Exception($"Expected {typeof(T).Name}.");
}

sealed class FakeBackend : IControllerBackend
{
    public string Name => "fake";
    public bool Disposed { get; private set; }
    public bool Ready { get; private set; }
    public Dictionary<int, FakeSession> Sessions { get; } = new();

    public void Initialize() => Ready = true;
    public IControllerSession Create(int playerNumber)
    {
        if (!Ready) throw new InvalidOperationException("Initialize first.");
        var session = new FakeSession();
        Sessions.Add(playerNumber, session);
        return session;
    }

    public void Dispose() => Disposed = true;
}

sealed class FakeSession : IControllerSession
{
    public ControllerState Last { get; private set; }
    public bool Disposed { get; private set; }
    public void Submit(in ControllerState state)
    {
        if (Disposed) throw new ObjectDisposedException(nameof(FakeSession));
        Last = state;
    }
    public void Dispose() => Disposed = true;
}

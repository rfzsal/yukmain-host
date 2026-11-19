namespace YukMain.Host.Core.Controllers;

/// <summary>Backend initialization is explicit because it may modify Windows driver state.</summary>
public interface IControllerBackend : IDisposable
{
    string Name { get; }
    void Initialize();
    IControllerSession Create(int playerNumber);
}

public interface IControllerSession : IDisposable
{
    void Submit(in ControllerState state);
}

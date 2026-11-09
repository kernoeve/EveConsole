namespace EveConsole.ViewModels;

/// <summary>
/// A view model that can be waiting for a key: the next key or mouse button pressed in its window is taken as the key
/// being chosen (see Views/KeyCaptureHook). The Mumble tool's keys, and push-to-talk for the agent.
/// </summary>
public interface IKeyCaptureHost
{
    /// <summary>Waiting for the next key or button pressed.</summary>
    bool IsCapturingKey { get; }

    void CancelKeyCapture();

    /// <summary>Takes what was pressed, as a Windows key code. Zero is something that cannot be used: it keeps waiting.</summary>
    void TakeCapturedKey(int vk);
}

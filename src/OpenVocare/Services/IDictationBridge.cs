namespace OpenVocare.Services;

public sealed record DictationBridgeResult(
    bool IsSuccess,
    string Message,
    string? Transcript = null,
    WindowTarget Target = default);

public interface IDictationBridge
{
    bool IsSessionActive { get; }
    DictationBridgeResult Probe();
    Task<DictationBridgeResult> StartAsync(CancellationToken cancellationToken = default);
    Task<DictationBridgeResult> StopAndReadAsync(CancellationToken cancellationToken = default);
    Task CancelAsync(CancellationToken cancellationToken = default);
}

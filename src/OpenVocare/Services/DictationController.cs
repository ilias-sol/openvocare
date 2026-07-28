using System.Diagnostics.CodeAnalysis;
using System.Diagnostics;
using OpenVocare.Models;

namespace OpenVocare.Services;

public enum DictationState
{
    Ready,
    Listening,
    Retrieving
}

public sealed record DictationStatus(DictationState State, string Message, bool IsError = false);
public sealed record TranscriptDelivered(string Text, PasteResult Result);

[SuppressMessage("Design", "CA1001:TypesThatOwnDisposableFieldsShouldBeDisposable", Justification = "The semaphore lives for the application lifetime.")]
public sealed class DictationController(
    IDictationBridge bridge,
    TextInjectionService textInjection,
    ITranscriptRewriteService? rewriteService = null,
    Func<RewriteSettings>? rewriteSettings = null,
    Func<bool>? restorePreviousClipboard = null,
    Func<TimeSpan>? maximumRecordingDuration = null)
{
    internal static readonly TimeSpan DefaultMaximumRecordingDuration =
        TimeSpan.FromMinutes(2);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _operationCancellation;
    private CancellationTokenSource? _recordingLimitCancellation;

    public DictationState State { get; private set; } = DictationState.Ready;
    public bool CanCancel => State != DictationState.Ready;
    public event EventHandler<DictationStatus>? StatusChanged;
    public event EventHandler<TranscriptDelivered>? TranscriptDelivered;

    public DictationBridgeResult Probe() => bridge.Probe();

    public async Task ToggleAsync()
    {
        if (!await _gate.WaitAsync(0)) return;
        try
        {
            if (State == DictationState.Ready)
            {
                await BeginAsync();
            }
            else if (State == DictationState.Listening)
            {
                await CompleteAsync();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StartHoldAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (State == DictationState.Ready)
            {
                await BeginAsync();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopHoldAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (State == DictationState.Listening)
            {
                await CompleteAsync();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CancelAsync()
    {
        CancelRecordingLimit();
        _operationCancellation?.Cancel();
        await _gate.WaitAsync();
        try
        {
            await bridge.CancelAsync();
        }
        finally
        {
            SetStatus(DictationState.Ready, "Dictation cancelled.");
            _gate.Release();
        }
    }

    private async Task BeginAsync()
    {
        using CancellationTokenSource cancellation = new();
        _operationCancellation = cancellation;
        try
        {
            DictationBridgeResult result = await bridge.StartAsync(cancellation.Token);
            SetStatus(
                result.IsSuccess ? DictationState.Listening : DictationState.Ready,
                result.Message,
                !result.IsSuccess);
            if (result.IsSuccess)
            {
                StartRecordingLimit();
            }
        }
        catch (OperationCanceledException)
        {
            SetStatus(DictationState.Ready, "Dictation cancelled.");
        }
        catch
        {
            try { await bridge.CancelAsync(); }
            catch { }
            SetStatus(DictationState.Ready, "Dictation could not be started.", true);
            throw;
        }
        finally
        {
            _operationCancellation = null;
        }
    }

    private async Task CompleteAsync()
    {
        CancelRecordingLimit();
        SetStatus(DictationState.Retrieving, "Waiting for ChatGPT transcription…");
        using CancellationTokenSource cancellation = new();
        _operationCancellation = cancellation;
        try
        {
            DictationBridgeResult result = await bridge.StopAndReadAsync(cancellation.Token);
            if (!result.IsSuccess || string.IsNullOrWhiteSpace(result.Transcript))
            {
                SetStatus(DictationState.Ready, result.Message, true);
                return;
            }

            string deliveredText = result.Transcript;
            bool rewriteFailed = false;
            RewriteSettings activeRewrite = rewriteSettings?.Invoke() ?? new RewriteSettings();
            if (activeRewrite.Mode != RewriteMode.Verbatim && rewriteService is not null)
            {
                SetStatus(DictationState.Retrieving, "Rewriting transcript with Luna...");
                try
                {
                    deliveredText = await rewriteService.RewriteAsync(
                        result.Transcript, activeRewrite, cancellation.Token);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    AppLog.Write(
                        "Transcript rewrite failed; delivering verbatim text "
                        + $"({exception.GetType().Name}).");
                    rewriteFailed = true;
                }
            }

            Stopwatch deliveryTimer = Stopwatch.StartNew();
            bool restoreClipboard = restorePreviousClipboard?.Invoke() == true;
            PasteResult paste = await textInjection.CopyAndTryPasteAsync(
                deliveredText,
                result.Target,
                restoreClipboard,
                cancellation.Token);
            AppLog.WriteDeferred(
                $"Delivery timing: total={deliveryTimer.Elapsed.TotalMilliseconds:F1}ms, "
                + $"result={paste}, rewrite={activeRewrite.Mode}.");
            (string message, bool error) = paste switch
            {
                PasteResult.Pasted => ("Transcript pasted and copied to the clipboard.", false),
                PasteResult.PastedClipboardRestored => ("Transcript pasted. Previous clipboard restored.", false),
                PasteResult.CopiedFocusRestoreFailed => ("Transcript copied, but the destination application could not be focused.", true),
                PasteResult.CopiedElevatedTarget => ("Transcript copied. Run this app as administrator to paste into elevated applications.", true),
                PasteResult.CopiedPasswordField => ("Transcript copied. Automatic paste is blocked for password fields.", true),
                PasteResult.CopiedShortcutStillHeld => ("Transcript copied. Release the shortcut modifiers before automatic paste.", true),
                PasteResult.ClipboardChangedBeforePaste => ("Clipboard changed before paste, so automatic paste was cancelled.", true),
                PasteResult.CopiedInputBlocked => ("Transcript copied, but Windows blocked automatic paste.", true),
                _ => ("The clipboard was unavailable; the transcript could not be delivered.", true)
            };
            if (rewriteFailed && paste != PasteResult.ClipboardUnavailable)
            {
                message = "Rewrite was unavailable, so the verbatim transcript was delivered.";
                error = true;
            }
            if (TranscriptWasDelivered(paste))
            {
                TranscriptDelivered?.Invoke(this, new TranscriptDelivered(deliveredText, paste));
            }
            SetStatus(DictationState.Ready, message, error);
        }
        catch (OperationCanceledException)
        {
            try
            {
                await bridge.CancelAsync();
            }
            finally
            {
                SetStatus(DictationState.Ready, "Dictation cancelled.");
            }
        }
        catch
        {
            try { await bridge.CancelAsync(); }
            catch { }
            SetStatus(DictationState.Ready, "Dictation failed unexpectedly.", true);
            throw;
        }
        finally
        {
            _operationCancellation = null;
        }
    }

    internal static bool TranscriptWasDelivered(PasteResult result) =>
        result is not PasteResult.ClipboardUnavailable
            and not PasteResult.ClipboardChangedBeforePaste;

    private void StartRecordingLimit()
    {
        CancelRecordingLimit();
        _recordingLimitCancellation = new CancellationTokenSource();
        TimeSpan duration =
            maximumRecordingDuration?.Invoke() ?? DefaultMaximumRecordingDuration;
        _ = StopAtRecordingLimitAsync(duration, _recordingLimitCancellation.Token);
    }

    private async Task StopAtRecordingLimitAsync(
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(duration, cancellationToken);
            await _gate.WaitAsync(cancellationToken);
            try
            {
                if (State == DictationState.Listening)
                {
                    string durationLabel = duration.TotalMinutes == 1
                        ? "One-minute"
                        : $"{duration.TotalMinutes:0}-minute";
                    SetStatus(
                        DictationState.Retrieving,
                        $"{durationLabel} recording limit reached. Transcribing now.");
                    await CompleteAsync();
                }
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // Expected whenever the user stops or cancels before the limit.
        }
    }

    private void CancelRecordingLimit()
    {
        CancellationTokenSource? cancellation = _recordingLimitCancellation;
        _recordingLimitCancellation = null;
        cancellation?.Cancel();
        cancellation?.Dispose();
    }

    private void SetStatus(DictationState state, string message, bool isError = false)
    {
        State = state;
        StatusChanged?.Invoke(this, new DictationStatus(state, message, isError));
    }
}

using System.Diagnostics;

namespace OpenVocare.Services;

public sealed class DirectCodexTranscriptionBridge(
    IAudioRecorder recorder,
    ICodexTranscriptionClient transcriptionClient,
    Func<WindowTarget>? captureDestination = null) : IDictationBridge
{
    private static readonly TimeSpan MinimumRecordingDuration = TimeSpan.FromMilliseconds(350);
    private readonly Func<WindowTarget> _captureDestination =
        captureDestination ?? WindowTarget.Capture;
    private Stopwatch? _recordingClock;

    public bool IsSessionActive => recorder.IsRecording;

    public DictationBridgeResult Probe() => transcriptionClient.Probe();

    public async Task<DictationBridgeResult> StartAsync(CancellationToken cancellationToken = default)
    {
        if (IsSessionActive)
        {
            return new DictationBridgeResult(false, "A dictation session is already active.");
        }

        DictationBridgeResult readiness = transcriptionClient.Probe();
        if (!readiness.IsSuccess)
        {
            return readiness;
        }

        try
        {
            await recorder.StartAsync(cancellationToken);
            _ = transcriptionClient.WarmUpAsync(cancellationToken);
            _recordingClock = Stopwatch.StartNew();
            return new DictationBridgeResult(true, "Listening through ChatGPT transcription.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AudioCaptureException exception)
        {
            await recorder.CancelAsync();
            return new DictationBridgeResult(false, exception.Message);
        }
        catch (Exception)
        {
            await recorder.CancelAsync();
            return new DictationBridgeResult(false, "The microphone could not be started.");
        }
    }

    public async Task<DictationBridgeResult> StopAndReadAsync(CancellationToken cancellationToken = default)
    {
        if (!IsSessionActive)
        {
            return new DictationBridgeResult(false, "No direct dictation session is active.");
        }

        // Lock the user's destination as soon as recording stops. This lets the
        // user switch applications while speaking without allowing later
        // network timing or popups to redirect the completed transcript.
        WindowTarget target = _captureDestination();
        TimeSpan duration = _recordingClock?.Elapsed ?? TimeSpan.Zero;
        _recordingClock = null;

        Stopwatch totalTimer = Stopwatch.StartNew();
        Stopwatch stageTimer = Stopwatch.StartNew();
        byte[] audio;
        try
        {
            audio = await recorder.StopAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            await recorder.CancelAsync();
            return new DictationBridgeResult(false, "The recording could not be completed.", Target: target);
        }

        try
        {
            double captureStopMilliseconds = stageTimer.Elapsed.TotalMilliseconds;
            if (duration < MinimumRecordingDuration)
            {
                return new DictationBridgeResult(false, "The shortcut was released too quickly.", Target: target);
            }
            if (!WavAudioInspector.HasAudibleSignal(audio))
            {
                return new DictationBridgeResult(
                    false,
                    "No microphone signal was detected. Check the selected input and try again.",
                    Target: target);
            }

            stageTimer.Restart();
            string transcript = await transcriptionClient.TranscribeAsync(audio, cancellationToken);
            double transcriptionMilliseconds = stageTimer.Elapsed.TotalMilliseconds;
            AppLog.WriteDeferred(
                $"Dictation pipeline timing: total={totalTimer.Elapsed.TotalMilliseconds:F0}ms, "
                + $"capture-stop={captureStopMilliseconds:F0}ms, "
                + $"transcription={transcriptionMilliseconds:F0}ms, "
                + $"audio-bytes={audio.Length}, recording={duration.TotalMilliseconds:F0}ms.");
            return string.IsNullOrWhiteSpace(transcript)
                ? new DictationBridgeResult(false, "ChatGPT did not detect any speech.", Target: target)
                : new DictationBridgeResult(true, "Direct transcription completed.", transcript, target);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (CodexTranscriptionException exception)
        {
            return new DictationBridgeResult(false, exception.Message, Target: target);
        }
        finally
        {
            Array.Clear(audio);
        }
    }

    public async Task CancelAsync(CancellationToken cancellationToken = default)
    {
        await recorder.CancelAsync();
        _recordingClock = null;
    }
}

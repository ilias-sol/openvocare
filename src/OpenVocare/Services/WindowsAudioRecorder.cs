using Windows.Media;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.Storage.Streams;

namespace OpenVocare.Services;

public interface IAudioRecorder
{
    bool IsRecording { get; }
    Task StartAsync(CancellationToken cancellationToken = default);
    Task<byte[]> StopAsync(CancellationToken cancellationToken = default);
    Task CancelAsync();
}

public sealed class AudioCaptureException(string message, Exception innerException)
    : Exception(message, innerException);

public sealed class WindowsAudioRecorder(
    Func<string?>? selectedDeviceId = null,
    Func<string?>? selectedDeviceName = null) : IAudioRecorder, IDisposable
{
    private MediaCapture? _capture;
    private InMemoryRandomAccessStream? _stream;

    public bool IsRecording => _capture is not null;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (IsRecording)
        {
            throw new InvalidOperationException("Audio recording is already active.");
        }

        MediaCapture capture = new();
        InMemoryRandomAccessStream stream = new();
        string? deviceId = selectedDeviceId?.Invoke();
        string? deviceName = selectedDeviceName?.Invoke();
        try
        {
            MediaCaptureInitializationSettings settings = new()
            {
                StreamingCaptureMode = StreamingCaptureMode.Audio,
                MediaCategory = MediaCategory.Speech,
                AudioProcessing = AudioProcessing.Default
            };
            if (!string.IsNullOrWhiteSpace(deviceId))
            {
                settings.AudioDeviceId = deviceId;
            }
            await capture.InitializeAsync(settings).AsTask(cancellationToken);
            MediaEncodingProfile profile = MediaEncodingProfile.CreateWav(AudioEncodingQuality.Auto);
            await capture.StartRecordToStreamAsync(profile, stream).AsTask(cancellationToken);
            _capture = capture;
            _stream = stream;
        }
        catch (OperationCanceledException)
        {
            capture.Dispose();
            stream.Dispose();
            throw;
        }
        catch (Exception exception)
        {
            capture.Dispose();
            stream.Dispose();
            if (!string.IsNullOrWhiteSpace(deviceId))
            {
                string label = string.IsNullOrWhiteSpace(deviceName)
                    ? "The selected microphone"
                    : $"The selected microphone ({deviceName})";
                throw new AudioCaptureException(
                    $"{label} is unavailable. Choose another microphone in settings.",
                    exception);
            }
            throw;
        }
    }

    public async Task<byte[]> StopAsync(CancellationToken cancellationToken = default)
    {
        MediaCapture capture = _capture
            ?? throw new InvalidOperationException("Audio recording is not active.");
        InMemoryRandomAccessStream stream = _stream
            ?? throw new InvalidOperationException("The audio stream is unavailable.");
        _capture = null;
        _stream = null;

        try
        {
            await capture.StopRecordAsync().AsTask(cancellationToken);
            stream.Seek(0);
            if (stream.Size > int.MaxValue)
            {
                throw new InvalidOperationException("The recording is too large.");
            }

            byte[] bytes = new byte[(int)stream.Size];
            using DataReader reader = new(stream.GetInputStreamAt(0));
            uint loaded = await reader.LoadAsync((uint)bytes.Length).AsTask(cancellationToken);
            if (loaded != bytes.Length)
            {
                Array.Resize(ref bytes, (int)loaded);
            }
            reader.ReadBytes(bytes);
            return bytes;
        }
        finally
        {
            capture.Dispose();
            stream.Dispose();
        }
    }

    public async Task CancelAsync()
    {
        MediaCapture? capture = _capture;
        InMemoryRandomAccessStream? stream = _stream;
        _capture = null;
        _stream = null;
        if (capture is not null)
        {
            try { await capture.StopRecordAsync(); }
            catch { }
            capture.Dispose();
        }
        stream?.Dispose();
    }

    public void Dispose() => CancelAsync().GetAwaiter().GetResult();
}

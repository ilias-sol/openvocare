using OpenVocare.Services;

namespace OpenVocare.Tests;

public sealed class DirectCodexTranscriptionBridgeTests
{
    [Fact]
    public async Task Destination_IsCapturedWhenRecordingStops_NotWhenItStarts()
    {
        FakeRecorder recorder = new();
        FakeTranscriptionClient client = new();
        WindowTarget stopDestination = new(new IntPtr(84), 12);
        int captureCalls = 0;
        DirectCodexTranscriptionBridge bridge = new(
            recorder,
            client,
            () =>
            {
                captureCalls++;
                return stopDestination;
            });

        DictationBridgeResult started = await bridge.StartAsync();

        Assert.True(started.IsSuccess);
        Assert.Equal(default, started.Target);
        Assert.Equal(0, captureCalls);

        await Task.Delay(375);
        DictationBridgeResult completed = await bridge.StopAndReadAsync();

        Assert.True(completed.IsSuccess);
        Assert.Equal("Captured transcript.", completed.Transcript);
        Assert.Equal(stopDestination, completed.Target);
        Assert.Equal(1, captureCalls);
    }

    private sealed class FakeRecorder : IAudioRecorder
    {
        public bool IsRecording { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            IsRecording = true;
            return Task.CompletedTask;
        }

        public Task<byte[]> StopAsync(CancellationToken cancellationToken = default)
        {
            IsRecording = false;
            return Task.FromResult("audible-test-data"u8.ToArray());
        }

        public Task CancelAsync()
        {
            IsRecording = false;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeTranscriptionClient : ICodexTranscriptionClient
    {
        public DictationBridgeResult Probe() => new(true, "Ready.");

        public Task WarmUpAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<string> TranscribeAsync(
            byte[] wavAudio,
            CancellationToken cancellationToken = default) =>
            Task.FromResult("Captured transcript.");
    }
}

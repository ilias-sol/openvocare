using OpenVocare.Services;

namespace OpenVocare.Tests;

public sealed class DictationControllerTests
{
    [Fact]
    public async Task Toggle_StartsListeningThenRetrievesWithoutSubmitting()
    {
        FakeBridge bridge = new();
        DictationController controller = new(bridge, new TextInjectionService());

        await controller.ToggleAsync();
        Assert.Equal(DictationState.Listening, controller.State);
        Assert.Equal(1, bridge.StartCalls);

        await controller.ToggleAsync();
        Assert.Equal(DictationState.Ready, controller.State);
        Assert.Equal(1, bridge.StopCalls);
    }

    [Fact]
    public async Task Hold_StartsOnPressAndStopsOnRelease()
    {
        FakeBridge bridge = new();
        DictationController controller = new(bridge, new TextInjectionService());

        await controller.StartHoldAsync();
        await controller.StopHoldAsync();

        Assert.Equal(DictationState.Ready, controller.State);
        Assert.Equal(1, bridge.StartCalls);
        Assert.Equal(1, bridge.StopCalls);
    }

    [Fact]
    public async Task Cancel_ReturnsToReadyAndDelegatesComposerCleanup()
    {
        FakeBridge bridge = new();
        DictationController controller = new(bridge, new TextInjectionService());
        await controller.ToggleAsync();

        await controller.CancelAsync();

        Assert.Equal(DictationState.Ready, controller.State);
        Assert.Equal(1, bridge.CancelCalls);
    }

    [Fact]
    public async Task FailedCompletion_ResetsStateAndCancelsTheBridge()
    {
        FakeBridge bridge = new() { StopError = new InvalidOperationException("boom") };
        DictationController controller = new(bridge, new TextInjectionService());
        await controller.StartHoldAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(controller.StopHoldAsync);

        Assert.Equal(DictationState.Ready, controller.State);
        Assert.Equal(1, bridge.CancelCalls);
    }

    [Fact]
    public async Task FailedCancellation_StillResetsState()
    {
        FakeBridge bridge = new() { CancelError = new InvalidOperationException("boom") };
        DictationController controller = new(bridge, new TextInjectionService());
        await controller.StartHoldAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(controller.CancelAsync);

        Assert.Equal(DictationState.Ready, controller.State);
    }

    private sealed class FakeBridge : IDictationBridge
    {
        public bool IsSessionActive { get; private set; }
        public int StartCalls { get; private set; }
        public int StopCalls { get; private set; }
        public int CancelCalls { get; private set; }
        public Exception? StopError { get; init; }
        public Exception? CancelError { get; init; }

        public DictationBridgeResult Probe() => new(true, "Found.");

        public Task<DictationBridgeResult> StartAsync(CancellationToken cancellationToken = default)
        {
            StartCalls++;
            IsSessionActive = true;
            return Task.FromResult(new DictationBridgeResult(true, "Listening."));
        }

        public Task<DictationBridgeResult> StopAndReadAsync(CancellationToken cancellationToken = default)
        {
            StopCalls++;
            if (StopError is not null)
            {
                return Task.FromException<DictationBridgeResult>(StopError);
            }
            IsSessionActive = false;
            // A failed read keeps the unit test away from the real clipboard while still
            // exercising the controller's complete toggle state transition.
            return Task.FromResult(new DictationBridgeResult(false, "No transcript."));
        }

        public Task CancelAsync(CancellationToken cancellationToken = default)
        {
            CancelCalls++;
            if (CancelError is not null)
            {
                return Task.FromException(CancelError);
            }
            IsSessionActive = false;
            return Task.CompletedTask;
        }
    }
}

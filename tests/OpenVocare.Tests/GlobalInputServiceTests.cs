using OpenVocare.Models;
using OpenVocare.Services;

namespace OpenVocare.Tests;

public sealed class GlobalInputServiceTests
{
    private const int WmXButtonDown = 0x020B;
    private const int WmXButtonUp = 0x020C;
    private const int WmXButtonDoubleClick = 0x020D;
    private const int WmKeyDown = 0x0100;
    private const uint VkEscape = 0x1B;
    private const uint XButton1Data = 1u << 16;
    private const uint XButton2Data = 2u << 16;

    [Fact]
    public void BoundSideButton_ActivatesOnDownAndConsumesDownAndUp()
    {
        ShortcutSettings settings = new()
        {
            MouseButton = MouseShortcutButton.XButton1,
            ConsumeMouseButton = true
        };

        GlobalInputService.MouseHookDecision down = GlobalInputService.EvaluateMouseMessage(WmXButtonDown, XButton1Data, settings);
        GlobalInputService.MouseHookDecision up = GlobalInputService.EvaluateMouseMessage(WmXButtonUp, XButton1Data, settings);
        GlobalInputService.MouseHookDecision doubleClick = GlobalInputService.EvaluateMouseMessage(WmXButtonDoubleClick, XButton1Data, settings);

        Assert.True(down.Press);
        Assert.False(down.Release);
        Assert.True(down.Consume);
        Assert.False(up.Press);
        Assert.True(up.Release);
        Assert.True(up.Consume);
        Assert.False(doubleClick.Press);
        Assert.False(doubleClick.Release);
        Assert.True(doubleClick.Consume);
    }

    [Fact]
    public void OtherSideButton_IsLeftUntouched()
    {
        ShortcutSettings settings = new()
        {
            MouseButton = MouseShortcutButton.XButton1,
            ConsumeMouseButton = true
        };

        GlobalInputService.MouseHookDecision decision = GlobalInputService.EvaluateMouseMessage(WmXButtonUp, XButton2Data, settings);

        Assert.False(decision.Press);
        Assert.False(decision.Release);
        Assert.False(decision.Consume);
    }

    [Fact]
    public void NormalActionSetting_AllowsMatchedButtonMessagesThrough()
    {
        ShortcutSettings settings = new()
        {
            MouseButton = MouseShortcutButton.XButton2,
            ConsumeMouseButton = false
        };

        GlobalInputService.MouseHookDecision down = GlobalInputService.EvaluateMouseMessage(WmXButtonDown, XButton2Data, settings);
        GlobalInputService.MouseHookDecision up = GlobalInputService.EvaluateMouseMessage(WmXButtonUp, XButton2Data, settings);

        Assert.True(down.Press);
        Assert.False(down.Release);
        Assert.False(down.Consume);
        Assert.False(up.Press);
        Assert.True(up.Release);
        Assert.False(up.Consume);
    }

    [Fact]
    public void ShortcutSnapshot_DoesNotTrackLaterDraftChanges()
    {
        ShortcutSettings draft = new()
        {
            KeyboardShortcutEnabled = false,
            Modifiers = HotkeyModifiers.Control,
            Key = "F8",
            MouseButton = MouseShortcutButton.XButton1,
            ConsumeMouseButton = true,
            KeyboardActivationMode = ShortcutActivationMode.Toggle,
            MouseActivationMode = ShortcutActivationMode.Toggle
        };

        ShortcutSettings snapshot = GlobalInputService.CopySettings(draft);
        draft.MouseButton = MouseShortcutButton.XButton2;
        draft.ConsumeMouseButton = false;

        Assert.Equal(MouseShortcutButton.XButton1, snapshot.MouseButton);
        Assert.False(snapshot.KeyboardShortcutEnabled);
        Assert.True(snapshot.ConsumeMouseButton);
        Assert.Equal(ShortcutActivationMode.Toggle, snapshot.KeyboardActivationMode);
        Assert.Equal(ShortcutActivationMode.Toggle, snapshot.MouseActivationMode);
    }

    [Fact]
    public void Escape_IsRecognizedAsTheCancellationKey()
    {
        Assert.True(GlobalInputService.IsCancellationKey(WmKeyDown, VkEscape));
        Assert.False(GlobalInputService.IsCancellationKey(WmKeyDown, 0x20));
    }

    [Fact]
    public void BareTypingKey_IsRejectedAsAnUnsafeGlobalShortcut()
    {
        ShortcutSettings settings = new() { Modifiers = HotkeyModifiers.None, Key = "D" };

        Assert.NotNull(GlobalInputService.GetShortcutValidationError(settings));
    }

    [Fact]
    public void BareFunctionKey_IsAllowed()
    {
        ShortcutSettings settings = new() { Modifiers = HotkeyModifiers.None, Key = "F8" };

        Assert.Null(GlobalInputService.GetShortcutValidationError(settings));
    }

    [Fact]
    public void DisabledKeyboardShortcut_AllowsMouseOnlyActivation()
    {
        ShortcutSettings settings = new()
        {
            KeyboardShortcutEnabled = false,
            Key = "Not a key",
            MouseButton = MouseShortcutButton.XButton1
        };

        Assert.Null(GlobalInputService.GetShortcutValidationError(settings));
    }

    [Fact]
    public void DisablingEveryActivationShortcut_IsRejected()
    {
        ShortcutSettings settings = new()
        {
            KeyboardShortcutEnabled = false,
            MouseButton = MouseShortcutButton.None
        };

        Assert.NotNull(GlobalInputService.GetShortcutValidationError(settings));
    }

    [Fact]
    public void RegistrationAlwaysSuppressesKeyRepeat()
    {
        const uint modNoRepeat = 0x4000;

        Assert.Equal(modNoRepeat, GlobalInputService.RegistrationModifiers(HotkeyModifiers.None));
        Assert.Equal(modNoRepeat | (uint)HotkeyModifiers.Control, GlobalInputService.RegistrationModifiers(HotkeyModifiers.Control));
    }
}

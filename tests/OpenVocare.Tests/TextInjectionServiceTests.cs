using OpenVocare.Services;

namespace OpenVocare.Tests;

public sealed class TextInjectionServiceTests
{
    [Fact]
    public void NativeInputLayout_MatchesWindowsX64Abi()
    {
        Assert.Equal(40, TextInjectionService.NativeInputSize);
        Assert.Equal(8, TextInjectionService.NativeInputUnionOffset);
        Assert.Equal(16, TextInjectionService.NativeKeyboardExtraInfoOffset);
    }

    [Fact]
    public void TargetIdentity_RequiresTheCapturedWindowAndProcess()
    {
        IntPtr handle = new(42);

        Assert.True(TextInjectionService.IsTargetIdentityValid(handle, 7, handle, 7));
        Assert.False(TextInjectionService.IsTargetIdentityValid(handle, 7, new IntPtr(43), 7));
        Assert.False(TextInjectionService.IsTargetIdentityValid(handle, 7, handle, 8));
        Assert.False(TextInjectionService.IsTargetIdentityValid(IntPtr.Zero, 7, IntPtr.Zero, 7));
    }

    [Fact]
    public void ForegroundFastPath_RequiresTheCapturedTarget()
    {
        WindowTarget target = new(new IntPtr(42), 7);

        Assert.True(TextInjectionService.IsTargetAlreadyForeground(
            target, new IntPtr(42), 7));
        Assert.False(TextInjectionService.IsTargetAlreadyForeground(
            target, new IntPtr(43), 7));
        Assert.False(TextInjectionService.IsTargetAlreadyForeground(
            target, new IntPtr(42), 8));
    }

    [Fact]
    public void PasteWorkflow_RequiresAnExplicitCapturedWindowTarget()
    {
        System.Reflection.MethodInfo method = typeof(TextInjectionService)
            .GetMethod(nameof(TextInjectionService.CopyAndTryPasteAsync))!;

        Assert.Contains(method.GetParameters(), parameter => parameter.ParameterType == typeof(WindowTarget));
        Assert.Contains(nameof(PasteResult.CopiedFocusRestoreFailed), Enum.GetNames<PasteResult>());
        Assert.Contains(nameof(PasteResult.PastedClipboardRestored), Enum.GetNames<PasteResult>());
    }

    [Fact]
    public void ClipboardRestore_RequiresTheTranscriptToStillOwnTheClipboard()
    {
        Assert.True(TextInjectionService.CanRestoreClipboard(42, 42));
        Assert.False(TextInjectionService.CanRestoreClipboard(42, 43));
        Assert.False(TextInjectionService.CanRestoreClipboard(0, 0));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(null, true)]
    public void PasswordInspection_FailsClosedWhenWindowsCannotClassifyTheField(
        bool? isPassword,
        bool expected)
    {
        Assert.Equal(expected, TextInjectionService.ShouldBlockPasswordPaste(isPassword));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, true)]
    [InlineData(true, true, false)]
    [InlineData(true, null, true)]
    [InlineData(false, null, true)]
    public void ElevationBoundary_FailsClosedWhenTheTargetCannotBeInspected(
        bool? currentElevated,
        bool? targetElevated,
        bool expected)
    {
        Assert.Equal(
            expected,
            TextInjectionService.ShouldBlockElevationBoundary(
                currentElevated,
                targetElevated));
    }

    [Theory]
    [InlineData(PasteResult.Pasted, true)]
    [InlineData(PasteResult.PastedClipboardRestored, true)]
    [InlineData(PasteResult.CopiedPasswordField, true)]
    [InlineData(PasteResult.ClipboardChangedBeforePaste, false)]
    [InlineData(PasteResult.ClipboardUnavailable, false)]
    public void DeliveryPolicy_ExcludesClipboardRaces(
        PasteResult result,
        bool expected)
    {
        Assert.Equal(expected, DictationController.TranscriptWasDelivered(result));
    }
}

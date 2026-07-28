using OpenVocare.Models;

namespace OpenVocare.Tests;

public sealed class ShortcutCopyTests
{
    [Theory]
    [InlineData(
        ShortcutActivationMode.Hold,
        "Hold while speaking, then focus the destination and release to transcribe and paste.")]
    [InlineData(
        ShortcutActivationMode.Toggle,
        "Press once to start, then focus the destination and press again to transcribe and paste.")]
    public void KeyboardShortcutDescription_ReflectsActivationMode(
        ShortcutActivationMode mode,
        string expected)
    {
        Assert.Equal(expected, MainWindow.GetKeyboardShortcutDescription(mode));
    }

    [Theory]
    [InlineData(
        ShortcutActivationMode.Hold,
        "Keep the shortcut held while speaking.")]
    [InlineData(
        ShortcutActivationMode.Toggle,
        "Press once to start recording and again to stop.")]
    public void KeyboardActivationDescription_ReflectsActivationMode(
        ShortcutActivationMode mode,
        string expected)
    {
        Assert.Equal(expected, MainWindow.GetKeyboardActivationDescription(mode));
    }

    [Theory]
    [InlineData(
        ShortcutActivationMode.Hold,
        "Keep the side button held, then focus the destination and release.")]
    [InlineData(
        ShortcutActivationMode.Toggle,
        "Click once to start, then focus the destination and click again to stop.")]
    public void MouseActivationDescription_ReflectsActivationMode(
        ShortcutActivationMode mode,
        string expected)
    {
        Assert.Equal(expected, MainWindow.GetMouseActivationDescription(mode));
    }
}

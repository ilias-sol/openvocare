using OpenVocare.Models;

namespace OpenVocare.Tests;

public sealed class MainWindowShortcutTests
{
    [Fact]
    public void ActiveShortcutSummary_ShowsKeyboardShortcut_WhenMouseShortcutIsDisabled()
    {
        ShortcutSettings shortcut = new()
        {
            Modifiers = HotkeyModifiers.Control | HotkeyModifiers.Alt,
            Key = "Space"
        };

        Assert.Equal("Ctrl + Alt + Space", MainWindow.FormatActiveShortcuts(shortcut));
    }

    [Theory]
    [InlineData(MouseShortcutButton.XButton1, "Back side button")]
    [InlineData(MouseShortcutButton.XButton2, "Forward side button")]
    public void ActiveShortcutSummary_ShowsBothConfiguredShortcuts(
        MouseShortcutButton mouseButton,
        string expectedSummary)
    {
        ShortcutSettings shortcut = new()
        {
            Modifiers = HotkeyModifiers.Control | HotkeyModifiers.Alt,
            Key = "Space",
            MouseButton = mouseButton
        };

        Assert.Equal($"Ctrl + Alt + Space{Environment.NewLine}{expectedSummary}", MainWindow.FormatActiveShortcuts(shortcut));
    }

    [Fact]
    public void ActiveShortcutSummary_ShowsOnlyMouse_WhenKeyboardShortcutIsDisabled()
    {
        ShortcutSettings shortcut = new()
        {
            KeyboardShortcutEnabled = false,
            MouseButton = MouseShortcutButton.XButton1
        };

        Assert.Equal("Back side button", MainWindow.FormatActiveShortcuts(shortcut));
    }
}

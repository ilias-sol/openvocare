using System.Text.Json.Serialization;

namespace OpenVocare.Models;

public enum MouseShortcutButton
{
    None,
    XButton1,
    XButton2
}

public enum ShortcutActivationMode
{
    Hold,
    Toggle
}

public enum RewriteMode
{
    Verbatim,
    Minimal,
    Professional,
    Ramble,
    Translate,
    Custom
}

[Flags]
public enum HotkeyModifiers : uint
{
    None = 0,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Win = 0x0008
}

public sealed class AppSettings
{
    public ShortcutSettings Shortcut { get; set; } = new();
    public MicrophoneSettings Microphone { get; set; } = new();
    public PrivacySettings Privacy { get; set; } = new();
    public ClipboardSettings Clipboard { get; set; } = new();
    public HistorySettings History { get; set; } = new();
    public RewriteSettings Rewrite { get; set; } = new();
    public RecordingSettings Recording { get; set; } = new();
    public bool LaunchAtLogin { get; set; }
    public bool CloseButtonQuits { get; set; }
}

public sealed class MicrophoneSettings
{
    public string? DeviceId { get; set; }
    public string? DeviceName { get; set; }
}

public sealed class ShortcutSettings
{
    public bool KeyboardShortcutEnabled { get; set; } = true;
    public HotkeyModifiers Modifiers { get; set; } = HotkeyModifiers.Control | HotkeyModifiers.Alt;
    public string Key { get; set; } = "Space";
    public MouseShortcutButton MouseButton { get; set; }
    public bool ConsumeMouseButton { get; set; } = true;
    public ShortcutActivationMode KeyboardActivationMode { get; set; } = ShortcutActivationMode.Hold;
    public ShortcutActivationMode MouseActivationMode { get; set; } = ShortcutActivationMode.Hold;
}

public sealed class PrivacySettings
{
    public bool ErrorNotificationsEnabled { get; set; } = true;
    public bool CompletionNotificationsEnabled { get; set; }

    [JsonPropertyName("NotificationsEnabled")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? LegacyNotificationsEnabled { get; set; }
}

public sealed class ClipboardSettings
{
    public bool RestoreAfterPaste { get; set; }
}

public sealed class HistorySettings
{
    public bool Enabled { get; set; }
}

public sealed class RecordingSettings
{
    public int MaximumDurationMinutes { get; set; } = 2;
}

public sealed class RewriteSettings
{
    public RewriteMode Mode { get; set; } = RewriteMode.Verbatim;
    public string TranslationLanguage { get; set; } = "English";
    public string? ActiveCustomProfileId { get; set; }
    public List<CustomRewriteProfile> CustomProfiles { get; set; } = [];
}

public sealed class CustomRewriteProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New profile";
    public string Instruction { get; set; } =
        "Rewrite in a friendly, concise tone.";

    public override string ToString() => Name;
}

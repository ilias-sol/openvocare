using OpenVocare.Models;
using OpenVocare.Services;

namespace OpenVocare.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"CodexBridge.Tests.{Guid.NewGuid():N}");

    [Fact]
    public async Task SaveAndLoad_RoundTripsBridgeSettings()
    {
        SettingsStore store = new(new AppPaths(_directory));
        AppSettings expected = new()
        {
            Shortcut = new ShortcutSettings
            {
                KeyboardShortcutEnabled = false,
                Key = "F8",
                MouseButton = MouseShortcutButton.XButton2,
                ConsumeMouseButton = false,
                KeyboardActivationMode = ShortcutActivationMode.Toggle,
                MouseActivationMode = ShortcutActivationMode.Toggle
            },
            Microphone = new MicrophoneSettings
            {
                DeviceId = "capture-device-id",
                DeviceName = "Studio microphone"
            },
            Privacy = new PrivacySettings
            {
                ErrorNotificationsEnabled = false,
                CompletionNotificationsEnabled = true
            },
            Clipboard = new ClipboardSettings { RestoreAfterPaste = true },
            History = new HistorySettings { Enabled = true },
            Recording = new RecordingSettings { MaximumDurationMinutes = 10 },
            Rewrite = new RewriteSettings
            {
                Mode = RewriteMode.Custom,
                TranslationLanguage = "German",
                ActiveCustomProfileId = "friendly-email",
                CustomProfiles =
                [
                    new CustomRewriteProfile
                    {
                        Id = "friendly-email",
                        Name = "Friendly email",
                        Instruction = "Turn this into a concise and friendly email."
                    }
                ]
            },
            LaunchAtLogin = true,
            CloseButtonQuits = true
        };

        await store.SaveAsync(expected);
        AppSettings actual = await store.LoadAsync();

        Assert.Equal("F8", actual.Shortcut.Key);
        Assert.False(actual.Shortcut.KeyboardShortcutEnabled);
        Assert.Equal(MouseShortcutButton.XButton2, actual.Shortcut.MouseButton);
        Assert.False(actual.Shortcut.ConsumeMouseButton);
        Assert.Equal(ShortcutActivationMode.Toggle, actual.Shortcut.KeyboardActivationMode);
        Assert.Equal(ShortcutActivationMode.Toggle, actual.Shortcut.MouseActivationMode);
        Assert.Equal("capture-device-id", actual.Microphone.DeviceId);
        Assert.Equal("Studio microphone", actual.Microphone.DeviceName);
        Assert.False(actual.Privacy.ErrorNotificationsEnabled);
        Assert.True(actual.Privacy.CompletionNotificationsEnabled);
        Assert.True(actual.Clipboard.RestoreAfterPaste);
        Assert.True(actual.History.Enabled);
        Assert.Equal(10, actual.Recording.MaximumDurationMinutes);
        Assert.Equal(RewriteMode.Custom, actual.Rewrite.Mode);
        Assert.Equal("German", actual.Rewrite.TranslationLanguage);
        Assert.Equal("friendly-email", actual.Rewrite.ActiveCustomProfileId);
        CustomRewriteProfile profile = Assert.Single(actual.Rewrite.CustomProfiles);
        Assert.Equal("Friendly email", profile.Name);
        Assert.Equal("Turn this into a concise and friendly email.", profile.Instruction);
        Assert.True(actual.LaunchAtLogin);
        Assert.True(actual.CloseButtonQuits);
    }

    [Fact]
    public async Task Load_OldProviderPropertiesAreIgnored()
    {
        AppPaths paths = new(_directory);
        await File.WriteAllTextAsync(paths.SettingsPath, """
            { "Audio": { "DeviceId": "old" }, "Transcription": { "Provider": 5 },
              "Privacy": { "HistoryEnabled": true, "NotificationsEnabled": false },
              "Shortcut": { "Key": "F9" } }
            """);

        AppSettings settings = await new SettingsStore(paths).LoadAsync();

        Assert.Equal("F9", settings.Shortcut.Key);
        Assert.False(settings.Privacy.ErrorNotificationsEnabled);
        Assert.False(settings.Privacy.CompletionNotificationsEnabled);
        string saved = await File.ReadAllTextAsync(paths.SettingsPath);
        Assert.Contains("Transcription", saved);
    }

    [Fact]
    public async Task Load_InvalidJsonPreservesOriginalAndReturnsDefaults()
    {
        AppPaths paths = new(_directory);
        await File.WriteAllTextAsync(paths.SettingsPath, "{ definitely-not-json");
        SettingsStore store = new(paths);

        AppSettings settings = await store.LoadAsync();

        Assert.Equal("Space", settings.Shortcut.Key);
        Assert.True(settings.Privacy.ErrorNotificationsEnabled);
        Assert.False(settings.Privacy.CompletionNotificationsEnabled);
        Assert.NotNull(store.LastLoadWarning);
        Assert.Single(Directory.GetFiles(_directory, "settings.json.invalid-*"));
    }

    [Fact]
    public async Task Load_NullSectionsAreNormalized()
    {
        AppPaths paths = new(_directory);
        await File.WriteAllTextAsync(
            paths.SettingsPath,
            """{ "Shortcut": null, "Microphone": null, "Privacy": null }""");

        AppSettings settings = await new SettingsStore(paths).LoadAsync();

        Assert.NotNull(settings.Shortcut);
        Assert.NotNull(settings.Microphone);
        Assert.NotNull(settings.Privacy);
        Assert.NotNull(settings.Clipboard);
        Assert.NotNull(settings.History);
        Assert.NotNull(settings.Rewrite);
        Assert.NotNull(settings.Recording);
        Assert.Equal(2, settings.Recording.MaximumDurationMinutes);
        Assert.Equal("Space", settings.Shortcut.Key);
        Assert.False(settings.Clipboard.RestoreAfterPaste);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Load_LegacyNotificationSettingMigratesToBothControls(bool enabled)
    {
        AppPaths paths = new(_directory);
        await File.WriteAllTextAsync(
            paths.SettingsPath,
            System.Text.Json.JsonSerializer.Serialize(new
            {
                Privacy = new { NotificationsEnabled = enabled }
            }));

        AppSettings settings = await new SettingsStore(paths).LoadAsync();

        Assert.Equal(enabled, settings.Privacy.ErrorNotificationsEnabled);
        Assert.Equal(enabled, settings.Privacy.CompletionNotificationsEnabled);
        Assert.Null(settings.Privacy.LegacyNotificationsEnabled);
    }

    [Fact]
    public async Task Load_UnsupportedRecordingDurationFallsBackToTwoMinutes()
    {
        AppPaths paths = new(_directory);
        await File.WriteAllTextAsync(
            paths.SettingsPath,
            """{"Recording":{"MaximumDurationMinutes":999}}""");

        AppSettings settings = await new SettingsStore(paths).LoadAsync();

        Assert.Equal(2, settings.Recording.MaximumDurationMinutes);
    }

    [Fact]
    public async Task Load_CustomModeWithoutAValidProfileFallsBackToNoRewrite()
    {
        AppPaths paths = new(_directory);
        await File.WriteAllTextAsync(
            paths.SettingsPath,
            """{"Rewrite":{"Mode":6,"CustomProfiles":[]}}""");

        AppSettings settings = await new SettingsStore(paths).LoadAsync();

        Assert.Equal(RewriteMode.Verbatim, settings.Rewrite.Mode);
        Assert.Null(settings.Rewrite.ActiveCustomProfileId);
    }

    [Fact]
    public async Task Load_BoundsTranslationLanguageAndCustomProfileFields()
    {
        AppPaths paths = new(_directory);
        string longLanguage = new(
            'L',
            SettingsStore.MaximumTranslationLanguageLength + 20);
        string longName = new(
            'N',
            SettingsStore.MaximumCustomProfileNameLength + 20);
        string longInstruction = new(
            'I',
            SettingsStore.MaximumCustomInstructionLength + 20);
        await File.WriteAllTextAsync(
            paths.SettingsPath,
            System.Text.Json.JsonSerializer.Serialize(new
            {
                Rewrite = new
                {
                    Mode = RewriteMode.Custom,
                    TranslationLanguage = longLanguage,
                    ActiveCustomProfileId = "bounded",
                    CustomProfiles = new[]
                    {
                        new
                        {
                            Id = "bounded",
                            Name = longName,
                            Instruction = longInstruction
                        }
                    }
                }
            }));

        AppSettings settings = await new SettingsStore(paths).LoadAsync();

        Assert.Equal(
            SettingsStore.MaximumTranslationLanguageLength,
            settings.Rewrite.TranslationLanguage.Length);
        CustomRewriteProfile profile = Assert.Single(settings.Rewrite.CustomProfiles);
        Assert.Equal(SettingsStore.MaximumCustomProfileNameLength, profile.Name.Length);
        Assert.Equal(SettingsStore.MaximumCustomInstructionLength, profile.Instruction.Length);
    }

    [Fact]
    public async Task Save_CancelledWriteDoesNotLeaveTemporaryFile()
    {
        AppPaths paths = new(_directory);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new SettingsStore(paths).SaveAsync(new AppSettings(), cancellation.Token));

        Assert.False(File.Exists(paths.SettingsPath + ".tmp"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}

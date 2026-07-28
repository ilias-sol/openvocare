using System.Text.Json;
using OpenVocare.Models;

namespace OpenVocare.Services;

public sealed class SettingsStore(AppPaths paths)
{
    internal const int MaximumCustomProfiles = 20;
    internal const int MaximumCustomProfileNameLength = 60;
    internal const int MaximumCustomInstructionLength = 2000;
    internal const int MaximumTranslationLanguageLength = 80;
    internal static readonly int[] AllowedRecordingDurationMinutes = [1, 2, 5, 10, 30];
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public string? LastLoadWarning { get; private set; }

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(paths.SettingsPath))
        {
            return new AppSettings();
        }

        try
        {
            await using FileStream stream = File.OpenRead(paths.SettingsPath);
            AppSettings settings = await JsonSerializer.DeserializeAsync<AppSettings>(
                stream, SerializerOptions, cancellationToken).ConfigureAwait(false) ?? new AppSettings();
            Normalize(settings);
            return settings;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            string backupPath = paths.SettingsPath + $".invalid-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
            try
            {
                File.Move(paths.SettingsPath, backupPath, true);
                LastLoadWarning = $"Invalid settings were reset. The original file is {Path.GetFileName(backupPath)}.";
            }
            catch (IOException)
            {
                LastLoadWarning = "Invalid settings were reset, but the original file could not be archived.";
            }
            return new AppSettings();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        Normalize(settings);
        string temporaryPath = paths.SettingsPath + ".tmp";
        try
        {
            await using (FileStream stream = File.Create(temporaryPath))
            {
                await JsonSerializer.SerializeAsync(stream, settings, SerializerOptions, cancellationToken)
                    .ConfigureAwait(false);
            }
            File.Move(temporaryPath, paths.SettingsPath, true);
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    internal static void Normalize(AppSettings settings)
    {
        settings.Shortcut ??= new ShortcutSettings();
        settings.Microphone ??= new MicrophoneSettings();
        settings.Privacy ??= new PrivacySettings();
        settings.Clipboard ??= new ClipboardSettings();
        settings.History ??= new HistorySettings();
        settings.Rewrite ??= new RewriteSettings();
        settings.Recording ??= new RecordingSettings();
        if (settings.Privacy.LegacyNotificationsEnabled is bool legacyNotificationsEnabled)
        {
            settings.Privacy.ErrorNotificationsEnabled = legacyNotificationsEnabled;
            settings.Privacy.CompletionNotificationsEnabled = legacyNotificationsEnabled;
            settings.Privacy.LegacyNotificationsEnabled = null;
        }
        settings.Microphone.DeviceId = NormalizeOptional(settings.Microphone.DeviceId);
        settings.Microphone.DeviceName = NormalizeOptional(settings.Microphone.DeviceName);
        if (!Enum.IsDefined(settings.Shortcut.MouseButton))
        {
            settings.Shortcut.MouseButton = MouseShortcutButton.None;
        }
        if (!Enum.IsDefined(settings.Shortcut.KeyboardActivationMode))
        {
            settings.Shortcut.KeyboardActivationMode = ShortcutActivationMode.Hold;
        }
        if (!Enum.IsDefined(settings.Shortcut.MouseActivationMode))
        {
            settings.Shortcut.MouseActivationMode = ShortcutActivationMode.Hold;
        }
        if (!Enum.IsDefined(settings.Rewrite.Mode))
        {
            settings.Rewrite.Mode = RewriteMode.Verbatim;
        }
        settings.Rewrite.TranslationLanguage = string.IsNullOrWhiteSpace(settings.Rewrite.TranslationLanguage)
            ? "English"
            : Truncate(
                settings.Rewrite.TranslationLanguage.Trim(),
                MaximumTranslationLanguageLength);
        settings.Rewrite.CustomProfiles ??= [];
        HashSet<string> profileIds = new(StringComparer.Ordinal);
        List<CustomRewriteProfile> profiles = [];
        foreach (CustomRewriteProfile? profile in settings.Rewrite.CustomProfiles)
        {
            if (profile is null
                || string.IsNullOrWhiteSpace(profile.Name)
                || string.IsNullOrWhiteSpace(profile.Instruction))
            {
                continue;
            }
            string id = string.IsNullOrWhiteSpace(profile.Id) || !profileIds.Add(profile.Id)
                ? CreateUniqueProfileId(profileIds)
                : profile.Id;
            profileIds.Add(id);
            profiles.Add(new CustomRewriteProfile
            {
                Id = id,
                Name = Truncate(profile.Name.Trim(), MaximumCustomProfileNameLength),
                Instruction = Truncate(
                    profile.Instruction.Trim(),
                    MaximumCustomInstructionLength)
            });
            if (profiles.Count == MaximumCustomProfiles)
            {
                break;
            }
        }
        settings.Rewrite.CustomProfiles = profiles;
        settings.Rewrite.ActiveCustomProfileId =
            profiles.Any(profile => profile.Id == settings.Rewrite.ActiveCustomProfileId)
                ? settings.Rewrite.ActiveCustomProfileId
                : profiles.FirstOrDefault()?.Id;
        if (settings.Rewrite.Mode == RewriteMode.Custom && profiles.Count == 0)
        {
            settings.Rewrite.Mode = RewriteMode.Verbatim;
        }
        if (!AllowedRecordingDurationMinutes.Contains(
                settings.Recording.MaximumDurationMinutes))
        {
            settings.Recording.MaximumDurationMinutes = 2;
        }
        settings.Shortcut.Key = string.IsNullOrWhiteSpace(settings.Shortcut.Key)
            ? "Space"
            : settings.Shortcut.Key.Trim();
    }

    private static string CreateUniqueProfileId(HashSet<string> existing)
    {
        string id;
        do
        {
            id = Guid.NewGuid().ToString("N");
        }
        while (!existing.Add(id));
        return id;
    }

    private static string Truncate(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..maximumLength];

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

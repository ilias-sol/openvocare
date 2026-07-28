using System.Text.Json;
using System.Diagnostics.CodeAnalysis;
using OpenVocare.Models;

namespace OpenVocare.Services;

[SuppressMessage("Design", "CA1001:TypesThatOwnDisposableFieldsShouldBeDisposable", Justification = "The store lives for the application lifetime.")]
public sealed class TranscriptHistoryStore(AppPaths paths)
{
    internal const int MaximumEntries = 200;
    internal const int MaximumEntryCharacters = 100_000;
    internal const int MaximumTotalCharacters = 2_000_000;
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };
    private readonly SemaphoreSlim _gate = new(1, 1);

    public string? LastLoadWarning { get; private set; }

    public async Task<IReadOnlyList<TranscriptHistoryEntry>> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await LoadCoreAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<TranscriptHistoryEntry> AddAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        TranscriptHistoryEntry entry =
            new(Guid.NewGuid(), DateTimeOffset.Now, NormalizeText(text));
        await _gate.WaitAsync(cancellationToken);
        try
        {
            List<TranscriptHistoryEntry> entries = NormalizeEntries(
                [entry, .. await LoadCoreAsync(cancellationToken)]);
            await SaveCoreAsync(entries, cancellationToken);
            return entry;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            List<TranscriptHistoryEntry> entries = [.. await LoadCoreAsync(cancellationToken)];
            entries.RemoveAll(entry => entry.Id == id);
            await SaveCoreAsync(entries, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await SaveCoreAsync([], cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DrainAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        _gate.Release();
    }

    private async Task<List<TranscriptHistoryEntry>> LoadCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(paths.HistoryPath))
        {
            return [];
        }
        try
        {
            await using FileStream stream = File.OpenRead(paths.HistoryPath);
            List<TranscriptHistoryEntry?> entries =
                await JsonSerializer.DeserializeAsync<List<TranscriptHistoryEntry?>>(
                    stream, SerializerOptions, cancellationToken) ?? [];
            return NormalizeEntries(entries);
        }
        catch (JsonException)
        {
            ArchiveInvalidHistory();
            AppLog.Write("Invalid transcript history was archived and reset.");
            return [];
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            AppLog.Write(
                $"Transcript history could not be read ({exception.GetType().Name}).");
            return [];
        }
    }

    private static List<TranscriptHistoryEntry> NormalizeEntries(
        IEnumerable<TranscriptHistoryEntry?> entries)
    {
        List<TranscriptHistoryEntry> normalizedEntries = [];
        int totalCharacters = 0;
        foreach (TranscriptHistoryEntry? entry in entries)
        {
            if (entry is null || string.IsNullOrWhiteSpace(entry.Text))
            {
                continue;
            }
            if (normalizedEntries.Count >= MaximumEntries
                || totalCharacters >= MaximumTotalCharacters)
            {
                break;
            }

            string text = NormalizeText(entry.Text);
            int remainingCharacters = MaximumTotalCharacters - totalCharacters;
            if (text.Length > remainingCharacters)
            {
                text = TruncateWithoutSplittingSurrogate(text, remainingCharacters);
            }
            if (text.Length == 0)
            {
                break;
            }

            normalizedEntries.Add(entry with { Text = text });
            totalCharacters += text.Length;
        }
        return normalizedEntries;
    }

    private static string NormalizeText(string text)
    {
        string normalized = text.Trim();
        return normalized.Length <= MaximumEntryCharacters
            ? normalized
            : TruncateWithoutSplittingSurrogate(normalized, MaximumEntryCharacters);
    }

    private static string TruncateWithoutSplittingSurrogate(string text, int maximumCharacters)
    {
        int length = Math.Min(text.Length, maximumCharacters);
        if (length > 0
            && length < text.Length
            && char.IsHighSurrogate(text[length - 1])
            && char.IsLowSurrogate(text[length]))
        {
            length--;
        }
        return text[..length];
    }

    private void ArchiveInvalidHistory()
    {
        string backupPath =
            paths.HistoryPath + $".invalid-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
        try
        {
            File.Move(paths.HistoryPath, backupPath, true);
            LastLoadWarning =
                $"Invalid transcript history was reset. The original file is {Path.GetFileName(backupPath)}.";
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            LastLoadWarning =
                "Invalid transcript history was reset, but the original file could not be archived.";
        }
    }

    private async Task SaveCoreAsync(
        IReadOnlyList<TranscriptHistoryEntry> entries,
        CancellationToken cancellationToken)
    {
        string temporaryPath = paths.HistoryPath + ".tmp";
        try
        {
            await using (FileStream stream = File.Create(temporaryPath))
            {
                await JsonSerializer.SerializeAsync(
                    stream, entries, SerializerOptions, cancellationToken);
            }
            File.Move(temporaryPath, paths.HistoryPath, true);
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }
}

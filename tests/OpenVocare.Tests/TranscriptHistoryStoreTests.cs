using OpenVocare.Services;

namespace OpenVocare.Tests;

public sealed class TranscriptHistoryStoreTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"CodexBridge.History.Tests.{Guid.NewGuid():N}");

    [Fact]
    public async Task AddLoadAndDelete_RoundTripsLocalHistory()
    {
        TranscriptHistoryStore store = new(new AppPaths(_directory));

        var first = await store.AddAsync("First transcript");
        var second = await store.AddAsync("Second transcript");
        IReadOnlyList<OpenVocare.Models.TranscriptHistoryEntry> loaded = await store.LoadAsync();

        Assert.Equal(2, loaded.Count);
        Assert.Equal(second.Id, loaded[0].Id);
        Assert.Equal("First transcript", loaded[1].Text);

        await store.DeleteAsync(first.Id);
        loaded = await store.LoadAsync();
        Assert.Single(loaded);
        Assert.Equal(second.Id, loaded[0].Id);
    }

    [Fact]
    public async Task Clear_RemovesEveryEntry()
    {
        TranscriptHistoryStore store = new(new AppPaths(_directory));
        await store.AddAsync("Temporary transcript");

        await store.ClearAsync();

        Assert.Empty(await store.LoadAsync());
    }

    [Fact]
    public async Task InvalidHistory_IsArchivedBeforeTheStoreResets()
    {
        AppPaths paths = new(_directory);
        await File.WriteAllTextAsync(
            paths.HistoryPath,
            "{ definitely-not-valid-history");
        TranscriptHistoryStore store = new(paths);

        IReadOnlyList<OpenVocare.Models.TranscriptHistoryEntry> entries =
            await store.LoadAsync();

        Assert.Empty(entries);
        Assert.False(File.Exists(paths.HistoryPath));
        Assert.Single(Directory.GetFiles(_directory, "history.json.invalid-*"));
        Assert.NotNull(store.LastLoadWarning);
    }

    [Fact]
    public async Task Load_DropsInvalidEntriesAndBoundsTheVisibleHistory()
    {
        AppPaths paths = new(_directory);
        var entries = Enumerable.Range(0, 205)
            .Select(index => new OpenVocare.Models.TranscriptHistoryEntry(
                Guid.NewGuid(),
                DateTimeOffset.UtcNow.AddMinutes(-index),
                index == 0 ? " " : $"Transcript {index}"))
            .ToList();
        await File.WriteAllTextAsync(
            paths.HistoryPath,
            System.Text.Json.JsonSerializer.Serialize(entries));

        IReadOnlyList<OpenVocare.Models.TranscriptHistoryEntry> loaded =
            await new TranscriptHistoryStore(paths).LoadAsync();

        Assert.Equal(200, loaded.Count);
        Assert.DoesNotContain(loaded, entry => string.IsNullOrWhiteSpace(entry.Text));
    }

    [Fact]
    public async Task Add_BoundsIndividualHistoryEntry()
    {
        TranscriptHistoryStore store = new(new AppPaths(_directory));
        string oversized = new('x', TranscriptHistoryStore.MaximumEntryCharacters + 1_000);

        Task add = store.AddAsync(oversized);
        await store.DrainAsync();
        IReadOnlyList<OpenVocare.Models.TranscriptHistoryEntry> loaded =
            await store.LoadAsync();

        Assert.True(add.IsCompletedSuccessfully);
        Assert.Single(loaded);
        Assert.Equal(TranscriptHistoryStore.MaximumEntryCharacters, loaded[0].Text.Length);
    }

    [Fact]
    public async Task Load_BoundsTotalHistoryText()
    {
        AppPaths paths = new(_directory);
        string text = new('x', TranscriptHistoryStore.MaximumEntryCharacters);
        var entries = Enumerable
            .Range(
                0,
                (TranscriptHistoryStore.MaximumTotalCharacters
                    / TranscriptHistoryStore.MaximumEntryCharacters) + 5)
            .Select(index => new OpenVocare.Models.TranscriptHistoryEntry(
                Guid.NewGuid(),
                DateTimeOffset.UtcNow.AddMinutes(-index),
                text))
            .ToList();
        await File.WriteAllTextAsync(
            paths.HistoryPath,
            System.Text.Json.JsonSerializer.Serialize(entries));

        IReadOnlyList<OpenVocare.Models.TranscriptHistoryEntry> loaded =
            await new TranscriptHistoryStore(paths).LoadAsync();

        Assert.All(
            loaded,
            entry => Assert.InRange(
                entry.Text.Length,
                1,
                TranscriptHistoryStore.MaximumEntryCharacters));
        Assert.InRange(
            loaded.Sum(entry => entry.Text.Length),
            1,
            TranscriptHistoryStore.MaximumTotalCharacters);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }
    }
}

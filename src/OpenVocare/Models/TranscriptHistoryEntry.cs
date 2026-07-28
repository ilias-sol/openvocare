namespace OpenVocare.Models;

public sealed record TranscriptHistoryEntry(
    Guid Id,
    DateTimeOffset CreatedAt,
    string Text);

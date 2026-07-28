using OpenVocare.Services;

namespace OpenVocare.Tests;

public sealed class AppPathsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"OpenVocare.Paths.{Guid.NewGuid():N}");

    [Fact]
    public void Constructor_CreatesOnlySettingsAndLogLocations()
    {
        AppPaths paths = new(_directory);

        Assert.Equal(_directory, paths.Root);
        Assert.Equal(Path.Combine(_directory, "settings.json"), paths.SettingsPath);
        Assert.True(Directory.Exists(paths.LogsDirectory));
        Assert.False(Directory.Exists(Path.Combine(_directory, "audio")));
        Assert.False(Directory.Exists(Path.Combine(_directory, "models")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}

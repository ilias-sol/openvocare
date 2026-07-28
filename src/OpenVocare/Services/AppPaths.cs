namespace OpenVocare.Services;

public sealed class AppPaths
{
    public AppPaths(string? root = null)
    {
        if (root is null)
        {
            string localAppData =
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            Root = Path.Combine(localAppData, "OpenVocare");
        }
        else
        {
            Root = root;
        }
        LogsDirectory = Path.Combine(Root, "logs");
        SettingsPath = Path.Combine(Root, "settings.json");
        HistoryPath = Path.Combine(Root, "history.json");
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(LogsDirectory);
    }

    public string Root { get; }
    public string LogsDirectory { get; }
    public string SettingsPath { get; }
    public string HistoryPath { get; }

}

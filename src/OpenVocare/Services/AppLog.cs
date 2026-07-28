using System.Text;

namespace OpenVocare.Services;

public static class AppLog
{
    private const long MaximumLogBytes = 2 * 1024 * 1024;
    private static readonly object Sync = new();
    private static string? _path;

    public static string? Path => _path;

    public static void Initialize(string logsDirectory)
    {
        try
        {
            Directory.CreateDirectory(logsDirectory);
            _path = System.IO.Path.Combine(logsDirectory, "OpenVocare.log");
            RotateIfNeeded();
            Write("OpenVocare started.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _path = null;
        }
    }

    public static void Write(string message, Exception? exception = null)
    {
        string? path = _path;
        if (path is null)
        {
            return;
        }

        StringBuilder entry = new();
        entry.Append(DateTimeOffset.Now.ToString("O"));
        entry.Append(' ');
        entry.AppendLine(message.ReplaceLineEndings(" "));
        if (exception is not null)
        {
            entry.AppendLine(exception.ToString());
        }

        try
        {
            lock (Sync)
            {
                File.AppendAllText(path, entry.ToString(), Encoding.UTF8);
            }
        }
        catch (Exception writeException) when (writeException is IOException or UnauthorizedAccessException)
        {
            // Diagnostics must never interfere with dictation or shutdown.
        }
    }

    public static void WriteDeferred(string message) =>
        ThreadPool.QueueUserWorkItem(
            static state => Write((string)state!),
            message,
            preferLocal: false);

    private static void RotateIfNeeded()
    {
        string? path = _path;
        if (path is null || !File.Exists(path) || new FileInfo(path).Length < MaximumLogBytes)
        {
            return;
        }

        File.Move(path, path + ".previous", true);
    }
}

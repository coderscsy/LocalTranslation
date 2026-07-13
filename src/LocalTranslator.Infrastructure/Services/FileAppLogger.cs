using LocalTranslator.Core.Abstractions;

namespace LocalTranslator.Infrastructure.Services;

public sealed class FileAppLogger : IAppLogger
{
    private readonly object _syncRoot = new();
    private readonly string _logFilePath;

    public FileAppLogger()
    {
        var logDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LocalTranslator",
            "logs");
        Directory.CreateDirectory(logDirectory);
        _logFilePath = Path.Combine(logDirectory, $"app-{DateTime.Today:yyyyMMdd}.log");
    }

    public void Info(string message) => Write("INF", message);

    public void Error(string message, Exception exception) =>
        Write("ERR", $"{message}{Environment.NewLine}{exception}");

    private void Write(string level, string message)
    {
        var line = $"{DateTimeOffset.Now:O} [{level}] {message}{Environment.NewLine}";
        lock (_syncRoot)
        {
            File.AppendAllText(_logFilePath, line);
        }
    }
}


namespace EdifierCenter.Core;

/// <summary>
/// 轻量文件日志：写入运行目录 logs\edifier-yyyyMMdd.log（可移植式布局，随 exe 走）。
/// 线程安全；任何失败静默吞掉（日志永远不能拖垮主程序）。
/// </summary>
public static class FileLogger
{
    private static readonly object Gate = new();
    private static string? _directory;

    public static string LogsDirectory => _directory ?? Path.Combine(AppContext.BaseDirectory, "logs");

    public static void Init(string baseDirectory)
    {
        _directory = Path.Combine(baseDirectory, "logs");
        Directory.CreateDirectory(_directory);
        Info("=== Edifier Center 启动 ===");
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Debug(string message) => Write("DEBUG", message);

    public static void Error(string message, Exception? exception = null) =>
        Write("ERROR", exception is null ? message : $"{message} :: {exception}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(LogsDirectory);
                var path = Path.Combine(LogsDirectory, $"edifier-{DateTime.Now:yyyyMMdd}.log");
                File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch { /* 静默 */ }
    }
}

using System.Collections.Concurrent;
using System.Text;

namespace EdifierCenter.Core;

/// <summary>
/// 轻量文件日志：写入运行目录 logs\edifier-yyyyMMdd.log（可移植式布局，随 exe 走）。
/// 写入经内存队列由后台定时器批量落盘（高频推送不阻塞调用线程）；
/// 自动清理 7 天前的旧日志；任何失败静默吞掉（日志永远不能拖垮主程序）。
/// </summary>
public static class FileLogger
{
    private static readonly ConcurrentQueue<string> Pending = new();
    private static readonly object Gate = new();
    private static string? _directory;
    private static Timer? _flushTimer;

    public static string LogsDirectory => _directory ?? Path.Combine(AppContext.BaseDirectory, "logs");

    public static void Init(string baseDirectory)
    {
        _directory = Path.Combine(baseDirectory, "logs");
        Directory.CreateDirectory(_directory);
        CleanupOldLogs(keepDays: 7);
        _flushTimer ??= new Timer(_ => Flush(), null, 500, 500);
        Info("=== Edifier Center 启动 ===");
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Debug(string message) => Write("DEBUG", message);

    public static void Error(string message, Exception? exception = null) =>
        Write("ERROR", exception is null ? message : $"{message} :: {exception}");

    private static void Write(string level, string message) =>
        Pending.Enqueue($"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}");

    private static void Flush()
    {
        try
        {
            if (Pending.IsEmpty) return;
            var batch = new StringBuilder();
            while (Pending.TryDequeue(out var line)) batch.AppendLine(line);
            lock (Gate)
            {
                Directory.CreateDirectory(LogsDirectory);
                File.AppendAllText(
                    Path.Combine(LogsDirectory, $"edifier-{DateTime.Now:yyyyMMdd}.log"),
                    batch.ToString());
            }
        }
        catch { /* 静默 */ }
    }

    private static void CleanupOldLogs(int keepDays)
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-keepDays);
            foreach (var file in Directory.GetFiles(LogsDirectory, "edifier-*.log"))
            {
                if (File.GetLastWriteTime(file) < cutoff) File.Delete(file);
            }
        }
        catch { /* 静默 */ }
    }
}

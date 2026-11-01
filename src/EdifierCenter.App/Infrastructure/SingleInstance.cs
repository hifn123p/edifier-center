namespace EdifierCenter.App.Infrastructure;

/// <summary>单实例互斥。二次启动直接退出（M2 可加"唤起已有实例"）。</summary>
public static class SingleInstance
{
    private static Mutex? _mutex;

    public static bool Acquire()
    {
        _mutex = new Mutex(true, @"Local\EdifierCenter.SingleInstance", out var createdNew);
        return createdNew;
    }

    public static void Release()
    {
        try { _mutex?.ReleaseMutex(); } catch { }
        _mutex?.Dispose();
        _mutex = null;
    }
}

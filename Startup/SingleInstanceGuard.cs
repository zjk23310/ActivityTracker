using System;
using System.IO.Pipes;
using System.Security.Principal;
using System.Threading;

namespace ActivityTracker.Startup;

// 单实例守卫。
// 必须在创建数据库连接和追踪器之前检查，否则第二个实例
// 可能已经写入了数据才发现自己不该启动。
//
// 互斥体和管道名都带上当前用户的 SID：
//   - 同一个用户重复启动会被拦住
//   - 同一台机器上不同用户登录时互不干扰
internal sealed class SingleInstanceGuard : IDisposable
{
    private const string MutexPrefix = @"Local\ActivityTracker.SingleInstance.";//@表示不转义，直接使用原始字符串
    private const string PipePrefix = "ActivityTracker.Wake.";

    private readonly Mutex _mutex;//Mutex有名字的占位锁

    //通过占位锁发现已有实例，通过管道名唤醒已有实例

    // 用来唤醒已有实例的管道名，第一个实例负责监听
    public string PipeName { get; }
    //第一个 ActivityTracker 在后台监听一个固定名字的通信通道；第二个 ActivityTracker 启动时，通过这个通道通知第一个：“用户又打开程序了，把窗口弹出来。”

    // true 表示本进程拿到了所有权，是第一个实例
    public bool IsFirstInstance { get; }

    public SingleInstanceGuard()//无参构造
    {
        var userKey = GetCurrentUserKey();

        PipeName = PipePrefix + userKey;

        // 不在构造时直接持有，先试着立刻拿到；
        // 拿不到说明已经有实例在跑
        _mutex = new Mutex(
            initiallyOwned: false,//创建互斥体时，是否直接获得所有权，这里不直接获得
            MutexPrefix + userKey);

        try
        {
            IsFirstInstance =
                _mutex.WaitOne(TimeSpan.Zero);//不等待直接返回，拿到就返回true，没拿到就返回false
        }
        catch (AbandonedMutexException)
        {
            // 上一个实例是崩溃退出的，互斥体被遗留。
            //这里考虑到上一个实例非正常退出
            // 这种情况我们接管它，算作第一个实例。
            IsFirstInstance = true;
        }
    }

    // 通知已经在运行的实例把主窗口显示出来。
    // 连上了返回 true；管道不存在或超时返回 false，
    // 由调用方决定要不要给用户提示。
    //
    // 服务端处理完一次连接后要重建管道，中间有一瞬间的空档，
    // 所以这里重试几次，比单次连接稳。
    public static bool TryWakeExistingInstance(
        string pipeName,
        int timeoutMilliseconds = 800)
    {
        const int attempts = 4;
        var perTry = Math.Max(150, timeoutMilliseconds / attempts);//每次尝试的超时时间，至少 150ms

        for (var i = 0; i < attempts; i++)
        {
            try
            {
                using var client = new NamedPipeClientStream(
                    ".",
                    pipeName,
                    PipeDirection.Out);

                client.Connect(perTry);
                return true;
            }
            catch
            {
                // 对方还没开始监听，或者刚好在重建
                Thread.Sleep(100);
            }
        }

        return false;
    }

    // 取当前登录用户的 SID。
    // 拿不到时退化成用户名，保证程序仍能启动。
    private static string GetCurrentUserKey()
    {
        try
        {
            var sid = WindowsIdentity.GetCurrent().User?.Value;//问号表示如果User为null就不访问Value，直接返回null

            if (!string.IsNullOrEmpty(sid))
                return sid;
        }
        catch
        {
            // 忽略，走下面的兜底
        }

        return Environment.UserName;
    }

    public void Dispose()//释放
    {
        if (IsFirstInstance)
        {
            try
            {
                _mutex.ReleaseMutex();//释放互斥体
            }
            catch
            {
                // 没持有或已经被释放，忽略即可
            }
        }

        _mutex.Dispose();//释放互斥体
    }
}

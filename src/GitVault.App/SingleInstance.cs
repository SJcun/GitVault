using System.Security.Principal;

namespace GitVault.App;

/// <summary>以命名互斥量保护桌面应用生命周期；取得和释放必须在同一线程。</summary>
internal sealed class SingleInstance(Mutex mutex) : IDisposable
{
    /// <summary>Global 命名空间跨 Windows 会话，用户 SID 隔离不同用户的本机设置。</summary>
    internal static string UserMutexName
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return @"Global\GitVault." + identity.User!.Value;
        }
    }

    /// <summary>无等待取得实例锁；异常退出留下的互斥量由新实例接管。</summary>
    internal static SingleInstance? TryAcquire(string name)
    {
        var mutex = new Mutex(false, name);
        try
        {
            try
            {
                if (mutex.WaitOne(0)) return new SingleInstance(mutex);
            }
            catch (AbandonedMutexException)
            {
                // WaitOne 抛出遗弃异常时，当前线程已经取得所有权。
                return new SingleInstance(mutex);
            }
            mutex.Dispose();
            return null;
        }
        catch
        {
            mutex.Dispose();
            throw;
        }
    }

    /// <summary>在拥有者线程释放互斥量，并关闭系统句柄。</summary>
    public void Dispose()
    {
        mutex.ReleaseMutex();
        mutex.Dispose();
    }
}

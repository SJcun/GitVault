using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;
using GitVault.App;
using Xunit;

namespace GitVault.App.Tests;

/// <summary>通过独立进程验证全局用户实例锁与真实 WPF 启动入口。</summary>
public sealed class SingleInstanceTests
{
    /// <summary>名称按 SID 隔离用户，并显式选择跨 Windows 会话命名空间。</summary>
    [Fact]
    public void MutexNameIsGlobalAndUserSpecific()
    {
        Assert.Equal(@"Global\GitVault." + WindowsIdentity.GetCurrent().User!.Value, SingleInstance.UserMutexName);
    }

    /// <summary>其他进程占用时拒绝；异常退出后接管遗弃互斥量，正常释放后可再次取得。</summary>
    [Fact]
    public void MutexRejectsOtherProcessAndRecoversAfterTermination()
    {
        var name = @"Global\GitVault.Tests." + Guid.NewGuid().ToString("N");
        using var holder = StartMutexHolder(name);
        try
        {
            WaitForReady(holder);
            // 保留观察句柄，使进程退出后仍可实际走到 AbandonedMutexException 分支。
            using var observer = Mutex.OpenExisting(name);
            Assert.Null(SingleInstance.TryAcquire(name));
            holder.Kill();
            Assert.True(holder.WaitForExit(10000));
            using (var recovered = SingleInstance.TryAcquire(name)) Assert.NotNull(recovered);
            using (var next = SingleInstance.TryAcquire(name)) Assert.NotNull(next);
        }
        finally { Stop(holder); }
    }

    /// <summary>真实可执行文件在已有用户实例锁时只显示说明，关闭说明后退出。</summary>
    [Fact]
    public void SecondDesktopProcessShowsNoticeAndExits()
    {
        using var holder = StartMutexHolder(SingleInstance.UserMutexName);
        try
        {
            WaitForReady(holder);
            var appPath = Path.Combine(Path.GetDirectoryName(typeof(SingleInstance).Assembly.Location)!, "GitVault.exe");
            using var second = Process.Start(new ProcessStartInfo(appPath) { UseShellExecute = false })!;
            try
            {
                var timeout = Stopwatch.StartNew();
                while (!second.HasExited && second.MainWindowTitle != "GitVault 已在运行" && timeout.Elapsed < TimeSpan.FromSeconds(20))
                {
                    Thread.Sleep(50);
                    second.Refresh();
                }
                Assert.False(second.HasExited);
                Assert.Equal("GitVault 已在运行", second.MainWindowTitle);
                Assert.True(second.CloseMainWindow());
                Assert.True(second.WaitForExit(10000));
                Assert.Equal(0, second.ExitCode);
            }
            finally { Stop(second); }
        }
        finally { Stop(holder); }
    }

    /// <summary>测试进程只持有命名互斥量，不创建 ViewModel，也不接触生产设置。</summary>
    private static Process StartMutexHolder(string name)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardInput = true
        };
        start.Environment["GITVAULT_TEST_MUTEX_NAME"] = name;
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes("""
            $ErrorActionPreference = 'Stop'
            $mutex = New-Object Threading.Mutex($false, $env:GITVAULT_TEST_MUTEX_NAME)
            if (-not $mutex.WaitOne(0)) { throw '测试用户的 GitVault 已在运行' }
            [Console]::WriteLine('ready')
            [Console]::Out.Flush()
            [Console]::In.ReadLine() | Out-Null
            $mutex.ReleaseMutex()
            $mutex.Dispose()
            """)));
        return Process.Start(start)!;
    }

    /// <summary>先等待子进程明确确认所有权，避免依赖启动速度制造竞争。</summary>
    private static void WaitForReady(Process process) =>
        Assert.Equal("ready", process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20)).GetAwaiter().GetResult());

    /// <summary>清理本测试启动的进程，包括断言失败时的持锁者或说明窗口。</summary>
    private static void Stop(Process process)
    {
        if (!process.HasExited) process.Kill();
        Assert.True(process.WaitForExit(10000));
    }
}

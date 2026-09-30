using System.Windows;

namespace GitVault.App;

/// <summary>WPF 应用入口，使用系统权限运行。</summary>
public partial class App : Application
{
    /// <summary>由启动和退出的同一 UI 线程持有的当前用户实例锁。</summary>
    private SingleInstance? instance;

    /// <summary>先保护共享设置，再创建主窗口和 ViewModel；第二实例说明原因后退出。</summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        instance = SingleInstance.TryAcquire(SingleInstance.UserMutexName);
        if (instance is null)
        {
            MessageBox.Show("GitVault 已在运行，请使用已打开的窗口（也可能位于当前用户的其他 Windows 会话）。",
                "GitVault 已在运行", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }
        base.OnStartup(e);
        MainWindow = new MainWindow();
        MainWindow.Show();
    }

    /// <summary>窗口和设置操作结束后释放实例锁。</summary>
    protected override void OnExit(ExitEventArgs e)
    {
        try { base.OnExit(e); }
        finally { instance?.Dispose(); }
    }
}

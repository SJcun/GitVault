using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace GitVault.App;

/// <summary>连接 Windows 设备通知和窗口生命周期，业务逻辑交给 ViewModel。</summary>
public partial class MainWindow : Window
{
    /// <summary>唯一的主窗口状态。</summary>
    private readonly MainViewModel viewModel = new();
    /// <summary>合并短时间内重复的设备到达通知。</summary>
    private readonly DispatcherTimer deviceTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    /// <summary>启动完成后才响应窗口激活。</summary>
    private bool initialized;

    /// <summary>初始化视图、设备通知和关闭检查。</summary>
    public MainWindow()
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += async (_, _) => { await viewModel.InitializeAsync(); initialized = true; };
        SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WindowMessage);
        deviceTimer.Tick += async (_, _) => { deviceTimer.Stop(); await viewModel.DeviceChangedAsync(); };
        Activated += async (_, _) => { if (initialized) await viewModel.RecheckSelectionAsync(); };
        Closing += OnClosing;
    }

    /// <summary>只处理设备变化消息，延迟统一扫描以避免通知风暴。</summary>
    private nint WindowMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x0219) { deviceTimer.Stop(); deviceTimer.Start(); }
        return 0;
    }

    /// <summary>任务未结束时留在窗口，用户可显式取消并检查状态。</summary>
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!viewModel.IsBusy) { deviceTimer.Stop(); return; }
        e.Cancel = true;
        viewModel.Feedback = "操作正在进行，请等待完成或先取消操作。";
    }
}

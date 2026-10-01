using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows.Threading;
using GitVault.App;
using GitVault.Core;
using Xunit;

namespace GitVault.App.Tests;

/// <summary>在独立 STA Dispatcher 中验证通知调度；不创建第二个 WPF Application。</summary>
[Collection("WPF")]
public sealed class RefreshSchedulingTests
{
    /// <summary>使用无绑定清单和隔离设置测试时序，两秒节流不被 Git 耗时掩盖。</summary>
    [Fact]
    public async Task NotificationsAreThrottledCoalescedAndCancelled()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            dispatcher.BeginInvoke(new Action(async () =>
            {
                var boundary = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "GitVaultSchedulingTests"));
                var root = Path.Combine(boundary, Guid.NewGuid().ToString("N"));
                try
                {
                    var vaults = new VaultService();
                    var vault = vaults.Create(Path.Combine(root, "代码库"), "调度测试");
                    for (var index = 0; index < 2; index++)
                    {
                        var entry = new VaultRepository { Name = "项目" + index, RelativePath = $"repos/Project{index}.git" };
                        Directory.CreateDirectory(vaults.RepositoryPath(vault, entry));
                        vault = vaults.Register(vault, entry);
                    }
                    await VerifyAsync(root, vault);
                    completion.SetResult();
                }
                catch (Exception error) { completion.SetException(error); }
                finally
                {
                    // 仅清理本测试拥有的目录；未绑定项目不会执行任何 Git 命令。
                    if (Path.GetFullPath(root).StartsWith(boundary + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                        && Directory.Exists(root)) Directory.Delete(root, true);
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            }));
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromMinutes(1));
    }

    /// <summary>验证重复激活、忙碌补扫、设备优先与两种取消入口。</summary>
    private static async Task VerifyAsync(string root, VaultLocation vault)
    {
        var viewModel = new MainViewModel { IsBusy = true };
        var settings = new AppSettings { KnownVaultPaths = [vault.RootPath] };
        SetField(viewModel, "current", new VaultService().Open(vault.RootPath));
        SetField(viewModel, "settings", settings);
        SetField(viewModel, "settingsStore", new SettingsService(Path.Combine(root, "刷新调度设置")));
        SetField(viewModel, "settingsAvailable", true);
        viewModel.IsOnline = true;
        foreach (var entry in new VaultService().Open(vault.RootPath).Manifest.Repositories)
            viewModel.Items.Add(new RepositoryItem(entry, null));
        viewModel.SelectedItem = viewModel.Items[0];
        viewModel.IsBusy = false;

        var starts = new List<long>();
        var scans = 0;
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(MainViewModel.Feedback)) return;
            if (viewModel.Feedback == "刷新项目状态…") starts.Add(Stopwatch.GetTimestamp());
            if (viewModel.Feedback == "扫描 U 盘…") scans++;
        };
        await Task.WhenAll(Enumerable.Range(0, 30).Select(_ => viewModel.WindowActivatedAsync()));
        Assert.Single(starts);
        var repeated = Task.WhenAll(Enumerable.Range(0, 30).Select(_ => viewModel.WindowActivatedAsync()));
        await Task.Delay(50);
        Assert.False(repeated.IsCompleted);
        await repeated;
        Assert.Equal(2, starts.Count);
        Assert.True(Stopwatch.GetElapsedTime(starts[0], starts[1]) >= TimeSpan.FromSeconds(1.9));

        // 手动刷新立即执行全部项目，消费等待中的自动复查，不产生第三次自动执行。
        var delayed = viewModel.WindowActivatedAsync();
        var manual = viewModel.RefreshCommand.ExecuteAsync(null);
        // 无绑定项目的手动刷新同步完成；若误用节流等待，这里会得到尚未完成的任务。
        Assert.True(manual.IsCompleted);
        await manual;
        await delayed;
        Assert.Equal(2, starts.Count);
        Assert.All(viewModel.Items, item => Assert.Equal("本机未绑定", item.Summary));

        // 在受控忙碌阶段收集大量设备通知，结束时只补扫一次。
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = ExecuteAsync(viewModel, token => release.Task.WaitAsync(token));
        for (var index = 0; index < 30; index++)
        {
            await viewModel.DeviceChangedAsync();
            await viewModel.WindowActivatedAsync();
        }
        Assert.Equal(0, scans);
        release.SetResult();
        await operation;
        await DrainAsync(viewModel);
        Assert.Equal(1, scans);
        Assert.Equal(2, starts.Count);

        // 自动复查运行期间的事件也合并：设备扫描包含刷新，消耗同批窗口复查。
        var queued = false;
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(MainViewModel.Feedback) || viewModel.Feedback != "刷新项目状态…" || queued) return;
            queued = true;
            for (var index = 0; index < 30; index++)
            {
                _ = viewModel.WindowActivatedAsync();
                _ = viewModel.DeviceChangedAsync();
            }
        };
        await viewModel.WindowActivatedAsync();
        Assert.Equal(3, starts.Count);
        Assert.Equal(2, scans);

        // 取消运行中任务同时清空两类待办；取消节流等待也不会重新启动自动刷新。
        var blocked = ExecuteAsync(viewModel, token => Task.Delay(Timeout.Infinite, token));
        await viewModel.WindowActivatedAsync();
        await viewModel.DeviceChangedAsync();
        viewModel.CancelCommand.Execute(null);
        await blocked;
        await DrainAsync(viewModel);
        Assert.Equal(2, scans);
        var cancelledDelay = viewModel.WindowActivatedAsync();
        viewModel.CancelCommand.Execute(null);
        await cancelledDelay;
        await Task.Delay(100);
        Assert.Equal(3, starts.Count);

    }

    /// <summary>后台项目检查期间同时收到传输和设备通知，先传输，再补扫。</summary>
    internal static async Task VerifyTransferPriorityAsync(MainViewModel viewModel, GitCommandService git)
    {
        await git.RunAsync(viewModel.SelectedItem!.LocalPath, ["-c", "user.name=Test", "-c", "user.email=test@example.invalid",
            "-c", "commit.gpgSign=false", "commit", "--allow-empty", "-m", "验证通知期间的传输"]);
        Task? push = null;
        var scans = 0;
        var pushCompleted = false;
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.CanPush) && viewModel.IsBusy && viewModel.CanPush && push is null)
            {
                _ = viewModel.DeviceChangedAsync();
                _ = viewModel.WindowActivatedAsync();
                push = viewModel.PushCommand.ExecuteAsync(null);
            }
            if (args.PropertyName == nameof(MainViewModel.Feedback) && viewModel.Feedback == "推送到 U 盘完成") pushCompleted = true;
            if (args.PropertyName == nameof(MainViewModel.Feedback) && viewModel.Feedback == "扫描 U 盘…")
            {
                Assert.True(pushCompleted);
                scans++;
            }
        };
        await viewModel.RecheckRowsAsync();
        Assert.NotNull(push);
        await push;
        await DrainAsync(viewModel);
        Assert.Equal(1, scans);
        Assert.Equal(SyncKind.Synced, viewModel.SelectedItem.Status?.Kind);
        Assert.All(viewModel.Items, item => Assert.NotNull(item.Status));
    }

    /// <summary>沿用既有测试的小范围反射入口，控制任务时序而不增加生产调度框架。</summary>
    private static Task ExecuteAsync(MainViewModel viewModel, Func<CancellationToken, Task> action) =>
        (Task)typeof(MainViewModel).GetMethod("ExecuteAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(viewModel, ["受控任务", action])!;

    /// <summary>等待当前自动任务以及任务结束后接续的通知处理。</summary>
    private static async Task DrainAsync(MainViewModel viewModel)
    {
        while (typeof(MainViewModel).GetField("automaticRefreshTask", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(viewModel) is Task task) await task;
    }

    /// <summary>仅替换隔离测试所需的设置、清单和服务字段。</summary>
    private static void SetField(MainViewModel viewModel, string name, object value) =>
        typeof(MainViewModel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(viewModel, value);
}

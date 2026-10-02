using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using GitVault.App;
using Xunit;

namespace GitVault.App.Tests;

/// <summary>验证真实文件日志顺序与受控慢写入下的容量、故障和 UI 响应。</summary>
public sealed class BufferedLogTests : IDisposable
{
    /// <summary>本测试独占的临时日志目录。</summary>
    private readonly string root = Path.Combine(Path.GetTempPath(), "GitVaultLogTests", Guid.NewGuid().ToString("N"));

    /// <summary>批量写入不遗漏、不重复，跨日期仍按消息产生日期分文件。</summary>
    [Fact]
    public async Task WritesEveryLineOnceInOrderAndUsesMessageDate()
    {
        var log = new BufferedLog(() => root);
        var first = new DateTime(2026, 10, 1, 23, 59, 59);
        for (var index = 0; index < 5000; index++) log.Add("line-" + index, index < 2500 ? first : first.AddSeconds(2));
        Assert.True(await log.StopAsync(TimeSpan.FromSeconds(10)));
        var lines = File.ReadAllLines(Path.Combine(root, "gitvault-2026-10-01.log"))
            .Concat(File.ReadAllLines(Path.Combine(root, "gitvault-2026-10-02.log"))).ToArray();
        Assert.Equal(5000, lines.Length);
        Assert.Equal(Enumerable.Range(0, 5000).Select(index => "line-" + index), lines.Select(line => line[11..]));
        Assert.Empty(log.TakeDisplay().Warning);
        Assert.False(log.Add("stopped"));
    }

    /// <summary>写入停滞时队列有界，溢出的近期消息仍可见，落盘不完整明确告警。</summary>
    [Fact]
    public async Task OverflowIsVisibleAndAcceptedLinesRemainOrdered()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var written = new List<string>();
        var log = new BufferedLog(() => root, async (_, text) =>
        {
            started.TrySetResult();
            await release.Task;
            written.AddRange(text.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        });
        for (var index = 0; index < 100; index++) log.Add("line-" + index);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (var index = 100; index < BufferedLog.Capacity + 105; index++) log.Add("line-" + index);
        var display = log.TakeDisplay();
        Assert.Contains("队列已满", display.Warning);
        Assert.Contains("line-10104", display.Text);
        Assert.True(display.Text.Length <= 60000);
        release.SetResult();
        Assert.False(await log.StopAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(BufferedLog.Capacity + 100, written.Count);
        Assert.Equal(Enumerable.Range(0, written.Count).Select(index => "line-" + index), written.Select(line => line[11..]));
    }

    /// <summary>真实不可写位置不会使后台任务崩溃，保留界面文本和诊断。</summary>
    [Fact]
    public async Task FileFailureRetainsDisplayAndReportsIncompleteLog()
    {
        Directory.CreateDirectory(root);
        var occupied = Path.Combine(root, "occupied");
        File.WriteAllText(occupied, "keep");
        var log = new BufferedLog(() => occupied);
        log.Add("Git completed");
        Assert.False(await log.StopAsync(TimeSpan.FromSeconds(5)));
        var display = log.TakeDisplay();
        Assert.Contains("Git completed", display.Text);
        Assert.Contains("无法写入", display.Warning);
        Assert.Equal("keep", File.ReadAllText(occupied));
    }

    /// <summary>关闭等待有上限；超时后仍由同一后台任务收尾，不重复写入。</summary>
    [Fact]
    public async Task ShutdownTimeoutReportsPendingLog()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = 0;
        var log = new BufferedLog(() => root, async (_, _) =>
        {
            started.SetResult();
            await release.Task;
            writes++;
        });
        log.Add("pending");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var watch = Stopwatch.StartNew();
        Assert.False(await log.StopAsync(TimeSpan.FromMilliseconds(30)));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1));
        Assert.Contains("尚未全部写入", log.TakeDisplay().Warning);
        release.SetResult();
        await log.StopAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, writes);
    }

    /// <summary>真实 ViewModel 在慢写入期间批量显示日志，Dispatcher 定时器继续响应。</summary>
    [Fact]
    public async Task SlowWriterDoesNotBlockDispatcherOrHideRecentMessages()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            dispatcher.BeginInvoke(new Action(async () =>
            {
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var viewModel = new MainViewModel(new BufferedLog(() => root, async (_, _) => await release.Task));
                var gaps = new List<double>();
                var previous = Stopwatch.GetTimestamp();
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
                timer.Tick += (_, _) =>
                {
                    var now = Stopwatch.GetTimestamp();
                    gaps.Add(Stopwatch.GetElapsedTime(previous, now).TotalMilliseconds);
                    previous = now;
                };
                try
                {
                    timer.Start();
                    await Task.Delay(250);
                    var baseline = gaps.Order().ToArray();
                    gaps.Clear();
                    var updates = 0;
                    viewModel.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(MainViewModel.LogText)) updates++; };
                    for (var index = 0; index < 1000; index++) viewModel.AppendLog("slow-line-" + index);
                    await Task.Delay(250);
                    Assert.Contains("slow-line-999", viewModel.LogText);
                    Assert.InRange(updates, 1, 10);
                    Assert.True(gaps.Count >= 3);
                    var pressure = gaps.Order().ToArray();
                    Assert.True(pressure[(int)Math.Ceiling(pressure.Length * .95) - 1]
                        <= baseline[(int)Math.Ceiling(baseline.Length * .95) - 1] + 60);
                    release.SetResult();
                    Assert.True(await viewModel.StopLoggingAsync());
                    completion.SetResult();
                }
                catch (Exception error) { completion.SetException(error); }
                finally
                {
                    release.TrySetResult();
                    timer.Stop();
                    await viewModel.StopLoggingAsync();
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            }));
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }

    /// <summary>仅删除本测试拥有的临时目录。</summary>
    public void Dispose()
    {
        var boundary = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "GitVaultLogTests")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(root).StartsWith(boundary, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("测试清理越界。");
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}

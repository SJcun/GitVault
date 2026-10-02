using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using GitVault.App;
using GitVault.Core;

// 在独立 STA 中比较同样的 1000 条合成 Git 日志回调，使用真实 NTFS 文件写入。
var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var thread = new Thread(() =>
{
    var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
    SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
    app.Dispatcher.BeginInvoke(new Action(async () =>
    {
        try { await MeasureAsync(); completion.SetResult(); }
        catch (Exception error) { completion.SetException(error); }
        finally { app.Shutdown(); }
    }));
    app.Run();
});
thread.SetApartmentState(ApartmentState.STA);
thread.Start();
await completion.Task;

// 只创建和清理 artifacts 内的本次独占目录；不初始化生产设置或运行实际 Git。
async Task MeasureAsync()
{
    var boundary = Path.GetFullPath(Path.Combine("artifacts", "log-benchmark"));
    var root = Path.Combine(boundary, Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    var samples = new List<object>();
    try
    {
        for (var iteration = 1; iteration <= 20; iteration++)
        {
            var folder = Path.Combine(root, iteration.ToString());
            var model = new MainViewModel();
            typeof(MainViewModel).GetField("settingsStore", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(model, new SettingsService(folder));
            var git = (GitCommandService)typeof(MainViewModel).GetField("git", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(model)!;
            var updates = 0;
            var gaps = new List<double>();
            var previous = Stopwatch.GetTimestamp();
            var timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(20) };
            timer.Tick += (_, _) =>
            {
                var now = Stopwatch.GetTimestamp();
                gaps.Add(Stopwatch.GetElapsedTime(previous, now).TotalMilliseconds);
                previous = now;
            };
            model.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(MainViewModel.LogText)) updates++; };
            timer.Start();
            var watch = Stopwatch.StartNew();
            await Task.Run(() =>
            {
                for (var index = 0; index < 1000; index++) git.Log!("line-" + index + " " + new string('x', 120));
            });
            // 同一个程序兼容旧版本：新版本明确排空，旧版本的每条 Dispatcher 回调已经写完文件。
            var stop = typeof(MainViewModel).GetMethod("StopLoggingAsync");
            if (stop is not null && !await (Task<bool>)stop.Invoke(model, null)!)
                throw new InvalidOperationException("日志基准出现丢失或写入失败。");
            watch.Stop();
            await Task.Delay(100);
            timer.Stop();
            var lines = Directory.GetFiles(folder, "gitvault-*.log").SelectMany(File.ReadAllLines).ToArray();
            if (lines.Length != 1000 || !model.LogText.Contains("line-999 "))
                throw new InvalidOperationException("基准未完成全部日志的文件和界面更新。");
            var ordered = gaps.Order().ToArray();
            samples.Add(new
            {
                iteration, milliseconds = watch.Elapsed.TotalMilliseconds, updates, lines = lines.Length,
                dispatcherP95Milliseconds = ordered.Length == 0 ? 0 : ordered[(int)Math.Ceiling(ordered.Length * .95) - 1],
                dispatcherMaxMilliseconds = ordered.Length == 0 ? 0 : ordered[^1]
            });
        }
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            runtime = Environment.Version.ToString(), os = Environment.OSVersion.ToString(),
            volume = new DriveInfo(Path.GetPathRoot(root)!).DriveFormat, samples
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
    finally
    {
        if (!Path.GetFullPath(root).StartsWith(boundary + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("日志基准清理越界。");
        Directory.Delete(root, true);
    }
}

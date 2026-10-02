using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using GitVault.App;
using GitVault.Core;

// 使用真实 STA 和 Dispatcher 测量刷新；single 模式对单项目重复 20 轮以复查首样本波动。
var repeatSingle = args.Length > 0 && args[0] == "single";
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

// 只访问本次生成的数据；常规模式每种项目数冷/热各一次，single 模式单项目各二十次。
async Task MeasureAsync()
{
    var boundary = Path.GetFullPath(Path.Combine("artifacts", "refresh-ui-benchmark"));
    var root = Path.Combine(boundary, Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    var git = new GitCommandService();
    var vaults = new VaultService();
    var samples = new List<object>();
    try
    {
        var source = Path.Combine(root, "source");
        await git.RunAsync(null, ["init", "-b", "main", source]);
        await git.RunAsync(source, ["-c", "user.name=Benchmark", "-c", "user.email=benchmark@example.invalid",
            "-c", "commit.gpgSign=false", "commit", "--allow-empty", "-m", "界面基准"]);
        var vault = vaults.Create(Path.Combine(root, "vault"), "界面刷新基准");
        var first = await new RepositoryService(git, vaults).ImportAsync(vault, source, "Project0");
        var firstRemote = vaults.RepositoryPath(vault, first);
        var settings = new AppSettings { KnownVaultPaths = [vault.RootPath] };
        for (var index = 0; index < (repeatSingle ? 1 : 30); index++)
        {
            // 每个绑定使用独立裸仓库和工作目录，避免共享路径人为提高缓存命中率。
            var entry = index == 0 ? first : new VaultRepository { Name = $"Project{index}", RelativePath = $"repos/Project{index}.git" };
            if (index > 0)
            {
                await git.RunAsync(null, ["clone", "--bare", "--no-hardlinks", firstRemote, vaults.RepositoryPath(vault, entry)]);
                vault = vaults.Register(vault, entry);
            }
            var local = Path.Combine(root, $"local{index}");
            await git.RunAsync(null, ["clone", "--no-hardlinks", firstRemote, local]);
            settings.Bindings.Add(new RepositoryBinding(vault.Manifest.VaultId, entry.RepoId, local));
        }
        vault = vaults.Open(vault.RootPath);
        foreach (var count in repeatSingle ? new[] { 1 } : new[] { 1, 10, 30 })
        {
            for (var iteration = 0; iteration < (repeatSingle ? 20 : 1); iteration++)
            {
                var viewModel = new MainViewModel { IsBusy = true };
                SetField(viewModel, "current", vault);
                SetField(viewModel, "settings", settings);
                SetField(viewModel, "settingsAvailable", true);
                SetField(viewModel, "settingsStore", new SettingsService(Path.Combine(root, $"settings{count}")));
                viewModel.IsOnline = true;
                foreach (var binding in settings.Bindings.Take(count))
                    viewModel.Items.Add(new RepositoryItem(vault.Manifest.Repositories.Single(entry => entry.RepoId == binding.RepoId), binding.LocalPath));
                viewModel.SelectedItem = viewModel.Items[^1];
                viewModel.IsBusy = false;
                var commands = 0;
                var service = (GitCommandService)typeof(MainViewModel).GetField("git", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(viewModel)!;
                var originalLog = service.Log;
                // 保留真实界面与文件日志路径，统计命令数同时观察消息循环响应。
                service.Log = line => { if (line.StartsWith("git ", StringComparison.Ordinal)) commands++; originalLog?.Invoke(line); };
                foreach (var scenario in new[] { "cold", "warm" })
                {
                    foreach (var item in viewModel.Items) item.Invalidate("等待测量");
                    commands = 0;
                    var watch = Stopwatch.StartNew();
                    double? selectedMilliseconds = null;
                    var gaps = new List<double>();
                    var previous = Stopwatch.GetTimestamp();
                    var timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(20) };
                    timer.Tick += (_, _) =>
                    {
                        var now = Stopwatch.GetTimestamp();
                        gaps.Add(Stopwatch.GetElapsedTime(previous, now).TotalMilliseconds);
                        previous = now;
                    };
                    void Changed(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
                    {
                        if (args.PropertyName == nameof(MainViewModel.StatusText) && viewModel.SelectedItem?.Status is not null)
                            selectedMilliseconds ??= watch.Elapsed.TotalMilliseconds;
                    }
                    viewModel.PropertyChanged += Changed;
                    timer.Start();
                    await viewModel.RecheckRowsAsync();
                    watch.Stop();
                    timer.Stop();
                    viewModel.PropertyChanged -= Changed;
                    if (selectedMilliseconds is null || viewModel.Items.Any(item => item.Status?.Kind != SyncKind.Synced))
                        throw new InvalidOperationException("界面基准未完成真实状态更新。");
                    var ordered = gaps.Order().ToArray();
                    samples.Add(new
                    {
                        count, scenario, iteration = iteration + 1, commands, selectedMilliseconds, allMilliseconds = watch.Elapsed.TotalMilliseconds,
                        dispatcherP95Milliseconds = ordered.Length == 0 ? 0 : ordered[(int)Math.Ceiling(ordered.Length * .95) - 1],
                        dispatcherMaxMilliseconds = ordered.Length == 0 ? 0 : ordered[^1]
                    });
                    // 清空上一轮待显示日志，再开始下一轮测量。
                    await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                }
                await viewModel.StopLoggingAsync();
            }
        }
        Console.WriteLine(JsonSerializer.Serialize(new { samples }, new JsonSerializerOptions { WriteIndented = true }));
    }
    finally
    {
        // 仅清理当前基准目录；只读 Git 对象先恢复属性。
        if (!Path.GetFullPath(root).StartsWith(boundary + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("界面基准清理目录越界。");
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(root, true);
    }
}

// 复用现有 ViewModel，不为测量引入新的生产构造接口。
static void SetField(MainViewModel viewModel, string name, object value) =>
    typeof(MainViewModel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(viewModel, value);

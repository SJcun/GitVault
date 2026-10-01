using System.Diagnostics;
using System.Text.Json;
using GitVault.Core;

// 基准只在项目 artifacts 中创建唯一目录；输出原始样本，便于同条件前后比较。
var boundary = Path.GetFullPath(Path.Combine("artifacts", "refresh-benchmark"));
var root = Path.Combine(boundary, Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var git = new GitCommandService();
var vaults = new VaultService();
var samples = new List<object>();
var version = (await git.RunAsync(null, ["--version"])).Output.Trim();
try
{
    foreach (var profile in new[] { "small", "files", "refs" })
    {
        var local = Path.Combine(root, profile, "local");
        await git.RunAsync(null, ["init", "-b", "main", local]);
        // 文件样本含 1000 个已跟踪文件；引用样本含 30 个分支和 30 个轻量标签。
        var files = profile == "files" ? 1000 : 1;
        for (var index = 0; index < files; index++)
            await File.WriteAllTextAsync(Path.Combine(local, $"file-{index}.txt"), "benchmark\n");
        await git.RunAsync(local, ["add", "."]);
        await CommitAsync(local);
        if (profile == "refs")
        {
            for (var index = 0; index < 30; index++)
            {
                await git.RunAsync(local, ["branch", $"branch-{index}"]);
                await git.RunAsync(local, ["tag", $"tag-{index}"]);
            }
        }
        var vault = vaults.Create(Path.Combine(root, profile, "vault"), "刷新基准");
        var repository = await new RepositoryService(git, vaults).ImportAsync(vault, local, "Project");
        var remote = vaults.RepositoryPath(vault, repository);
        var writer = Path.Combine(root, profile, "writer");
        await git.RunAsync(null, ["clone", "--no-hardlinks", remote, writer]);
        for (var index = 0; index < 20; index++)
        {
            // 新服务表示应用内容缓存冷；第二次使用同一服务表示缓存热，磁盘缓存不人为清空。
            var service = new RepositoryService(git, vaults);
            await MeasureAsync(service, "cold", SyncKind.Synced);
            await MeasureAsync(service, "warm", SyncKind.Synced);
            await CommitAsync(writer);
            await git.RunAsync(writer, ["push", remote, "main"]);
            await MeasureAsync(service, "incoming", SyncKind.Behind);
            await git.RunAsync(local, ["merge", "--ff-only", "refs/gitvault/" + vault.Manifest.VaultId.ToString("N")
                + "/" + repository.RepoId.ToString("N") + "/heads/main"]);

            // 仅把命令日志计为进程启动，stderr 进度不计入；测量不包括准备提交和推送。
            async Task MeasureAsync(RepositoryService service, string scenario, SyncKind expected)
            {
                var commands = 0;
                git.Log = line => { if (line.StartsWith("git ", StringComparison.Ordinal)) commands++; };
                var watch = Stopwatch.StartNew();
                var state = await service.RefreshAsync(vault, repository, local);
                watch.Stop();
                git.Log = null;
                if (state.Kind != expected) throw new InvalidOperationException("基准状态不符合预期。");
                samples.Add(new { profile, scenario, iteration = index + 1, commands, milliseconds = watch.Elapsed.TotalMilliseconds });
            }
        }
    }
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        version, runtime = Environment.Version.ToString(), os = Environment.OSVersion.ToString(),
        volume = new DriveInfo(Path.GetPathRoot(root)!).DriveFormat, samples
    }, new JsonSerializerOptions { WriteIndented = true }));
}
finally
{
    // 只删除本次生成且位于预期边界内的目录，不触碰其他基准或用户仓库。
    if (!Path.GetFullPath(root).StartsWith(boundary + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("基准清理目录越界。");
    foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
    Directory.Delete(root, true);
}

// 提交使用本次命令的测试身份，不改变用户全局配置或触发签名。
async Task CommitAsync(string path) => await git.RunAsync(path,
    ["-c", "user.name=Benchmark", "-c", "user.email=benchmark@example.invalid", "-c", "commit.gpgSign=false",
        "commit", "--allow-empty", "-m", "刷新基准"]);

using GitVault.Core;
using Xunit;

namespace GitVault.Core.Tests;

/// <summary>验证刷新调用量与缓存边界，动态状态仍使用真实 Git 和文件系统。</summary>
public sealed partial class RepositoryTests
{
    /// <summary>冷、热刷新达到调用量目标；同一 HEAD 上的标签增删和冲突仍会更新。</summary>
    [Fact]
    public async Task RefreshReducesCommandsButStillReadsTagChanges()
    {
        var (local, vault, repository) = await SeedAsync();
        var service = new RepositoryService(git, vaults);
        var commands = new List<string>();
        git.Log = line => { if (line.StartsWith("git ", StringComparison.Ordinal)) commands.Add(line); };
        Assert.Equal(SyncKind.Synced, (await service.RefreshAsync(vault, repository, local)).Kind);
        Assert.True(commands.Count <= 25, $"冷缓存启动了 {commands.Count} 个 Git 进程。");
        Assert.Single(commands, line => line.Contains("\"ls-tree\""));
        Assert.DoesNotContain(commands, line => line.Contains("\"merge-base\"") || line.Contains("\"rev-list\""));

        commands.Clear();
        Assert.Equal(SyncKind.Synced, (await service.RefreshAsync(vault, repository, local + Path.DirectorySeparatorChar)).Kind);
        Assert.True(commands.Count <= 20, $"热缓存启动了 {commands.Count} 个 Git 进程。");
        Assert.DoesNotContain(commands, line => line.Contains("\"ls-tree\"") || line.Contains("\"grep\""));
        git.Log = null;

        await git.RunAsync(local, ["tag", "local-added"]);
        Assert.Equal(1, (await service.RefreshAsync(vault, repository, local)).TagsToPush);
        await git.RunAsync(local, ["tag", "-d", "local-added"]);
        var remote = vaults.RepositoryPath(vault, repository);
        await git.RunAsync(remote, ["tag", "remote-added"]);
        var state = await service.RefreshAsync(vault, repository, local);
        Assert.Equal((SyncKind.Synced, 0, 1), (state.Kind, state.TagsToPush, state.TagsToPull));
        await git.RunAsync(remote, ["tag", "-d", "remote-added"]);
        Assert.Equal(0, (await service.RefreshAsync(vault, repository, local)).TagsToPull);

        await git.RunAsync(local, ["tag", "conflict"]);
        await git.RunAsync(remote, ["-c", "user.name=Test", "-c", "user.email=test@example.invalid",
            "-c", "tag.gpgSign=false", "tag", "-a", "conflict", "-m", "不同标签对象"]);
        Assert.Equal("conflict", (await service.RefreshAsync(vault, repository, local)).TagConflict);
    }

    /// <summary>内容缓存不会掩盖脏文件、切分支、新提交和普通或 linked worktree 的六种操作标记。</summary>
    [Fact]
    public async Task WarmRefreshDetectsDynamicStateAndWorktreeMarkers()
    {
        var (local, vault, repository) = await SeedAsync();
        await repositories.RefreshAsync(vault, repository, local);
        await File.WriteAllTextAsync(Path.Combine(local, "untracked.txt"), "dirty");
        Assert.True((await repositories.RefreshAsync(vault, repository, local)).IsDirty);
        File.Delete(Path.Combine(local, "untracked.txt"));
        Assert.False((await repositories.RefreshAsync(vault, repository, local)).IsDirty);
        await git.RunAsync(local, ["checkout", "-b", "changed"]);
        Assert.Equal("changed", (await repositories.RefreshAsync(vault, repository, local)).Branch);
        await git.RunAsync(local, ["checkout", "main"]);
        var before = await HeadAsync(local);
        await CommitAsync(local, "new.txt", "new commit");
        var state = await repositories.RefreshAsync(vault, repository, local);
        Assert.NotEqual(before, state.Head);
        Assert.Equal((SyncKind.Ahead, 1), (state.Kind, state.Ahead));

        var worktree = Path.Combine(root, "linked worktree");
        await git.RunAsync(local, ["worktree", "add", "-b", "linked", worktree]);
        foreach (var path in new[] { local, worktree })
        {
            await repositories.RefreshAsync(vault, repository, path);
            foreach (var marker in new[] { "MERGE_HEAD", "CHERRY_PICK_HEAD", "REVERT_HEAD", "rebase-merge", "rebase-apply", "sequencer" })
            {
                var located = (await git.RunAsync(path, ["rev-parse", "--git-path", marker])).Output.Trim();
                var markerPath = Path.GetFullPath(located, path);
                var directory = marker is "rebase-merge" or "rebase-apply" or "sequencer";
                if (directory) Directory.CreateDirectory(markerPath);
                else await File.WriteAllTextAsync(markerPath, await HeadAsync(path));
                try
                {
                    var operation = await repositories.RefreshAsync(vault, repository, path);
                    Assert.True(operation.HasOperation, path + ": " + marker);
                    Assert.Equal(SyncKind.Unsupported, operation.Kind);
                }
                finally
                {
                    if (directory) Directory.Delete(markerPath);
                    else File.Delete(markerPath);
                }
            }
            Assert.False((await repositories.RefreshAsync(vault, repository, path)).HasOperation);
        }
    }

    /// <summary>fetch 期间改变分支、HEAD、工作区和操作标记，最终状态必须来自第二次动态快照。</summary>
    [Fact]
    public async Task RefreshRereadsDynamicStateAfterFetch()
    {
        var (local, vault, repository) = await SeedAsync();
        var initial = await HeadAsync(local);
        await CommitAsync(local, "next.txt", "new head");
        var next = await HeadAsync(local);
        await git.RunAsync(local, ["branch", "changed-during-fetch"]);
        await git.RunAsync(local, ["reset", "--hard", initial]);
        var changed = false;
        git.Log = line =>
        {
            if (changed || !line.Contains("\"fetch\"")) return;
            changed = true;
            // 等价于外部工具切换已存在的分支并开始操作；所有引用都指向真实提交。
            File.WriteAllText(Path.Combine(local, ".git", "HEAD"), "ref: refs/heads/changed-during-fetch\n");
            File.WriteAllText(Path.Combine(local, ".git", "MERGE_HEAD"), next);
            File.WriteAllText(Path.Combine(local, "during-fetch.txt"), "dirty");
        };
        var state = await repositories.RefreshAsync(vault, repository, local);
        Assert.True(changed);
        Assert.Equal("changed-during-fetch", state.Branch);
        Assert.Equal(next, state.Head);
        Assert.True(state.IsDirty);
        Assert.True(state.HasOperation);
        Assert.Equal(SyncKind.Unsupported, state.Kind);
    }
    /// <summary>同一仓库仅保留最近三个提交，旧提交被淘汰后重新检查。</summary>
    [Fact]
    public async Task ContentCacheEvictsOldCommits()
    {
        var (local, vault, repository) = await SeedAsync();
        var initial = await HeadAsync(local);
        await repositories.RefreshAsync(vault, repository, local);
        for (var index = 0; index < 3; index++)
        {
            await CommitAsync(local, "version.txt", index.ToString());
            await repositories.RefreshAsync(vault, repository, local);
        }
        await git.RunAsync(local, ["reset", "--hard", initial]);
        var commands = new List<string>();
        git.Log = line => { if (line.StartsWith("git ", StringComparison.Ordinal)) commands.Add(line); };
        Assert.Equal(SyncKind.Synced, (await repositories.RefreshAsync(vault, repository, local)).Kind);
        Assert.Single(commands, line => line.Contains("\"ls-tree\""));
        Assert.Single(commands, line => line.Contains("\"grep\""));
    }

    /// <summary>失败或取消的内容检查会重试；不支持内容的成功检查则可缓存。</summary>
    [Fact]
    public async Task ContentCacheDoesNotStoreFailedOrCancelledReads()
    {
        var (local, vault, repository) = await SeedAsync();
        var service = new RepositoryService(git, vaults);
        // 用日志入口在内容读取开始处注入失败，不模拟后续的真实仓库状态。
        git.Log = line =>
        {
            if (line.Contains("\"ls-tree\"")) throw new IOException("测试读取失败");
        };
        await Assert.ThrowsAsync<IOException>(() => service.RefreshAsync(vault, repository, local));
        using var cancellation = new CancellationTokenSource();
        git.Log = line =>
        {
            if (line.Contains("\"ls-tree\"")) cancellation.Cancel();
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RefreshAsync(vault, repository, local, cancellation.Token));
        var commands = new List<string>();
        git.Log = line => { if (line.StartsWith("git ", StringComparison.Ordinal)) commands.Add(line); };
        Assert.Equal(SyncKind.Synced, (await service.RefreshAsync(vault, repository, local)).Kind);
        Assert.Single(commands, line => line.Contains("\"ls-tree\""));
        git.Log = null;

        await CommitAsync(local, ".gitattributes", "*.bin filter=lfs\n");
        Assert.Equal(SyncKind.Unsupported, (await service.RefreshAsync(vault, repository, local)).Kind);
        commands.Clear();
        git.Log = line => { if (line.StartsWith("git ", StringComparison.Ordinal)) commands.Add(line); };
        Assert.Equal(SyncKind.Unsupported, (await service.RefreshAsync(vault, repository, local)).Kind);
        Assert.DoesNotContain(commands, line => line.Contains("\"ls-tree\"") || line.Contains("\"grep\""));
    }
}

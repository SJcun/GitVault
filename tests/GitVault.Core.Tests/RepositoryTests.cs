using GitVault.Core;
using Xunit;

namespace GitVault.Core.Tests;

/// <summary>使用真实 Git 和隔离临时目录验证数据传输，而不是模拟命令输出。</summary>
public sealed class RepositoryTests : IDisposable
{
    /// <summary>本测试独占的目录，包含中文、空格和 shell 特殊字符。</summary>
    private readonly string root = Path.Combine(Path.GetTempPath(), "GitVaultTests", Guid.NewGuid().ToString("N"), "中文 项目 & $ `");
    /// <summary>真实核心服务。</summary>
    private readonly GitCommandService git = new();
    private readonly VaultService vaults = new();
    private readonly RepositoryService repositories;

    /// <summary>创建隔离环境。</summary>
    public RepositoryTests()
    {
        Directory.CreateDirectory(root);
        repositories = new RepositoryService(git, vaults);
    }

    /// <summary>从 A 导入、B 克隆、双向提交并验证文件内容与 remote 不变。</summary>
    [Fact]
    public async Task RoundTripPreservesExistingRemotesAndCommittedData()
    {
        var (a, vault, repository) = await SeedAsync();
        await git.RunAsync(a, ["remote", "add", "origin", "https://example.invalid/original.git"]);
        await git.RunAsync(a, ["remote", "add", "usb", "Z:/existing.git"]);
        await git.RunAsync(a, ["config", "branch.main.remote", "origin"]);
        await git.RunAsync(a, ["config", "branch.main.merge", "refs/heads/main"]);
        var before = await File.ReadAllTextAsync(Path.Combine(a, ".git", "config"));
        var b = Path.Combine(root, "电脑 B");
        await repositories.CloneAsync(vault, repository, b);
        Assert.Empty((await git.RunAsync(b, ["remote"])).Output.Trim());
        Assert.Equal(SyncKind.Synced, (await repositories.RefreshAsync(vault, repository, b)).Kind);
        await CommitAsync(a, "from-a.txt", "电脑 A 的提交");
        Assert.Equal(1, (await repositories.RefreshAsync(vault, repository, a)).Ahead);
        await File.WriteAllTextAsync(Path.Combine(a, "未提交.txt"), "不能传输");
        await repositories.PushAsync(vault, repository, a);
        Assert.Equal(SyncKind.Behind, (await repositories.RefreshAsync(vault, repository, b)).Kind);
        await repositories.PullAsync(vault, repository, b);
        Assert.Equal("电脑 A 的提交", await File.ReadAllTextAsync(Path.Combine(b, "from-a.txt")));
        Assert.False(File.Exists(Path.Combine(b, "未提交.txt")));
        await CommitAsync(b, "from-b.txt", "电脑 B 的提交");
        await repositories.PushAsync(vault, repository, b);
        File.Delete(Path.Combine(a, "未提交.txt"));
        await repositories.PullAsync(vault, repository, a);
        Assert.Equal(await HeadAsync(a), await HeadAsync(b));
        Assert.Equal("电脑 B 的提交", await File.ReadAllTextAsync(Path.Combine(a, "from-b.txt")));
        Assert.Equal(before, await File.ReadAllTextAsync(Path.Combine(a, ".git", "config")));
    }

    /// <summary>分叉状态拒绝推送和自动合并，三个仓库的提交均保持原值。</summary>
    [Fact]
    public async Task DivergenceDoesNotRewriteEitherSide()
    {
        var (a, vault, repository) = await SeedAsync();
        var b = Path.Combine(root, "B");
        await repositories.CloneAsync(vault, repository, b);
        await CommitAsync(a, "a.txt", "A");
        await repositories.PushAsync(vault, repository, a);
        await CommitAsync(b, "b.txt", "B");
        var before = await HeadAsync(b);
        var remoteBefore = await HeadAsync(vaults.RepositoryPath(vault, repository));
        var state = await repositories.RefreshAsync(vault, repository, b);
        Assert.Equal(SyncKind.Diverged, state.Kind);
        Assert.Equal((1, 1), (state.Ahead, state.Behind));
        await Assert.ThrowsAsync<InvalidOperationException>(() => repositories.PushAsync(vault, repository, b));
        await Assert.ThrowsAsync<InvalidOperationException>(() => repositories.PullAsync(vault, repository, b));
        Assert.Equal(before, await HeadAsync(b));
        Assert.Equal(remoteBefore, await HeadAsync(vaults.RepositoryPath(vault, repository)));
    }

    /// <summary>拉取不覆盖工作区修改或未跟踪文件。</summary>
    [Fact]
    public async Task DirtyWorktreePreventsPull()
    {
        var (a, vault, repository) = await SeedAsync();
        var b = Path.Combine(root, "B");
        await repositories.CloneAsync(vault, repository, b);
        await CommitAsync(a, "change.txt", "new");
        await repositories.PushAsync(vault, repository, a);
        await File.WriteAllTextAsync(Path.Combine(b, "initial.txt"), "my edits");
        var before = await HeadAsync(b);
        await Assert.ThrowsAsync<InvalidOperationException>(() => repositories.PullAsync(vault, repository, b));
        Assert.Equal(before, await HeadAsync(b));
        Assert.Equal("my edits", await File.ReadAllTextAsync(Path.Combine(b, "initial.txt")));
    }

    /// <summary>新分支可显式推送，删除后刷新不沿用旧缓存；标签不会隐式推送。</summary>
    [Fact]
    public async Task NewBranchAndPruningStayInsidePrivateReferences()
    {
        var (a, vault, repository) = await SeedAsync();
        await git.RunAsync(a, ["checkout", "-b", "feature/demo"]);
        await CommitAsync(a, "feature.txt", "feature");
        await git.RunAsync(a, ["-c", "user.name=Tester", "-c", "user.email=test@example.invalid", "tag", "-a", "local-tag", "-m", "local"]);
        await git.RunAsync(a, ["config", "push.followTags", "true"]);
        Assert.Equal(SyncKind.MissingBranch, (await repositories.RefreshAsync(vault, repository, a)).Kind);
        Assert.Equal(SyncKind.Synced, (await repositories.PushAsync(vault, repository, a)).Kind);
        var remote = vaults.RepositoryPath(vault, repository);
        Assert.Empty((await git.RunAsync(remote, ["tag", "--list"])).Output.Trim());
        await git.RunAsync(remote, ["update-ref", "-d", "refs/heads/feature/demo"]);
        Assert.Equal(SyncKind.MissingBranch, (await repositories.RefreshAsync(vault, repository, a)).Kind);
    }

    /// <summary>换位置后按同一清单身份继续传输；原盘符被其他 Vault 占用会拒绝。</summary>
    [Fact]
    public async Task VaultMoveWorksAndIdentityReplacementIsRejected()
    {
        var (a, vault, repository) = await SeedAsync();
        var moved = Path.Combine(root, "换盘符后的位置");
        Directory.Move(vault.RootPath, moved);
        var reopened = vaults.Open(moved);
        Assert.Equal(vault.Manifest.VaultId, reopened.Manifest.VaultId);
        Assert.Equal(SyncKind.Synced, (await repositories.RefreshAsync(reopened, repository, a)).Kind);
        vaults.Create(vault.RootPath, "另一块盘");
        await Assert.ThrowsAsync<IOException>(() => repositories.PushAsync(vault, repository, a));
    }

    /// <summary>无共同历史不能误绑定，绑定有效仓库不改动本机配置。</summary>
    [Fact]
    public async Task BindingRejectsUnrelatedHistory()
    {
        var (a, vault, repository) = await SeedAsync();
        var unrelated = await NewRepositoryAsync("无关项目", "different");
        Assert.Equal(SyncKind.Unrelated, (await repositories.RefreshAsync(vault, repository, unrelated)).Kind);
        await Assert.ThrowsAsync<InvalidOperationException>(() => repositories.BindAsync(vault, repository, unrelated));
        await repositories.BindAsync(vault, repository, a);
    }

    /// <summary>未提交、游离 HEAD、正在合并和浅仓库不进入传输流程。</summary>
    [Fact]
    public async Task UnsupportedGitStatesAreExplicit()
    {
        var empty = Path.Combine(root, "empty");
        await git.RunAsync(null, ["init", "-b", "main", empty]);
        var vault = vaults.Create(Path.Combine(root, "vault"), "测试");
        await Assert.ThrowsAsync<InvalidOperationException>(() => repositories.ImportAsync(vault, empty, "empty"));
        var a = await NewRepositoryAsync("A");
        var repository = await repositories.ImportAsync(vault, a, "Project");
        await git.RunAsync(a, ["checkout", "--detach"]);
        Assert.Equal(SyncKind.Unsupported, (await repositories.RefreshAsync(vault, repository, a)).Kind);
        await git.RunAsync(a, ["checkout", "main"]);
        await File.WriteAllTextAsync(Path.Combine(a, ".git", "MERGE_HEAD"), await HeadAsync(a));
        Assert.True((await repositories.RefreshAsync(vault, repository, a)).HasOperation);
        File.Delete(Path.Combine(a, ".git", "MERGE_HEAD"));
        var shallow = Path.Combine(root, "shallow");
        await git.RunAsync(null, ["clone", "--no-local", "--depth=1", a, shallow]);
        Assert.Contains("浅克隆", (await repositories.RefreshAsync(vault, repository, shallow)).Message);
    }

    /// <summary>绑定选错目录时给出可执行的说明，而不是 Git 的原始报错。</summary>
    [Fact]
    public async Task BindingUnhelpfulDirectoryExplainsWhatToSelect()
    {
        var (a, vault, repository) = await SeedAsync();
        // 换一台电脑后用户容易误选代码库根目录或其中的裸仓库目录。
        var wrong = await Assert.ThrowsAsync<InvalidOperationException>(() => repositories.BindAsync(vault, repository, vault.RootPath));
        Assert.Contains("不是 Git 工作区", wrong.Message);
        Assert.Contains("克隆到本机", wrong.Message);
        var bare = await Assert.ThrowsAsync<InvalidOperationException>(() => repositories.BindAsync(vault, repository, vaults.RepositoryPath(vault, repository)));
        Assert.Contains("裸仓库", bare.Message);
        // 普通目录既不是仓库也不是其子目录，同样要给出说明。
        var plain = Path.Combine(root, "普通目录");
        Directory.CreateDirectory(plain);
        var none = await Assert.ThrowsAsync<InvalidOperationException>(() => repositories.BindAsync(vault, repository, plain));
        Assert.Contains("不是 Git 工作区", none.Message);
        // 真正的本地工作区仍能正常绑定。
        await repositories.BindAsync(vault, repository, a);
    }

    /// <summary>不静默忽略 LFS 与子模块内容。</summary>
    [Fact]
    public async Task LfsAndSubmoduleAreNotReportedAsFullyTransferred()
    {
        var (a, vault, repository) = await SeedAsync();
        await CommitAsync(a, ".gitattributes", "*.bin filter=lfs diff=lfs merge=lfs -text");
        Assert.Contains("LFS", (await repositories.RefreshAsync(vault, repository, a)).Message);
        await git.RunAsync(a, ["rm", ".gitattributes"]);
        await git.RunAsync(a, ["update-index", "--add", "--cacheinfo", $"160000,{await HeadAsync(a)},nested"]);
        await CommitIndexAsync(a);
        Assert.Contains("子模块", (await repositories.RefreshAsync(vault, repository, a)).Message);
    }

    /// <summary>远端拒绝推送时抛出实际 Git 错误，远端提交保持不变。</summary>
    [Fact]
    public async Task RejectedPushKeepsRemoteHead()
    {
        var (a, vault, repository) = await SeedAsync();
        var remote = vaults.RepositoryPath(vault, repository);
        var before = await HeadAsync(remote);
        await File.WriteAllTextAsync(Path.Combine(remote, "hooks", "pre-receive"), "#!/bin/sh\necho 'test rejection' >&2\nexit 1\n");
        await CommitAsync(a, "new.txt", "new");
        var error = await Assert.ThrowsAsync<GitException>(() => repositories.PushAsync(vault, repository, a));
        Assert.Contains("test rejection", error.Message);
        Assert.Equal(before, await HeadAsync(remote));
        Assert.Equal(SyncKind.Ahead, (await repositories.RefreshAsync(vault, repository, a)).Kind);
    }

    /// <summary>已有目标目录不能被覆盖。</summary>
    [Fact]
    public async Task ExistingDirectoriesSurviveImportAndClone()
    {
        var (a, vault, repository) = await SeedAsync();
        await Assert.ThrowsAsync<IOException>(() => repositories.ImportAsync(vault, a, "Project"));
        var destination = Path.Combine(root, "existing");
        Directory.CreateDirectory(destination);
        await File.WriteAllTextAsync(Path.Combine(destination, "keep.txt"), "keep");
        await Assert.ThrowsAsync<IOException>(() => repositories.CloneAsync(vault, repository, destination));
        Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(destination, "keep.txt")));
    }

    /// <summary>清单保存失败后，完整裸仓库能重新登记且不重复复制。</summary>
    [Fact]
    public async Task UnregisteredRepositoryCanBeRecovered()
    {
        var a = await NewRepositoryAsync("A");
        var vault = vaults.Create(Path.Combine(root, "vault"), "测试");
        var orphan = Path.Combine(vault.RootPath, "repos", "Orphan.git");
        await git.RunAsync(null, ["clone", "--bare", "--no-hardlinks", a, orphan]);
        Assert.Equal(1, await repositories.RecoverAsync(vault));
        var current = vaults.Open(vault.RootPath);
        Assert.Single(current.Manifest.Repositories);
        Assert.Equal(await HeadAsync(a), await HeadAsync(orphan));
        Assert.Equal(0, await repositories.RecoverAsync(current));
    }

    /// <summary>模拟清单只读导致的真实登记失败，源项目和已完成的裸仓库均保留。</summary>
    [Fact]
    public async Task ManifestWriteFailureLeavesRecoverableRepository()
    {
        var a = await NewRepositoryAsync("A");
        var vault = vaults.Create(Path.Combine(root, "vault"), "测试");
        var manifest = Path.Combine(vault.RootPath, "vault.json");
        File.SetAttributes(manifest, FileAttributes.ReadOnly);
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => repositories.ImportAsync(vault, a, "Recoverable"));
            Assert.Empty(vaults.Open(vault.RootPath).Manifest.Repositories);
            Assert.Equal(await HeadAsync(a), await HeadAsync(Path.Combine(vault.RootPath, "repos", "Recoverable.git")));
        }
        finally { File.SetAttributes(manifest, FileAttributes.Normal); }
        Assert.Equal(1, await repositories.RecoverAsync(vault));
    }

    /// <summary>导入包含所有本地分支与标签，后续推送不会改变其他分支。</summary>
    [Fact]
    public async Task ImportIncludesBranchesAndTagsAndPushUpdatesOnlyCurrentBranch()
    {
        var a = await NewRepositoryAsync("A");
        await git.RunAsync(a, ["branch", "other"]);
        await git.RunAsync(a, ["tag", "v1"]);
        var vault = vaults.Create(Path.Combine(root, "vault"), "测试");
        var repository = await repositories.ImportAsync(vault, a, "Project");
        var remote = vaults.RepositoryPath(vault, repository);
        var original = await HeadAsync(a);
        Assert.Equal(original, (await git.RunAsync(remote, ["rev-parse", "refs/heads/other"])).Output.Trim());
        Assert.Equal(original, (await git.RunAsync(remote, ["rev-parse", "refs/tags/v1"])).Output.Trim());
        await CommitAsync(a, "new.txt", "main only");
        await repositories.PushAsync(vault, repository, a);
        Assert.Equal(original, (await git.RunAsync(remote, ["rev-parse", "refs/heads/other"])).Output.Trim());
        Assert.Equal(await HeadAsync(a), await HeadAsync(remote));
    }

    /// <summary>路径穿越与损坏配置不能被静默接受或重置。</summary>
    [Fact]
    public void InvalidManifestAndSettingsAreRejectedWithoutOverwrite()
    {
        var vault = vaults.Create(Path.Combine(root, "vault"), "测试");
        Assert.Throws<InvalidDataException>(() => vaults.RepositoryPath(vault, new VaultRepository { RelativePath = "../outside.git" }));
        var manifest = Path.Combine(vault.RootPath, "vault.json");
        File.WriteAllText(manifest, "{}");
        Assert.ThrowsAny<Exception>(() => vaults.Open(vault.RootPath));
        var store = new SettingsService(Path.Combine(root, "settings"));
        store.Save(new AppSettings());
        var path = Path.Combine(store.DirectoryPath, "settings.json");
        File.WriteAllText(path, "invalid");
        Assert.ThrowsAny<Exception>(() => store.Load());
        Assert.Equal("invalid", File.ReadAllText(path));
    }

    /// <summary>U 盘仓库消失后刷新必须失败，不能构造成功状态。</summary>
    [Fact]
    public async Task MissingRemoteFailsRefresh()
    {
        var (a, vault, repository) = await SeedAsync();
        var source = vaults.RepositoryPath(vault, repository);
        Directory.Move(source, source + ".unplugged");
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => repositories.RefreshAsync(vault, repository, a));
    }

    /// <summary>运行中的 Git 子进程收到取消后及时退出。</summary>
    [Fact]
    public async Task CancellationStopsRunningGit()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(700));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => git.RunAsync(null, ["-c", "alias.wait=!sleep 30", "wait"], cancellation.Token));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10));
    }

    /// <summary>构造一个已导入 Vault 的工作区。</summary>
    private async Task<(string Local, VaultLocation Vault, VaultRepository Repository)> SeedAsync()
    {
        var a = await NewRepositoryAsync("电脑 A");
        var vault = vaults.Create(Path.Combine(root, "U 盘", "GitVault"), "测试代码库");
        var repository = await repositories.ImportAsync(vault, a, "Project");
        return (a, vaults.Open(vault.RootPath), repository);
    }

    /// <summary>创建拥有首个提交的普通 Git 仓库。</summary>
    private async Task<string> NewRepositoryAsync(string name, string content = "initial")
    {
        var path = Path.Combine(root, name);
        await git.RunAsync(null, ["init", "-b", "main", path]);
        await git.RunAsync(path, ["config", "core.autocrlf", "false"]);
        await CommitAsync(path, "initial.txt", content);
        return path;
    }

    /// <summary>写入并提交一个文件。</summary>
    private async Task CommitAsync(string path, string file, string content)
    {
        await File.WriteAllTextAsync(Path.Combine(path, file), content);
        await git.RunAsync(path, ["add", "--", file]);
        await CommitIndexAsync(path);
    }

    /// <summary>使用测试专属身份提交，不写入用户全局配置。</summary>
    private Task<GitResult> CommitIndexAsync(string path) => git.RunAsync(path,
        ["-c", "user.name=GitVault Tests", "-c", "user.email=test@example.invalid", "-c", "commit.gpgSign=false", "commit", "-m", "测试提交"]);

    /// <summary>读取提交 OID 进行实际数据比对。</summary>
    private async Task<string> HeadAsync(string path) => (await git.RunAsync(path, ["rev-parse", "HEAD"])).Output.Trim();

    /// <summary>仅清理本测试拥有的目录；Git 对象可能带只读属性。</summary>
    public void Dispose()
    {
        var owned = Path.GetFullPath(root);
        var boundary = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "GitVaultTests")) + Path.DirectorySeparatorChar;
        if (!owned.StartsWith(boundary, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("测试清理目录越界。");
        if (!Directory.Exists(owned)) return;
        foreach (var file in Directory.EnumerateFiles(owned, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(owned, recursive: true);
    }
}

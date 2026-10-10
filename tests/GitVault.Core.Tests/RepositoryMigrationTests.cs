using System.Text.Json.Nodes;
using GitVault.Core;
using Xunit;

namespace GitVault.Core.Tests;

/// <summary>使用真实裸仓库验证跨代码库迁移，失败和取消均不得删除原项目。</summary>
public sealed partial class RepositoryTests
{
    /// <summary>保留全部引用、配置和 hooks，移除原登记及目录，并继续同步原本的工作区。</summary>
    [Fact]
    public async Task MigrationPreservesCompleteRepositoryAndRemovesSource()
    {
        var (local, sourceVault, repository) = await SeedAsync();
        var source = vaults.RepositoryPath(sourceVault, repository);
        await git.RunAsync(source, ["branch", "vault-only"]);
        await git.RunAsync(source, ["tag", "lightweight"]);
        await git.RunAsync(source, ["-c", "user.name=Test", "-c", "user.email=test@example.invalid", "tag", "-a", "annotated", "-m", "附注标签"]);
        await git.RunAsync(source, ["update-ref", "refs/custom/backup", await HeadAsync(source)]);
        await git.RunAsync(source, ["symbolic-ref", "HEAD", "refs/heads/vault-only"]);
        await git.RunAsync(source, ["config", "core.logAllRefUpdates", "true"]);
        await File.WriteAllTextAsync(Path.Combine(source, "hooks", "custom-hook"), "保留自定义 hook");
        var originalFiles = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
            .ToDictionary(file => Path.GetRelativePath(source, file), File.ReadAllBytes);
        var otherSource = await repositories.ImportAsync(sourceVault, local, "原库其他项目");
        var targetVault = vaults.Create(Path.Combine(root, "目标代码库"), "目标");
        var otherTarget = await repositories.ImportAsync(targetVault, local, "目标原有项目");
        await File.WriteAllTextAsync(Path.Combine(local, "未提交.txt"), "本地修改保留");
        var localConfig = await File.ReadAllTextAsync(Path.Combine(local, ".git", "config"));

        var result = await repositories.MigrateAsync(sourceVault, repository, targetVault);
        var destination = vaults.VerifyRepository(targetVault, result.Repository);

        Assert.Null(result.CleanupWarning);
        Assert.Equal(repository.RepoId, result.Repository.RepoId);
        Assert.Equal(otherSource.RepoId, Assert.Single(vaults.Open(sourceVault.RootPath).Manifest.Repositories).RepoId);
        Assert.Equal(2, vaults.Open(targetVault.RootPath).Manifest.Repositories.Count);
        Assert.Contains(vaults.Open(targetVault.RootPath).Manifest.Repositories, entry => entry.RepoId == otherTarget.RepoId);
        Assert.False(Directory.Exists(source));
        Assert.Empty(Directory.GetDirectories(Path.Combine(sourceVault.RootPath, "repos"), ".migrated-*"));
        Assert.Equal(originalFiles.Count, Directory.GetFiles(destination, "*", SearchOption.AllDirectories).Length);
        foreach (var (relative, bytes) in originalFiles) Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(destination, relative)));
        Assert.Equal(localConfig, await File.ReadAllTextAsync(Path.Combine(local, ".git", "config")));
        Assert.Equal("本地修改保留", await File.ReadAllTextAsync(Path.Combine(local, "未提交.txt")));
        Assert.Equal(SyncKind.Synced, (await repositories.RefreshAsync(targetVault, result.Repository, local)).Kind);
        await CommitAsync(local, "迁移后.txt", "仍可推送");
        await repositories.PushAsync(targetVault, result.Repository, local);
        Assert.Equal(await HeadAsync(local), (await git.RunAsync(destination, ["rev-parse", "refs/heads/main"])).Output.Trim());
    }

    /// <summary>同库、同名、同标识或已有目标目录均拒绝迁移，不覆盖已有内容。</summary>
    [Theory]
    [InlineData("same-vault")]
    [InlineData("name")]
    [InlineData("id")]
    [InlineData("directory")]
    public async Task MigrationConflictKeepsBothVaultsUnchanged(string conflict)
    {
        var (local, sourceVault, repository) = await SeedAsync();
        var target = conflict == "same-vault" ? sourceVault : vaults.Create(Path.Combine(root, "target"), "目标");
        if (conflict == "name") await repositories.ImportAsync(target, local, repository.Name);
        if (conflict == "id") vaults.Register(target, new VaultRepository { RepoId = repository.RepoId, Name = "不同名称", RelativePath = "repos/other.git" });
        if (conflict == "directory")
        {
            var destination = vaults.RepositoryPath(target, repository);
            Directory.CreateDirectory(destination);
            await File.WriteAllTextAsync(Path.Combine(destination, "original.txt"), "不能覆盖");
        }
        var sourceManifest = File.ReadAllBytes(Path.Combine(sourceVault.RootPath, "vault.json"));
        var targetManifest = File.ReadAllBytes(Path.Combine(target.RootPath, "vault.json"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => repositories.MigrateAsync(sourceVault, repository, target));

        Assert.Equal(sourceManifest, File.ReadAllBytes(Path.Combine(sourceVault.RootPath, "vault.json")));
        Assert.Equal(targetManifest, File.ReadAllBytes(Path.Combine(target.RootPath, "vault.json")));
        Assert.True(Directory.Exists(vaults.VerifyRepository(sourceVault, repository)));
        if (conflict == "directory") Assert.Equal("不能覆盖", await File.ReadAllTextAsync(Path.Combine(vaults.RepositoryPath(target, repository), "original.txt")));
    }

    /// <summary>复制后取消仍保留原目录和登记，不把临时副本加入目标清单。</summary>
    [Fact]
    public async Task MigrationCancellationBeforeCommitRetainsSource()
    {
        var (_, sourceVault, repository) = await SeedAsync();
        var target = vaults.Create(Path.Combine(root, "target"), "目标");
        using var cancellation = new CancellationTokenSource();
        git.Log = message =>
        {
            if (message.Contains(".migrate-") && message.Contains("\"fsck\"")) cancellation.Cancel();
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repositories.MigrateAsync(sourceVault, repository, target, cancellation.Token));

        git.Log = null;
        Assert.True(Directory.Exists(vaults.VerifyRepository(sourceVault, repository)));
        Assert.Empty(vaults.Open(target.RootPath).Manifest.Repositories);
        Assert.False(Directory.Exists(vaults.RepositoryPath(target, repository)));
        Assert.Single(Directory.GetDirectories(Path.Combine(target.RootPath, "repos"), ".migrate-*"));
    }

    /// <summary>复制期间原库新增引用时停止提交，原库的新引用不会丢失。</summary>
    [Fact]
    public async Task MigrationRejectsSourceChangesDuringCopy()
    {
        var (_, sourceVault, repository) = await SeedAsync();
        var source = vaults.VerifyRepository(sourceVault, repository);
        var target = vaults.Create(Path.Combine(root, "target"), "目标");
        var head = await HeadAsync(source);
        git.Log = message =>
        {
            if (!message.Contains(".migrate-") || !message.Contains("\"fsck\"")) return;
            git.Log = null;
            // 日志回调同步触发，直接写入合法引用，避免在测试同步上下文中阻塞异步 Git。
            var heads = Path.Combine(source, "refs", "heads");
            Directory.CreateDirectory(heads);
            File.WriteAllText(Path.Combine(heads, "concurrent-change"), head + "\n");
        };

        var error = await Assert.ThrowsAsync<IOException>(() => repositories.MigrateAsync(sourceVault, repository, target));

        Assert.Contains("引用发生变化", error.Message);
        Assert.Contains("concurrent-change", (await git.RunAsync(source, ["branch", "--list"])).Output);
        Assert.True(Directory.Exists(vaults.VerifyRepository(sourceVault, repository)));
        Assert.Empty(vaults.Open(target.RootPath).Manifest.Repositories);
    }

    /// <summary>目标身份在复制中变化时拒绝写入，防止盘符被其他代码库复用。</summary>
    [Fact]
    public async Task MigrationRejectsReplacedTargetIdentity()
    {
        var (_, sourceVault, repository) = await SeedAsync();
        var target = vaults.Create(Path.Combine(root, "target"), "目标");
        git.Log = message =>
        {
            if (!message.Contains(".migrate-") || !message.Contains("\"fsck\"")) return;
            git.Log = null;
            var manifestPath = Path.Combine(target.RootPath, "vault.json");
            var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!;
            manifest["vaultId"] = Guid.NewGuid().ToString();
            File.WriteAllText(manifestPath, manifest.ToJsonString());
        };

        var error = await Assert.ThrowsAsync<IOException>(() => repositories.MigrateAsync(sourceVault, repository, target));

        Assert.Contains("目标代码库身份发生变化", error.Message);
        Assert.True(Directory.Exists(vaults.VerifyRepository(sourceVault, repository)));
        Assert.Empty(vaults.Open(target.RootPath).Manifest.Repositories);
    }

    /// <summary>原清单无法保存时恢复原目录；已验证的目标副本也保留，保证可恢复。</summary>
    [Fact]
    public async Task MigrationManifestWriteFailureRestoresSourceDirectory()
    {
        var (_, sourceVault, repository) = await SeedAsync();
        var target = vaults.Create(Path.Combine(root, "target"), "目标");
        var manifest = File.ReadAllBytes(Path.Combine(sourceVault.RootPath, "vault.json"));
        FileStream? backupLock = null;
        git.Log = message =>
        {
            if (!message.Contains(".migrate-") || !message.Contains("\"fsck\"")) return;
            git.Log = null;
            backupLock = new FileStream(Path.Combine(sourceVault.RootPath, "vault.json.bak"), FileMode.Open, FileAccess.Read, FileShare.None);
        };
        try
        {
            var error = await Assert.ThrowsAsync<IOException>(() => repositories.MigrateAsync(sourceVault, repository, target));
            Assert.Contains("目标副本已完整登记", error.Message);
            Assert.Equal(manifest, File.ReadAllBytes(Path.Combine(sourceVault.RootPath, "vault.json")));
            Assert.True(Directory.Exists(vaults.VerifyRepository(sourceVault, repository)));
            Assert.True(Directory.Exists(vaults.VerifyRepository(target, repository)));
            Assert.Empty(Directory.GetDirectories(Path.Combine(sourceVault.RootPath, "repos"), ".migrated-*"));
        }
        finally { backupLock?.Dispose(); git.Log = null; }
    }


    /// <summary>损坏的原仓库不能进入移除阶段，完整性检查失败时保留原登记和目录。</summary>
    [Fact]
    public async Task MigrationIntegrityFailureRetainsSource()
    {
        var (_, sourceVault, repository) = await SeedAsync();
        var source = vaults.VerifyRepository(sourceVault, repository);
        var oid = await HeadAsync(source);
        var objectPath = Path.Combine(source, "objects", oid[..2], oid[2..]);
        File.SetAttributes(objectPath, FileAttributes.Normal);
        File.Delete(objectPath);
        var target = vaults.Create(Path.Combine(root, "target"), "目标");

        await Assert.ThrowsAsync<GitException>(() => repositories.MigrateAsync(sourceVault, repository, target));

        Assert.True(Directory.Exists(vaults.VerifyRepository(sourceVault, repository)));
        Assert.Empty(vaults.Open(target.RootPath).Manifest.Repositories);
        Assert.False(Directory.Exists(vaults.RepositoryPath(target, repository)));
    }

    /// <summary>目标清单被其他进程占用时保留原项目，完整但未登记的目标可通过恢复列表找回。</summary>
    [Fact]
    public async Task MigrationTargetRegistrationFailureRetainsSource()
    {
        var (_, sourceVault, repository) = await SeedAsync();
        var target = vaults.Create(Path.Combine(root, "target"), "目标");
        using (var manifestLock = new FileStream(Path.Combine(target.RootPath, "vault.json.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            await Assert.ThrowsAsync<IOException>(() => repositories.MigrateAsync(sourceVault, repository, target));
        }

        Assert.True(Directory.Exists(vaults.VerifyRepository(sourceVault, repository)));
        Assert.Empty(vaults.Open(target.RootPath).Manifest.Repositories);
        Assert.True(Directory.Exists(vaults.RepositoryPath(target, repository)));
        Assert.Equal(1, (await repositories.RecoverAsync(target)).Recovered);
    }

    /// <summary>正在写入的仓库有 Git 锁时停止复制，原库保持完整。</summary>
    [Fact]
    public async Task MigrationRejectsRepositoryLock()
    {
        var (_, sourceVault, repository) = await SeedAsync();
        var source = vaults.VerifyRepository(sourceVault, repository);
        await File.WriteAllTextAsync(Path.Combine(source, "config.lock"), "");
        var target = vaults.Create(Path.Combine(root, "target"), "目标");

        var error = await Assert.ThrowsAsync<IOException>(() => repositories.MigrateAsync(sourceVault, repository, target));

        Assert.Contains("Git 锁", error.Message);
        Assert.True(Directory.Exists(vaults.VerifyRepository(sourceVault, repository)));
        Assert.Empty(vaults.Open(target.RootPath).Manifest.Repositories);
    }
}

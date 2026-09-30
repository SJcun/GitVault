using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using GitVault.Core;
using Xunit;

namespace GitVault.Core.Tests;

/// <summary>用真实清单和独立进程验证登记事务的并发保护。</summary>
public sealed class ManifestConcurrencyTests : IDisposable
{
    /// <summary>本测试唯一拥有的临时目录。</summary>
    private readonly string root = Path.Combine(Path.GetTempPath(), "GitVaultTests", Guid.NewGuid().ToString("N"));
    /// <summary>不共享内存锁的真实清单服务。</summary>
    private readonly VaultService vaults = new();

    /// <summary>同时使用旧快照登记，所有成功项目均保留；失败项目可在释放锁后重试。</summary>
    [Fact]
    public async Task ConcurrentRegistrationsRetainEverySuccessfulEntry()
    {
        var vault = vaults.Create(root, "并发测试");
        var entries = Enumerable.Range(0, 12).Select(index => Entry("项目" + index)).ToArray();
        using var start = new Barrier(entries.Length);
        var tasks = entries.Select(entry => Task.Factory.StartNew(() =>
        {
            start.SignalAndWait();
            try
            {
                new VaultService().Register(vault, entry);
                return true;
            }
            catch (IOException error) when (error.Message.Contains("正在被其他进程更新"))
            {
                return false;
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
        var succeeded = await Task.WhenAll(tasks);
        var successfulIds = entries.Where((_, index) => succeeded[index]).Select(entry => entry.RepoId).ToHashSet();
        Assert.NotEmpty(successfulIds);
        Assert.True(successfulIds.SetEquals(vaults.Open(root).Manifest.Repositories.Select(entry => entry.RepoId)));

        // 重试仍传入原始快照，必须合并锁内重新读取的最新清单。
        foreach (var entry in entries.Where((_, index) => !succeeded[index])) vaults.Register(vault, entry);
        Assert.True(entries.Select(entry => entry.RepoId).ToHashSet()
            .SetEquals(vaults.Open(root).Manifest.Repositories.Select(entry => entry.RepoId)));
    }

    /// <summary>独立进程持锁时立即拒绝，进程被终止后可使用原锁文件继续登记。</summary>
    [Fact]
    public async Task IndependentProcessLockRejectsWritesAndSurvivesOwnerTermination()
    {
        var vault = vaults.Create(root, "进程锁测试");
        var original = File.ReadAllText(Path.Combine(root, "vault.json"));
        var lockPath = Path.Combine(root, "vault.json.lock");
        using var holder = StartLockHolder(lockPath);
        try
        {
            Assert.Equal("ready", await holder.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20)));
            var error = Assert.Throws<IOException>(() => vaults.Register(vault, Entry("被阻止")));
            Assert.Contains("代码库正在被其他进程更新，请稍后重试", error.Message);
            Assert.Equal(original, File.ReadAllText(Path.Combine(root, "vault.json")));
        }
        finally
        {
            if (!holder.HasExited) holder.Kill();
            Assert.True(holder.WaitForExit(10000));
        }
        Assert.True(File.Exists(lockPath));
        var entry = Entry("恢复登记");
        vaults.Register(vault, entry);
        Assert.Equal(entry.RepoId, Assert.Single(vaults.Open(root).Manifest.Repositories).RepoId);
        Assert.True(File.Exists(lockPath));
        Assert.Equal(0, new FileInfo(lockPath).Length);
    }

    /// <summary>旧快照的身份不能覆盖磁盘上已替换的清单，拒绝后也应释放锁。</summary>
    [Fact]
    public void ReplacedIdentityIsRejectedWithoutWriting()
    {
        var vault = vaults.Create(root, "身份测试");
        var path = Path.Combine(root, "vault.json");
        var changed = JsonNode.Parse(File.ReadAllText(path))!;
        changed["vaultId"] = Guid.NewGuid().ToString();
        File.WriteAllText(path, changed.ToJsonString());
        var original = File.ReadAllText(path);
        Assert.Contains("身份发生变化", Assert.Throws<IOException>(() => vaults.Register(vault, Entry("旧身份"))).Message);
        Assert.Equal(original, File.ReadAllText(path));
        var current = vaults.Open(root);
        vaults.Register(current, Entry("新身份"));
        Assert.Single(vaults.Open(root).Manifest.Repositories);
    }

    /// <summary>同 RepoId、同显示名称或同路径均不能重复登记，失败保持清单原样。</summary>
    [Theory]
    [InlineData("id")]
    [InlineData("name")]
    [InlineData("path")]
    public void DuplicateRegistrationIsRejected(string duplicate)
    {
        var vault = vaults.Create(root, "重复测试");
        var entry = Entry("已登记");
        vaults.Register(vault, entry);
        var other = Entry("新项目");
        if (duplicate == "id") other.RepoId = entry.RepoId;
        if (duplicate == "name") other.Name = entry.Name;
        if (duplicate == "path") other.RelativePath = entry.RelativePath;
        var original = File.ReadAllText(Path.Combine(root, "vault.json"));
        Assert.Throws<InvalidOperationException>(() => vaults.Register(vault, other));
        Assert.Equal(original, File.ReadAllText(Path.Combine(root, "vault.json")));
    }

    /// <summary>构造唯一身份和 Vault 内部相对路径，不需要执行长时间 Git 操作。</summary>
    private static VaultRepository Entry(string name) => new() { Name = name, RelativePath = $"repos/{name}.git" };

    /// <summary>Windows PowerShell 子进程持有真实独占句柄，以标准输出确认锁已取得。</summary>
    private static Process StartLockHolder(string path)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardInput = true
        };
        start.Environment["GITVAULT_TEST_LOCK_PATH"] = path;
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes("""
            $ErrorActionPreference = 'Stop'
            $handle = [IO.File]::Open($env:GITVAULT_TEST_LOCK_PATH, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
            [Console]::WriteLine('ready')
            [Console]::Out.Flush()
            [Console]::In.ReadLine() | Out-Null
            $handle.Dispose()
            """)));
        return Process.Start(start)!;
    }

    /// <summary>清理本测试拥有的准确目录，禁止越过临时测试根目录。</summary>
    public void Dispose()
    {
        var owned = Path.GetFullPath(root);
        var boundary = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "GitVaultTests")) + Path.DirectorySeparatorChar;
        if (!owned.StartsWith(boundary, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("测试清理目录越界。");
        if (Directory.Exists(owned)) Directory.Delete(owned, recursive: true);
    }
}

using System.Text.Json.Nodes;
using GitVault.Core;
using Xunit;

namespace GitVault.Core.Tests;

/// <summary>真实裸仓库恢复：区分候选损坏、全局故障和取消，保留已成功登记的结果。</summary>
public sealed partial class RepositoryTests
{
    /// <summary>改变候选创建顺序，损坏目录都不阻止两个完整仓库恢复；重复执行只跳过登记项。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RecoveryContinuesAfterBrokenCandidateRegardlessOfOrder(bool brokenFirst)
    {
        var source = await NewRepositoryAsync("source");
        var vault = vaults.Create(Path.Combine(root, "recovery"), "恢复测试");
        var broken = Path.Combine(vault.RootPath, "repos", "Broken.git");
        if (brokenFirst) Directory.CreateDirectory(broken);
        foreach (var name in new[] { "First", "Last" })
            await git.RunAsync(null, ["clone", "--bare", "--no-hardlinks", source, Path.Combine(vault.RootPath, "repos", name + ".git")]);
        if (!brokenFirst) Directory.CreateDirectory(broken);
        Directory.CreateDirectory(Path.Combine(vault.RootPath, "repos", ".import-hidden.git"));
        var result = await repositories.RecoverAsync(vault);
        Assert.Equal((2, 0, false), (result.Recovered, result.Skipped, result.Cancelled));
        Assert.Equal(broken, Assert.Single(result.Failures).Path);
        Assert.Equal(new[] { "First", "Last" }, vaults.Open(vault.RootPath).Manifest.Repositories.Select(entry => entry.Name).Order());
        Assert.True(Directory.Exists(broken));
        var repeated = await repositories.RecoverAsync(vault);
        Assert.Equal((0, 2, 1), (repeated.Recovered, repeated.Skipped, repeated.Failures.Count));
    }

    /// <summary>裸仓库缺少提交对象时 fsck 失败，只报告该候选，后续完整仓库仍可登记。</summary>
    [Fact]
    public async Task RecoveryReportsMissingObjectsAndContinues()
    {
        var source = await NewRepositoryAsync("source");
        var vault = vaults.Create(Path.Combine(root, "recovery"), "恢复测试");
        var broken = Path.Combine(vault.RootPath, "repos", "Broken.git");
        await git.RunAsync(null, ["clone", "--bare", "--no-hardlinks", source, broken]);
        var oid = await HeadAsync(source);
        var objectFile = Path.Combine(broken, "objects", oid[..2], oid[2..]);
        File.SetAttributes(objectFile, FileAttributes.Normal);
        File.Delete(objectFile);
        await git.RunAsync(null, ["clone", "--bare", "--no-hardlinks", source, Path.Combine(vault.RootPath, "repos", "Good.git")]);
        var result = await repositories.RecoverAsync(vault);
        Assert.Equal(1, result.Recovered);
        Assert.Equal(broken, Assert.Single(result.Failures).Path);
        Assert.Equal("Good", Assert.Single(vaults.Open(vault.RootPath).Manifest.Repositories).Name);
    }

    /// <summary>第二个候选开始验证时取消，第一项登记和磁盘内容保留。</summary>
    [Fact]
    public async Task RecoveryCancellationReturnsCompletedCount()
    {
        var source = await NewRepositoryAsync("source");
        var vault = vaults.Create(Path.Combine(root, "recovery"), "恢复测试");
        foreach (var name in new[] { "One", "Two", "Three" })
            await git.RunAsync(null, ["clone", "--bare", "--no-hardlinks", source, Path.Combine(vault.RootPath, "repos", name + ".git")]);
        using var cancellation = new CancellationTokenSource();
        var checks = 0;
        git.Log = line =>
        {
            if (line.Contains("\"--is-bare-repository\"") && ++checks == 2) cancellation.Cancel();
        };
        var result = await repositories.RecoverAsync(vault, cancellation.Token);
        Assert.Equal((1, true, 0), (result.Recovered, result.Cancelled, result.Failures.Count));
        Assert.Single(vaults.Open(vault.RootPath).Manifest.Repositories);
        Assert.Equal(3, Directory.GetDirectories(Path.Combine(vault.RootPath, "repos"), "*.git").Length);
    }

    /// <summary>验证期间清单身份替换或格式损坏必须全局中止，不能归为候选失败。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RecoveryAbortsWhenManifestChangesDuringValidation(bool replaceIdentity)
    {
        var source = await NewRepositoryAsync("source");
        var vault = vaults.Create(Path.Combine(root, "recovery"), "恢复测试");
        await git.RunAsync(null, ["clone", "--bare", "--no-hardlinks", source, Path.Combine(vault.RootPath, "repos", "Good.git")]);
        var manifest = Path.Combine(vault.RootPath, "vault.json");
        var changed = false;
        git.Log = line =>
        {
            if (changed || !line.Contains("\"fsck\"")) return;
            changed = true;
            if (!replaceIdentity) File.WriteAllText(manifest, "broken");
            else
            {
                var json = JsonNode.Parse(File.ReadAllText(manifest))!;
                json["vaultId"] = Guid.NewGuid();
                File.WriteAllText(manifest, json.ToJsonString());
            }
        };
        await Assert.ThrowsAnyAsync<Exception>(() => repositories.RecoverAsync(vault));
        Assert.True(changed);
        Assert.True(Directory.Exists(Path.Combine(vault.RootPath, "repos", "Good.git")));
    }

    /// <summary>清单登记被独立锁阻止时全局失败，不报告本轮恢复成功。</summary>
    [Fact]
    public async Task RecoveryManifestLockFailureIsGlobal()
    {
        var source = await NewRepositoryAsync("source");
        var vault = vaults.Create(Path.Combine(root, "recovery"), "恢复测试");
        await git.RunAsync(null, ["clone", "--bare", "--no-hardlinks", source, Path.Combine(vault.RootPath, "repos", "Good.git")]);
        using var held = new FileStream(Path.Combine(vault.RootPath, "vault.json.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var error = await Assert.ThrowsAsync<IOException>(() => repositories.RecoverAsync(vault));
        Assert.Contains("正在被其他进程更新", error.Message);
        Assert.Empty(vaults.Open(vault.RootPath).Manifest.Repositories);
    }
    /// <summary>第二项登记遇锁时全局中止，第一项已完成登记仍保留。</summary>
    [Fact]
    public async Task RecoveryGlobalWriteFailureRetainsEarlierSuccess()
    {
        var source = await NewRepositoryAsync("source");
        var vault = vaults.Create(Path.Combine(root, "recovery"), "恢复测试");
        foreach (var name in new[] { "One", "Two" })
            await git.RunAsync(null, ["clone", "--bare", "--no-hardlinks", source, Path.Combine(vault.RootPath, "repos", name + ".git")]);
        FileStream? held = null;
        var checks = 0;
        git.Log = line =>
        {
            if (line.Contains("\"fsck\"") && ++checks == 2)
                held = new FileStream(Path.Combine(vault.RootPath, "vault.json.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        };
        try
        {
            await Assert.ThrowsAsync<IOException>(() => repositories.RecoverAsync(vault));
            Assert.Single(vaults.Open(vault.RootPath).Manifest.Repositories);
            Assert.Equal(2, Directory.GetDirectories(Path.Combine(vault.RootPath, "repos"), "*.git").Length);
        }
        finally { held?.Dispose(); }
    }

    /// <summary>目录联接候选被拒绝且不读取外部仓库，正常候选仍登记。</summary>
    [Fact]
    public async Task RecoveryRejectsJunctionCandidate()
    {
        var source = await NewRepositoryAsync("source");
        var vault = vaults.Create(Path.Combine(root, "recovery"), "恢复测试");
        var external = Path.Combine(root, "outside.git");
        await git.RunAsync(null, ["clone", "--bare", "--no-hardlinks", source, external]);
        var link = Path.Combine(vault.RootPath, "repos", "Linked.git");
        // PowerShell 的字面路径只指向本测试目录，联接创建不需要管理员权限。
        var script = "New-Item -ItemType Junction -Path '" + link.Replace("'", "''")
            + "' -Target '" + external.Replace("'", "''") + "' -ErrorAction Stop | Out-Null";
        var start = new System.Diagnostics.ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script)));
        using var process = System.Diagnostics.Process.Start(start)!;
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
        try
        {
            await git.RunAsync(null, ["clone", "--bare", "--no-hardlinks", source, Path.Combine(vault.RootPath, "repos", "Good.git")]);
            var result = await repositories.RecoverAsync(vault);
            Assert.Equal(1, result.Recovered);
            Assert.Equal(link, Assert.Single(result.Failures).Path);
            Assert.True(Directory.Exists(external));
        }
        finally { Directory.Delete(link); }
    }
}

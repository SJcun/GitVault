using System.Text.Json;
using GitVault.Core;
using Xunit;

namespace GitVault.Core.Tests;

/// <summary>真实文件系统验证有效备份、明确恢复和保存失败后的原文件保护。</summary>
public sealed class MetadataBackupTests : IDisposable
{
    /// <summary>本测试独占的目录与设置服务。</summary>
    private readonly string root = Path.Combine(Path.GetTempPath(), "GitVaultMetadataTests", Guid.NewGuid().ToString("N"));
    /// <summary>使用本次独占目录的设置服务。</summary>
    private SettingsService Store => new(root);
    /// <summary>本次验证的主设置文件路径。</summary>
    private string Main => Path.Combine(root, "settings.json");

    /// <summary>首次保存无伪造备份；第二次保存的备份是上一有效版本。</summary>
    [Fact]
    public void FirstAndSecondSaveHaveCorrectVersions()
    {
        Store.Save(new AppSettings());
        Assert.False(File.Exists(Main + ".bak"));
        var original = File.ReadAllBytes(Main);
        Store.Save(new AppSettings { GitPath = "portable-git.exe" });
        Assert.Equal("portable-git.exe", Store.Load().GitPath);
        Assert.Equal(original, File.ReadAllBytes(Main + ".bak"));
        Assert.Equal("git", Store.ReadBackup().GitPath);
        Assert.Empty(Directory.GetFiles(root, "*.tmp"));
    }

    /// <summary>主文件损坏或缺失时禁止默默重建，明确恢复才写回；损坏现场和备份保留。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BrokenOrMissingMainRequiresExplicitRecovery(bool missing)
    {
        Store.Save(new AppSettings());
        Store.Save(new AppSettings { GitPath = "later-git.exe" });
        var backup = File.ReadAllBytes(Main + ".bak");
        if (missing) File.Delete(Main);
        else File.WriteAllText(Main, "broken");
        Assert.ThrowsAny<Exception>(() => Store.Load());
        Assert.ThrowsAny<Exception>(() => Store.Save(new AppSettings()));
        Assert.Equal(backup, File.ReadAllBytes(Main + ".bak"));
        if (!missing) Assert.Equal("broken", File.ReadAllText(Main));
        var restored = Store.RestoreBackup();
        Assert.Equal("git", restored.GitPath);
        Assert.Equal("git", Store.Load().GitPath);
        Assert.Equal(backup, File.ReadAllBytes(Main + ".bak"));
        if (!missing) Assert.Equal("broken", File.ReadAllText(Assert.Single(Directory.GetFiles(root, "settings.json.before-restore.*"))));
        Assert.Empty(Directory.GetFiles(root, "*.tmp"));
    }

    /// <summary>备份格式和绑定语义无效时拒绝恢复，损坏主文件不变。</summary>
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"gitPath\":\"git\",\"knownVaultPaths\":[],\"bindings\":[{\"vaultId\":\"00000000-0000-0000-0000-000000000000\",\"repoId\":\"00000000-0000-0000-0000-000000000000\",\"localPath\":\"relative\"}]}")]
    public void InvalidSettingsBackupCannotReplaceMain(string invalid)
    {
        Store.Save(new AppSettings());
        File.WriteAllText(Main, "broken");
        File.WriteAllText(Main + ".bak", invalid);
        Assert.ThrowsAny<Exception>(() => Store.ReadBackup());
        Assert.ThrowsAny<Exception>(() => Store.RestoreBackup());
        Assert.Equal("broken", File.ReadAllText(Main));
        Assert.Equal(invalid, File.ReadAllText(Main + ".bak"));
        Assert.Empty(Directory.GetFiles(root, "*.before-restore.*"));
    }

    /// <summary>主文件只读或被占用时替换失败，原内容保持完整有效，临时文件清理。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ProtectedMainSurvivesFailedSave(bool readOnly)
    {
        Store.Save(new AppSettings());
        var original = File.ReadAllBytes(Main);
        FileStream? held = null;
        try
        {
            if (readOnly) File.SetAttributes(Main, FileAttributes.ReadOnly);
            else held = new FileStream(Main, FileMode.Open, FileAccess.Read, FileShare.Read);
            Assert.ThrowsAny<Exception>(() => Store.Save(new AppSettings { GitPath = "new.exe" }));
            Assert.Equal(original, File.ReadAllBytes(Main));
            Assert.Equal("git", Store.Load().GitPath);
        }
        finally { held?.Dispose(); File.SetAttributes(Main, FileAttributes.Normal); }
        Assert.Empty(Directory.GetFiles(root, "*.tmp"));
    }

    /// <summary>固定备份不可更新时保存失败，主文件和已有备份均保留。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BackupUpdateFailurePreservesMain(bool directory)
    {
        Store.Save(new AppSettings());
        Store.Save(new AppSettings { GitPath = "second.exe" });
        var original = File.ReadAllBytes(Main);
        var oldBackup = File.ReadAllBytes(Main + ".bak");
        if (directory) { File.Delete(Main + ".bak"); Directory.CreateDirectory(Main + ".bak"); }
        else File.SetAttributes(Main + ".bak", FileAttributes.ReadOnly);
        try
        {
            Assert.ThrowsAny<Exception>(() => Store.Save(new AppSettings { GitPath = "third.exe" }));
            Assert.Equal(original, File.ReadAllBytes(Main));
            if (!directory) Assert.Equal(oldBackup, File.ReadAllBytes(Main + ".bak"));
        }
        finally { if (!directory) File.SetAttributes(Main + ".bak", FileAttributes.Normal); }
        Assert.Empty(Directory.GetFiles(root, "*.tmp"));
    }

    /// <summary>受控临时写入或备份写入中断模拟写满，保留原主备和其他任务的临时文件。</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void PartialTemporaryWriteCleansOnlyOwnedFiles(int failOnWrite)
    {
        Store.Save(new AppSettings());
        Store.Save(new AppSettings { GitPath = "second.exe" });
        var original = File.ReadAllBytes(Main);
        var backup = File.ReadAllBytes(Main + ".bak");
        var unrelated = Path.Combine(root, "other-operation.tmp");
        File.WriteAllText(unrelated, "keep");
        var writes = 0;
        var error = Assert.Throws<IOException>(() => JsonFile.Write(Main, new AppSettings { GitPath = "third.exe" }, SettingsService.Validate,
            (path, bytes) =>
            {
                File.WriteAllBytes(path, ++writes == failOnWrite ? bytes[..3] : bytes);
                if (writes == failOnWrite) throw new IOException("模拟磁盘写满");
            }));
        Assert.Contains("模拟磁盘写满", error.Message);
        Assert.Equal(original, File.ReadAllBytes(Main));
        Assert.Equal(backup, File.ReadAllBytes(Main + ".bak"));
        Assert.Equal(unrelated, Assert.Single(Directory.GetFiles(root, "*.tmp")));
        Assert.Equal("keep", File.ReadAllText(unrelated));
    }

    /// <summary>临时文件被占用导致清理失败时，保留主写入错误并附上次要诊断。</summary>
    [Fact]
    public void CleanupErrorDoesNotReplaceWriteError()
    {
        Store.Save(new AppSettings());
        FileStream? held = null;
        string? temporary = null;
        try
        {
            var error = Assert.Throws<IOException>(() => JsonFile.Write(Main, new AppSettings(), SettingsService.Validate,
                (path, bytes) =>
                {
                    temporary = path;
                    held = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
                    throw new IOException("主要写入错误");
                }));
            Assert.Equal("主要写入错误", error.Message);
            Assert.Single(error.Data.Keys.Cast<string>());
            Assert.Equal("git", Store.Load().GitPath);
        }
        finally
        {
            held?.Dispose();
            if (temporary is not null && Path.GetFullPath(temporary).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) File.Delete(temporary);
        }
    }

    /// <summary>清单备份保留身份，恢复使用相同完整校验并拒绝不同身份。</summary>
    [Fact]
    public void VaultBackupPreservesIdentityAndRejectsMismatch()
    {
        var vaults = new VaultService();
        var vault = vaults.Create(Path.Combine(root, "vault"), "备份测试");
        var path = Path.Combine(vault.RootPath, "vault.json");
        vault = vaults.Register(vault, new VaultRepository { Name = "One", RelativePath = "repos/One.git" });
        vault = vaults.Register(vault, new VaultRepository { Name = "Two", RelativePath = "repos/Two.git" });
        Assert.Equal(2, vault.Manifest.Repositories.Count);
        Assert.Single(vaults.ReadBackup(vault.RootPath, vault.Manifest.VaultId).Manifest.Repositories);
        var backup = File.ReadAllBytes(path + ".bak");
        File.WriteAllText(path, "broken");
        Assert.Throws<IOException>(() => vaults.RestoreBackup(vault.RootPath, Guid.NewGuid()));
        Assert.Equal("broken", File.ReadAllText(path));
        var restored = vaults.RestoreBackup(vault.RootPath, vault.Manifest.VaultId);
        Assert.Equal(vault.Manifest.VaultId, restored.Manifest.VaultId);
        Assert.Single(restored.Manifest.Repositories);
        Assert.Equal(backup, File.ReadAllBytes(path + ".bak"));
        Assert.Equal("broken", File.ReadAllText(Assert.Single(Directory.GetFiles(vault.RootPath, "vault.json.before-restore.*"))));
    }

    /// <summary>备份的版本、条目唯一性和路径边界错误不能因恢复绕过校验。</summary>
    [Theory]
    [InlineData("schema")]
    [InlineData("duplicate")]
    [InlineData("path")]
    public void InvalidVaultBackupIsRejectedWithoutWriting(string problem)
    {
        var vaults = new VaultService();
        var vault = vaults.Create(Path.Combine(root, "vault"), "备份测试");
        var first = new VaultRepository { Name = "One", RelativePath = "repos/One.git" };
        vault.Manifest.Repositories.Add(first);
        if (problem == "schema") vault.Manifest.SchemaVersion = 2;
        if (problem == "duplicate") vault.Manifest.Repositories.Add(new VaultRepository { Name = "One", RelativePath = "repos/Two.git" });
        if (problem == "path") first.RelativePath = "../outside.git";
        var path = Path.Combine(vault.RootPath, "vault.json");
        File.WriteAllText(path + ".bak", JsonSerializer.Serialize(vault.Manifest, SettingsService.JsonOptions));
        File.WriteAllText(path, "broken");
        Assert.Throws<InvalidDataException>(() => vaults.RestoreBackup(vault.RootPath, vault.Manifest.VaultId));
        Assert.Equal("broken", File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(vault.RootPath, "*.before-restore.*"));
    }

    /// <summary>备份恢复与登记共用独占锁，锁被占用时不替换主文件。</summary>
    [Fact]
    public void VaultRecoveryHonorsManifestLock()
    {
        var vaults = new VaultService();
        var vault = vaults.Create(Path.Combine(root, "vault"), "备份测试");
        vaults.Register(vault, new VaultRepository { Name = "One", RelativePath = "repos/One.git" });
        var path = Path.Combine(vault.RootPath, "vault.json");
        File.WriteAllText(path, "broken");
        using var held = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        Assert.Throws<IOException>(() => vaults.RestoreBackup(vault.RootPath, vault.Manifest.VaultId));
        Assert.Equal("broken", File.ReadAllText(path));
    }

    /// <summary>当前主文件仍是另一有效身份时，即使备份有效也不能覆盖。</summary>
    [Fact]
    public void ValidForeignMainCannotBeReplacedByBackup()
    {
        var vaults = new VaultService();
        var vault = vaults.Create(Path.Combine(root, "vault"), "有效主文件");
        var path = Path.Combine(vault.RootPath, "vault.json");
        var original = File.ReadAllBytes(path);
        var backup = new VaultManifest { Name = "另一身份备份" };
        File.WriteAllText(path + ".bak", JsonSerializer.Serialize(backup, SettingsService.JsonOptions));
        Assert.Throws<IOException>(() => vaults.RestoreBackup(vault.RootPath, backup.VaultId));
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(vault.RootPath, "*.before-restore.*"));
    }

    /// <summary>只清理本测试拥有的文件，不扫描其他保存任务的临时目录。</summary>
    public void Dispose()
    {
        var boundary = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "GitVaultMetadataTests")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(root).StartsWith(boundary, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("测试清理越界。");
        if (!Directory.Exists(root)) return;
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(root, true);
    }
}

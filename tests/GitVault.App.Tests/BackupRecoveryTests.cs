using System.IO;
using System.Windows;
using System.Windows.Threading;
using GitVault.App;
using GitVault.Core;
using Xunit;

namespace GitVault.App.Tests;

/// <summary>复用既有 WPF 宿主验证明确恢复选择，不重复创建 Application。</summary>
internal static class BackupRecoveryTests
{
    /// <summary>取消恢复保持现场并禁用普通操作；确认后恢复设置，再初始化真实 Git。</summary>
    internal static async Task VerifySettingsAsync(string root)
    {
        var store = new SettingsService(Path.Combine(root, "settings-backup-ui"));
        store.Save(new AppSettings());
        store.Save(new AppSettings { GitPath = "invalid-later.exe" });
        var main = Path.Combine(store.DirectoryPath, "settings.json");
        File.WriteAllText(main, "broken");
        var model = new MainViewModel(null, store);
        try
        {
            Choose("恢复本机设置", false);
            await model.InitializeAsync();
            Assert.False(model.CanWork);
            Assert.True(model.RecoverSettingsCommand.CanExecute(null));
            Assert.Equal("broken", File.ReadAllText(main));
            Choose("恢复本机设置", true);
            await model.RecoverSettingsCommand.ExecuteAsync(null);
            Assert.True(model.CanWork);
            Assert.False(model.ShowSettingsRecovery);
            Assert.Equal("git", store.Load().GitPath);
            Assert.Equal("broken", File.ReadAllText(Assert.Single(Directory.GetFiles(store.DirectoryPath, "settings.json.before-restore.*"))));
        }
        finally { await model.StopLoggingAsync(); }
    }

    /// <summary>清单恢复取消时不改文件，确认后保持身份并补登记备份缺少的完整裸仓库。</summary>
    internal static async Task VerifyVaultAsync(string root, string source, GitCommandService git)
    {
        var vaults = new VaultService();
        var vault = vaults.Create(Path.Combine(root, "vault-backup-ui"), "界面恢复测试");
        await new RepositoryService(git, vaults).ImportAsync(vault, source, "Project");
        var main = Path.Combine(vault.RootPath, "vault.json");
        var backup = File.ReadAllBytes(main + ".bak");
        File.WriteAllText(main, "broken");
        var model = new MainViewModel(null, new SettingsService(Path.Combine(root, "vault-backup-ui-settings")));
        try
        {
            Choose("恢复代码库信息", false);
            Assert.Null(await model.ReadVaultWithRecoveryAsync(vault.RootPath, vault.Manifest.VaultId, CancellationToken.None));
            Assert.Equal("broken", File.ReadAllText(main));
            Choose("恢复代码库信息", true);
            var restored = await model.ReadVaultWithRecoveryAsync(vault.RootPath, vault.Manifest.VaultId, CancellationToken.None);
            Assert.Equal(vault.Manifest.VaultId, restored!.Manifest.VaultId);
            Assert.Equal("Project", Assert.Single(restored.Manifest.Repositories).Name);
            Assert.Equal("broken", File.ReadAllText(Assert.Single(Directory.GetFiles(vault.RootPath, "vault.json.before-restore.*"))));
            // 恢复后的补登记按正常保存规则更新备份；此前备份保留在主文件恢复步骤中。
            Assert.NotEmpty(backup);
            Assert.True(await model.StopLoggingAsync());
            Assert.Contains("备份恢复后检查：登记 1 个", model.LogText);
        }
        finally { await model.StopLoggingAsync(); }
    }

    /// <summary>在模态窗口已显示后提交真实的取消/确认选择。</summary>
    private static void Choose(string title, bool restore) =>
        Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            Application.Current.Windows.OfType<Window>().Single(window => window.Title == title).DialogResult = restore));
}

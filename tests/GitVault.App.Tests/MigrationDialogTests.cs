using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GitVault.App;
using GitVault.Core;
using Xunit;

namespace GitVault.App.Tests;

/// <summary>复用已有 WPF 宿主验证迁移对话框、当前代码库切换和本机绑定转移。</summary>
internal static class MigrationDialogTests
{
    /// <summary>取消不修改代码库；确认后移除原项目、选中目标项目，并保留其他绑定。</summary>
    internal static async Task VerifyAsync(string root, string local, GitCommandService git)
    {
        var vaults = new VaultService();
        var repositories = new RepositoryService(git, vaults);
        var source = vaults.Create(Path.Combine(root, "migration-source"), "迁移原库");
        var target = vaults.Create(Path.Combine(root, "migration-target"), "迁移目标库");
        var alternative = vaults.Create(Path.Combine(root, "migration-alternative"), "迁移目标库");
        var repository = await repositories.ImportAsync(source, local, "迁移项目");
        var existing = await repositories.ImportAsync(target, local, "目标已有项目");
        var store = new SettingsService(Path.Combine(root, "migration-settings"));
        var settings = new AppSettings
        {
            Bindings = [new(source.Manifest.VaultId, repository.RepoId, local), new(target.Manifest.VaultId, existing.RepoId, local)]
        };
        var model = new MainViewModel(null, store);
        try
        {
            typeof(MainViewModel).GetField("settings", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(model, settings);
            typeof(MainViewModel).GetField("settingsAvailable", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(model, true);
            model.IsBusy = true;
            await (Task)typeof(MainViewModel).GetMethod("LoadVaultAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(model, [source, CancellationToken.None])!;
            Assert.False(model.MigrateCommand.CanExecute(null));
            model.IsBusy = false;
            Assert.True(model.MigrateCommand.CanExecute(null));
            var sourceManifest = File.ReadAllBytes(Path.Combine(source.RootPath, "vault.json"));
            var targetManifest = File.ReadAllBytes(Path.Combine(target.RootPath, "vault.json"));

            ComboBox? emptySelector = null;
            var emptyCanSubmit = true;
            _ = Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                var dialog = Application.Current.Windows.OfType<Window>().Single(window => window.Title == "迁移到其他代码库");
                var panel = (StackPanel)dialog.Content;
                emptySelector = panel.Children.OfType<DockPanel>().Single().Children.OfType<ComboBox>().Single();
                emptyCanSubmit = panel.Children.OfType<StackPanel>().Single().Children.OfType<Button>().Single(button => button.IsDefault).IsEnabled;
                dialog.DialogResult = false;
            }));
            await model.MigrateCommand.ExecuteAsync(null);
            Assert.NotNull(emptySelector);
            Assert.Empty(emptySelector.Items);
            Assert.False(emptyCanSubmit);
            Assert.Equal(sourceManifest, File.ReadAllBytes(Path.Combine(source.RootPath, "vault.json")));
            Assert.Equal(targetManifest, File.ReadAllBytes(Path.Combine(target.RootPath, "vault.json")));

            // 已识别的同名代码库均可选择，以路径区分；当前代码库不能作为目标。
            model.VaultChoices.Add(new VaultChoice(target));
            model.VaultChoices.Add(new VaultChoice(alternative));
            VaultChoice[] candidates = [];

            Task? reactivation = null;
            _ = Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                var dialog = Application.Current.Windows.OfType<Window>().Single(window => window.Title == "迁移到其他代码库");
                var panel = (StackPanel)dialog.Content;
                var selector = panel.Children.OfType<DockPanel>().Single().Children.OfType<ComboBox>().Single();
                candidates = selector.Items.Cast<VaultChoice>().ToArray();
                selector.SelectedItem = candidates.Single(choice => choice.Location.RootPath == target.RootPath);
                dialog.Closed += (_, _) => reactivation = model.WindowActivatedAsync();
                panel.Children.OfType<StackPanel>().Single().Children.OfType<Button>().Single(button => button.IsDefault)
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }));
            await model.MigrateCommand.ExecuteAsync(null);
            if (reactivation is not null) await reactivation;
            Assert.Equal(2, candidates.Length);
            Assert.DoesNotContain(candidates, choice => choice.Location.Manifest.VaultId == source.Manifest.VaultId);
            Assert.Equal(2, candidates.Select(choice => choice.Label).Distinct().Count());
            Assert.All(candidates, choice => Assert.Contains(choice.Location.RootPath, choice.Label));

            Assert.Empty(vaults.Open(source.RootPath).Manifest.Repositories);
            Assert.False(Directory.Exists(vaults.RepositoryPath(source, repository)));
            Assert.Equal(target.RootPath, model.VaultPath);
            Assert.Equal(repository.RepoId, model.SelectedItem?.Repository.RepoId);
            Assert.Equal(local, model.SelectedItem?.LocalPath);
            Assert.Equal(SyncKind.Synced, model.SelectedItem?.Status?.Kind);
            var bindings = store.Load().Bindings;
            Assert.Equal(2, bindings.Count);
            Assert.DoesNotContain(bindings, binding => binding.VaultId == source.Manifest.VaultId && binding.RepoId == repository.RepoId);
            Assert.Contains(bindings, binding => binding.VaultId == target.Manifest.VaultId && binding.RepoId == repository.RepoId && binding.LocalPath == local);
            Assert.Contains(bindings, binding => binding.VaultId == target.Manifest.VaultId && binding.RepoId == existing.RepoId);
            Assert.Equal("迁移完成：「迁移项目」已移至「迁移目标库」。", model.Feedback);

            // 新导入但未在当前电脑绑定的项目，也可以直接从代码库迁移。
            var unbound = await repositories.ImportAsync(source, local, "未绑定项目");
            model.IsBusy = true;
            await (Task)typeof(MainViewModel).GetMethod("LoadVaultAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(model, [source, CancellationToken.None])!;
            model.IsBusy = false;
            Assert.Null(model.SelectedItem?.LocalPath);
            Assert.True(model.MigrateCommand.CanExecute(null));
            ChooseTarget(target.RootPath);
            await model.MigrateCommand.ExecuteAsync(null);
            Assert.Equal(unbound.RepoId, model.SelectedItem?.Repository.RepoId);
            Assert.Null(model.SelectedItem?.LocalPath);
            Assert.True(model.NeedsBinding);
            Assert.Equal(2, store.Load().Bindings.Count);

            // 仓库提交成功后本机设置保存失败，明确报告已迁移，清空过期列表，随后刷新可恢复。
            var last = await repositories.ImportAsync(source, local, "绑定保存失败");
            settings.Bindings.Add(new RepositoryBinding(source.Manifest.VaultId, last.RepoId, local));
            model.IsBusy = true;
            await (Task)typeof(MainViewModel).GetMethod("LoadVaultAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(model, [source, CancellationToken.None])!;
            model.IsBusy = false;
            ChooseTarget(target.RootPath);
            using (var settingsLock = new FileStream(Path.Combine(store.DirectoryPath, "settings.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
                await model.MigrateCommand.ExecuteAsync(null);
            Assert.Equal("迁移已完成，本机绑定或状态更新失败 · 查看日志", model.Feedback);
            Assert.Empty(model.Items);
            Assert.Empty(vaults.Open(source.RootPath).Manifest.Repositories);
            Assert.True(Directory.Exists(vaults.VerifyRepository(target, last)));
            Assert.Contains(target.RootPath, model.LogText);
            await model.RefreshCommand.ExecuteAsync(null);
            Assert.Equal(4, model.Items.Count);
            Assert.Contains(store.Load().Bindings, binding => binding.VaultId == target.Manifest.VaultId && binding.RepoId == last.RepoId && binding.LocalPath == local);
            await MigrationReminderTests.VerifyAsync(root, local, git, source, target, repository);
        }
        finally { await model.StopLoggingAsync(); }
    }

    /// <summary>在真实迁移表单打开后选择已有目标代码库并提交。</summary>
    private static void ChooseTarget(string path) =>
        Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            var dialog = Application.Current.Windows.OfType<Window>().Single(window => window.Title == "迁移到其他代码库");
            var panel = (StackPanel)dialog.Content;
            var selector = panel.Children.OfType<DockPanel>().Single().Children.OfType<ComboBox>().Single();
            selector.SelectedItem = selector.Items.Cast<VaultChoice>().Single(choice => choice.Location.RootPath == path);
            panel.Children.OfType<StackPanel>().Single().Children.OfType<Button>().Single(button => button.IsDefault)
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }));
}

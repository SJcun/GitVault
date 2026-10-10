using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GitVault.App;
using GitVault.Core;
using Xunit;

namespace GitVault.App.Tests;

/// <summary>复用 WPF 宿主模拟另一台电脑的旧绑定和 U 盘连接变化。</summary>
internal static class MigrationReminderTests
{
    /// <summary>迁移后插盘提醒并定位项目，重复通知不打扰，重插可再提醒，绑定后停止提醒。</summary>
    internal static async Task VerifyAsync(string root, string local, GitCommandService git,
        VaultLocation source, VaultLocation target, VaultRepository repository)
    {
        var store = new SettingsService(Path.Combine(root, "reminder-settings"));
        var settings = new AppSettings
        {
            KnownVaultPaths = [source.RootPath, target.RootPath],
            Bindings = [new(source.Manifest.VaultId, repository.RepoId, local)]
        };
        var model = new MainViewModel(null, store);
        var messages = new List<(string Text, string Action)>();
        var answers = new Queue<bool>([true, false]);
        // 定时关闭真实模态窗口，额外弹窗也会被记录，避免回归失败时测试一直等待。
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) =>
        {
            var dialog = Application.Current.Windows.OfType<Window>().FirstOrDefault(window => window.Title == "项目迁移提醒");
            if (dialog is null) return;
            var panel = (StackPanel)dialog.Content;
            messages.Add((panel.Children.OfType<TextBlock>().Last().Text,
                panel.Children.OfType<StackPanel>().Single().Children.OfType<Button>().Single(button => button.IsDefault).Content.ToString()!));
            if (messages.Count == 1)
            {
                // 同一插盘事件和窗口激活发生在弹窗期间，也不能重复提醒。
                _ = model.DeviceChangedAsync();
                _ = model.WindowActivatedAsync();
            }
            dialog.DialogResult = answers.Count > 0 && answers.Dequeue();
        };
        try
        {
            typeof(MainViewModel).GetField("settings", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(model, settings);
            typeof(MainViewModel).GetField("settingsAvailable", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(model, true);
            model.IsBusy = true;
            await (Task)typeof(MainViewModel).GetMethod("LoadVaultAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(model, [source, CancellationToken.None])!;
            model.IsBusy = false;
            timer.Start();

            // 原代码库不再连接，扫描仍须发现目标库中的迁移项目并允许切换。
            Directory.Move(source.RootPath, Path.Combine(root, "reminder-source-disconnected"));
            await model.DeviceChangedAsync();
            Assert.Single(messages);
            Assert.Contains(repository.Name, messages[0].Text);
            Assert.Contains("绑定已有项目", messages[0].Text);
            Assert.DoesNotContain("目标已有项目", messages[0].Text);
            Assert.DoesNotContain("未绑定项目", messages[0].Text);
            Assert.Equal("打开待绑定项目", messages[0].Action);
            Assert.Equal(target.RootPath, model.VaultPath);
            Assert.Equal(repository.RepoId, model.SelectedItem?.Repository.RepoId);
            Assert.Null(model.SelectedItem?.LocalPath);
            Assert.True(model.NeedsBinding);
            Assert.True(model.BindCommand.CanExecute(null));
            Assert.Single(store.Load().Bindings);
            await model.RefreshCommand.ExecuteAsync(null);
            Assert.Single(messages);

            // 断开后再次接入会重新提醒，取消不修改原绑定。
            var disconnected = Path.Combine(root, "reminder-target-disconnected");
            Directory.Move(target.RootPath, disconnected);
            await model.DeviceChangedAsync();
            Assert.False(model.IsOnline);
            Directory.Move(disconnected, target.RootPath);
            await model.DeviceChangedAsync();
            Assert.Equal(2, messages.Count);
            Assert.Contains("需要重新绑定", model.Feedback);
            Assert.Equal(source.Manifest.VaultId, Assert.Single(store.Load().Bindings).VaultId);
            await model.RefreshCommand.ExecuteAsync(null);
            Assert.Equal(2, messages.Count);

            // 新代码库已有有效绑定时，即使保留旧绑定，重插也不再弹窗。
            await new RepositoryService(git, new VaultService()).BindAsync(target, repository, local);
            settings.Bindings.Add(new RepositoryBinding(target.Manifest.VaultId, repository.RepoId, local));
            store.Save(settings);
            await model.RefreshCommand.ExecuteAsync(null);
            Assert.Equal(local, model.SelectedItem?.LocalPath);
            Assert.False(model.NeedsBinding);
            Directory.Move(target.RootPath, disconnected);
            await model.DeviceChangedAsync();
            Directory.Move(disconnected, target.RootPath);
            await model.DeviceChangedAsync();
            Assert.Equal(2, messages.Count);
            Assert.True(model.IsOnline);
        }
        finally
        {
            timer.Stop();
            await model.StopLoggingAsync();
        }
    }
}

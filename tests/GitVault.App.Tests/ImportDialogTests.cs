using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GitVault.App;
using GitVault.Core;
using Xunit;

namespace GitVault.App.Tests;

/// <summary>在独立 WPF 线程中验证表单关闭与自动刷新之间的竞争。</summary>
public sealed class ImportDialogTests
{
    /// <summary>已有项目时关闭导入表单，自动刷新不能抢占用户确认的导入。</summary>
    [Fact]
    public async Task ConfirmImportSurvivesWindowReactivation()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Resources["PrimaryButton"] = new Style(typeof(Button));
            app.Resources["Muted"] = System.Windows.Media.Brushes.Gray;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            // 隔离的测试宿主放在屏幕外，为模态窗口提供有效 Owner。
            var owner = new Window { ShowInTaskbar = false, ShowActivated = false, Width = 1, Height = 1, Left = -10000, Top = -10000 };
            owner.Show();
            app.Dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await VerifyImportAsync(); completion.SetResult(); }
                catch (Exception error) { completion.SetException(error); }
                finally { app.Shutdown(); }
            }));
            app.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromMinutes(3));
    }

    /// <summary>所有仓库和设置都位于测试临时目录，不接触用户代码库。</summary>
    private static async Task VerifyImportAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "GitVaultDialogTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var local = Path.Combine(root, "本地项目");
            var git = new GitCommandService();
            await git.RunAsync(null, ["init", "-b", "main", local]);
            await git.RunAsync(local, ["-c", "user.name=Test", "-c", "user.email=test@example.invalid",
                "commit", "--allow-empty", "-m", "测试提交"]);
            var vaults = new VaultService();
            var vault = vaults.Create(Path.Combine(root, "代码库"), "测试代码库");
            var existing = await new RepositoryService(git, vaults).ImportAsync(vault, local, "已有项目");
            var settingsStore = new SettingsService(Path.Combine(root, "设置"));
            var settings = new AppSettings();
            settings.Bindings.Add(new RepositoryBinding(vault.Manifest.VaultId, existing.RepoId, local));
            var viewModel = new MainViewModel();
            SetField(viewModel, "settingsStore", settingsStore);
            SetField(viewModel, "settings", settings);
            SetField(viewModel, "settingsAvailable", true);
            viewModel.IsBusy = true;
            await (Task)typeof(MainViewModel).GetMethod("LoadVaultAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(viewModel, [vault, CancellationToken.None])!;
            viewModel.IsBusy = false;

            Task? reactivation = null;
            // ShowDialog 的嵌套消息循环执行此回调；Closed 模拟返回主窗口时的自动刷新。
            _ = Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                var dialog = Application.Current.Windows.OfType<Window>().Single(window => window.Title == "加入 U 盘代码库");
                var panel = (StackPanel)dialog.Content;
                var inputs = panel.Children.OfType<DockPanel>().SelectMany(row => row.Children.OfType<TextBox>()).ToArray();
                inputs[0].Text = local;
                inputs[1].Text = "新增项目";
                dialog.Closed += (_, _) => reactivation = viewModel.RecheckSelectionAsync();
                panel.Children.OfType<StackPanel>().Single().Children.OfType<Button>().Single(button => button.IsDefault)
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }));
            await viewModel.ImportCommand.ExecuteAsync(null);
            if (reactivation is not null) await reactivation;

            Assert.Equal(2, vaults.Open(vault.RootPath).Manifest.Repositories.Count);
            Assert.Equal(2, viewModel.Items.Count);
            Assert.Equal("新增项目", viewModel.SelectedItem?.Name);
            Assert.Equal(SyncKind.Synced, viewModel.SelectedItem?.Status?.Kind);
            Assert.Single(viewModel.Commits);
            Assert.Equal(2, settingsStore.Load().Bindings.Count);
            Assert.Equal("加入代码库完成", viewModel.Feedback);
            Assert.True(viewModel.CanUseVault);

            // 取消表单也必须释放保护，随后正常的窗口激活仍能刷新。
            _ = Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
                Application.Current.Windows.OfType<Window>().Single(window => window.Title == "加入 U 盘代码库").DialogResult = false));
            await viewModel.ImportCommand.ExecuteAsync(null);
            Assert.True(viewModel.CanUseVault);
            await viewModel.RecheckSelectionAsync();
            Assert.Equal("刷新当前仓库完成", viewModel.Feedback);
            Assert.Equal(2, viewModel.Items.Count);
        }
        finally
        {
            // Git 对象文件可能是只读的；仅清理本测试创建的唯一目录。
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(root, true);
        }
    }

    /// <summary>替换本机设置依赖并加载隔离状态，避免修改生产代码的构造入口。</summary>
    private static void SetField(MainViewModel viewModel, string name, object value) =>
        typeof(MainViewModel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(viewModel, value);
}

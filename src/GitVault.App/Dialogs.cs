using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using GitVault.Core;
using Microsoft.Win32;

namespace GitVault.App;

/// <summary>表单字段，可选目录或程序选择器、说明文字，或从另一个字段派生内容。</summary>
public sealed record InputField(string Label, string Value = "", string? Picker = null, string? Hint = null,
    int MirrorFrom = -1, bool MirrorFileName = false);

/// <summary>集中处理简单输入对话框，避免为一次性表单引入框架。</summary>
public static class Dialogs
{
    /// <summary>正在处理的模态窗口数量，包含关闭时尚未返回的窗口。</summary>
    private static int modalDepth;

    /// <summary>表单仍在处理时，阻止主窗口激活或设备通知抢先启动后台操作。</summary>
    public static bool IsOpen => modalDepth > 0;

    /// <summary>直到模态调用返回才解除保护，避免关闭窗口时的激活事件抢占提交。</summary>
    private static bool? ShowModal(Func<bool?> show)
    {
        modalDepth++;
        try { return show(); }
        finally { modalDepth--; }
    }

    /// <summary>取路径的文件夹名，用于把所选项目目录变成默认名称。</summary>
    public static string FolderNameOf(string path)
    {
        var trimmed = path.Trim().TrimEnd('\\', '/');
        return Path.GetFileName(trimmed);
    }

    /// <summary>显示模态表单，取消时不返回输入。</summary>
    public static string[]? Ask(string title, string description, params InputField[] fields)
    {
        var window = new Window
        {
            Title = title, Width = 610, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Application.Current.MainWindow
        };
        var panel = new StackPanel { Margin = new Thickness(26) };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 22, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 22) });
        var inputs = new List<TextBox>();
        foreach (var field in fields)
        {
            panel.Children.Add(new TextBlock { Text = field.Label, Margin = new Thickness(0, 0, 0, 6) });
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, field.Hint is null ? 16 : 4) };
            var input = new TextBox { Text = field.Value, MinWidth = 200 };
            System.Windows.Automation.AutomationProperties.SetName(input, field.Label);
            if (field.Picker is not null)
            {
                var browse = new Button { Content = "浏览…", Margin = new Thickness(8, 0, 0, 0) };
                DockPanel.SetDock(browse, Dock.Right);
                browse.Click += (_, _) =>
                {
                    if (field.Picker == "file")
                    {
                        var picker = new OpenFileDialog { Filter = "Git 程序 (git.exe)|git.exe|程序 (*.exe)|*.exe" };
                        if (picker.ShowDialog(window) == true) input.Text = picker.FileName;
                    }
                    else
                    {
                        var picker = new OpenFolderDialog { Title = field.Label };
                        if (picker.ShowDialog(window) == true) input.Text = picker.FolderName;
                    }
                };
                row.Children.Add(browse);
            }
            row.Children.Add(input);
            panel.Children.Add(row);
            if (field.Hint is not null)
                panel.Children.Add(new TextBlock
                {
                    Text = field.Hint, Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("Muted"),
                    FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14)
                });
            inputs.Add(input);
        }
        WireMirrors(fields, inputs);
        var error = new TextBlock { Foreground = System.Windows.Media.Brushes.Firebrick, TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(error);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var cancel = new Button { Content = "取消", IsCancel = true };
        var submit = new Button { Content = "确定", IsDefault = true, Style = (Style)Application.Current.FindResource("PrimaryButton") };
        submit.Click += (_, _) =>
        {
            if (inputs.Any(input => string.IsNullOrWhiteSpace(input.Text))) { error.Text = "请填写所有字段。"; return; }
            window.DialogResult = true;
        };
        actions.Children.Add(cancel);
        actions.Children.Add(submit);
        panel.Children.Add(actions);
        window.Content = panel;
        window.Loaded += (_, _) => inputs.FirstOrDefault()?.Focus();
        return ShowModal(window.ShowDialog) == true ? inputs.Select(input => input.Text.Trim()).ToArray() : null;
    }

    /// <summary>从已识别的其他代码库选择迁移目标，浏览补充的目录须为有效且不同身份的代码库。</summary>
    public static string? SelectMigrationTarget(string projectName, Guid sourceVaultId, IEnumerable<VaultChoice> choices)
    {
        var window = new Window
        {
            Title = "迁移到其他代码库", Width = 610, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Application.Current.MainWindow
        };
        var panel = new StackPanel { Margin = new Thickness(26) };
        panel.Children.Add(new TextBlock { Text = window.Title, FontSize = 22, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock
        {
            Text = $"将「{projectName}」的完整仓库迁移到目标代码库。验证成功后移除原代码库中的项目，本地工作目录保持原样。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 22)
        });
        panel.Children.Add(new TextBlock { Text = "目标代码库", Margin = new Thickness(0, 0, 0, 6) });
        // 代码库标识相同的副本也不是迁移目标；显示路径以区分同名代码库。
        var targets = new ObservableCollection<VaultChoice>(choices.Where(choice => choice.Location.Manifest.VaultId != sourceVaultId));
        var selector = new ComboBox
        {
            ItemsSource = targets, DisplayMemberPath = nameof(VaultChoice.Label), MinWidth = 200,
            Padding = new Thickness(10, 8, 10, 8), SelectedIndex = targets.Count == 1 ? 0 : -1
        };
        System.Windows.Automation.AutomationProperties.SetName(selector, "目标代码库");
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var browse = new Button { Content = "浏览…", Margin = new Thickness(8, 0, 0, 0) };
        DockPanel.SetDock(browse, Dock.Right);
        row.Children.Add(browse);
        row.Children.Add(selector);
        panel.Children.Add(row);
        panel.Children.Add(new TextBlock
        {
            Text = "选择其他代码库；列表中没有目标时可点击浏览。目标中不能已有同名项目。",
            Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("Muted"),
            FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14)
        });
        var error = new TextBlock { Foreground = System.Windows.Media.Brushes.Firebrick, TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(error);
        browse.Click += (_, _) =>
        {
            var picker = new OpenFolderDialog { Title = "选择目标代码库" };
            if (picker.ShowDialog(window) != true) return;
            try
            {
                var location = new VaultService().Open(picker.FolderName);
                if (location.Manifest.VaultId == sourceVaultId)
                {
                    error.Text = "请选择其他代码库，不能迁移到当前代码库或它的副本。";
                    return;
                }
                var choice = targets.FirstOrDefault(target => string.Equals(target.Location.RootPath, location.RootPath, StringComparison.OrdinalIgnoreCase));
                if (choice is null) { choice = new VaultChoice(location); targets.Add(choice); }
                selector.SelectedItem = choice;
                error.Text = "";
            }
            catch (Exception failure) { error.Text = failure.Message; }
        };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var cancel = new Button { Content = "取消", IsCancel = true };
        var submit = new Button { Content = "确定", IsDefault = true, IsEnabled = selector.SelectedItem is VaultChoice, Style = (Style)Application.Current.FindResource("PrimaryButton") };
        selector.SelectionChanged += (_, _) => submit.IsEnabled = selector.SelectedItem is VaultChoice;
        cancel.Click += (_, _) => window.DialogResult = false;
        submit.Click += (_, _) => window.DialogResult = true;
        actions.Children.Add(cancel);
        actions.Children.Add(submit);
        panel.Children.Add(actions);
        window.Content = panel;
        window.Loaded += (_, _) => selector.Focus();
        return ShowModal(window.ShowDialog) == true ? ((VaultChoice)selector.SelectedItem).Location.RootPath : null;
    }

    /// <summary>源字段变化时同步派生字段；用户手动改过派生字段后不再覆盖。</summary>
    private static void WireMirrors(InputField[] fields, List<TextBox> inputs)
    {
        for (var target = 0; target < fields.Length; target++)
        {
            var field = fields[target];
            if (field.MirrorFrom < 0 || field.MirrorFrom >= inputs.Count) continue;
            var source = inputs[field.MirrorFrom];
            var mirror = inputs[target];
            var edited = false;
            var syncing = false;
            mirror.TextChanged += (_, _) => { if (!syncing) edited = true; };
            source.TextChanged += (_, _) =>
            {
                if (edited || string.IsNullOrWhiteSpace(source.Text)) return;
                syncing = true;
                mirror.Text = field.MirrorFileName ? FolderNameOf(source.Text) : source.Text.Trim();
                syncing = false;
            };
        }
    }

    /// <summary>打开一个目录供用户选择。</summary>
    public static string? Folder(string title)
    {
        var dialog = new OpenFolderDialog { Title = title };
        return ShowModal(() => dialog.ShowDialog(Application.Current.MainWindow)) == true ? dialog.FolderName : null;
    }

    /// <summary>展示说明并等待用户确认，取消时返回 false。</summary>
    public static bool Confirm(string title, string message, string confirmText)
    {
        var window = new Window
        {
            Title = title, Width = 580, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Application.Current.MainWindow
        };
        var panel = new StackPanel { Margin = new Thickness(26) };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 22) });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "取消", IsCancel = true };
        var confirm = new Button { Content = confirmText, IsDefault = true, Style = (Style)Application.Current.FindResource("PrimaryButton") };
        confirm.Click += (_, _) => window.DialogResult = true;
        cancel.Click += (_, _) => window.DialogResult = false;
        actions.Children.Add(cancel);
        actions.Children.Add(confirm);
        panel.Children.Add(actions);
        window.Content = panel;
        return ShowModal(window.ShowDialog) == true;
    }

    /// <summary>提供可选择并复制的错误详情。</summary>
    public static void Error(string message)
    {
        var window = new Window { Title = "操作未完成", Width = 670, Height = 360, Owner = Application.Current.MainWindow, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new DockPanel { Margin = new Thickness(20) };
        var close = new Button { Content = "关闭", IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        close.Click += (_, _) => window.Close();
        DockPanel.SetDock(close, Dock.Bottom);
        panel.Children.Add(close);
        panel.Children.Add(new TextBox { Text = message, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        window.Content = panel;
        ShowModal(window.ShowDialog);
    }
}

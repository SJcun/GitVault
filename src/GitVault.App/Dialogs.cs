using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace GitVault.App;

/// <summary>表单字段，可选目录或程序选择器、说明文字，或从另一个字段派生内容。</summary>
public sealed record InputField(string Label, string Value = "", string? Picker = null, string? Hint = null,
    int MirrorFrom = -1, bool MirrorFileName = false);

/// <summary>集中处理简单输入对话框，避免为一次性表单引入框架。</summary>
public static class Dialogs
{
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
        return window.ShowDialog() == true ? inputs.Select(input => input.Text.Trim()).ToArray() : null;
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
        return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FolderName : null;
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
        return window.ShowDialog() == true;
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
        window.ShowDialog();
    }
}

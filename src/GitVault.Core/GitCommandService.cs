using System.Diagnostics;
using System.IO;
using System.Text;

namespace GitVault.Core;

/// <summary>以独立参数启动 Git，异步读取双输出流并支持终止进程树。</summary>
public sealed class GitCommandService
{
    /// <summary>使用中的 Git 可执行文件。</summary>
    public string GitPath { get; set; } = "git";
    /// <summary>命令与输出日志，由界面决定保存及显示方式。</summary>
    public Action<string>? Log { get; set; }

    /// <summary>执行命令；不经过 shell，也不将 stderr 内容直接判为失败。</summary>
    public async Task<GitResult> RunAsync(string? directory, IEnumerable<string> arguments,
        CancellationToken cancellationToken = default, bool check = true)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var args = arguments.ToArray();
        var start = new ProcessStartInfo(GitPath)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        if (directory is not null)
        {
            start.ArgumentList.Add("-C");
            start.ArgumentList.Add(directory);
        }
        // -C 是全局选项，必须排在任何子命令之前，否则会被当成子命令参数。
        var protectedConfig = ApplySafeDirectory(start, directory, args);
        try
        {
            foreach (var arg in args) start.ArgumentList.Add(arg);
            start.Environment["GIT_TERMINAL_PROMPT"] = "0";
            start.Environment["GCM_INTERACTIVE"] = "Never";
            start.Environment["GIT_EDITOR"] = "false";
            start.Environment["GIT_SEQUENCE_EDITOR"] = "false";
            var command = $"git {string.Join(' ', start.ArgumentList.Select(a => '\"' + a + '\"'))}";
            Log?.Invoke(command);
            using var process = new Process { StartInfo = start };
            process.Start();
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = ReadErrorsAsync(process.StandardError);
            try
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // 取消不能视为回滚：先终止 Git 及子进程，调用方随后重新检查仓库。
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                await process.WaitForExitAsync(CancellationToken.None);
                await Task.WhenAll(outputTask, errorTask);
                throw;
            }
            var result = new GitResult(process.ExitCode, await outputTask, await errorTask);
            if (check && result.ExitCode != 0) throw new GitException(command, result);
            return result;
        }
        finally
        {
            File.Delete(protectedConfig);
        }
    }

    /// <summary>为本次命令及本地传输子进程提供临时全局配置，结束后自动删除。</summary>
    private static string ApplySafeDirectory(ProcessStartInfo start, string? directory, string[] arguments)
    {
        // 本地传输会清除 GIT_CONFIG_COUNT / GIT_CONFIG_PARAMETERS，但保留 GIT_CONFIG_GLOBAL。
        // 只信任本次命令明确传入的绝对路径，不能用 * 关闭所有仓库的所有权检查。
        var directories = arguments.Where(Path.IsPathFullyQualified).ToList();
        if (directory is not null) directories.Add(Path.GetFullPath(directory));
        var content = new StringBuilder();
        start.Environment.TryGetValue("GIT_CONFIG_GLOBAL", out var originalGlobal);
        if (originalGlobal is not null)
        {
            AppendConfigValue(content, "include", "path", originalGlobal);
        }
        else
        {
            // 让 Git 自己展开 ~，保留原来的 XDG、用户配置顺序及条件 include 语义。
            start.Environment.TryGetValue("XDG_CONFIG_HOME", out var xdg);
            AppendConfigValue(content, "include", "path", string.IsNullOrEmpty(xdg)
                ? "~/.config/git/config" : Path.Combine(xdg, "git", "config"));
            AppendConfigValue(content, "include", "path", "~/.gitconfig");
        }
        foreach (var path in directories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            AppendConfigValue(content, "safe", "directory", Path.GetFullPath(path));
            AppendConfigValue(content, "safe", "directory", Path.Combine(Path.GetFullPath(path), ".git"));
        }
        var temporary = Path.Combine(Path.GetTempPath(), $"gitvault-{Guid.NewGuid():N}.gitconfig");
        // 每次调用独占文件，写完关闭句柄后再让 Git 读取，避免 Windows 文件共享冲突。
        File.WriteAllText(temporary, content.ToString(), new UTF8Encoding(false));
        start.Environment["GIT_CONFIG_GLOBAL"] = temporary;
        return temporary;
    }

    /// <summary>转义配置中的路径，防止空格、注释符和反斜杠改变配置含义。</summary>
    private static void AppendConfigValue(StringBuilder content, string section, string key, string value)
    {
        var escaped = value.Replace('\\', '/').Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\t", "\\t");
        content.Append('[').Append(section).Append("]\n\t").Append(key).Append(" = \"")
            .Append(escaped).Append("\"\n");
    }

    /// <summary>持续转发 Git 进度，保留完整错误信息用于诊断。</summary>
    private async Task<string> ReadErrorsAsync(StreamReader reader)
    {
        var content = new StringBuilder();
        while (await reader.ReadLineAsync() is { } line)
        {
            content.AppendLine(line);
            Log?.Invoke(line);
        }
        return content.ToString();
    }
}

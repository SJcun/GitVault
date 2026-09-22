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
        ApplySafeDirectory(start, directory);
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

    /// <summary>抑制 Git 的 dubious ownership 检查；不改动用户全局配置。</summary>
    private static void ApplySafeDirectory(ProcessStartInfo start, string? directory)
    {
        // safe.directory 只影响 Git 的所有者校验，不改变传输、引用或工作区数据。
        // 必须用环境变量而不是命令行 -c：git 会把 -c 转成 GIT_CONFIG_COUNT 传给子进程，
        // 但传之前会过滤 safe.directory，导致 push/fetch 派生的 receive-pack、upload-pack
        // 仍按可疑所有权拒绝 FAT32 上的裸仓库。环境变量由子进程原样继承。
        var values = new List<string> { "*" };
        if (!string.IsNullOrWhiteSpace(directory))
        {
            // 目录形式与仓库形式各写一份，覆盖 Git 两种匹配路径。
            var full = Path.GetFullPath(directory);
            values.Add(full);
            values.Add(Path.Combine(full, ".git"));
        }
        start.Environment["GIT_CONFIG_COUNT"] = values.Count.ToString();
        for (var i = 0; i < values.Count; i++)
        {
            start.Environment[$"GIT_CONFIG_KEY_{i}"] = "safe.directory";
            start.Environment[$"GIT_CONFIG_VALUE_{i}"] = values[i];
        }
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

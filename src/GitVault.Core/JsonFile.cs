using System.Text.Json;

namespace GitVault.Core;

/// <summary>同目录写入并刷新临时文件，保留上一有效版本；恢复必须由调用方明确发起。</summary>
internal static class JsonFile
{
    /// <summary>读取完整 JSON 并执行与保存、备份恢复一致的语义校验。</summary>
    internal static T Read<T>(string path, Action<T> validate)
    {
        var value = JsonSerializer.Deserialize<T>(File.ReadAllBytes(path), SettingsService.JsonOptions)
            ?? throw new InvalidDataException("JSON 文件内容为空：" + path);
        validate(value);
        return value;
    }

    /// <summary>验证新旧内容后先更新有效备份，再替换主文件；测试可注入临时写入故障。</summary>
    internal static void Write<T>(string path, T value, Action<T> validate, Action<string, byte[]>? write = null)
    {
        validate(value);
        byte[]? previous = null;
        if (File.Exists(path))
        {
            previous = File.ReadAllBytes(path);
            var previousValue = JsonSerializer.Deserialize<T>(previous, SettingsService.JsonOptions)
                ?? throw new InvalidDataException("现有 JSON 内容为空：" + path);
            validate(previousValue);
        }
        else if (File.Exists(path + ".bak"))
            throw new InvalidDataException("主文件缺失且存在备份，请先明确选择恢复：" + path);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, SettingsService.JsonOptions);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var backupTemporary = path + ".bak." + Guid.NewGuid().ToString("N") + ".tmp";
        Exception? failure = null;
        try
        {
            (write ?? WriteDurable)(temporary, bytes);
            if (previous is not null)
            {
                (write ?? WriteDurable)(backupTemporary, previous);
                File.Move(backupTemporary, path + ".bak", overwrite: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception error) { failure = error; throw; }
        finally
        {
            Cleanup(temporary, failure);
            Cleanup(backupTemporary, failure);
        }
    }

    /// <summary>明确恢复有效备份；保留原主文件副本，不把损坏内容覆盖到有效备份。</summary>
    internal static T Restore<T>(string path, Action<T> validate)
    {
        var bytes = File.ReadAllBytes(path + ".bak");
        var value = JsonSerializer.Deserialize<T>(bytes, SettingsService.JsonOptions)
            ?? throw new InvalidDataException("备份内容为空。");
        validate(value);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        Exception? failure = null;
        try
        {
            WriteDurable(temporary, bytes);
            if (File.Exists(path)) File.Copy(path, path + ".before-restore." + Guid.NewGuid().ToString("N"));
            File.Move(temporary, path, overwrite: true);
            return value;
        }
        catch (Exception error) { failure = error; throw; }
        finally { Cleanup(temporary, failure); }
    }

    /// <summary>关闭文件前请求刷新到磁盘；此调用不构成介质断电安全保证。</summary>
    private static void WriteDurable(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    /// <summary>只清理本次准确临时路径；次要清理错误附在原异常上，保留主要诊断。</summary>
    private static void Cleanup(string path, Exception? failure)
    {
        try { File.Delete(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            if (failure is null) throw;
            failure.Data["临时文件清理失败：" + path] = error.Message;
        }
    }
}

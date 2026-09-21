using System.Text.Json;

namespace GitVault.Core;

/// <summary>以临时文件替换方式保存 JSON，不覆盖无法解析的现有设置。</summary>
public sealed class SettingsService(string? directory = null)
{
    /// <summary>当前电脑的数据目录；测试可使用临时目录。</summary>
    public string DirectoryPath { get; } = directory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GitVault");

    /// <summary>读取设置；格式错误交给界面报告，禁止默默重置。</summary>
    public AppSettings Load()
    {
        var path = Path.Combine(DirectoryPath, "settings.json");
        if (!File.Exists(path)) return new AppSettings();
        var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException("本机设置为空，请检查 settings.json。");
        if (string.IsNullOrWhiteSpace(settings.GitPath) || settings.Bindings is null || settings.KnownVaultPaths is null)
            throw new InvalidDataException("本机设置字段不完整，请检查 settings.json。");
        return settings;
    }

    /// <summary>保存本机设置。</summary>
    public void Save(AppSettings settings) => WriteJson(Path.Combine(DirectoryPath, "settings.json"), settings);

    /// <summary>共同使用的可读 JSON 格式。</summary>
    internal static JsonSerializerOptions JsonOptions { get; } = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>先完整写入临时文件，再替换目标；异常时保留原文件。</summary>
    internal static void WriteJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, JsonOptions));
        File.Move(temporary, path, overwrite: true);
    }
}

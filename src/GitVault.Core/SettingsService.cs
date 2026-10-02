using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

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
        if (!File.Exists(path))
        {
            if (File.Exists(path + ".bak")) throw new InvalidDataException("本机设置缺失，检测到备份；请明确选择恢复。");
            return new AppSettings();
        }
        return JsonFile.Read<AppSettings>(path, Validate);
    }

    /// <summary>保存完整有效设置，并保留上一有效版本。</summary>
    public void Save(AppSettings settings) => JsonFile.Write(Path.Combine(DirectoryPath, "settings.json"), settings, Validate);

    /// <summary>只检查备份，不修改主文件，供用户确认绑定和 Git 路径。</summary>
    public AppSettings ReadBackup() => JsonFile.Read<AppSettings>(Path.Combine(DirectoryPath, "settings.json.bak"), Validate);

    /// <summary>用户明确同意后恢复；损坏原文件另存，备份原样保留。</summary>
    public AppSettings RestoreBackup() => JsonFile.Restore<AppSettings>(Path.Combine(DirectoryPath, "settings.json"), Validate);

    /// <summary>路径不要求在线，但格式、绑定身份和同一项目的唯一性必须有效。</summary>
    internal static void Validate(AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.GitPath) || settings.Bindings is null || settings.KnownVaultPaths is null)
            throw new InvalidDataException("本机设置字段不完整，请检查 settings.json。");
        if (settings.KnownVaultPaths.Any(path => string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)))
            throw new InvalidDataException("本机设置的代码库路径必须是完整路径。");
        var ids = new HashSet<(Guid Vault, Guid Repository)>();
        foreach (var binding in settings.Bindings)
        {
            if (binding is null || binding.VaultId == Guid.Empty || binding.RepoId == Guid.Empty
                || string.IsNullOrWhiteSpace(binding.LocalPath) || !Path.IsPathFullyQualified(binding.LocalPath)
                || !ids.Add((binding.VaultId, binding.RepoId)))
                throw new InvalidDataException("本机设置的仓库绑定无效或重复。");
        }
    }
    /// <summary>共同使用的可读 JSON 格式，直接保留中文等 Unicode 文字，必要的 JSON 字符仍会转义。</summary>
    internal static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

}

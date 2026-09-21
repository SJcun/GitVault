using System.Text.Json;

namespace GitVault.Core;

/// <summary>创建、识别并校验移动磁盘上的 Vault 清单。</summary>
public sealed class VaultService
{
    /// <summary>只在空目录中创建 Vault，不占用已有项目目录。</summary>
    public VaultLocation Create(string root, string name)
    {
        root = Path.GetFullPath(root);
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("请填写 Vault 名称。");
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
            throw new InvalidOperationException("请选择不存在或空的目录创建 Vault。");
        Directory.CreateDirectory(Path.Combine(root, "repos"));
        var manifest = new VaultManifest { Name = name.Trim() };
        SettingsService.WriteJson(Path.Combine(root, "vault.json"), manifest);
        return Open(root);
    }

    /// <summary>加载清单并验证身份、唯一性及路径边界。</summary>
    public VaultLocation Open(string root)
    {
        root = Path.GetFullPath(root);
        var manifest = JsonSerializer.Deserialize<VaultManifest>(File.ReadAllText(Path.Combine(root, "vault.json")), SettingsService.JsonOptions)
            ?? throw new InvalidDataException("Vault 清单为空。");
        if (manifest.SchemaVersion != 1 || manifest.VaultId == Guid.Empty || string.IsNullOrWhiteSpace(manifest.Name) || manifest.Repositories is null)
            throw new InvalidDataException("Vault 清单格式无效或版本暂不支持。");
        var location = new VaultLocation(root, manifest);
        var ids = new HashSet<Guid>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var repository in manifest.Repositories)
        {
            if (repository is null || repository.RepoId == Guid.Empty || string.IsNullOrWhiteSpace(repository.Name) || !ids.Add(repository.RepoId)
                || !paths.Add(RepositoryPath(location, repository)))
                throw new InvalidDataException("Vault 仓库登记重复或字段无效。");
        }
        return location;
    }

    /// <summary>每次传输重新检查磁盘身份与仓库登记，防止盘符被其他设备复用。</summary>
    public string VerifyRepository(VaultLocation vault, VaultRepository repository)
    {
        var current = Open(vault.RootPath);
        if (current.Manifest.VaultId != vault.Manifest.VaultId)
            throw new IOException("此位置已不是原来的 Vault，请重新选择 U 盘。");
        var entry = current.Manifest.Repositories.SingleOrDefault(r => r.RepoId == repository.RepoId);
        if (entry is null || entry.RelativePath != repository.RelativePath)
            throw new IOException("仓库登记已变化，请刷新 Vault。");
        var path = RepositoryPath(current, entry);
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException("U 盘仓库缺失：" + path);
        return path;
    }

    /// <summary>约束裸仓库必须位于 repos 内，且不能经目录链接跳到其他位置。</summary>
    public string RepositoryPath(VaultLocation vault, VaultRepository repository)
    {
        if (string.IsNullOrWhiteSpace(repository.RelativePath) || Path.IsPathRooted(repository.RelativePath))
            throw new InvalidDataException("仓库路径必须是 repos 内的相对路径。");
        var parent = Path.GetFullPath(Path.Combine(vault.RootPath, "repos"));
        var path = Path.GetFullPath(Path.Combine(vault.RootPath, repository.RelativePath));
        if (!path.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("仓库路径超出了 repos 目录。");
        for (var current = new DirectoryInfo(path); current is not null; current = current.Parent)
        {
            if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException("Vault 仓库路径不能包含目录链接。");
            if (string.Equals(current.FullName, Path.GetFullPath(vault.RootPath), StringComparison.OrdinalIgnoreCase)) break;
        }
        return path;
    }

    /// <summary>登记已完成的裸仓库；合并最新清单，不覆盖其他已登记项目。</summary>
    public VaultLocation Register(VaultLocation vault, VaultRepository repository)
    {
        var current = Open(vault.RootPath);
        if (current.Manifest.VaultId != vault.Manifest.VaultId) throw new IOException("Vault 身份发生变化。");
        var path = RepositoryPath(current, repository);
        if (current.Manifest.Repositories.Any(r => r.RepoId == repository.RepoId ||
            string.Equals(RepositoryPath(current, r), path, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("此仓库已登记，请刷新。");
        current.Manifest.Repositories.Add(repository);
        SettingsService.WriteJson(Path.Combine(current.RootPath, "vault.json"), current.Manifest);
        return current;
    }

    /// <summary>检查已知路径以及移动磁盘默认位置，不递归扫描磁盘。</summary>
    public IReadOnlyList<VaultLocation> Discover(IEnumerable<string> knownPaths)
    {
        var paths = new HashSet<string>(knownPaths, StringComparer.OrdinalIgnoreCase);
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Removable || !drive.IsReady) continue;
            paths.Add(Path.Combine(drive.RootDirectory.FullName, "GitVault"));
            // 已知自定义位置按相对盘根路径检查，支持同一 U 盘换盘符。
            foreach (var known in knownPaths)
            {
                var root = Path.GetPathRoot(known);
                if (!string.IsNullOrEmpty(root)) paths.Add(Path.Combine(drive.RootDirectory.FullName, Path.GetRelativePath(root, known)));
            }
        }
        var result = new List<VaultLocation>();
        foreach (var path in paths)
        {
            try { if (File.Exists(Path.Combine(path, "vault.json"))) result.Add(Open(path)); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException) { }
        }
        return result;
    }
}

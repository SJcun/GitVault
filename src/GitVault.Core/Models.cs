using System.Text.Json.Serialization;

namespace GitVault.Core;

/// <summary>随 U 盘携带的仓库清单，不保存本机路径。</summary>
public sealed class VaultManifest
{
    /// <summary>清单格式版本，未知版本禁止写回。</summary>
    [JsonRequired] public int SchemaVersion { get; set; } = 1;
    /// <summary>跨盘符保持不变的 Vault 标识。</summary>
    [JsonRequired] public Guid VaultId { get; set; } = Guid.NewGuid();
    /// <summary>显示名称。</summary>
    [JsonRequired] public string Name { get; set; } = "我的代码库";
    /// <summary>已经登记的裸仓库。</summary>
    [JsonRequired] public List<VaultRepository> Repositories { get; set; } = [];
}

/// <summary>Vault 内的一项裸仓库登记。</summary>
public sealed class VaultRepository
{
    /// <summary>仓库身份，不依赖目录名。</summary>
    [JsonRequired] public Guid RepoId { get; set; } = Guid.NewGuid();
    /// <summary>列表显示名称。</summary>
    [JsonRequired] public string Name { get; set; } = "";
    /// <summary>相对于 Vault 根目录的裸仓库路径。</summary>
    [JsonRequired] public string RelativePath { get; set; } = "";
}

/// <summary>已验证的 Vault 位置与清单快照。</summary>
public sealed record VaultLocation(string RootPath, VaultManifest Manifest);

/// <summary>仅保存在当前电脑上的设置。</summary>
public sealed class AppSettings
{
    /// <summary>Git 可执行文件路径，默认从 PATH 查找。</summary>
    public string GitPath { get; set; } = "git";
    /// <summary>曾经手动打开的 Vault 位置。</summary>
    public List<string> KnownVaultPaths { get; set; } = [];
    /// <summary>当前电脑的工作目录绑定。</summary>
    public List<RepositoryBinding> Bindings { get; set; } = [];
}

/// <summary>以双重标识绑定本机工作目录。</summary>
public sealed record RepositoryBinding(Guid VaultId, Guid RepoId, string LocalPath);

/// <summary>当前分支与 U 盘同名分支的关系。</summary>
public enum SyncKind { Synced, Ahead, Behind, Diverged, MissingBranch, Unrelated, Unsupported }

/// <summary>用户选择目录后判断出的类型，用于给出下一步引导而不是报错。</summary>
public enum DirectoryKind
{
    /// <summary>GitVault 创建的 U 盘代码库。</summary>
    CodeLibrary,
    /// <summary>普通本地 Git 项目，可加入代码库。</summary>
    LocalProject,
    /// <summary>已存在但既非代码库也非 Git 项目。</summary>
    Plain,
    /// <summary>目录不存在，可直接创建。</summary>
    Missing
}

/// <summary>一次成功检查得到的状态；失败不会构造为已同步。</summary>
public sealed record RepositoryStatus(
    string Branch, string Head, string? RemoteHead, int Ahead, int Behind,
    bool IsDirty, bool HasOperation, SyncKind Kind, string Message, DateTimeOffset CheckedAt);

/// <summary>供界面展示的一条提交。</summary>
public sealed record CommitInfo(string Hash, string Subject, string Author, string Time);

/// <summary>Git 进程退出后的完整结果。</summary>
public sealed record GitResult(int ExitCode, string Output, string Error);

/// <summary>Git 失败时保留命令、退出码与诊断输出。</summary>
public sealed class GitException(string command, GitResult result)
    : Exception($"Git 操作失败（退出码 {result.ExitCode}）\n{command}\n{result.Error}\n{result.Output}")
{
    /// <summary>原始 Git 执行结果。</summary>
    public GitResult Result { get; } = result;
}

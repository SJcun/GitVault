using CommunityToolkit.Mvvm.ComponentModel;
using GitVault.Core;

namespace GitVault.App;

/// <summary>主列表中仓库的状态投影。</summary>
public partial class RepositoryItem(VaultRepository repository, string? localPath) : ObservableObject
{
    /// <summary>原始仓库登记。</summary>
    public VaultRepository Repository { get; } = repository;
    /// <summary>仓库名称。</summary>
    public string Name => Repository.Name;
    /// <summary>本机绑定路径，空表示未绑定。</summary>
    [ObservableProperty] private string? localPath = localPath;
    /// <summary>最近一次成功取得的状态。</summary>
    [ObservableProperty] private RepositoryStatus? status;
    /// <summary>状态摘要，错误时不沿用旧的同步结论。</summary>
    [ObservableProperty] private string summary = localPath is null ? "本机未绑定" : "等待刷新";

    /// <summary>更新状态和列表中的文本。</summary>
    public void Apply(RepositoryStatus value)
    {
        Status = value;
        Summary = $"{value.Branch} · {value.Message}";
    }

    /// <summary>使缓存失效，防止失败后继续显示已同步。</summary>
    public void Invalidate(string reason)
    {
        Status = null;
        Summary = reason;
    }
}

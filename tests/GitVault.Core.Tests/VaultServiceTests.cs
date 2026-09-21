using GitVault.Core;
using Xunit;

namespace GitVault.Core.Tests;

/// <summary>目录类型判断必须不要求用户认识内部配置文件，普通 Git 项目不能误报为错误。</summary>
public sealed class VaultServiceTests : IDisposable
{
    /// <summary>本测试独占的目录。
    /// </summary>
    private readonly string root = Path.Combine(Path.GetTempPath(), "GitVaultTests", Guid.NewGuid().ToString("N"));
    /// <summary>真实核心服务。
    /// </summary>
    private readonly VaultService vaults = new();

    /// <summary>创建隔离环境。
    /// </summary>
    public VaultServiceTests() => Directory.CreateDirectory(root);

    /// <summary>代码库、本地 Git 项目、普通目录与不存在目录各有明确类型。</summary>
    [Fact]
    public void ProbeClassifiesSelectedDirectory()
    {
        var library = vaults.Create(Path.Combine(root, "U 盘代码库"), "测试");
        Assert.Equal(DirectoryKind.CodeLibrary, vaults.Probe(library.RootPath));

        var project = Path.Combine(root, "本地项目");
        Directory.CreateDirectory(Path.Combine(project, ".git"));
        Assert.Equal(DirectoryKind.LocalProject, vaults.Probe(project));

        // .git 为文件的链接工作区同样是本地 Git 项目。
        var linked = Path.Combine(root, "链接工作区");
        Directory.CreateDirectory(linked);
        File.WriteAllText(Path.Combine(linked, ".git"), "gitdir: ../其他位置/.git/worktrees/linked");
        Assert.Equal(DirectoryKind.LocalProject, vaults.Probe(linked));

        var plain = Path.Combine(root, "普通目录");
        Directory.CreateDirectory(plain);
        File.WriteAllText(Path.Combine(plain, "说明.txt"), "普通文件");
        Assert.Equal(DirectoryKind.Plain, vaults.Probe(plain));

        var empty = Path.Combine(root, "空目录");
        Directory.CreateDirectory(empty);
        Assert.Equal(DirectoryKind.Plain, vaults.Probe(empty));

        Assert.Equal(DirectoryKind.Missing, vaults.Probe(Path.Combine(root, "还不存在")));
    }

    /// <summary>判断结果稳定，重复探测不改变目录内容。</summary>
    [Fact]
    public void ProbeDoesNotModifyDirectory()
    {
        var project = Path.Combine(root, "项目");
        Directory.CreateDirectory(Path.Combine(project, ".git"));
        Assert.Equal(DirectoryKind.LocalProject, vaults.Probe(project));
        Assert.Equal(DirectoryKind.LocalProject, vaults.Probe(project));
        Assert.True(Directory.Exists(Path.Combine(project, ".git")));
        Assert.False(File.Exists(Path.Combine(project, "vault.json")));
    }

    /// <summary>仅清理本测试拥有的目录。</summary>
    public void Dispose()
    {
        var owned = Path.GetFullPath(root);
        var boundary = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "GitVaultTests")) + Path.DirectorySeparatorChar;
        if (!owned.StartsWith(boundary, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("测试清理目录越界。");
        if (!Directory.Exists(owned)) return;
        foreach (var file in Directory.EnumerateFiles(owned, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(owned, recursive: true);
    }
}

namespace GitVault.Core;

/// <summary>封装离线仓库传输流程；调用方串行执行，避免同一工作目录相互竞争。</summary>
public sealed class RepositoryService(GitCommandService git, VaultService vaults)
{
    /// <summary>将本地分支和标签复制成裸仓库；失败目录保留以便检查。</summary>
    public async Task<VaultRepository> ImportAsync(VaultLocation vault, string localPath, string name, CancellationToken token = default)
    {
        var local = await InspectLocalAsync(localPath, token);
        RequireSupported(local);
        ValidateName(name);
        var current = vaults.Open(vault.RootPath);
        if (current.Manifest.VaultId != vault.Manifest.VaultId) throw new IOException("Vault 身份发生变化。");
        var repository = new VaultRepository { Name = name, RelativePath = $"repos/{name}.git" };
        var destination = vaults.RepositoryPath(current, repository);
        if (Path.Exists(destination)) throw new IOException("目标仓库目录已经存在，请更换名称或恢复登记。");
        var temporary = Path.Combine(Path.GetDirectoryName(destination)!, $".import-{Guid.NewGuid():N}");
        await git.RunAsync(null, ["clone", "--bare", "--no-hardlinks", "--progress", "--", Path.GetFullPath(localPath), temporary], token);
        await RequireBareAsync(temporary, token);
        await git.RunAsync(temporary, ["fsck", "--connectivity-only"], token);
        // clone --bare 已包含本地分支；不重复 push，也不修改源仓库的 remote。
        token.ThrowIfCancellationRequested();
        if (vaults.Open(vault.RootPath).Manifest.VaultId != vault.Manifest.VaultId) throw new IOException("Vault 身份发生变化。");
        Directory.Move(temporary, destination);
        vaults.Register(current, repository);
        return repository;
    }

    /// <summary>克隆到全新目录，只移除本次克隆生成的 origin。</summary>
    public async Task CloneAsync(VaultLocation vault, VaultRepository repository, string destination, CancellationToken token = default)
    {
        var source = vaults.VerifyRepository(vault, repository);
        await RequireBareAsync(source, token);
        await RequireTransferContentsAsync(source, token);
        destination = Path.GetFullPath(destination);
        if (Path.Exists(destination)) throw new IOException("克隆目标必须是不存在的新目录；已有项目请使用绑定。");
        var parent = Path.GetDirectoryName(destination) ?? throw new IOException("不能克隆到磁盘根目录。");
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException("请先选择存在的父目录。");
        var temporary = Path.Combine(parent, $".gitvault-clone-{Guid.NewGuid():N}");
        await git.RunAsync(null, ["clone", "--no-hardlinks", "--progress", "--", source, temporary], token);
        await git.RunAsync(temporary, ["remote", "remove", "origin"], token);
        var local = await InspectLocalAsync(temporary, token);
        RequireSupported(local);
        await FetchAsync(vault, repository, temporary, token);
        token.ThrowIfCancellationRequested();
        Directory.Move(temporary, destination);
    }

    /// <summary>只有当前同名分支具有共同历史时才绑定已有项目。</summary>
    public async Task BindAsync(VaultLocation vault, VaultRepository repository, string localPath, CancellationToken token = default)
    {
        var state = await RefreshAsync(vault, repository, localPath, token);
        if (state.Kind == SyncKind.Unsupported) throw new InvalidOperationException(state.Message);
        if (state.Kind is SyncKind.Unrelated or SyncKind.MissingBranch)
            throw new InvalidOperationException("请在本地切换到与 U 盘同名且具有共同历史的分支后再绑定。");
    }

    /// <summary>抓取到专属引用空间，计算当前分支关系；失败交给调用方显示为未知。</summary>
    public async Task<RepositoryStatus> RefreshAsync(VaultLocation vault, VaultRepository repository, string localPath, CancellationToken token = default)
    {
        var local = await InspectLocalAsync(localPath, token);
        if (local.Problem is not null) return Status(local, null, 0, 0, SyncKind.Unsupported, local.Problem);
        await FetchAsync(vault, repository, localPath, token);
        // fetch 期间用户可能在 IDE 提交或切分支；以后一次快照为准。
        local = await InspectLocalAsync(localPath, token);
        if (local.Problem is not null) return Status(local, null, 0, 0, SyncKind.Unsupported, local.Problem);
        var remote = await git.RunAsync(localPath, ["rev-parse", "--verify", "--quiet", ReferencePrefix(vault, repository) + local.Branch], token, check: false);
        if (remote.ExitCode == 1) return Status(local, null, 0, 0, SyncKind.MissingBranch, "U 盘没有同名分支");
        if (remote.ExitCode != 0) throw new GitException("读取 U 盘缓存分支", remote);
        var remoteHead = remote.Output.Trim();
        var common = await git.RunAsync(localPath, ["merge-base", local.Head, remoteHead], token, check: false);
        if (common.ExitCode == 1) return Status(local, remoteHead, 0, 0, SyncKind.Unrelated, "两个分支没有共同历史");
        if (common.ExitCode != 0) throw new GitException("检查共同历史", common);
        var counts = (await git.RunAsync(localPath, ["rev-list", "--left-right", "--count", $"{local.Head}...{remoteHead}"], token)).Output
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var ahead = int.Parse(counts[0]);
        var behind = int.Parse(counts[1]);
        var kind = (ahead, behind) switch
        {
            (0, 0) => SyncKind.Synced, (> 0, 0) => SyncKind.Ahead,
            (0, > 0) => SyncKind.Behind, _ => SyncKind.Diverged
        };
        var message = kind switch
        {
            SyncKind.Synced => "当前分支已同步", SyncKind.Ahead => $"本地领先 {ahead} 次提交",
            SyncKind.Behind => $"U 盘领先 {behind} 次提交", _ => $"已分叉：本地 {ahead} / U 盘 {behind}"
        };
        return Status(local, remoteHead, ahead, behind, kind, message);
    }

    /// <summary>普通推送仅发送当前分支；分叉与落后状态直接拒绝。</summary>
    public async Task<RepositoryStatus> PushAsync(VaultLocation vault, VaultRepository repository, string localPath, CancellationToken token = default)
    {
        var state = await RefreshAsync(vault, repository, localPath, token);
        if (state.Kind is not (SyncKind.Ahead or SyncKind.MissingBranch or SyncKind.Synced))
            throw new InvalidOperationException("当前状态不能推送：" + state.Message);
        await EnsureUnchangedAsync(localPath, state, false, token);
        var destination = vaults.VerifyRepository(vault, repository);
        // 禁用用户配置中的自动推标签，确保按钮只操作当前分支。
        await git.RunAsync(localPath, ["-c", "push.followTags=false", "push", "--progress", "--recurse-submodules=no", destination,
            $"refs/heads/{state.Branch}:refs/heads/{state.Branch}"], token);
        return await RefreshAsync(vault, repository, localPath, token);
    }

    /// <summary>只合入已验证的提交 OID，拒绝脏工作区、分叉和未完成的 Git 操作。</summary>
    public async Task<RepositoryStatus> PullAsync(VaultLocation vault, VaultRepository repository, string localPath, CancellationToken token = default)
    {
        var state = await RefreshAsync(vault, repository, localPath, token);
        if (state.Kind is not (SyncKind.Behind or SyncKind.Synced))
            throw new InvalidOperationException("当前状态不能快进拉取：" + state.Message);
        await EnsureUnchangedAsync(localPath, state, true, token);
        vaults.VerifyRepository(vault, repository);
        await git.RunAsync(localPath, ["-c", "merge.autoStash=false", "-c", "core.logAllRefUpdates=true", "merge", "--ff-only", "--no-autostash", state.RemoteHead!], token);
        return await RefreshAsync(vault, repository, localPath, token);
    }

    /// <summary>读取最近提交，以字段分隔符避免依赖本地化输出。</summary>
    public async Task<IReadOnlyList<CommitInfo>> RecentAsync(string localPath, CancellationToken token = default)
    {
        var result = await git.RunAsync(localPath, ["log", "-20", "--date=iso-strict", "--format=%h%x1f%s%x1f%an%x1f%ad"], token, check: false);
        if (result.ExitCode != 0) return [];
        return result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r').Split('\x1f')).Where(parts => parts.Length == 4)
            .Select(parts => new CommitInfo(parts[0], parts[1], parts[2], parts[3])).ToList();
    }

    /// <summary>恢复已创建但清单登记失败的完整裸仓库；忽略导入临时目录。</summary>
    public async Task<int> RecoverAsync(VaultLocation vault, CancellationToken token = default)
    {
        var current = vaults.Open(vault.RootPath);
        if (current.Manifest.VaultId != vault.Manifest.VaultId) throw new IOException("Vault 身份发生变化。");
        var count = 0;
        foreach (var path in Directory.EnumerateDirectories(Path.Combine(current.RootPath, "repos"), "*.git"))
        {
            var relative = Path.GetRelativePath(current.RootPath, path);
            if (current.Manifest.Repositories.Any(r => string.Equals(vaults.RepositoryPath(current, r), path, StringComparison.OrdinalIgnoreCase))) continue;
            var entry = new VaultRepository { Name = Path.GetFileNameWithoutExtension(path), RelativePath = relative };
            vaults.RepositoryPath(current, entry);
            await RequireBareAsync(path, token);
            await git.RunAsync(path, ["fsck", "--connectivity-only"], token);
            current = vaults.Register(current, entry);
            count++;
        }
        return count;
    }

    /// <summary>使用仅属于此绑定的引用空间，避免污染已有 remote 和 FETCH_HEAD。</summary>
    private async Task FetchAsync(VaultLocation vault, VaultRepository repository, string localPath, CancellationToken token)
    {
        var source = vaults.VerifyRepository(vault, repository);
        await RequireBareAsync(source, token);
        await git.RunAsync(localPath, ["fetch", "--progress", "--no-tags", "--prune", "--no-recurse-submodules", "--no-write-fetch-head",
            "--refmap=", source, $"+refs/heads/*:{ReferencePrefix(vault, repository)}*"], token);
    }

    /// <summary>形成不同 Vault/仓库相互隔离的缓存引用前缀。</summary>
    private static string ReferencePrefix(VaultLocation vault, VaultRepository repository) =>
        $"refs/gitvault/{vault.Manifest.VaultId:N}/{repository.RepoId:N}/heads/";

    /// <summary>获取工作区快照，优先使用 Git 自身定位路径，兼容 .git 文件。</summary>
    private async Task<LocalSnapshot> InspectLocalAsync(string path, CancellationToken token)
    {
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException("本地工作目录不存在：" + path);
        var root = (await git.RunAsync(path, ["rev-parse", "--show-toplevel"], token)).Output.Trim();
        if (!string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("请选择 Git 工作区根目录：" + root);
        var branchResult = await git.RunAsync(path, ["symbolic-ref", "--quiet", "--short", "HEAD"], token, check: false);
        var branch = branchResult.Output.Trim();
        var headResult = await git.RunAsync(path, ["rev-parse", "--verify", "--quiet", "HEAD"], token, check: false);
        var head = headResult.Output.Trim();
        var dirty = (await git.RunAsync(path, ["status", "--porcelain=v1", "-z", "--untracked-files=normal"], token)).Output.Length > 0;
        var operation = false;
        foreach (var marker in new[] { "MERGE_HEAD", "CHERRY_PICK_HEAD", "REVERT_HEAD", "rebase-merge", "rebase-apply", "sequencer" })
        {
            var markerPath = (await git.RunAsync(path, ["rev-parse", "--git-path", marker], token)).Output.Trim();
            operation |= Path.Exists(Path.GetFullPath(markerPath, Path.GetFullPath(path)));
        }
        string? problem = null;
        if (headResult.ExitCode != 0) problem = "尚未创建首次提交，请先在 Git 工具中提交。";
        else if (branchResult.ExitCode != 0) problem = "HEAD 未附着到分支，请先切换到本地分支。";
        else if (operation) problem = "存在未完成的 merge/rebase 等操作，请先在 Git 工具中处理。";
        else if ((await git.RunAsync(path, ["rev-parse", "--is-shallow-repository"], token)).Output.Trim() == "true")
            problem = "首版不支持浅克隆，请先补全历史。";
        else problem = await TransferProblemAsync(path, token);
        return new LocalSnapshot(branch, head, dirty, operation, problem);
    }

    /// <summary>拒绝只传输指针却遗漏实际内容的 LFS 与子模块项目。</summary>
    private async Task<string?> TransferProblemAsync(string path, CancellationToken token)
    {
        var modules = await git.RunAsync(path, ["ls-tree", "-r", "HEAD"], token);
        if (modules.Output.Split('\n').Any(line => line.StartsWith("160000 ", StringComparison.Ordinal)))
            return "首版不支持包含子模块的项目；子模块内容不会随裸仓库传输。";
        var lfs = await git.RunAsync(path, ["grep", "-I", "-l", "-e", "filter[[:space:]]*=[[:space:]]*lfs", "HEAD", "--", ".gitattributes", ":(glob)**/.gitattributes"], token, check: false);
        if (lfs.ExitCode == 0) return "首版不支持 Git LFS 项目；LFS 实际文件需要单独迁移。";
        if (lfs.ExitCode != 1) throw new GitException("检查 Git LFS 属性", lfs);
        return null;
    }

    /// <summary>在克隆前核实默认分支的额外内容依赖。</summary>
    private async Task RequireTransferContentsAsync(string path, CancellationToken token)
    {
        var problem = await TransferProblemAsync(path, token);
        if (problem is not null) throw new InvalidOperationException(problem);
    }

    /// <summary>确认目标确为裸仓库。</summary>
    private async Task RequireBareAsync(string path, CancellationToken token)
    {
        if ((await git.RunAsync(path, ["rev-parse", "--is-bare-repository"], token)).Output.Trim() != "true")
            throw new InvalidDataException("U 盘目标不是裸 Git 仓库：" + path);
    }

    /// <summary>传输前复核分支与提交，避免使用界面里的过期判断。</summary>
    private async Task EnsureUnchangedAsync(string path, RepositoryStatus previous, bool cleanRequired, CancellationToken token)
    {
        var current = await InspectLocalAsync(path, token);
        RequireSupported(current);
        if (current.Branch != previous.Branch || current.Head != previous.Head)
            throw new InvalidOperationException("操作期间本地分支或提交发生变化，请刷新后重试。");
        if (cleanRequired && current.Dirty) throw new InvalidOperationException("工作区有修改或未跟踪文件，请先处理后再拉取。");
    }

    /// <summary>将不支持的仓库状态转换为可读异常。</summary>
    private static void RequireSupported(LocalSnapshot local)
    {
        if (local.Problem is not null) throw new InvalidOperationException(local.Problem);
    }

    /// <summary>限制仓库名为安全的单个 Windows 目录名。</summary>
    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name != name.Trim() || name.EndsWith('.') || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name is "." or "..")
            throw new InvalidOperationException("仓库名称不能包含路径分隔符、非法字符或结尾空格/句点。");
        var stem = name.Split('.')[0].ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" || Enumerable.Range(1, 9).Any(i => stem == $"COM{i}" || stem == $"LPT{i}"))
            throw new InvalidOperationException("仓库名称不能使用 Windows 保留名称。");
    }

    /// <summary>把内部快照组合成界面状态。</summary>
    private static RepositoryStatus Status(LocalSnapshot local, string? remote, int ahead, int behind, SyncKind kind, string message) =>
        new(local.Branch, local.Head, remote, ahead, behind, local.Dirty, local.Operation, kind, message, DateTimeOffset.Now);

    /// <summary>本地检查结果；Problem 为空表示可以进一步比较提交。</summary>
    private sealed record LocalSnapshot(string Branch, string Head, bool Dirty, bool Operation, string? Problem);
}

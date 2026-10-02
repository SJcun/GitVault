namespace GitVault.Core;

/// <summary>封装离线仓库传输流程；调用方串行执行，避免同一工作目录相互竞争。</summary>
public sealed class RepositoryService(GitCommandService git, VaultService vaults)
{
    /// <summary>按规范化路径保留每个仓库最近三个提交的内容检查；不缓存动态状态或读取失败。</summary>
    private readonly Dictionary<string, List<(string Oid, string? Problem)>> contentChecks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>将本地分支和标签复制成裸仓库；失败目录保留以便检查。</summary>
    public async Task<VaultRepository> ImportAsync(VaultLocation vault, string localPath, string name, CancellationToken token = default)
    {
        var local = await InspectLocalAsync(localPath, token);
        RequireSupported(local);
        ValidateName(name);
        var current = vaults.Open(vault.RootPath);
        if (current.Manifest.VaultId != vault.Manifest.VaultId) throw new IOException("代码库身份发生变化。");
        var repository = new VaultRepository { Name = name, RelativePath = $"repos/{name}.git" };
        var destination = vaults.RepositoryPath(current, repository);
        if (Path.Exists(destination)) throw new IOException("代码库中已有同名项目，请更换名称，或使用“恢复仓库列表”。");
        var temporary = Path.Combine(Path.GetDirectoryName(destination)!, $".import-{Guid.NewGuid():N}");
        await git.RunAsync(null, ["clone", "--bare", "--no-hardlinks", "--progress", "--", Path.GetFullPath(localPath), temporary], token);
        await RequireBareAsync(temporary, token);
        await git.RunAsync(temporary, ["fsck", "--connectivity-only"], token);
        // clone --bare 已包含本地分支；不重复 push，也不修改源仓库的 remote。
        token.ThrowIfCancellationRequested();
        if (vaults.Open(vault.RootPath).Manifest.VaultId != vault.Manifest.VaultId) throw new IOException("代码库身份发生变化。");
        Directory.Move(temporary, destination);
        vaults.Register(current, repository);
        return repository;
    }

    /// <summary>克隆到全新目录，只移除本次克隆生成的 origin。</summary>
    public async Task CloneAsync(VaultLocation vault, VaultRepository repository, string destination, CancellationToken token = default)
    {
        var source = vaults.VerifyRepository(vault, repository);
        await RequireBareAsync(source, token);
        var sourceHead = (await git.RunAsync(source, ["rev-parse", "--verify", "HEAD"], token)).Output.Trim();
        await RequireTransferContentsAsync(source, sourceHead, token);
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
        if (remote.ExitCode == 1)
        {
            var missingTags = await GetTagChangesAsync(localPath, vaults.VerifyRepository(vault, repository), local.Head, null, token);
            return Status(local, null, 0, 0, SyncKind.MissingBranch, "U 盘没有同名分支", missingTags);
        }
        if (remote.ExitCode != 0) throw new GitException("读取 U 盘缓存分支", remote);
        var remoteHead = remote.Output.Trim();
        var ahead = 0;
        var behind = 0;
        // 同一对象必然已同步；标签仍独立读取，不能用提交相同推断标签没有变化。
        if (local.Head != remoteHead)
        {
            var common = await git.RunAsync(localPath, ["merge-base", local.Head, remoteHead], token, check: false);
            if (common.ExitCode == 1) return Status(local, remoteHead, 0, 0, SyncKind.Unrelated, "两个分支没有共同历史");
            if (common.ExitCode != 0) throw new GitException("检查共同历史", common);
            var counts = (await git.RunAsync(localPath, ["rev-list", "--left-right", "--count", $"{local.Head}...{remoteHead}"], token)).Output
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            ahead = int.Parse(counts[0]);
            behind = int.Parse(counts[1]);
        }
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
        var tags = await GetTagChangesAsync(localPath, vaults.VerifyRepository(vault, repository), local.Head, remoteHead, token);
        return Status(local, remoteHead, ahead, behind, kind, message, tags);
    }

    /// <summary>原子推送当前分支及其历史中的新标签；分叉与落后状态直接拒绝。</summary>
    public async Task<RepositoryStatus> PushAsync(VaultLocation vault, VaultRepository repository, string localPath, CancellationToken token = default)
    {
        var state = await RefreshAsync(vault, repository, localPath, token);
        if (state.Kind is not (SyncKind.Ahead or SyncKind.MissingBranch or SyncKind.Synced))
            throw new InvalidOperationException("当前状态不能推送：" + state.Message);
        if (state.TagConflict is not null) throw new InvalidOperationException("同名标签指向不同对象，请先在 Git 工具中处理：" + state.TagConflict);
        await EnsureUnchangedAsync(localPath, state, false, token);
        var destination = vaults.VerifyRepository(vault, repository);
        var tags = await GetTagChangesAsync(localPath, destination, state.Head, state.RemoteHead, token);
        if (tags.Conflict is not null) throw new InvalidOperationException("同名标签指向不同对象，请先在 Git 工具中处理：" + tags.Conflict);
        var refs = new List<string> { $"refs/heads/{state.Branch}:refs/heads/{state.Branch}" };
        refs.AddRange(tags.Push.Select(name => $"refs/tags/{name}:refs/tags/{name}"));
        // 显式列出标签并要求原子更新，避免用户 Git 配置或部分推送扩大同步范围。
        await git.RunAsync(localPath, ["-c", "push.followTags=false", "push", "--atomic", "--progress", "--recurse-submodules=no", destination, .. refs], token);
        return await RefreshAsync(vault, repository, localPath, token);
    }

    /// <summary>快进当前分支后拉取其历史中的新标签；同名异指向时不改写标签。</summary>
    public async Task<RepositoryStatus> PullAsync(VaultLocation vault, VaultRepository repository, string localPath, CancellationToken token = default)
    {
        var state = await RefreshAsync(vault, repository, localPath, token);
        if (state.Kind is not (SyncKind.Behind or SyncKind.Synced))
            throw new InvalidOperationException("当前状态不能快进拉取：" + state.Message);
        if (state.TagConflict is not null) throw new InvalidOperationException("同名标签指向不同对象，请先在 Git 工具中处理：" + state.TagConflict);
        var source = vaults.VerifyRepository(vault, repository);
        var tags = await GetTagChangesAsync(localPath, source, state.Head, state.RemoteHead, token);
        if (tags.Conflict is not null) throw new InvalidOperationException("同名标签指向不同对象，请先在 Git 工具中处理：" + tags.Conflict);
        // 检查已抓取的同一目标提交，再复核本地状态；拒绝发生在分支和标签更新之前。
        if (state.Kind == SyncKind.Behind)
            await RequireTransferContentsAsync(localPath, state.RemoteHead!, token);
        await EnsureUnchangedAsync(localPath, state, true, token);
        if (state.Kind == SyncKind.Behind)
            await git.RunAsync(localPath, ["-c", "merge.autoStash=false", "-c", "core.logAllRefUpdates=true", "merge", "--ff-only", "--no-autostash", state.RemoteHead!], token);
        if (tags.Pull.Length > 0)
        {
            var refs = tags.Pull.Select(name => $"refs/tags/{name}:refs/tags/{name}");
            // 只抓取预先确认属于当前分支的标签，不改动其他标签或已有 remote。
            await git.RunAsync(localPath, ["fetch", "--atomic", "--progress", "--no-tags", "--no-prune", "--no-recurse-submodules", "--no-write-fetch-head", "--refmap=", source, .. refs], token);
        }
        return await RefreshAsync(vault, repository, localPath, token);
    }

    /// <summary>读取最近提交及其本机标签，以字段分隔符避免依赖本地化输出。</summary>
    public async Task<IReadOnlyList<CommitInfo>> RecentAsync(string localPath, CancellationToken token = default)
    {
        var result = await git.RunAsync(localPath, ["log", "-20", "--date=iso-strict", "--format=%h%x1f%s%x1f%an%x1f%ad%x1f%H"], token, check: false);
        if (result.ExitCode != 0) return [];
        var tagResult = await git.RunAsync(localPath,
            ["for-each-ref", "--format=%(refname:strip=2)%09%(objecttype)%09%(objectname)%09%(*objecttype)%09%(*objectname)", "refs/tags"], token);
        var tagsByCommit = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var line in tagResult.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.TrimEnd('\r').Split('\t');
            if (parts.Length != 5) continue;
            // 轻量标签直接指向提交；附注标签通过解引用字段找到目标提交。
            var commit = parts[1] == "commit" ? parts[2] : parts[3] == "commit" ? parts[4] : null;
            if (commit is null) continue;
            if (!tagsByCommit.TryGetValue(commit, out var names)) tagsByCommit[commit] = names = [];
            names.Add(parts[0]);
        }
        return result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r').Split('\x1f')).Where(parts => parts.Length == 5)
            .Select(parts => new CommitInfo(parts[0], parts[1], parts[2], parts[3])
            {
                Tags = tagsByCommit.TryGetValue(parts[4], out var names) ? "  标签：" + string.Join("、", names) : ""
            }).ToList();
    }

    /// <summary>逐项恢复完整裸仓库；候选错误继续，清单、身份和登记失败立即中止。</summary>
    public async Task<RecoveryResult> RecoverAsync(VaultLocation vault, CancellationToken token = default)
    {
        var current = OpenRecoveryVault(vault);
        var count = 0;
        var skipped = 0;
        var failures = new List<RecoveryFailure>();
        // 先取得本轮候选快照，避免登记产生的清单文件影响遍历。
        var paths = Directory.GetDirectories(Path.Combine(current.RootPath, "repos"), "*.git");
        foreach (var path in paths)
        {
            if (token.IsCancellationRequested) return new(count, skipped, failures, true);
            current = OpenRecoveryVault(vault);
            if (current.Manifest.Repositories.Any(r => string.Equals(vaults.RepositoryPath(current, r), path, StringComparison.OrdinalIgnoreCase)))
            {
                skipped++;
                continue;
            }
            if (Path.GetFileName(path).StartsWith(".import-", StringComparison.OrdinalIgnoreCase)) continue;
            var entry = new VaultRepository { Name = Path.GetFileNameWithoutExtension(path), RelativePath = Path.GetRelativePath(current.RootPath, path) };
            try
            {
                ValidateName(entry.Name);
                vaults.RepositoryPath(current, entry);
                await RequireBareAsync(path, token);
                await git.RunAsync(path, ["fsck", "--connectivity-only"], token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return new(count, skipped, failures, true);
            }
            catch (Exception error) when (error is GitException or IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
            {
                // 先重查全局状态；拔盘、身份替换和清单损坏不能伪装成单项目失败。
                OpenRecoveryVault(vault);
                failures.Add(new(path, error.Message));
                continue;
            }
            if (token.IsCancellationRequested) return new(count, skipped, failures, true);
            // 登记失败属于全局写入故障，不能吞掉后继续处理其他候选。
            current = vaults.Register(current, entry);
            count++;
        }
        return new(count, skipped, failures, false);
    }

    /// <summary>恢复每个候选前后检查清单身份及 repos 根目录，全局异常原样上报。</summary>
    private VaultLocation OpenRecoveryVault(VaultLocation expected)
    {
        var current = vaults.Open(expected.RootPath);
        if (current.Manifest.VaultId != expected.Manifest.VaultId) throw new IOException("代码库身份发生变化。");
        if (!Directory.Exists(Path.Combine(current.RootPath, "repos"))) throw new DirectoryNotFoundException("代码库的 repos 目录不存在。");
        return current;
    }
    /// <summary>按提交可达性找出当前分支相关标签，并以原始引用对象识别同名冲突。</summary>
    private async Task<TagChanges> GetTagChangesAsync(string localPath, string remotePath, string localHead, string? remoteHead, CancellationToken token)
    {
        var localRefs = await ReadTagRefsAsync(localPath, token);
        var remoteRefs = await ReadTagRefsAsync(remotePath, token);
        var localNames = await ReadMergedTagsAsync(localPath, localHead, token);
        var remoteNames = remoteHead is null ? [] : await ReadMergedTagsAsync(remotePath, remoteHead, token);
        var conflict = localNames.Concat(remoteNames).Distinct(StringComparer.Ordinal)
            .FirstOrDefault(name => localRefs.TryGetValue(name, out var local) && remoteRefs.TryGetValue(name, out var remote) && local != remote);
        var push = localNames.Where(name => !remoteRefs.ContainsKey(name)).ToArray();
        var pull = remoteNames.Where(name => !localRefs.ContainsKey(name)).ToArray();
        return new TagChanges(push, pull, conflict);
    }

    /// <summary>读取标签的原始引用 OID，保留附注标签对象与轻量标签的区别。</summary>
    private async Task<Dictionary<string, string>> ReadTagRefsAsync(string path, CancellationToken token)
    {
        var result = await git.RunAsync(path, ["show-ref", "--tags"], token, check: false);
        if (result.ExitCode == 1) return new Dictionary<string, string>(StringComparer.Ordinal);
        if (result.ExitCode != 0) throw new GitException("读取标签引用", result);
        var tags = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.TrimEnd('\r').Split(' ', 2);
            tags.Add(parts[1]["refs/tags/".Length..], parts[0]);
        }
        return tags;
    }

    /// <summary>仅选择指向指定分支历史中提交的标签，排除其他分支与非提交对象。</summary>
    private async Task<string[]> ReadMergedTagsAsync(string path, string head, CancellationToken token) =>
        (await git.RunAsync(path, ["tag", "--list", "--merged", head], token)).Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

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
        var selected = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!Directory.Exists(selected)) throw new DirectoryNotFoundException("本地工作目录不存在：" + path);
        // 代码库根目录与其中的裸仓库都不含工作区；先按目录特征给出针对性说明，而不是只抛 Git 的原始报错。
        if (File.Exists(Path.Combine(selected, "vault.json")))
            throw new InvalidOperationException("选中的是 U 盘代码库根目录（内含 vault.json 和 repos），它本身不是 Git 工作区。"
                + "\n请选择本机上已有的 Git 项目根目录；本机还没有工作目录时，请点击“克隆到本机”。");
        if (Directory.Exists(Path.Combine(selected, "objects")) && Directory.Exists(Path.Combine(selected, "refs"))
            && string.Equals((await git.RunAsync(selected, ["rev-parse", "--is-bare-repository"], token, check: false)).Output.Trim(),
                "true", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("选中的是 U 盘代码库中的裸仓库目录，它没有工作区。"
                + "\n请点击“克隆到本机”把它克隆到本机；已经有本地工作目录时，请选择那个目录，而不是 U 盘里的目录。");
        var locate = await git.RunAsync(path, ["rev-parse", "--show-toplevel"], token, check: false);
        if (locate.ExitCode != 0)
            throw new InvalidOperationException("选中的目录不是 Git 工作区，也不是工作区的子目录：" + selected
                + "\n请选择本机上已有的 Git 项目根目录；还没有工作目录时，请点击“克隆到本机”。");
        var root = locate.Output.Trim();
        if (!string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), selected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("请选择 Git 工作区根目录：" + root);
        var branchResult = await git.RunAsync(path, ["symbolic-ref", "--quiet", "--short", "HEAD"], token, check: false);
        var branch = branchResult.Output.Trim();
        var headResult = await git.RunAsync(path, ["rev-parse", "--verify", "--quiet", "HEAD"], token, check: false);
        var head = headResult.Output.Trim();
        var dirty = (await git.RunAsync(path, ["status", "--porcelain=v1", "-z", "--untracked-files=normal"], token)).Output.Length > 0;
        // 一次查询取得全部标记路径，仍由 Git 定位，以兼容 linked worktree 的独立管理目录。
        var markers = await git.RunAsync(path, ["rev-parse", "--git-path", "MERGE_HEAD", "--git-path", "CHERRY_PICK_HEAD",
            "--git-path", "REVERT_HEAD", "--git-path", "rebase-merge", "--git-path", "rebase-apply", "--git-path", "sequencer"], token);
        var operation = markers.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(marker => Path.Exists(Path.GetFullPath(marker, selected)));
        string? problem = null;
        if (headResult.ExitCode != 0) problem = "尚未创建首次提交，请先在 Git 工具中提交。";
        else if (branchResult.ExitCode != 0) problem = "HEAD 未附着到分支，请先切换到本地分支。";
        else if (operation) problem = "存在未完成的 merge/rebase 等操作，请先在 Git 工具中处理。";
        else if ((await git.RunAsync(path, ["rev-parse", "--is-shallow-repository"], token)).Output.Trim() == "true")
            problem = "首版不支持浅克隆，请先补全历史。";
        else problem = await TransferProblemAsync(path, head, token);
        return new LocalSnapshot(branch, head, dirty, operation, problem);
    }

    /// <summary>复用同一仓库和完整提交 OID 的检查结果；取消、Git 错误与读取失败不进入缓存。</summary>
    private async Task<string?> TransferProblemAsync(string path, string commitOid, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var key = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (contentChecks.TryGetValue(key, out var entries))
        {
            var index = entries.FindIndex(entry => entry.Oid == commitOid);
            if (index >= 0) return entries[index].Problem;
        }
        var problem = await ReadTransferProblemAsync(path, commitOid, token);
        token.ThrowIfCancellationRequested();
        if (entries is null) contentChecks[key] = entries = [];
        if (entries.Count == 3) entries.RemoveAt(0);
        entries.Add((commitOid, problem));
        return problem;
    }

    /// <summary>检查指定提交，拒绝只传输指针却遗漏实际内容的 LFS 与子模块项目。</summary>
    private async Task<string?> ReadTransferProblemAsync(string path, string commitOid, CancellationToken token)
    {
        var modules = await git.RunAsync(path, ["ls-tree", "-r", commitOid], token);
        if (modules.Output.Split('\n').Any(line => line.StartsWith("160000 ", StringComparison.Ordinal)))
            return "首版不支持包含子模块的项目；子模块内容不会随裸仓库传输。";
        var lfs = await git.RunAsync(path, ["grep", "-I", "-l", "-e", "filter[[:space:]]*=[[:space:]]*lfs", commitOid, "--", ".gitattributes", ":(glob)**/.gitattributes"], token, check: false);
        if (lfs.ExitCode == 0) return "首版不支持 Git LFS 项目；LFS 实际文件需要单独迁移。";
        if (lfs.ExitCode != 1) throw new GitException("检查 Git LFS 属性", lfs);
        return null;
    }

    /// <summary>在克隆或快进前核实指定提交的额外内容依赖。</summary>
    private async Task RequireTransferContentsAsync(string path, string commitOid, CancellationToken token)
    {
        var problem = await TransferProblemAsync(path, commitOid, token);
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

    /// <summary>把分支关系及标签差异组合成界面状态。</summary>
    private static RepositoryStatus Status(LocalSnapshot local, string? remote, int ahead, int behind, SyncKind kind, string message, TagChanges? tags = null)
    {
        if (tags?.Conflict is not null) message += $"；标签冲突：{tags.Conflict}";
        else if (tags is not null && (tags.Push.Length > 0 || tags.Pull.Length > 0))
            message += $"；标签待推送 {tags.Push.Length} 个、待拉取 {tags.Pull.Length} 个";
        return new(local.Branch, local.Head, remote, ahead, behind, local.Dirty, local.Operation, kind, message, DateTimeOffset.Now)
        {
            TagsToPush = tags?.Push.Length ?? 0, TagsToPull = tags?.Pull.Length ?? 0, TagConflict = tags?.Conflict
        };
    }

    /// <summary>当前分支的双向标签差异及同名异指向标签。</summary>
    /// <param name="Push">本地有而 U 盘没有的相关标签。</param>
    /// <param name="Pull">U 盘有而本地没有的相关标签。</param>
    /// <param name="Conflict">两端同名但对象不同的标签。</param>
    private sealed record TagChanges(string[] Push, string[] Pull, string? Conflict);

    /// <summary>本地检查结果；Problem 为空表示可以进一步比较提交。</summary>
    private sealed record LocalSnapshot(string Branch, string Head, bool Dirty, bool Operation, string? Problem);
}

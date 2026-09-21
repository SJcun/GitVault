using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitVault.Core;

namespace GitVault.App;

/// <summary>组织主窗口操作，所有 Git 任务串行执行，取消后要求重新检查。</summary>
public partial class MainViewModel : ObservableObject
{
    /// <summary>核心服务与本机设置。</summary>
    private readonly GitCommandService git = new();
    private readonly VaultService vaults = new();
    private readonly SettingsService settingsStore = new();
    private readonly RepositoryService repositories;
    private AppSettings settings = new();
    /// <summary>当前选定的 Vault 与本次任务的取消源。</summary>
    private VaultLocation? current;
    private CancellationTokenSource? cancellation;
    /// <summary>设置损坏时禁止后续保存，避免覆盖用户文件。</summary>
    private bool settingsAvailable;

    /// <summary>Vault 内的仓库列表。</summary>
    public ObservableCollection<RepositoryItem> Items { get; } = [];
    /// <summary>支持按名称筛选的列表视图。</summary>
    public ICollectionView FilteredItems { get; }
    /// <summary>当前分支的提交记录。</summary>
    public ObservableCollection<CommitInfo> Commits { get; } = [];
    /// <summary>发现的 Vault；重复身份也保留供用户明确选择。</summary>
    public ObservableCollection<VaultChoice> VaultChoices { get; } = [];

    /// <summary>当前任务是否正在执行。</summary>
    [ObservableProperty] private bool isBusy;
    /// <summary>当前 Vault 是否可用。</summary>
    [ObservableProperty] private bool isOnline;
    /// <summary>顶部 Vault 标题。</summary>
    [ObservableProperty] private string vaultTitle = "打开你的离线代码库";
    /// <summary>顶部 Vault 路径说明。</summary>
    [ObservableProperty] private string vaultPath = "创建一个 Vault，或选择 U 盘上的已有目录。";
    /// <summary>列表搜索条件。</summary>
    [ObservableProperty] private string search = "";
    /// <summary>当前选中的仓库。</summary>
    [ObservableProperty] private RepositoryItem? selectedItem;
    /// <summary>当前设备选择。</summary>
    [ObservableProperty] private VaultChoice? selectedVault;
    /// <summary>底部操作结果。</summary>
    [ObservableProperty] private string feedback = "准备就绪 · 代码保存在你的设备上";
    /// <summary>可复制的近期操作日志。</summary>
    [ObservableProperty] private string logText = "";
    /// <summary>日志区域的展开状态。</summary>
    [ObservableProperty] private bool isLogOpen;

    /// <summary>初始化服务和日志回调。</summary>
    public MainViewModel()
    {
        repositories = new RepositoryService(git, vaults);
        FilteredItems = CollectionViewSource.GetDefaultView(Items);
        FilteredItems.Filter = item => item is RepositoryItem row && row.Name.Contains(Search, StringComparison.OrdinalIgnoreCase);
        git.Log = message => Application.Current.Dispatcher.BeginInvoke(() => AppendLog(message));
    }

    /// <summary>普通命令是否可执行。</summary>
    public bool CanWork => !IsBusy && settingsAvailable;
    /// <summary>操作 Vault 需要有效连接。</summary>
    public bool CanUseVault => CanWork && current is not null && IsOnline;
    /// <summary>克隆或绑定需要已选中仓库。</summary>
    public bool CanSelectLocal => CanUseVault && SelectedItem is not null;
    /// <summary>推送仅接受已验证的领先或缺失分支状态。</summary>
    public bool CanPush => CanSelectLocal && SelectedItem?.Status?.Kind is SyncKind.Ahead or SyncKind.MissingBranch;
    /// <summary>拉取仅接受干净工作区的落后状态。</summary>
    public bool CanPull => CanSelectLocal && SelectedItem?.Status is { Kind: SyncKind.Behind, IsDirty: false, HasOperation: false };
    /// <summary>打开工作目录不修改 Git 数据。</summary>
    public bool CanOpenLocal => SelectedItem?.LocalPath is { } path && Directory.Exists(path);
    /// <summary>有选择时显示详情。</summary>
    public bool HasSelection => SelectedItem is not null;
    /// <summary>无选择时显示引导。</summary>
    public bool ShowWelcome => SelectedItem is null;
    /// <summary>仓库尚未绑定。</summary>
    public bool NeedsBinding => SelectedItem is { LocalPath: null };
    /// <summary>当前仓库名称。</summary>
    public string DetailTitle => SelectedItem?.Name ?? "你的代码，随身携带";
    /// <summary>本地位置。</summary>
    public string LocalPath => SelectedItem?.LocalPath ?? "本机尚未绑定工作目录";
    /// <summary>U 盘位置。</summary>
    public string RemotePath => current is not null && SelectedItem is not null ? vaults.RepositoryPath(current, SelectedItem.Repository) : "";
    /// <summary>当前分支显示文本。</summary>
    public string BranchText => SelectedItem?.Status?.Branch is { Length: > 0 } branch ? branch : "—";
    /// <summary>当前状态摘要。</summary>
    public string StatusText => SelectedItem?.Status?.Message ?? SelectedItem?.Summary ?? "选择一个仓库开始";
    /// <summary>工作区与状态时间，离线时明确标为过期。</summary>
    public string WorktreeText => SelectedItem?.Status is { } state
        ? $"{(state.IsDirty ? "工作区有修改 · 推送仅包含已提交内容" : "工作区干净")}  ·  {state.CheckedAt:HH:mm:ss} 刷新"
        : "状态尚未验证";
    /// <summary>状态对应的下一步说明。</summary>
    public string Guidance => SelectedItem?.Status?.Kind switch
    {
        SyncKind.Diverged => "两边都有新提交。请打开本地目录，用现有 Git 工具合并或变基后再刷新。",
        SyncKind.Unrelated => "分支没有共同历史，请检查是否绑定了正确的项目。",
        SyncKind.Unsupported => SelectedItem.Status.Message,
        SyncKind.MissingBranch => "点击推送会在 U 盘创建当前同名分支。",
        SyncKind.Behind when SelectedItem.Status.IsDirty => "拉取前请先提交、暂存或移走工作区修改；程序不会自动 stash。",
        _ => "每次操作只同步当前分支。提交代码和切换分支请使用现有 Git 工具。"
    };
    /// <summary>设备连接说明。</summary>
    public string ConnectionText => IsOnline ? "● 已连接" : "○ 未连接";
    /// <summary>推送按钮在新分支状态下明确说明创建行为。</summary>
    public string PushLabel => SelectedItem?.Status?.Kind == SyncKind.MissingBranch ? "↑ 在 U 盘创建此分支" : "↑ 推送到 U 盘";

    /// <summary>启动时加载设置并发现设备；Git 缺失仍可进入设置修正路径。</summary>
    public async Task InitializeAsync()
    {
        try
        {
            settings = settingsStore.Load();
            settingsAvailable = true;
            git.GitPath = settings.GitPath;
        }
        catch (Exception error)
        {
            Feedback = "本机设置读取失败，请修复后重启。";
            Dialogs.Error(error.Message + "\n" + settingsStore.DirectoryPath);
            return;
        }
        await ExecuteAsync("检查环境", async token =>
        {
            await git.RunAsync(null, ["--version"], token);
            await DiscoverCoreAsync(token);
        });
    }

    /// <summary>重新发现设备，不对相同身份的多个位置作隐式选择。</summary>
    [RelayCommand(CanExecute = nameof(CanWork))]
    private Task DiscoverAsync() => ExecuteAsync("扫描 Vault", DiscoverCoreAsync);

    /// <summary>获取候选位置并在唯一匹配时恢复连接。</summary>
    private async Task DiscoverCoreAsync(CancellationToken token)
    {
        var found = await Task.Run(() => vaults.Discover(settings.KnownVaultPaths), token);
        VaultChoices.Clear();
        foreach (var location in found) VaultChoices.Add(new VaultChoice(location));
        var matches = current is null ? found : found.Where(v => v.Manifest.VaultId == current.Manifest.VaultId).ToList();
        if (matches.Count == 1) await LoadVaultAsync(matches[0], token);
        else if (matches.Count > 1)
        {
            IsOnline = false;
            SelectedVault = null;
            foreach (var item in Items) item.Invalidate("等待选择 Vault 位置");
            Feedback = "发现多个候选位置，请从顶部列表明确选择。";
        }
        else if (current is not null) MarkOffline();
        UpdateDetails();
    }

    /// <summary>手动选择任何位置的 Vault，包括固定磁盘形式的移动硬盘。</summary>
    [RelayCommand(CanExecute = nameof(CanWork))]
    private async Task OpenVaultAsync()
    {
        var path = Dialogs.Folder("选择包含 vault.json 的目录");
        if (path is null) return;
        await ExecuteAsync("打开 Vault", token => LoadVaultAsync(vaults.Open(path), token));
    }

    /// <summary>在空目录中创建 Vault。</summary>
    [RelayCommand(CanExecute = nameof(CanWork))]
    private async Task CreateVaultAsync()
    {
        var input = Dialogs.Ask("创建 Vault", "请选择 U 盘上的空目录，或输入一个新的目录路径。",
            new InputField("Vault 名称", "我的代码库"), new InputField("Vault 目录", "", "folder"));
        if (input is null) return;
        await ExecuteAsync("创建 Vault", token => LoadVaultAsync(vaults.Create(input[1], input[0]), token));
    }

    /// <summary>加载清单并恢复本机路径。</summary>
    private async Task LoadVaultAsync(VaultLocation location, CancellationToken token)
    {
        var selectedId = SelectedItem?.Repository.RepoId;
        var loaded = vaults.Open(location.RootPath);
        if (loaded.Manifest.VaultId != location.Manifest.VaultId)
        {
            MarkOffline();
            throw new IOException("此位置已被其他 Vault 占用，请重新打开正确的 Vault。");
        }
        current = loaded;
        IsOnline = true;
        VaultTitle = current.Manifest.Name;
        VaultPath = current.RootPath;
        if (!settings.KnownVaultPaths.Contains(current.RootPath, StringComparer.OrdinalIgnoreCase)) settings.KnownVaultPaths.Add(current.RootPath);
        settingsStore.Save(settings);
        if (!VaultChoices.Any(choice => choice.Location.RootPath == current.RootPath)) VaultChoices.Add(new VaultChoice(current));
        SelectedVault = VaultChoices.First(choice => choice.Location.RootPath == current.RootPath);
        Items.Clear();
        foreach (var repository in current.Manifest.Repositories)
        {
            var path = settings.Bindings.FirstOrDefault(binding => binding.VaultId == current.Manifest.VaultId && binding.RepoId == repository.RepoId)?.LocalPath;
            Items.Add(new RepositoryItem(repository, path));
        }
        SelectedItem = Items.FirstOrDefault(item => item.Repository.RepoId == selectedId) ?? Items.FirstOrDefault();
        await RefreshRowsAsync(token);
    }

    /// <summary>将已有工作区导入 U 盘。</summary>
    [RelayCommand(CanExecute = nameof(CanUseVault))]
    private async Task ImportAsync()
    {
        var input = Dialogs.Ask("加入本地项目", "复制本地分支和标签中的已提交内容。已有 remote 保持不变。",
            new InputField("本地 Git 工作区根目录", "", "folder"), new InputField("U 盘仓库名称"));
        if (input is null) return;
        await ExecuteAsync("导入项目", async token =>
        {
            var repository = await repositories.ImportAsync(current!, input[0], input[1], token);
            SaveBinding(repository, input[0]);
            await LoadVaultAsync(current!, token);
            SelectedItem = Items.First(item => item.Repository.RepoId == repository.RepoId);
            await LoadCommitsAsync(token);
        });
    }

    /// <summary>将 U 盘项目克隆到新建子目录。</summary>
    [RelayCommand(CanExecute = nameof(CanSelectLocal))]
    private async Task CloneAsync()
    {
        var item = SelectedItem!;
        var input = Dialogs.Ask("克隆到本机", "选择已有父目录和新文件夹名。克隆后通过 GitVault 同步，不配置固定盘符的 origin。",
            new InputField("目标父目录", "", "folder"), new InputField("新文件夹名称", item.Name));
        if (input is null) return;
        await ExecuteAsync("克隆项目", async token =>
        {
            if (input[1].IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || input[1] is "." or "..") throw new InvalidOperationException("请输入单个文件夹名称。");
            var destination = Path.Combine(input[0], input[1]);
            await repositories.CloneAsync(current!, item.Repository, destination, token);
            SaveBinding(item.Repository, destination);
            item.LocalPath = Path.GetFullPath(destination);
            await RefreshRowAsync(item, token);
            await LoadCommitsAsync(token);
        });
    }

    /// <summary>绑定已有工作区，先验证同名分支和共同历史。</summary>
    [RelayCommand(CanExecute = nameof(CanSelectLocal))]
    private async Task BindAsync()
    {
        var path = Dialogs.Folder("选择已有 Git 工作区根目录");
        if (path is null) return;
        var item = SelectedItem!;
        await ExecuteAsync("绑定本地项目", async token =>
        {
            await repositories.BindAsync(current!, item.Repository, path, token);
            SaveBinding(item.Repository, path);
            item.LocalPath = path;
            await RefreshRowAsync(item, token);
            await LoadCommitsAsync(token);
        });
    }

    /// <summary>保存本机路径，不写入 U 盘清单。</summary>
    private void SaveBinding(VaultRepository repository, string path)
    {
        settings.Bindings.RemoveAll(binding => binding.VaultId == current!.Manifest.VaultId && binding.RepoId == repository.RepoId);
        settings.Bindings.Add(new RepositoryBinding(current!.Manifest.VaultId, repository.RepoId, Path.GetFullPath(path)));
        settingsStore.Save(settings);
    }

    /// <summary>重新读取整个 Vault 和所有绑定状态。</summary>
    [RelayCommand(CanExecute = nameof(CanWork))]
    private Task RefreshAsync() => ExecuteAsync("刷新状态", token => current is null ? DiscoverCoreAsync(token) : LoadVaultAsync(current, token));

    /// <summary>逐个检查仓库，单个错误不阻止其他仓库显示结果。</summary>
    private async Task RefreshRowsAsync(CancellationToken token)
    {
        foreach (var item in Items) await RefreshRowAsync(item, token);
        await LoadCommitsAsync(token);
        var failed = Items.Count(item => item.LocalPath is not null && item.Status is null);
        if (failed > 0) Feedback = $"刷新完成，{failed} 个仓库检查失败 · 查看日志";
        UpdateDetails();
    }

    /// <summary>失败立即清空同步缓存；错误在列表和日志中均可见。</summary>
    private async Task RefreshRowAsync(RepositoryItem item, CancellationToken token)
    {
        try
        {
            vaults.VerifyRepository(current!, item.Repository);
            if (item.LocalPath is null) return;
            item.Apply(await repositories.RefreshAsync(current!, item.Repository, item.LocalPath, token));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            item.Invalidate("刷新失败 · 查看日志");
            AppendLog(item.Name + ": " + error.Message);
            IsLogOpen = true;
            Feedback = "仓库检查失败 · 查看日志";
            if (current is not null && !File.Exists(Path.Combine(current.RootPath, "vault.json"))) MarkOffline();
        }
    }

    /// <summary>推送当前分支。</summary>
    [RelayCommand(CanExecute = nameof(CanPush))]
    private Task PushAsync() => TransferAsync(true);

    /// <summary>快进拉取当前分支。</summary>
    [RelayCommand(CanExecute = nameof(CanPull))]
    private Task PullAsync() => TransferAsync(false);

    /// <summary>执行传输，失败后使旧状态失效。</summary>
    private Task TransferAsync(bool push)
    {
        var item = SelectedItem!;
        return ExecuteAsync(push ? "推送到 U 盘" : "从 U 盘拉取", async token =>
        {
            try
            {
                var state = push ? await repositories.PushAsync(current!, item.Repository, item.LocalPath!, token)
                    : await repositories.PullAsync(current!, item.Repository, item.LocalPath!, token);
                item.Apply(state);
                await LoadCommitsAsync(token);
            }
            catch { item.Invalidate("操作未完成 · 请刷新复核"); throw; }
        });
    }

    /// <summary>登记清单写入失败后留下的完整裸仓库。</summary>
    [RelayCommand(CanExecute = nameof(CanUseVault))]
    private Task RecoverAsync() => ExecuteAsync("恢复仓库登记", async token =>
    {
        var count = await repositories.RecoverAsync(current!, token);
        AppendLog($"恢复登记 {count} 个仓库。");
        await LoadVaultAsync(current!, token);
    });

    /// <summary>设置 Git 路径并先执行版本检查。</summary>
    [RelayCommand(CanExecute = nameof(CanWork))]
    private async Task SettingsAsync()
    {
        var input = Dialogs.Ask("设置", "使用已安装的 Git，或选择便携 Git 的 git.exe。", new InputField("Git 可执行文件", settings.GitPath, "file"));
        if (input is null) return;
        await ExecuteAsync("验证 Git", async token =>
        {
            var candidate = new GitCommandService { GitPath = input[0] };
            var version = await candidate.RunAsync(null, ["--version"], token);
            settings.GitPath = input[0];
            settingsStore.Save(settings);
            git.GitPath = input[0];
            AppendLog(version.Output.Trim());
        });
    }

    /// <summary>打开本地文件夹。</summary>
    [RelayCommand(CanExecute = nameof(CanOpenLocal))]
    private void OpenLocal()
    {
        try { Process.Start(new ProcessStartInfo(LocalPath) { UseShellExecute = true }); }
        catch (Exception error) { Dialogs.Error(error.Message); }
    }

    /// <summary>请求取消当前任务，不把取消视为回滚。</summary>
    [RelayCommand]
    private void Cancel() => cancellation?.Cancel();

    /// <summary>复制日志供用户诊断。</summary>
    [RelayCommand]
    private void CopyLog()
    {
        try { Clipboard.SetText(LogText.Length == 0 ? "尚无日志" : LogText); }
        catch (Exception error) { Dialogs.Error(error.Message); }
    }

    /// <summary>统一任务生命周期与错误展示。</summary>
    private async Task ExecuteAsync(string title, Func<CancellationToken, Task> action)
    {
        if (IsBusy) return;
        IsBusy = true;
        Feedback = title + "…";
        using var source = new CancellationTokenSource();
        cancellation = source;
        try
        {
            await action(source.Token);
            if (Feedback == title + "…") Feedback = title + "完成";
        }
        catch (OperationCanceledException)
        {
            foreach (var item in Items) item.Invalidate("已取消 · 请刷新复核");
            Feedback = "操作已取消，请刷新检查实际状态。";
            AppendLog(Feedback);
        }
        catch (Exception error)
        {
            Feedback = title + "未完成 · 查看日志";
            AppendLog(error.Message);
            IsLogOpen = true;
            if (current is not null && !File.Exists(Path.Combine(current.RootPath, "vault.json"))) MarkOffline();
            Dialogs.Error(error.Message);
        }
        finally { cancellation = null; IsBusy = false; UpdateDetails(); }
    }

    /// <summary>读取所选仓库的提交列表。</summary>
    private async Task LoadCommitsAsync(CancellationToken token)
    {
        Commits.Clear();
        if (SelectedItem?.LocalPath is not { } path || !Directory.Exists(path)) return;
        foreach (var commit in await repositories.RecentAsync(path, token)) Commits.Add(commit);
    }

    /// <summary>设备通知只触发发现，不在忙碌时并行启动 Git。</summary>
    public async Task DeviceChangedAsync()
    {
        if (CanWork) await DiscoverAsync();
    }

    /// <summary>窗口重新获得焦点时检查外部 Git 操作造成的状态变化。</summary>
    public async Task RecheckSelectionAsync()
    {
        if (CanUseVault && SelectedItem?.LocalPath is not null)
            await ExecuteAsync("刷新当前仓库", async token => { await RefreshRowAsync(SelectedItem, token); await LoadCommitsAsync(token); });
    }

    /// <summary>失去设备后清空状态，只保留本机路径与仓库清单。</summary>
    private void MarkOffline()
    {
        IsOnline = false;
        foreach (var item in Items) item.Invalidate("U 盘离线 · 数据已过期");
        Feedback = "未找到原 Vault，请插入 U 盘或重新选择位置。";
    }

    /// <summary>保存日志并限制界面中展示的字符数。</summary>
    private void AppendLog(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}";
        LogText += line;
        if (LogText.Length > 60000) LogText = LogText[^50000..];
        try
        {
            Directory.CreateDirectory(settingsStore.DirectoryPath);
            File.AppendAllText(Path.Combine(settingsStore.DirectoryPath, $"gitvault-{DateTime.Now:yyyy-MM-dd}.log"), line);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Feedback = "日志文件无法写入；可复制界面日志。";
        }
    }

    /// <summary>集中更新派生字段和按钮，避免不同状态下漏刷新。</summary>
    private void UpdateDetails()
    {
        foreach (var name in new[] { nameof(CanWork), nameof(CanUseVault), nameof(CanSelectLocal), nameof(CanPush), nameof(CanPull), nameof(CanOpenLocal),
            nameof(HasSelection), nameof(ShowWelcome), nameof(NeedsBinding), nameof(DetailTitle), nameof(LocalPath), nameof(RemotePath), nameof(BranchText),
            nameof(StatusText), nameof(WorktreeText), nameof(Guidance), nameof(ConnectionText), nameof(PushLabel) }) OnPropertyChanged(name);
        foreach (var command in new IRelayCommand[] { DiscoverCommand, OpenVaultCommand, CreateVaultCommand, ImportCommand, CloneCommand, BindCommand,
            RefreshCommand, PushCommand, PullCommand, RecoverCommand, SettingsCommand, OpenLocalCommand }) command.NotifyCanExecuteChanged();
    }

    /// <summary>忙碌状态变化时刷新命令可用性。</summary>
    partial void OnIsBusyChanged(bool value) => UpdateDetails();
    /// <summary>连接变化时刷新状态栏。</summary>
    partial void OnIsOnlineChanged(bool value) => UpdateDetails();
    /// <summary>即时应用仓库名称筛选。</summary>
    partial void OnSearchChanged(string value) => FilteredItems.Refresh();
    /// <summary>选择仓库后异步读取当前状态。</summary>
    partial void OnSelectedItemChanged(RepositoryItem? value)
    {
        Commits.Clear();
        UpdateDetails();
        if (!IsBusy) _ = RecheckSelectionAsync();
    }
    /// <summary>用户明确选择设备时加载对应位置。</summary>
    partial void OnSelectedVaultChanged(VaultChoice? value)
    {
        if (!IsBusy && value is not null) _ = ExecuteAsync("切换 Vault", token => LoadVaultAsync(value.Location, token));
    }
}

/// <summary>设备列表同时展示名称与路径，区分同一身份的复制盘。</summary>
public sealed record VaultChoice(VaultLocation Location)
{
    /// <summary>下拉列表显示文本。</summary>
    public string Label => $"{Location.Manifest.Name} · {Location.RootPath}";
}

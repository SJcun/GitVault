# GitVault

用 U 盘在不同 Windows 电脑之间传递 Git 提交的小工具。基于 **.NET 8 + WPF**，直接调用 Git；仓库和工作目录均保存在自己的设备上。

**第一次用、看不懂按钮？先看 [使用说明书](docs/使用说明书.md)。发布包中也提供可双击打开的 `使用说明书.html`。**

## 运行

解压 `GitVault-win-x64.zip`，保持整个目录完整，运行 `GitVault.exe`。发布包包含 .NET 8 运行时，不需要单独安装 .NET。需要已有 Git for Windows；在“设置”中可选择便携 Git 的 `git.exe`。

当前发布目标是 Windows x64。已在 Windows 10 22H2 构建并验证进程启动；其他系统版本需要对应环境验证。

## 两台电脑之间使用

1. 在电脑 A 打开程序，点击“创建 U 盘代码库”，选择 U 盘作为父目录并输入新文件夹名（例如 `GitVault`），程序会自动创建该目录。也可以在“选择已有代码库”里直接选中项目目录，按提示创建后自动进入加入流程。
2. 点击“加入 U 盘代码库”，选择已有 Git 工作区根目录，填写项目名称。初次导入包含本地分支、标签及其已提交内容。
3. 等待操作完成，通过 Windows 正常弹出 U 盘，再带到电脑 B。
4. 在电脑 B 打开同一个 U 盘代码库，选中项目并点击“克隆到本机”；目标是已有父目录中的新文件夹。
5. 在 IDE 或其他 Git 工具中修改并提交，然后回 GitVault 刷新，点击“推送到 U 盘”。
6. 在另一台电脑刷新后，点击“从 U 盘拉取”。只有工作区干净、且可快进时才允许拉取。

已有本地副本可通过“绑定已有项目”连接；当前分支必须与 U 盘同名且有共同历史。重新绑定仅更新本机记录，不删除旧工作目录。

绑定记录只保存在当前电脑上（`settings.json`），不随 U 盘携带。因此在另一台电脑上，同一个项目会显示为“本机未绑定”，这是正常现象，按下面方式二选一：

- 本机还没有这个项目的代码：点击“克隆到本机”。
- 本机已经有这个项目的工作目录：点击“绑定已有项目”，选中**本机那个项目目录**（例如 `D:\Projects\MyTool`）。不要选择 U 盘里的 `GitVault` 目录或 `repos\MyTool.git`——它们是代码库和裸仓库，没有工作区，程序会拒绝并提示。

程序会识别默认 `GitVault` 目录和已知自定义目录的盘符变化。移动硬盘被识别为固定盘、或目录无法自动找到时，使用“选择已有代码库”手动定位。多个候选位置不会自动混用。

## 状态与边界

| 状态 | 如何处理 |
| --- | --- |
| 当前分支已同步 | 无需传输 |
| 本地领先 | 推送已提交内容；未提交文件不会一起传输 |
| U 盘领先 | 工作区干净时快进拉取 |
| 两边分叉 | 使用现有 Git 工具合并或变基，再回来刷新 |
| U 盘无同名分支 | 主动点击“在 U 盘创建此分支” |
| 离线、失败或已取消 | 重连后刷新确认实际结果 |

后续推送/拉取只处理**当前分支**，不自动同步所有分支或标签。切分支、提交、stash、合并和冲突处理继续使用原来的 Git 工具。首版遇到当前提交包含 LFS 属性或子模块时拒绝自动迁移；这不是对所有历史提交中额外依赖的全面审计。浅克隆、Detached HEAD 和未完成的 Git 操作也不进入传输流程。

工具不修改既有仓库的 `origin`、`usb` 或 upstream。从 U 盘新克隆的工作区会移除克隆时生成的固定路径 `origin`，由 GitVault 动态定位 U 盘。普通 `git pull` 不会因此自动获得 U 盘地址。

高级用户如需在外部工具整合 U 盘提交，可以在工作区执行以下命令（替换路径和分支）：

```powershell
git fetch 'F:\GitVault\repos\MyTool.git' main
git merge FETCH_HEAD
```

上面两条是用户自行进行的合并流程，可能产生冲突或合并提交；GitVault 的“拉取”按钮始终只做快进。完成后回到工具刷新并推送即可。

## 文件与恢复

```text
U盘/GitVault/
  vault.json               代码库身份与项目路径登记
  repos/MyTool.git/        标准裸 Git 仓库

%LocalAppData%/GitVault/
  settings.json            本机 Git 路径、已知位置和目录绑定
  gitvault-yyyy-MM-dd.log  本机操作日志
```

- 传输期间避免同时在 IDE 中切分支、改写历史或操作相同仓库。
- 失败后不要直接反复推送，先看日志并刷新实际状态。
- 如果项目存储已创建但加入列表失败，点击“恢复仓库列表”。它会检查 `repos` 下完整的 `.git` 仓库。
- `.import-*` 或 `.gitvault-clone-*` 是未完成操作留下的临时目录，不会自动登记或删除。确认操作已结束后可人工检查、清理，再重试。
- 不自动删除 Git 锁文件，不做强制推送，不对原工作区执行重置或自动 stash。
- 突然拔盘、磁盘损坏和断电不能保证自动恢复；U 盘中转也不等同于独立备份。
- 程序自身不连接托管平台。仓库自定义的 Git hooks、过滤器仍由本机 Git 执行，其外部依赖需用户自行管理。

## U 盘格式与 git 权限

FAT32 / exFAT 是 U 盘最常见的格式，但它们**不记录文件属主**。git 2.35.2 起会把「读不到属主」直接判为可疑所有权（dubious ownership）并拒绝操作，报错形如：

```text
fatal: detected dubious ownership in repository at 'F:/GitVault/repos/MyTool.git'
'F:/GitVault/repos/MyTool.git' is on a file system that does not record ownership
```

GitVault 为每次 Git 调用生成独立的临时全局配置，通过 `GIT_CONFIG_GLOBAL` 传给 Git 和本地传输子进程。配置只信任本次操作涉及的具体路径，并继承用户原有的全局配置；命令结束后删除临时文件，**不需要修改你的全局 Git 配置**。

`-c` 和 `GIT_CONFIG_COUNT` 注入的配置会在 Git 启动本地传输子进程时被清理，因此仅验证直接访问裸仓库成功还不够，必须验证真实的 push / fetch。具体路径的 `safe.directory` 可以用于 FAT32 / exFAT，无需使用 `*` 信任所有仓库。

若日志出现 `safe.directory ... not absolute`，检查全局配置值是否误含了单引号等字符；这与当前仓库的所有权错误是两个问题。

## 开发与验证

安装 .NET 8 SDK 和 Git，项目通过 `global.json` 使用 8.0.4xx SDK 的最新补丁版本。首次构建需要访问 NuGet。

```powershell
.\build.ps1 -Task Build
.\build.ps1 -Task Test
.\build.ps1 -Task Publish
```

发布输出：`artifacts/publish/GitVault-win-x64/`，压缩包：`artifacts/GitVault-win-x64.zip`。

测试使用临时真实 Git 仓库模拟两台电脑与 U 盘，验证双向提交传输、分叉、工作区保护、已有 remote 保留、路径变化、缓存清理、推送失败及列表恢复。测试不会更改用户全局 Git 配置。

详细设计见 [V1 方案](docs/GitVault-V1-方案.md)，验证记录见 [验证说明](docs/验证说明.md)。

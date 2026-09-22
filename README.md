# GitVault

**用 U 盘，在不同 Windows 电脑之间同步 Git 提交。**

GitVault 是基于 **.NET 8 + WPF** 的桌面工具，将 U 盘上的标准 Git 裸仓库作为代码中转站。你仍在本机使用熟悉的 IDE 和 Git 工具开发、提交，再通过 GitVault 推送或拉取代码，无需配置 Git 托管服务。

```text
电脑 A 的工作目录           U 盘代码库             电脑 B 的工作目录
编辑、提交代码       ⇄      标准 Git 裸仓库    ⇄    编辑、提交代码
```

适合需要在多台 Windows 电脑之间携带项目、通过移动存储设备离线传递提交的个人开发流程。

[使用说明书](docs/使用说明书.md) · [设计方案](docs/GitVault-V1-方案.md) · [验证说明](docs/验证说明.md)

## 功能

- **管理多个项目**：创建或打开 U 盘代码库，导入已有本地 Git 项目，按名称搜索项目。
- **双向传递提交**：查看当前分支的领先、落后或分叉状态，推送提交，或快进拉取 U 盘上的更新。
- **接入另一台电脑**：克隆到新的本地目录，或绑定已经存在、具有共同历史的项目。
- **适应盘符变化**：识别默认 `GitVault` 目录和已知自定义目录的位置变化，也可手动选择代码库。
- **保护本地工作**：拉取前检查工作区，不自动合并分叉、不强制推送、不自动 stash 或重置工作区。
- **查看与恢复**：展示最近提交、操作日志，支持取消操作和恢复未登记的完整裸仓库。

## 运行要求

| 项目 | 要求 |
| --- | --- |
| 系统 | Windows x64；已有 Windows 10 22H2 构建与进程启动验证，其他版本需在对应环境验证 |
| Git | 本机安装 Git for Windows，或在“设置”中指定便携 Git 的 `git.exe` |
| .NET | 使用自包含发布包时无需单独安装；从源码开发需要 .NET 8 SDK |
| 本地项目 | 已初始化且至少有一次提交的 Git 工作区 |

发布包不包含 Git。程序自身不连接 Git 托管平台；仓库配置的 Git hooks、过滤器及其外部依赖仍由本机 Git 执行。

## 快速开始

### 1. 启动程序

取得 `GitVault-win-x64.zip` 后，完整解压并运行 `GitVault.exe`。请保留同目录下的 DLL 和其他文件，不要只复制 EXE。

如果还没有发布包，可按下文“从源码构建”自行生成。发布包内附有可双击打开的 `使用说明书.html`。

首次启动若提示找不到 Git，打开“设置”，选择本机的 `git.exe`，或在 Git 已加入 PATH 时保留默认值 `git`。

### 2. 在电脑 A 导入项目

1. 插入 U 盘，点击“创建 U 盘代码库”。选择父目录并输入新文件夹名称，例如在 `F:\` 下创建 `GitVault`。
2. 点击“加入 U 盘代码库”，选择本机 Git 工作区根目录，例如 `D:\Projects\MyTool`，填写项目名称。
3. 等待导入完成。首次导入包含本地分支、标签及其已提交内容。
4. 操作结束后，通过 Windows 安全弹出 U 盘。

代码库目录应与程序所在目录分开。选择项目时应选择工作区根目录，不要选择项目内部的 `.git` 目录。

### 3. 在电脑 B 接入项目

打开程序，使用“选择已有代码库”打开 U 盘上的 `GitVault` 目录，选中项目后按实际情况操作：

| 本机情况 | 操作 |
| --- | --- |
| 还没有这个项目 | 点击“克隆到本机”，选择已有父目录下尚不存在的新文件夹 |
| 已经有同一个项目 | 点击“绑定已有项目”，选择本机项目工作区；当前分支须与 U 盘同名且有共同历史 |

绑定记录只保存在当前电脑，因此换电脑后显示“本机未绑定”是正常现象。重新绑定只更新本机记录，不删除旧工作目录。

### 4. 日常使用

**插盘 → 选项目 → 刷新 → 有更新先拉取 → 开发并提交 → 刷新 → 推送 → 等待完成再弹出 U 盘。**

GitVault 传递的是已提交内容。编辑、提交、切换分支、stash 和解决冲突，请继续使用现有 IDE 或 Git 工具。

## 同步规则与限制

| 当前状态 | 处理方式 |
| --- | --- |
| 当前分支已同步 | 无需传输 |
| 本地领先 | 点击“推送到 U 盘”；未提交文件不会一起传输 |
| U 盘领先 | 本地工作区干净时，点击“从 U 盘拉取” |
| 两边分叉 | 使用其他 Git 工具合并或变基，完成后回来刷新 |
| U 盘无同名分支 | 点击“在 U 盘创建此分支” |
| 没有共同历史 | 检查绑定目录和当前分支是否正确 |
| 离线、操作失败或已取消 | 重新连接后刷新，确认实际状态 |

- 首次导入包含本地分支和标签；后续推送仅更新**当前分支**，拉取仅快进更新当前分支，不自动同步所有分支或标签。
- “刷新”会抓取 U 盘分支到专用引用空间并比较状态，不会自动合并工作区代码。
- 当前版本拒绝自动传输检测到 Git LFS、子模块、浅克隆、Detached HEAD 或未完成 Git 操作的项目。LFS 和子模块检查针对当前提交，并非对全部历史依赖的全面审计。
- 不修改既有仓库的 `origin`、`usb` 或 upstream。从 U 盘新克隆的工作区会移除克隆时生成的固定路径 `origin`，由 GitVault 动态定位 U 盘；普通 `git pull` 不会因此自动获得 U 盘地址。
- 传输期间请避免在 IDE 或其他 Git 工具中同时切分支、改写历史或操作相同仓库。

需要在外部工具整合 U 盘提交时，可在本地工作区执行以下命令。路径和分支请替换为实际值：

```powershell
git fetch 'F:\GitVault\repos\MyTool.git' main
git merge FETCH_HEAD
```

这是手动合并流程，可能产生冲突或合并提交。完成后回 GitVault 刷新并推送；程序中的“拉取”始终只做快进。

## 数据存放与故障排查

```text
U盘/GitVault/
├── vault.json                  代码库身份与项目路径登记
└── repos/
    └── MyTool.git/              标准 Git 裸仓库

%LocalAppData%/GitVault/
├── settings.json               本机 Git 路径、已知位置和工作目录绑定
└── gitvault-yyyy-MM-dd.log      本机操作日志
```

U 盘上的裸仓库用于存储 Git 数据，日常编辑应在本机工作区进行。

| 问题 | 排查方式 |
| --- | --- |
| 换盘符后没有找到代码库 | 点击“扫描 U 盘”；仍找不到时用“选择已有代码库”手动定位。移动硬盘可能被系统识别为固定盘 |
| 拉取按钮不可用 | 检查工作区是否有修改、是否分叉，以及是否存在未完成的 Git 操作 |
| 项目目录存在，但不在列表中 | 使用“恢复仓库列表”，检查并登记 `repos` 下完整的 `.git` 裸仓库；恢复后仍需按需绑定本机目录 |
| 操作失败或取消 | 展开“操作日志”，检查错误并刷新状态；取消不代表已经回滚 |
| 留下临时目录 | `.import-*` 或 `.gitvault-clone-*` 不会自动登记或删除；确认操作结束后人工检查、清理再重试 |

GitVault 针对 FAT32 / exFAT 等介质上的 Git 所有权检查，为每次 Git 调用生成临时配置，通过 `GIT_CONFIG_GLOBAL` 传递给 Git 及本地传输子进程。该配置继承原有用户配置，仅增加本次操作涉及路径的 `safe.directory` 条目，命令结束后删除，不写入用户全局 Git 配置。

如果日志出现 `safe.directory ... not absolute`，检查已有全局配置是否包含多余引号等字符。外部 Git 工具不会自动继承 GitVault 的临时配置。

程序不自动删除 Git 锁文件。突然拔盘、磁盘损坏或断电不保证能自动恢复，U 盘中转也不能替代独立备份。更多问题见[使用说明书](docs/使用说明书.md)。

## 从源码构建

在 Windows 上准备 Git 和 .NET 8 SDK，将源码克隆或下载到本机后，在项目根目录打开 PowerShell。

SDK 选择由 [`global.json`](global.json) 控制：基准版本为 `8.0.300`，使用 `latestFeature` 策略选择兼容的 .NET 8 SDK 功能带。首次构建需要访问 NuGet 下载依赖。

```powershell
# 构建 Release 版本
.\build.ps1 -Task Build

# 运行核心服务与 WPF 应用测试
.\build.ps1 -Task Test

# 生成包含 .NET 运行时的 Windows x64 发布包
.\build.ps1 -Task Publish
```

开发时直接运行：

```powershell
dotnet run --project src/GitVault.App/GitVault.App.csproj
```

| 输出 | 路径 |
| --- | --- |
| 发布目录 | `artifacts/publish/GitVault-win-x64/` |
| 发布压缩包 | `artifacts/GitVault-win-x64.zip` |
| 测试结果 | `artifacts/TestResults/` |
| 构建脚本使用的 NuGet 缓存 | `artifacts/nuget/` |

发布脚本会一并打包 README、使用说明和第三方依赖声明。

## 项目结构

```text
GitVault.sln                 解决方案
src/
├── GitVault.App/            WPF 界面、视图模型、对话框与图标资源
└── GitVault.Core/           Git 命令调用、仓库同步、代码库与设置管理
tests/
├── GitVault.Core.Tests/     基于真实临时 Git 仓库的核心测试
└── GitVault.App.Tests/      WPF 对话框交互回归测试
docs/                       使用说明、设计方案与验证记录
tools/Export-AppIcon.ps1     图标导出脚本
build.ps1                   构建、测试与发布入口
global.json                 .NET SDK 选择配置
```

主要技术：C#、.NET 8、WPF、CommunityToolkit.Mvvm、Git CLI 和 xUnit。

测试覆盖双向提交传输、分叉与工作区保护、已有 remote 保留、盘符对应路径变化、缓存引用清理、推送失败、仓库列表恢复及对话框交互等场景。核心测试使用临时真实 Git 仓库模拟两台电脑与 U 盘，不修改用户全局 Git 配置。已有验证结果和尚需实机验证的范围见[验证说明](docs/验证说明.md)。

Logo 与界面图标定义在 `src/GitVault.App/Assets/Icons.xaml`。修改 Logo 后，可在 Windows 下重新生成多尺寸 ICO 和预览 PNG，再构建或发布：

```powershell
powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -File tools/Export-AppIcon.ps1
```

## 参与贡献

欢迎通过仓库的 Issue 或 Pull Request 反馈问题、完善文档和提交修复。

- 报告问题时，请附上 Windows 与 Git 版本、复现步骤、预期结果和相关日志；提交前请移除日志中的私人路径或敏感信息。
- 修改同步逻辑时，请补充相应回归测试，特别关注未提交内容、现有 remote 和分叉历史的保护。
- 提交前运行 `Build` 和 `Test`，保持修改范围集中，并为新增类、方法及关键逻辑添加清晰的中文注释。

## 许可证

本项目采用 [Apache License 2.0](LICENSE)。第三方依赖的许可信息随发布包附带。

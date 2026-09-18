# SonettoLauncher

**SonettoHere 的独立桌面启动器** —— 一个 exe 包办：拉起后端与前端、在自带窗口里显示界面、关闭时优雅退出，
并整合「初始化环境」与「检查更新」两个脚本的全部功能。

> 本项目是 [SonettoHere](https://github.com/Miso2233/SonettoHere) 的**第三方配套工具**，
> 由社区成员独立开发维护，不是上游官方组件；运行时不修改上游任何代码。

<p>
  <img alt="platform" src="https://img.shields.io/badge/platform-Windows%2010%2F11%20x64-0078D6">
  <img alt=".NET" src="https://img.shields.io/badge/.NET-8.0-512BD4">
  <img alt="license" src="https://img.shields.io/badge/license-MIT-green">
  <a href="https://github.com/7starsseeker/SonettoLauncherHereToo/releases/latest"><img alt="release" src="https://img.shields.io/github/v/release/7starsseeker/SonettoLauncherHereToo?label=release&color=blue"></a>
</p>

---

## 命名

| 名称 | 含义 |
|---|---|
| **SonettoLauncher** | 本工具（启动器）的名字：exe 文件名、窗口标题、`%LOCALAPPDATA%` 数据目录、Release 附件名都用它 |
| **SonettoLauncherHereToo** | 本仓库（项目）名：呼应上游 **SonettoHere** ——「Sonetto 的 Launcher 也在这儿」 |
| SonettoHere | 上游本体（被启动器管理的那个应用），版本号来自它的 `version.py` |

启动器自身版本从 **1.0** 起（见 `SonettoLauncher.csproj` 的 `<Version>`），与本体版本相互独立：
窗口标题形如 `SonettoLauncher v1.0.4 — SonettoHere v4.0.0`。

## 与上游仓库的关系

| | 仓库 | 说明 |
|---|---|---|
| 上游本体 | <https://github.com/Miso2233/SonettoHere> | SonettoHere 本体（LangGraph ReAct Agent + FastAPI 后端 + Vue 前端） |
| 本仓库 | <https://github.com/7starsseeker/SonettoLauncherHereToo> | 本启动器，本体的**配套工具**，源码本仓库独立维护 |

- 本仓库**不包含**上游源码，也不是上游的分支。它只是一层「外壳」：安装、启动、关闭、初始化、更新。
- 启动器**不修改上游任何文件**：它以命令行方式调用上游的 `main.py`、`setup_guide.py`、`upgrade.py`，
  所有新增内容都在自己的目录里，运行时数据只写 `%LOCALAPPDATA%\SonettoLauncher\`。
- 上游自带的 `start.bat` / `setup.bat` / `upgrade.bat` **保持可用**，与本启动器可以并存（注意别同时启动两套服务即可）。
- 本启动器的源码**不在上游仓库里**，也不会随上游 PR 提交：开发者的工作副本里它只是与项目目录并排的一个
  本地文件夹，并在本地仓库的 `.git/info/exclude` 中被排除（该文件不参与提交），因此上游仓库与任何 PR
  都不会包含它。**本仓库（本页面）是它的唯一发布处。**

## 为什么做这个

上游原本的使用方式是：`start.bat` 拉起两个命令行黑窗口（后端 + 前端），再另外开浏览器标签页访问，
关闭时手动关掉那两个窗口。本项目把这些麻烦收进一个窗口，并且做到真正的**优雅退出**（而不是关窗口就等于强杀进程）。

| 痛点 | 本启动器的做法 |
|---|---|
| 两个黑窗口 + 一个浏览器标签页 | 一个窗口：上面是工具栏，下面内嵌显示界面（WebView2/Chromium） |
| 关窗口 = 强杀进程 | 分级优雅关闭：控制通道 → 控制台信号 → 最后才强杀；关闭后无残留进程 |
| 第一次配置的门槛（装依赖、配 LLM、设称呼） | 环境自检 + 一键初始化，脚本输出与交互都直接在窗口里完成 |
| 更新要手动跑 bat、跑完还得自己重启服务 | 「检查更新」= 停服 → 自动备份配置 → 窗口内执行 → 自动重启服务 |
| 前后端启动顺序容易搞错（Vite 需要后端先生成 Token） | 严格按上游顺序：后端就绪 → 前端就绪 → 打开界面 |

## 功能

- **一键启动**：自动定位上游项目目录（也可拖放/选择），按依赖顺序拉起后端 `:8000` 与前端 `:5173`，就绪后自动打开界面。
- **内嵌界面**：WebView2（Chromium）承载，无浏览器地址栏与标签页；页面外链自动交给系统浏览器打开。
- **优雅退出**：关闭窗口时先优雅停止前端，再优雅停止后端，全程有进度提示，超时才降级强杀。
- **窗口内任务控制台**：执行初始化/更新时**不弹独立控制台窗口**，脚本输出实时滚动，需要输入时直接在窗口里敲；
  支持随时「终止脚本」（连同 pip / npm 等子进程一起收掉）。
- **执行前自动备份**：`setup` 会覆盖 `config/personas` 里的个性文件，因此两个操作前都会自动备份 `config/` 关键文件。
- **环境状态一目了然**：工具栏实时显示后端/前端状态（运行中 / 已停止 / 外部），日志按钮直达日志目录。
- **边界情况处理**：端口被占用时让你选「停止旧服务并重启 / 沿用现有服务 / 取消」；
  应用自身的「重启后端」拉起的进程会被识别为外部服务并接管显示。
- **无黑窗口闪现**：子进程用隐藏控制台启动，全程不会闪出命令行窗口。
- **不留孤儿进程**：所有子进程归入一个 Job Object，启动器即使崩溃或被杀也不会留下残留进程。

## 快速开始

### 方式一：直接下载预编译 exe（推荐）

到 **[Releases 页面](https://github.com/7starsseeker/SonettoLauncherHereToo/releases/latest)** 下载附件（源码仓库里不含 exe）：

| 附件 | 大小 | 运行要求 |
|---|---|---|
| `SonettoLauncher-<版本>-win-x64-self-contained.exe` | 约 63 MB | 无需安装任何 .NET 运行时，双击即用 |
| `SonettoLauncher-<版本>-win-x64-framework-dependent.exe` | 约 1.3 MB | 需已安装 [.NET 8 桌面运行时](https://dotnet.microsoft.com/download/dotnet/8.0) |

使用步骤：

1. 下载 exe（两者任选其一）；
2. 放到 **SonettoHere 项目根目录**（与 `start.bat` 并排）双击即可。
   放在别处也行：启动器会从 exe 所在目录向上查找含 `main.py` 与 `web/package.json` 的目录；找不到时会弹目录选择框（选择会记忆）；
3. 若上游项目还没初始化（缺 `.venv` 或 `web/node_modules`），界面会提示并引导你点「开始初始化」。

共同前置条件：**Microsoft Edge WebView2 Runtime**（Windows 11 与大多数 Windows 10 已预装）。
若缺失，启动器会自动降级为「用 Edge 的无地址栏应用窗口显示界面」，功能不受影响。

### 方式二：从源码构建

需要 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)：

```powershell
git clone https://github.com/7starsseeker/SonettoLauncherHereToo.git
cd SonettoLauncherHereToo

pwsh build.ps1                      # 两种产物都出
pwsh build.ps1 -Only SelfContained  # 只要免运行时的那个
pwsh build.ps1 -Only FrameworkDependent
```

产物落在 `dist\self-contained\` 与 `dist\framework-dependent\`。

## 界面与操作

工具栏（左 → 右）：

| 元素 | 说明 |
|---|---|
| ● 后端 / ● 前端 | 实时状态，三档区分：**运行中**（绿色，外部启动的会标「（外部）」）／**无响应（进程仍在）**（橙色，端口不通但进程还没退出，例如监听被打挂）／**已停止**（红色，进程确实不在了）。每次状态变化都会在 `logs\launcher-*.log` 里留一条记录 |
| 重启服务 | 依次优雅停止后重新拉起 |
| 停止服务 | 优雅停止两端，页面切回状态页，可再一键启动 |
| 初始化环境 | 停服 → 备份配置 → 窗口内执行 `python setup_guide.py`（等价 `setup.bat`）→ 自动启动服务 |
| 检查更新 | 停服 → 备份配置 → 窗口内执行 `.venv\Scripts\python upgrade.py`（等价 `upgrade.bat`）→ 按结局分别处理（见下） |
| 打开日志 | 打开 `%LOCALAPPDATA%\SonettoLauncher\logs` |
| 浏览器打开 | 用系统默认浏览器打开当前界面地址 |

窗口主体是内嵌页面：启动阶段显示进度、步骤与实时日志（含可点击的补救按钮：重试 / 初始化 / 打开日志 / 选择项目目录）。

## 工作原理

### 优雅退出（本项目的核心）

| 服务 | 直接 Ctrl+C 会怎样 | 本启动器的做法 |
|---|---|---|
| 后端 uvicorn | 会优雅关闭（uvicorn 监听 SIGINT，FastAPI lifespan 的 shutdown 分支会执行） | 用自带的 `backend_wrapper.py`：把 `uvicorn.run` 包一层拿到 `Server` 实例，收到 stdin 的 `shutdown`（或 stdin 被关闭 = 启动器已退出）时置 `should_exit = True` —— uvicorn 官方推荐的程序化关闭路径，`main.py` 一行都不用改 |
| 前端 Vite | **硬退**。Vite 5 的 dev server 只监听 SIGTERM，而 Windows 上无法从外部向 node 投递 SIGTERM（Node 会当成直接终止），Ctrl+C 又只会变成 Vite 没监听的 SIGINT | 用自带的 `vite_run.mjs`：以 Vite 官方程序化 API `createServer()` 起服务，收到指令后调用 `server.close()`（关文件监听、通知 HMR、等待在途请求）；`vite.config.ts` 仍由 Vite 自己读取，端口 / 代理 / `__API_TOKEN__` 注入全部照旧 |

停止一个服务时按三级降级执行，只有前两级都失败才会强杀：

1. **控制通道**：向 stdin 写入 `shutdown` 并关闭 stdin（两个 wrapper 都同时监听两者）；
2. **控制台中断信号**：`AttachConsole` + `GenerateConsoleCtrlEvent(CTRL_C_EVENT)`，对后端等价于 uvicorn 的 Ctrl+C（第二次即 force 语义）；
3. **整树强杀**：`taskkill /T /F`。

此外，所有子进程都加入一个带 `KILL_ON_JOB_CLOSE` 的 Job Object：即使启动器自身崩溃或被强杀，也不会在系统里留下孤儿进程。

### 初始化 / 更新为什么必须先停服

`setup_guide.py` 会 `pip install` 动 `.venv`，`upgrade.py` 会 `git pull` 改项目文件 —— 这两件事原本的前提都是「程序没在跑」。
所以启动器按下述顺序执行，并保持界面可见：

1. **优雅停掉前后端**，界面切回状态页并显示「服务已停止」；
2. 停干净之后，才在**窗口内的任务控制台**里执行脚本；
3. 脚本结束后**自动重新拉起服务**；脚本失败则停在状态页，给出「启动服务 / 重试更新」按钮；
4. 若第 1 步之后端口仍在响应（没停干净），会**暂缓执行**并提示「再试一次停止」。

### 检查更新的五种结局

「检查更新」不再只有「成功 / 失败」两种笼统提示，而是按**实际结局**分别呈现，并给出对应的下一步：

| 结局 | 判定依据 | 界面表现 | 服务 |
|---|---|---|---|
| **更新完成** | 退出码 0，且确实拉到了新提交 | 收起任务控制台并自动重启 | 自动重启，工具栏提示「已拉取更新并完成配置迁移」 |
| **已是最新，无需更新** | 退出码 0，输出含「已是最新 / Already up to date」 | 中性色横幅，说明「没有改动任何文件」+「启动服务」按钮 | 保持停止，等你点按钮 |
| **未发现上游更新** | `git pull` 报「no upstream configured / no tracking information」 | 琥珀色横幅，说明是本地分支没有上游、**未做任何改动** +「启动服务 / 重试更新」 | 保持停止，等你点按钮 |
| **更新已终止** | 你点了「终止脚本」（退出码 -2） | 琥珀色横幅，提醒文件可能处于中间状态、建议重试 | 保持停止，等你点按钮 |
| **更新失败** | 其它非 0 退出码（网络、冲突、迁移报错等） | 红色横幅 + 完整输出保留在任务控制台 | 保持停止，等你点按钮 |

后四种都会**保留窗口内的任务控制台**（含脚本完整输出）方便直接回看原因；
「初始化环境」同样区分「完成 / 仍不完整 / 已终止 / 失败」。

### 执行前的自动备份

初始化是有破坏性的：`setup_guide.py` 会用模板**覆盖** `config/personas/USER.md`、`SOUL.md`
（这两类文件在 `.gitignore` 里，git 救不回来）；更新时的迁移脚本也可能改写配置。
因此执行前会把 `config/` 下的关键文件原样拷一份到：

```
%LOCALAPPDATA%\SonettoLauncher\backups\<setup|upgrade>-<时间戳>\
```

包含 `providers.yaml`、`auth_token.yaml`、`path_whitelist.yaml`、`mcp_servers.yaml`、`.env`
以及 `config/personas/` 下的 USER.md / SOUL.md / MEMORY.md 等（只保留最近 10 份）。

还会一并备份目录形式的本地资产 `local_tools/`（本地自用工具的 MCP server 代码）—— 它和
`mcp_servers.yaml` 是一套，只恢复配置却没恢复脚本，本地工具会静默用不了。拷贝时跳过
`__pycache__` 与 `.pyc`（可再生，不值得占备份体积）。
点「初始化环境」时会先弹确认框说明这些影响；若只想装依赖、不想动个性文件，可以在脚本跑到 `[6/6]` 之前点「终止脚本」。

## 文件位置

启动器不在上游项目目录里写任何东西，自己的数据都在 `%LOCALAPPDATA%\SonettoLauncher\`：

| 路径 | 内容 |
|---|---|
| `launcher.config.json` | 项目目录、窗口尺寸、各阶段超时、是否使用 wrapper 等 |
| `wrappers\` | 从 exe 内嵌资源释放出的两个 wrapper（内容一致时不重写） |
| `scripts\` | 初始化 / 更新用的临时 `.cmd` |
| `logs\` | 启动器日志与两端输出；**按会话分文件**（`launcher-*.log` / `backend-*.log` / `frontend-*.log`），保留 7 天。日志只追加不覆盖，历史会话的现场不会被后来的启动冲掉 |
| `backups\` | 执行初始化 / 更新前的配置备份（保留最近 10 份） |
| `webview2\` | WebView2 用户数据（登录态、localStorage 等） |

## 故障排查

| 现象 | 处理 |
|---|---|
| 提示「环境尚未初始化」 | 点「开始初始化」；或先跑一次上游的 `setup.bat` |
| 后端启动超时 | 看 `logs\backend-*.log`；首次启动要加载 MCP / 工具链，可在配置里调大 `BackendReadyTimeoutSeconds` |
| 前端起不来 | 看 `logs\frontend-*.log`；确认 `web\node_modules` 完整（缺就「检查更新」或 `cd web && npm install`） |
| 后端「意外退出」但界面还在 | 多半是应用内的「重启后端」另起了进程，启动器会标记为外部服务；也可直接点「重启服务」 |
| 工具栏显示「后端：无响应（进程仍在）」 | 进程没死，但端口不通 —— 先看 `logs\launcher-*.log` 里那条状态变化记录与 `logs\backend-*.log` 的最后几行，再点「重启服务」即可恢复 |
| 关闭窗口后端口仍被占用 | 看 `logs\launcher-*.log` 的停止记录，会写明是优雅退出还是被强制终止 |
| 内嵌界面空白 | 缺 WebView2 运行时，启动器应已自动降级为独立窗口；也可手动安装 WebView2 Runtime 后重试 |

## 已知限制

- 仅面向 **Windows x64**（依赖 Win32 控制台信号、Job Object、WebView2）。
- 需要上游项目已经能跑（`.venv` 与 `web/node_modules` 就绪）；启动器只负责编排与外壳，不替代上游依赖。
- 「检查更新」依赖上游 `upgrade.py` 的行为（`git pull` 需要分支配置好上游；迁移脚本会照常执行）。
- 任务控制台里个别只认真实 TTY 的命令行工具可能显示得朴素一些（功能不受影响）。
- 若上游把 `web/package.json` 的 `dev` 脚本改成不是纯 `vite` 起法，前端会自动退回 `npm run dev`（此时关闭会退化为硬退，日志里会说明）。

## 开发说明

```
launcher/
├── SonettoLauncher.csproj      # net8.0-windows / WinForms / 单文件发布配置
├── app.manifest                    # PerMonitorV2 DPI + 长路径
├── Program.cs / MainForm.cs        # 入口与主窗口（工具栏、WebView2、状态页、关闭流程）
├── ServiceManager.cs               # 启动编排、健康轮询、端口冲突、外部服务接管
├── ServiceProcess.cs               # 单服务进程：输出捕获、三级优雅停止
├── ScriptRunner.cs                 # 窗口内任务控制台（脚本 stdin/stdout 管道）
├── ConfigBackup.cs                 # 执行前的配置备份
├── Native/                         # Job Object / 控制台信号 / HTTP 探活等 Win32 封装
├── assets/loading.html             # 状态页与任务控制台（内嵌资源）
├── assets/app.ico                  # exe 图标（由上游 logo 渲染，见 tools/make_icon.py）
├── wrappers/                       # 两个 wrapper 脚本（内嵌资源，运行时释放）
├── tools/make_icon.py              # 从上游 logo.svg 生成多尺寸 ico
├── build.ps1                       # 打包（自包含 / 框架依赖）
├── publish-repo.ps1                # 把源码发布到本仓库
└── publish-release.ps1             # 把 exe 作为 Release 附件发布
```

发布新版本（**源码进仓库、exe 进 Release**）：

```powershell
pwsh publish-repo.ps1 -Message "feat: ..."        # 只推源码
pwsh build.ps1                                    # 重新打包两种产物
pwsh publish-release.ps1 -Notes "本次更新说明"     # 用 csproj 版本号自动打 tag 并上传 exe
```

`publish-repo.ps1` 使用临时 git 工作目录（默认 `Q:\TEMP\SonettoLauncherHereToo`），
只把源码拷过去提交推送，不会在上游项目里产生嵌套仓库；仓库里的 `.gitignore` 已排除 `dist/`，
预编译产物不会误提交。`publish-release.ps1` 的 tag 取自 `SonettoLauncher.csproj` 的 `<Version>`，
Release 已存在时会补传/覆盖附件，方便重打包后刷新。

## 更新日志

- **v1.0.4** — 执行初始化 / 更新前的自动备份现在也覆盖**目录形式的本地资产** `local_tools/`
  （本地自用工具的 MCP server 代码）：它与已备份的 `config/mcp_servers.yaml` 是一套，只恢复配置却丢了脚本，
  本地工具会静默用不了。拷贝时跳过 `__pycache__` 与 `.pyc`。
- **v1.0.3** — 统一命名：工具（exe/程序集/产品名/窗口标题/`%LOCALAPPDATA%` 数据目录/Release 附件名）一律叫
  **SonettoLauncher**，仓库名保持 **SonettoLauncherHereToo**；首次启动会把旧目录 `%LOCALAPPDATA%\SonettoHereLauncher`
  的数据迁过来（整体改名失败时逐个复制配置与备份，并在日志里说明）。
- **v1.0.2** — 加上 exe 图标（取自上游项目 `web/src/assets/icons/logo.svg` 的提灯标志，由 `tools/make_icon.py`
  渲染成 16–256px 多尺寸 ico），窗口标题栏与对话框同样使用该图标，启动页标题旁也显示同一标志；
  状态栏三档区分（运行中 / 无响应（进程仍在）/ 已停止）并在状态变化时记入日志；
  修复启动器日志每次启动被覆盖的问题（改为按会话分文件）；修复窗口标题里的版本号一直显示 1.0.0 的问题。
- **v1.0.1** — 「检查更新」按实际结局区分提示：更新完成 / 已是最新（无需更新）/ 未发现上游更新 / 已终止 / 更新失败；
  后四种保留任务控制台输出便于排查；「初始化环境」同步区分完成 / 仍不完整 / 已终止 / 失败。
- **v1.0.0** — 首个版本：一键启动、WebView2 内嵌界面、三级优雅退出与 Job Object 防孤儿、
  窗口内任务控制台（初始化 / 更新，支持交互输入与终止）、执行前自动备份、端口冲突与外部服务处理、两种打包产物。

## 版权与许可

**Copyright (c) 2026 7starsseeker — SonettoHere Launcher**

本项目以 **MIT 许可**发布，与上游 SonettoHere 项目保持一致：

- 你可以自由使用、修改、分发本项目，包括商业用途；
- 分发时需保留本版权声明与许可声明；
- 本项目按「现状」提供，不附带任何形式的担保。

上游 SonettoHere 本体及其相关名称、代码的版权归其原作者所有（Copyright (c) 2026 SonettoHere，MIT 许可）。
本项目为第三方配套工具，与上游作者无隶属关系；使用本启动器前请同时遵守上游项目的许可与说明。

其中 **exe 图标与启动页标志取自上游项目的 logo**（`web/src/assets/icons/logo.svg` 的油灯图案），
同样遵循上游的 MIT 许可；图标由 `tools/make_icon.py` 从该 SVG 渲染生成（深色圆角底板版为默认，
`assets/app-glyph.ico` 是透明背景的原版观感备选）。

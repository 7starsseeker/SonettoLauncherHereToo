using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using SonettoLauncher.Native;

namespace SonettoLauncher;

/// <summary>
/// 启动器主窗口：原生工具栏 + WebView2 显示界面。
/// 负责启动编排、状态显示、setup/upgrade 入口，以及关闭时的优雅停服。
/// </summary>
internal sealed class MainForm : Form
{
    private readonly LauncherConfig _config = LauncherConfig.Load();
    private readonly JobObject _job;
    private readonly LogBus _log;
    private readonly string[] _args;

    private readonly ToolStripStatusLabel _backendLabel = new("● 后端：未启动");
    private readonly ToolStripStatusLabel _frontendLabel = new("● 前端：未启动");
    private readonly ToolStripStatusLabel _hintLabel = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ToolStripButton _restartButton = new("重启服务");
    private readonly ToolStripButton _stopButton = new("停止服务");
    private readonly ToolStripButton _setupButton = new("初始化环境");
    private readonly ToolStripButton _upgradeButton = new("检查更新");
    private readonly ToolStripButton _logsButton = new("打开日志");
    private readonly ToolStripButton _browserButton = new("浏览器打开");

    private readonly WebView2 _webView = new() { Dock = DockStyle.Fill, Visible = false };
    private readonly Panel _fallbackPanel = new() { Dock = DockStyle.Fill, Visible = false, BackColor = Color.FromArgb(20, 22, 28) };
    private readonly TextBox _fallbackText = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = Color.FromArgb(20, 22, 28), ForeColor = Color.Gainsboro, Font = new Font("Microsoft YaHei UI", 10F) };
    private readonly Button _fallbackOpenButton = new() { Text = "在独立窗口打开界面", Anchor = AnchorStyles.Left, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(12, 5, 12, 5), Enabled = false };
    private readonly Panel _overlay = new() { Dock = DockStyle.Fill, Visible = false, BackColor = Color.FromArgb(18, 20, 26) };
    private readonly Label _overlayTitle = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.WhiteSmoke, Font = new Font("Microsoft YaHei UI", 15F) };
    private readonly Label _overlayDetail = new() { Dock = DockStyle.Bottom, Height = 44, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.Silver, Font = new Font("Microsoft YaHei UI", 9F) };
    private readonly ProgressBar _overlayBar = new() { Dock = DockStyle.Bottom, Height = 6, Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 24 };

    private readonly System.Windows.Forms.Timer _statusTimer;
    private readonly List<string> _pendingScripts = new();
    private readonly ToolStrip _toolbar = new();

    private ServiceManager? _services;
    private ScriptRunner? _scriptRunner;
    private bool _terminalOpen;
    private string? _root;
    private string? _appVersion;
    private string? _frontendUrl;
    private bool _webViewInitialized;
    private bool _pageLoaded;
    private bool _showingApp;
    private bool _busy;
    private bool _closing;
    private bool _shutdownComplete;
    private CancellationTokenSource? _startCts;

    public MainForm(string[] args)
    {
        _args = args;

        LauncherPaths.EnsureCreated();
        // 按会话分文件：日志只追加不覆盖，历史会话的现场不会丢（后端/前端日志同样按会话落盘）
        _log = new LogBus(Path.Combine(LauncherPaths.LogDir, $"launcher-{DateTime.Now:yyyyMMdd-HHmmss}.log"));
        _job = new JobObject(_log);
        _log.Line += OnLauncherLogLine;

        Text = $"SonettoLauncher v{Program.Version}";
        Icon = AppIcon.Create();
        Font = new Font("Microsoft YaHei UI", 9F);
        ClientSize = new Size(Math.Max(960, _config.WindowWidth), Math.Max(640, _config.WindowHeight));
        MinimumSize = new Size(900, 600);
        StartPosition = FormStartPosition.CenterScreen;

        BuildToolbar();
        BuildContent();

        _statusTimer = new System.Windows.Forms.Timer { Interval = 2500 };
        _statusTimer.Tick += (_, _) => RefreshStatus();
        _statusTimer.Start();

        Shown += async (_, _) => await InitializeAsync();
        FormClosing += OnFormClosing;
    }

    // ── 界面搭建 ────────────────────────────────────────────

    private void BuildToolbar()
    {
        _backendLabel.ForeColor = Color.Gray;
        _frontendLabel.ForeColor = Color.Gray;
        _hintLabel.ForeColor = Color.DimGray;

        _restartButton.Click += async (_, _) => await RestartServicesAsync();
        _stopButton.Click += async (_, _) => await StopServicesAsync();
        _setupButton.Click += async (_, _) => await RunSetupAsync();
        _upgradeButton.Click += async (_, _) => await RunUpgradeAsync();
        _logsButton.Click += (_, _) => OpenPath(LauncherPaths.LogDir);
        _browserButton.Click += (_, _) => OpenInBrowser(_frontendUrl ?? _services?.FrontendUrl);

        _toolbar.GripStyle = ToolStripGripStyle.Hidden;
        _toolbar.RenderMode = ToolStripRenderMode.System;
        _toolbar.Padding = new Padding(8, 5, 8, 5);
        _toolbar.Dock = DockStyle.Fill;
        _toolbar.AutoSize = true;

        _toolbar.Items.Add(_backendLabel);
        _toolbar.Items.Add(new ToolStripSeparator());
        _toolbar.Items.Add(_frontendLabel);
        _toolbar.Items.Add(new ToolStripSeparator());
        _toolbar.Items.Add(_restartButton);
        _toolbar.Items.Add(_stopButton);
        _toolbar.Items.Add(new ToolStripSeparator());
        _toolbar.Items.Add(_setupButton);
        _toolbar.Items.Add(_upgradeButton);
        _toolbar.Items.Add(new ToolStripSeparator());
        _toolbar.Items.Add(_logsButton);
        _toolbar.Items.Add(_browserButton);
        _toolbar.Items.Add(_hintLabel);
    }

    private void BuildContent()
    {
        var fallbackLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(32) };
        fallbackLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        fallbackLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        fallbackLayout.Controls.Add(_fallbackText, 0, 0);
        fallbackLayout.Controls.Add(_fallbackOpenButton, 0, 1);
        _fallbackPanel.Controls.Add(fallbackLayout);
        _fallbackOpenButton.Click += (_, _) => OpenExternalAppWindow(_frontendUrl ?? _services?.FrontendUrl);

        _overlay.Controls.Add(_overlayTitle);
        _overlay.Controls.Add(_overlayDetail);
        _overlay.Controls.Add(_overlayBar);

        // 工具栏单独占一行，内容区占满其余空间。
        // 不用「Dock=Top 的工具栏 + Dock=Fill 的 WebView2」：WinForms 的停靠按 z 序结算，
        // 填充控件可能先占满整个客户区，导致工具栏压在网页上。
        var contentHost = new Panel { Dock = DockStyle.Fill };
        contentHost.Controls.Add(_webView);
        contentHost.Controls.Add(_fallbackPanel);
        contentHost.Controls.Add(_overlay);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(_toolbar, 0, 0);
        root.Controls.Add(contentHost, 0, 1);

        Controls.Add(root);
    }

    // ── 初始化 ──────────────────────────────────────────────

    private async Task InitializeAsync()
    {
        _log.Write($"[启动器] SonettoLauncher v{Program.Version}，日志：{_log.FilePath}");

        // 数据目录从旧名（SonettoHereLauncher）搬过来的结果，方便排查「配置/备份去哪了」
        if (LauncherPaths.MigrationNote is { Length: > 0 } migrationNote)
        {
            _log.Write($"[启动器] {migrationNote}");
        }

        if (!_webViewInitialized)
        {
            await InitWebViewAsync();
        }

        try
        {
            WrapperResources.ExtractAll(_log);
        }
        catch (Exception ex)
        {
            _log.Write($"[启动器] 释放包装器失败：{ex.Message}");
        }

        var explicitRoot = GetArgument("--root");
        _root = ProjectLocator.Resolve(explicitRoot, _config.ProjectRoot, AppContext.BaseDirectory);

        if (_root is null)
        {
            _log.Write("[启动器] 未自动定位到项目目录");
            ReturnToStatusPage();
            PageBanner(
                "error",
                "没有找到项目目录",
                "启动器需要定位 SonettoHere 项目（包含 main.py 与 web 目录）。"
                + Environment.NewLine + "可以把 exe 放到项目根目录，或点下面的按钮手动选择。",
                new[] { new PageAction("chooseRoot", "选择项目目录", true), new PageAction("quit", "退出") });
            return;
        }

        // 命令行 --root 指定的目录不写进配置：既方便临时指向别的目录，也不会覆盖用户的选择
        if (explicitRoot is null)
        {
            _config.ProjectRoot = _root;
            _config.Save();
        }

        _appVersion = ProjectLocator.ReadAppVersion(_root);
        UpdateWindowTitle();
        ReturnToStatusPage();
        PushStatusPageHeader();
        PageStep("root", "done", $"项目目录：{_root}");
        _log.Write($"[启动器] 项目目录：{_root}（版本 {_appVersion ?? "未知"}）");

        var missing = ProjectLocator.MissingPrerequisites(_root);
        if (missing.Count > 0)
        {
            _log.Write($"[启动器] 环境未初始化，缺少：{string.Join("、", missing)}");
            PageBanner(
                "error",
                "环境尚未初始化",
                "缺少以下内容，需要先跑一次初始化（等同于 setup.bat）：" + Environment.NewLine
                + string.Join(Environment.NewLine, missing.Select(item => "· " + item)),
                new[] { new PageAction("setup", "开始初始化", true), new PageAction("openLogs", "打开日志") });
            return;
        }

        _services = CreateServiceManager();

        var conflict = await _services.DetectPortConflictAsync();
        if (conflict.Any)
        {
            _log.Write($"[启动器] 检测到端口占用：{conflict.Describe().Replace(Environment.NewLine, "；")}");
            using var dialog = new PortConflictDialog(
                $"检测到以下端口已被占用：{Environment.NewLine}{Environment.NewLine}{conflict.Describe()}{Environment.NewLine}{Environment.NewLine}"
                + "「停止旧服务并重启」会让启动器接管这些服务；「沿用现有服务」则只负责显示，不管理它们的生命周期。",
                "端口已被占用");
            dialog.ShowDialog(this);

            switch (dialog.Choice)
            {
                case PortConflictChoice.Cancel:
                    _log.Write("[启动器] 用户取消了启动");
                    PageBanner("error", "已取消启动", "关闭窗口即可退出启动器，或点下面的按钮重新启动。",
                        new[] { new PageAction("retry", "重新启动", true), new PageAction("quit", "退出") });
                    return;

                case PortConflictChoice.Adopt:
                    _services.AdoptExisting(conflict);
                    break;

                case PortConflictChoice.StopAndRestart:
                    SetBusy(true, "正在停止已有服务…");
                    await _services.StopConflictingAsync(conflict);
                    SetBusy(false, string.Empty);
                    break;
            }
        }

        await StartServicesAsync();
    }

    private ServiceManager CreateServiceManager()
    {
        var manager = new ServiceManager(_root!, _config, _log, _job);
        manager.Progress += OnServiceProgress;
        manager.OutputLine += OnServiceOutput;
        manager.BackendVanished += message => _ = HandleServiceVanishedAsync(true, message);
        manager.FrontendVanished += message => _ = HandleServiceVanishedAsync(false, message);
        return manager;
    }

    private async Task InitWebViewAsync()
    {
        _webViewInitialized = true;

        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(null, LauncherPaths.WebViewDataDir);
            await _webView.EnsureCoreWebView2Async(environment);

            var core = _webView.CoreWebView2;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = true;
            core.Settings.IsZoomControlEnabled = true;
            core.Settings.AreBrowserAcceleratorKeysEnabled = true;

            core.NewWindowRequested += OnNewWindowRequested;
            core.WebMessageReceived += OnWebMessageReceived;
            core.NavigationCompleted += OnNavigationCompleted;
            core.ProcessFailed += (_, e) => _log.Write($"[启动器] WebView2 进程异常：{e.ProcessFailedKind}");

            _webView.Visible = true;
            _fallbackPanel.Visible = false;
        }
        catch (Exception ex)
        {
            _webView.Visible = false;
            _fallbackPanel.Visible = true;
            _fallbackText.Text = "内嵌浏览器（WebView2 运行时）不可用，界面将改用独立的 Edge 应用窗口显示。"
                                 + Environment.NewLine + Environment.NewLine
                                 + "如需内嵌显示，请安装 Microsoft Edge WebView2 Runtime 后重启启动器。"
                                 + Environment.NewLine + Environment.NewLine
                                 + $"错误信息：{ex.Message}";
            _log.Write($"[启动器] WebView2 不可用，回退为独立窗口模式：{ex.Message}");
        }
    }

    private string? GetArgument(string name)
    {
        for (var i = 0; i < _args.Length - 1; i++)
        {
            if (string.Equals(_args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return _args[i + 1];
            }
        }

        return null;
    }

    // ── 启动 / 停止 ─────────────────────────────────────────

    private async Task StartServicesAsync()
    {
        if (_services is null || _root is null)
        {
            return;
        }

        _startCts?.Cancel();
        _startCts = new CancellationTokenSource();

        SetBusy(true, "正在启动服务…");
        ReturnToStatusPage();
        PageStep("root", "done", $"项目目录：{_root}");
        PageStep("backend", "active", "正在启动后端（:8000）…");
        PageStep("frontend", "pending", "等待后端就绪后启动前端（:5173）");
        PageStep("ready", "pending", "打开界面");

        var report = await _services.StartAllAsync(_startCts.Token);

        SetBusy(false, string.Empty);

        if (!report.Ok)
        {
            _log.Write($"[启动器] 启动失败：{report.Error}");
            PageStep("backend", "error", "启动失败");
            PageStep("frontend", "error", "未启动");
            PageBanner(
                "error",
                "服务启动失败",
                report.Error ?? "未知错误",
                new[]
                {
                    new PageAction("retry", "重试启动", true),
                    new PageAction("setup", "初始化环境"),
                    new PageAction("openLogs", "打开日志"),
                });
            return;
        }

        _frontendUrl = report.FrontendUrl;
        PageStep("backend", "done", "后端已就绪（:8000）");
        PageStep("frontend", "done", "前端已就绪（:5173）");
        PageStep("ready", "active", "正在打开界面…");

        // 前端必须等后端就绪后再起：vite.config.ts 启动时读取后端生成的 auth_token.yaml
        await Task.Delay(400);

        _log.Write($"[启动器] 打开界面：{_frontendUrl}");

        if (_webView.CoreWebView2 is not null)
        {
            _showingApp = true;
            _webView.CoreWebView2.Navigate(_frontendUrl);
        }
        else
        {
            PageStep("ready", "done", "已在独立窗口打开界面");
            _fallbackOpenButton.Enabled = true;
            OpenExternalAppWindow(_frontendUrl);
        }

        _log.Write("[启动器] 全部服务已就绪");
    }

    private async Task StopServicesAsync()
    {
        if (_services is null || _busy || _shutdownComplete)
        {
            return;
        }

        SetBusy(true, "正在停止服务…");
        ShowOverlay("正在停止服务…", "前端 → 后端，逐级优雅退出");

        var outcomes = await _services.StopAllAsync();

        HideOverlay();
        SetBusy(false, string.Empty);
        RefreshStatus();
        ReturnToStatusPage();

        var stragglers = outcomes.Where(outcome => !outcome.Stopped).ToList();
        if (stragglers.Count > 0)
        {
            PageBanner(
                "error",
                "有进程未能正常停止",
                string.Join(Environment.NewLine, stragglers.Select(item => "· " + item.Detail)),
                new[] { new PageAction("start", "重新启动", true), new PageAction("openLogs", "打开日志") });
        }
        else
        {
            PageBanner(
                "info",
                "服务已停止",
                "前端与后端都已优雅退出（后端日志里可以看到 lifespan shutdown 记录）。",
                new[] { new PageAction("start", "启动服务", true) });
        }
    }

    private async Task RestartServicesAsync()
    {
        if (_services is null || _busy || _shutdownComplete)
        {
            return;
        }

        SetBusy(true, "正在重启服务…");
        ShowOverlay("正在重启服务…", "先优雅停止，再重新启动");

        await _services.StopAllAsync();
        HideOverlay();

        _services.Dispose();
        _services = CreateServiceManager();

        SetBusy(false, string.Empty);
        await StartServicesAsync();
    }

    private async Task HandleServiceVanishedAsync(bool isBackend, string message)
    {
        if (_shutdownComplete || _services is null)
        {
            return;
        }

        var name = isBackend ? "后端" : "前端";
        _log.Write($"[启动器] {message}，检查是否被应用自身重新拉起…");

        for (var i = 0; i < 25; i++)
        {
            await Task.Delay(1000);

            if (_shutdownComplete)
            {
                return;
            }

            var alive = isBackend
                ? await _services.IsBackendAliveAsync()
                : await _services.IsFrontendAliveAsync();
            if (!alive)
            {
                continue;
            }

            if (isBackend && !await Net.HttpRespondsAsync(_services.BackendProbeUrl, 2500))
            {
                continue;
            }

            _log.Write($"[启动器] {name}已由应用自身重新拉起（不受启动器管理），关闭时仍会尝试优雅停止");
            _services.MarkRecoveredExternal(isBackend);
            ReturnToStatusPage();
            PageBanner(
                "info",
                $"{name}已重新启动",
                "应用自身的重启功能拉起了新进程，该进程不受启动器管理；关闭启动器时仍会尝试优雅停止它。",
                new[] { new PageAction("openBrowser", "用浏览器打开") });

            if (_frontendUrl is not null && _webView.CoreWebView2 is not null)
            {
                await Task.Delay(600);
                _showingApp = true;
                _webView.CoreWebView2.Navigate(_frontendUrl);
            }

            RefreshStatus();
            return;
        }

        ReturnToStatusPage();
        PageBanner(
            "error",
            $"{name}已退出",
            message + Environment.NewLine + "可以点下面的按钮重新拉起服务。",
            new[] { new PageAction("start", "启动服务", true), new PageAction("openLogs", "打开日志") });
    }

    // ── setup / upgrade ────────────────────────────────────

    private async Task RunSetupAsync()
    {
        if (_root is null || _busy)
        {
            return;
        }

        if (!ConfirmDestructiveSetup())
        {
            _log.Write("[启动器] 用户取消了初始化");
            return;
        }

        if (await AnyServiceAliveAsync() && !await StopServicesQuietlyAsync("初始化"))
        {
            return;
        }

        var python = ProcessUtil.Which("python");
        if (python is null)
        {
            ReturnToStatusPage();
            PageBanner(
                "error",
                "没有找到 Python",
                "初始化需要系统 Python（3.10+，且能在 PATH 中直接运行 python）。",
                new[] { new PageAction("openLogs", "打开日志") });
            return;
        }

        var backup = ConfigBackup.Create(_root, "setup", _log);
        var backupNote = backup is null ? string.Empty : Environment.NewLine + $"（执行前的配置备份：{backup}）";

        var result = await RunScriptInWindowAsync(
            "环境初始化 — setup_guide.py",
            "初始化",
            python,
            new[] { "-u", "setup_guide.py" },
            _root);

        // 结局分类：被终止 / 失败 / 完成（控制台在失败与终止时保留，方便回看输出）
        if (result.WasTerminated)
        {
            _log.Write("[启动器] 初始化被手动终止");
            PageBanner(
                "warn",
                "初始化已终止",
                "你在脚本执行过程中点了「终止脚本」。如果它当时正在安装依赖或写配置，环境可能处于中间状态。"
                + Environment.NewLine + "建议点「重试初始化」重新跑一遍。"
                + backupNote,
                new[] { new PageAction("setup", "重试初始化", true), new PageAction("openLogs", "打开日志") });
            return;
        }

        if (result.ExitCode != 0)
        {
            _log.Write($"[启动器] 初始化失败（exit={result.ExitCode}）");
            PageBanner(
                "error",
                "初始化失败",
                $"初始化脚本退出码为 {result.ExitCode}，上面的任务控制台里保留了完整输出。"
                + backupNote,
                new[] { new PageAction("setup", "重试初始化", true), new PageAction("openLogs", "打开日志") });
            return;
        }

        var missing = ProjectLocator.MissingPrerequisites(_root);
        if (missing.Count > 0)
        {
            _log.Write($"[启动器] 初始化结束但环境仍不完整：{string.Join("、", missing)}");
            PageBanner(
                "warn",
                "环境仍不完整",
                "初始化脚本正常结束，但仍缺少：" + Environment.NewLine
                + string.Join(Environment.NewLine, missing.Select(item => "· " + item)),
                new[] { new PageAction("setup", "重新初始化", true), new PageAction("openLogs", "打开日志") });
            return;
        }

        CloseTaskConsole();
        await RestartServicesAfterScriptAsync();
        SetHint("初始化完成，服务已启动");

        if (backup is not null)
        {
            _log.Write($"[启动器] 初始化完成；配置备份位于 {backup}");
        }
    }

    /// <summary>
    /// 初始化是有破坏性的（重装依赖 + 覆盖个性文件），执行前明确确认一次。
    /// </summary>
    private bool ConfirmDestructiveSetup()
    {
        var text = "「初始化环境」会做两件事：" + Environment.NewLine
                   + "· 重新安装 Python 依赖（pip install -r requirements.txt）" + Environment.NewLine
                   + "· 用模板覆盖 config/personas 下的 USER.md / SOUL.md 等个性文件" + Environment.NewLine
                   + Environment.NewLine
                   + "执行前会自动备份 config 目录下的配置与个性文件。是否继续？"
                   + Environment.NewLine + Environment.NewLine
                   + "（如果只想装依赖、不想动个性文件，可以在脚本跑到 [6/6] 之前点「终止脚本」。）";

        return MessageBox.Show(
            this,
            text,
            "初始化环境",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2) == DialogResult.Yes;
    }

    /// <summary>
    /// 在窗口内的「任务控制台」里运行脚本：输出实时显示、输入行写回脚本 stdin、可随时终止。
    /// 不开独立控制台窗口，脚本本身无需改动。结束时**不**关闭控制台 ——
    /// 由调用方按结局决定是否收起（失败/终止时保留，方便回看输出）。
    /// </summary>
    private async Task<ScriptRunResult> RunScriptInWindowAsync(
        string consoleTitle,
        string purpose,
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory)
    {
        SetBusy(true, $"正在执行{purpose}…（可直接在窗口里输入）");

        _terminalOpen = true;
        PageCall($"window.sonetto && window.sonetto.termOpen({Js(consoleTitle)})");
        PageBanner(
            "info",
            $"正在执行{purpose}",
            "服务已停止。脚本输出实时显示在下面的任务控制台里；脚本提示输入时，在输入行敲入内容后按 Enter（直接回车＝采用默认值）。",
            Array.Empty<PageAction>());

        var runner = new ScriptRunner(_log);
        _scriptRunner = runner;
        runner.Output += chunk => PageCall($"window.sonetto && window.sonetto.termWrite({Js(chunk)})");

        var started = runner.Start(fileName, arguments, workingDirectory, _job);
        var code = started ? await runner.WaitAsync() : -1;
        var result = new ScriptRunResult(code, runner.RecentOutput(60_000), code == -2);

        SetBusy(false, string.Empty);
        _scriptRunner = null;
        runner.Dispose();

        _log.Write($"[启动器] {purpose}脚本结束（exit={code}）");
        return result;
    }

    /// <summary>收起任务控制台（结局一目了然时用，例如成功）。</summary>
    private void CloseTaskConsole()
    {
        _terminalOpen = false;
        PageCall("window.sonetto && window.sonetto.termClose()");
    }

    /// <summary>脚本结束后恢复服务（只用于「没有改动任何文件」的结局）。</summary>
    private async Task RestartServicesAfterScriptAsync()
    {
        _appVersion = ProjectLocator.ReadAppVersion(_root!);
        UpdateWindowTitle();

        _services?.Dispose();
        _services = CreateServiceManager();
        await StartServicesAsync();
    }

    private void SetHint(string text) => _hintLabel.Text = text;

    /// <summary>脚本运行结果：退出码、输出尾部、是否被手动终止。</summary>
    private sealed record ScriptRunResult(int ExitCode, string Output, bool WasTerminated);

    private void UpdateWindowTitle()
    {
        // 工具名在前、被管理的应用版本在后（工具自己叫 SonettoLauncher，仓库叫 SonettoLauncherHereToo）
        var appPart = string.IsNullOrWhiteSpace(_appVersion) ? string.Empty : $" — SonettoHere {_appVersion}";
        Text = $"SonettoLauncher v{Program.Version}{appPart}";
    }

    private async Task RunUpgradeAsync()
    {
        if (_root is null || _busy)
        {
            return;
        }

        if (!File.Exists(ProjectLocator.VenvPython(_root)))
        {
            ReturnToStatusPage();
            PageBanner(
                "error",
                "还没有初始化环境",
                "检查更新需要 .venv 虚拟环境，请先执行初始化。",
                new[] { new PageAction("setup", "开始初始化", true) });
            return;
        }

        if (await AnyServiceAliveAsync() && !await StopServicesQuietlyAsync("更新"))
        {
            return;
        }

        // 迁移脚本可能改写配置，更新前先备份一份
        var upgradeBackup = ConfigBackup.Create(_root, "upgrade", _log);
        var backupNote = upgradeBackup is null ? string.Empty : Environment.NewLine + $"（执行前的配置备份：{upgradeBackup}）";

        var result = await RunScriptInWindowAsync(
            "检查更新 — upgrade.py",
            "更新",
            ProjectLocator.VenvPython(_root),
            new[] { "-u", "upgrade.py" },
            _root);

        // ── 结局分类：已终止 / 已是最新 / 未发现上游 / 更新失败 / 更新完成 ──
        if (result.WasTerminated)
        {
            _log.Write("[启动器] 更新检查被手动终止");
            PageBanner(
                "warn",
                "更新已终止",
                "你在脚本执行过程中点了「终止脚本」，更新没有跑完，文件可能处于中间状态。"
                + Environment.NewLine + "建议点「重试更新」重新执行，或直接启动服务。"
                + backupNote,
                new[] { new PageAction("upgrade", "重试更新", true), new PageAction("start", "启动服务") });
            return;
        }

        if (result.ExitCode == 0 && IndicatesUpToDate(result.Output))
        {
            _log.Write("[启动器] 检查更新：上游没有新提交（已是最新，未做任何改动）");
            PageBanner(
                "info",
                "已是最新版本，无需更新",
                "上游没有新的提交，依赖与配置迁移也都已就绪 —— 本次检查没有改动任何文件。"
                + Environment.NewLine + "服务当前处于停止状态，点下面的按钮即可启动。",
                new[] { new PageAction("start", "启动服务", true) });
            return;
        }

        if (result.ExitCode == 0)
        {
            CloseTaskConsole();
            _log.Write("[启动器] 更新完成，正在重启服务");
            await RestartServicesAfterScriptAsync();
            SetHint("检查更新：已拉取更新并完成配置迁移，服务已重启");

            if (upgradeBackup is not null)
            {
                _log.Write($"[启动器] 配置备份位于 {upgradeBackup}");
            }

            return;
        }

        if (IndicatesNoUpstream(result.Output))
        {
            _log.Write("[启动器] 检查更新：当前分支没有配置上游，git pull 无法执行（未做任何改动）");
            PageBanner(
                "warn",
                "未发现上游更新",
                "当前分支没有配置上游仓库，`git pull` 无法执行 —— 所以既没有拉取到更新，也没有改动任何文件。"
                + Environment.NewLine + "这通常出现在本地功能分支上（没有对应的远程分支）。"
                + Environment.NewLine + "服务当前处于停止状态，点下面的按钮即可启动。"
                + backupNote,
                new[] { new PageAction("start", "启动服务", true), new PageAction("upgrade", "重试更新") });
            return;
        }

        _log.Write($"[启动器] 更新失败（exit={result.ExitCode}）");
        PageBanner(
            "error",
            "更新失败",
            $"升级脚本退出码为 {result.ExitCode}，上面的任务控制台里保留了完整输出。服务当前处于停止状态。"
            + backupNote,
            new[] { new PageAction("start", "启动服务", true), new PageAction("upgrade", "重试更新") });
    }

    /// <summary>upgrade.py 的「确实拉到了新代码」标记。</summary>
    private static bool IndicatesPulledNewCode(string output) => output.Contains("已拉取更新");

    /// <summary>upgrade.py 的「上游无新提交」标记（排除掉确实拉到了新代码的情况）。</summary>
    private static bool IndicatesUpToDate(string output)
    {
        if (IndicatesPulledNewCode(output))
        {
            return false;
        }

        return output.Contains("已是最新") || output.Contains("Already up to date");
    }

    /// <summary>git pull 因分支没有上游而失败 —— 未做任何改动，不算真正的失败。</summary>
    private static bool IndicatesNoUpstream(string output) =>
        output.Contains("no upstream configured")
        || output.Contains("no tracking information")
        || output.Contains("没有跟踪信息");

    /// <summary>
    /// 为 setup/upgrade 准备干净环境：这两个脚本只有在「程序没在跑」时才该执行
    /// （pip 安装会动 .venv、git pull 会改文件），所以先优雅停服、再让用户看到服务已停止。
    /// </summary>
    private async Task<bool> StopServicesQuietlyAsync(string purpose)
    {
        if (_services is null)
        {
            return true;
        }

        SetBusy(true, "正在停止服务…");
        ShowOverlay("正在停止服务…", $"执行{purpose}前先停掉前后端");
        await _services.StopAllAsync();
        HideOverlay();
        SetBusy(false, string.Empty);
        RefreshStatus();
        ReturnToStatusPage();

        PageStep("backend", "pending", "已停止（等待服务重启）");
        PageStep("frontend", "pending", "已停止（等待服务重启）");
        PageStep("ready", "pending", $"执行{purpose}中");

        var stillAlive = await AnyServiceAliveAsync();
        if (stillAlive)
        {
            PageBanner(
                "error",
                "服务没有完全停止",
                $"仍有进程在响应端口。{purpose}会改动 .venv / 项目文件，建议先彻底停止服务再执行。",
                new[] { new PageAction("stop", "再试一次停止", true) });

            _log.Write($"[启动器] {purpose}前服务未完全停止，已暂缓执行");
            return false;
        }

        PageBanner(
            "info",
            $"服务已停止，正在执行{purpose}",
            "程序已退出运行状态（等同关闭 start.bat 的两个窗口）。"
            + Environment.NewLine + "请在打开的控制台窗口中完成操作；窗口关闭后启动器会自动重新拉起前后端。",
            Array.Empty<PageAction>());

        return true;
    }

    // ── WebView2 事件 ───────────────────────────────────────

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        _pageLoaded = true;
        FlushPendingScripts();
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        // 页面里的 window.open / target=_blank（例如搜索结果外链）交给系统浏览器
        e.Handled = true;
        _log.Write($"[启动器] 外部链接交给系统浏览器：{e.Uri}");
        OpenInBrowser(e.Uri);
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string command;
        string text;

        try
        {
            using var document = JsonDocument.Parse(e.WebMessageAsJson);
            command = document.RootElement.TryGetProperty("cmd", out var cmd) ? cmd.GetString() ?? string.Empty : string.Empty;
            text = document.RootElement.TryGetProperty("text", out var payload) ? payload.GetString() ?? string.Empty : string.Empty;
        }
        catch
        {
            return;
        }

        if (command is "termInput" or "termStop")
        {
            // 脚本输入不在日志里刷屏
            if (command == "termInput")
            {
                _scriptRunner?.WriteInput(text);
            }
            else
            {
                _scriptRunner?.Terminate();
            }

            return;
        }

        _log.Write($"[启动器] 界面指令：{command}");

        switch (command)
        {
            case "start":
            case "retry":
                _ = StartServicesAsync();
                break;
            case "stop":
                _ = StopServicesAsync();
                break;
            case "setup":
                _ = RunSetupAsync();
                break;
            case "upgrade":
                _ = RunUpgradeAsync();
                break;
            case "openLogs":
                OpenPath(LauncherPaths.LogDir);
                break;
            case "openBrowser":
                OpenInBrowser(_frontendUrl ?? _services?.FrontendUrl);
                break;
            case "chooseRoot":
                ChooseProjectRoot();
                break;
            case "quit":
                Close();
                break;
        }
    }

    private void ChooseProjectRoot()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "选择 SonettoHere 项目根目录（包含 main.py 与 web 文件夹）",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false,
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        if (!ProjectLocator.IsProjectRoot(dialog.SelectedPath))
        {
            MessageBox.Show(
                this,
                "该目录里没有找到 main.py 与 web/package.json，请重新选择。",
                "SonettoLauncher",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        _config.ProjectRoot = dialog.SelectedPath;
        _config.Save();
        _log.Write($"[启动器] 用户选择了项目目录：{dialog.SelectedPath}");

        _services?.Dispose();
        _services = null;
        _ = InitializeAsync();
    }

    // ── 状态与日志 ──────────────────────────────────────────

    /// <summary>两端任意一端仍在响应即视为「有服务在跑」。</summary>
    private async Task<bool> AnyServiceAliveAsync()
    {
        var services = _services;
        if (services is null)
        {
            return false;
        }

        return await services.IsBackendAliveAsync() || await services.IsFrontendAliveAsync();
    }

    private void RefreshStatus()
    {
        if (_services is null || _shutdownComplete || _statusProbeBusy)
        {
            return;
        }

        _statusProbeBusy = true;
        _ = RefreshStatusAsync();
    }

    private async Task RefreshStatusAsync()
    {
        try
        {
            var services = _services;
            if (services is null)
            {
                return;
            }

            var backendAlive = await services.IsBackendAliveAsync();
            var frontendAlive = await services.IsFrontendAliveAsync();

            if (services != _services || _shutdownComplete)
            {
                return;
            }

            ApplyServiceStatus(_backendLabel, "后端", services.Backend, backendAlive);
            ApplyServiceStatus(_frontendLabel, "前端", services.Frontend, frontendAlive);
        }
        finally
        {
            _statusProbeBusy = false;
        }
    }

    /// <summary>
    /// 刷新单个状态项，并在状态**变化**时记一条日志 ——
    /// 以后再出现「后端未运行」这类反馈，日志里能直接看到是何时、从什么状态变成的。
    /// </summary>
    private void ApplyServiceStatus(ToolStripStatusLabel label, string name, ServiceProcess service, bool responding)
    {
        var text = service.DescribeRuntime(responding);
        var previous = _statusText.TryGetValue(name, out var last) ? last : string.Empty;

        if (previous != text)
        {
            _log.Write($"[启动器] {name}状态：{(previous.Length == 0 ? "（初始）" : previous)} → {text}"
                       + (responding ? string.Empty : $"（PID {service.Pid}，进程{(service.HasExited ? "已退出" : "仍在")}）"));
            _statusText[name] = text;
        }

        label.Text = $"● {name}：{text}";
        label.ForeColor = responding
            ? Color.SeaGreen
            : service.IsManagedAndAlive ? Color.DarkOrange : Color.Firebrick;
    }

    private bool _statusProbeBusy;
    private readonly Dictionary<string, string> _statusText = new();

    private void SetBusy(bool busy, string hint)
    {
        _busy = busy;
        _restartButton.Enabled = !busy;
        _stopButton.Enabled = !busy;
        _setupButton.Enabled = !busy;
        _upgradeButton.Enabled = !busy;
        _hintLabel.Text = hint;
        Cursor = busy ? Cursors.AppStarting : Cursors.Default;
    }

    private void ShowOverlay(string title, string detail) => Ui(() =>
    {
        _overlayTitle.Text = title;
        _overlayDetail.Text = detail;
        _overlay.Visible = true;
        _overlay.BringToFront();
    });

    private void HideOverlay() => Ui(() => _overlay.Visible = false);

    private void SetOverlayDetail(string detail) => Ui(() => _overlayDetail.Text = detail);

    private void OnLauncherLogLine(string line) => PageLog(line);

    private void OnServiceOutput(string line) => PageLog(line);

    private void OnServiceProgress(string text)
    {
        _log.Write($"[启动器] {text}");
        PageCall($"window.sonetto && window.sonetto.setHeader(null, {Js(text)})");
    }

    // ── 与加载页通信 ────────────────────────────────────────

    private void Ui(Action action)
    {
        if (IsDisposed || Disposing)
        {
            return;
        }

        try
        {
            if (InvokeRequired)
            {
                BeginInvoke(action);
            }
            else
            {
                action();
            }
        }
        catch (ObjectDisposedException)
        {
            // 窗口已销毁
        }
        catch (InvalidOperationException)
        {
            // 句柄尚未创建或已释放
        }
    }

    private void ReturnToStatusPage()
    {
        if (_webView.CoreWebView2 is null)
        {
            return;
        }

        _showingApp = false;
        _pageLoaded = false;
        _terminalOpen = false;   // 全新页面里没有任务控制台
        _webView.CoreWebView2.NavigateToString(WrapperResources.ReadLoadingPage());

        // 重新导航后页面是全新的，表头与项目目录这一步要补回来（会排队到页面加载完再执行）
        PushStatusPageHeader();
        if (_root is not null)
        {
            PageStep("root", "done", $"项目目录：{_root}");
        }
    }

    private void PushStatusPageHeader()
    {
        PageCall($"window.sonetto && window.sonetto.setHeader({Js($"SonettoHere {_appVersion ?? string.Empty}".Trim())}, {Js(_root ?? string.Empty)})");
    }

    private void PageCall(string script) => Ui(() =>
    {
        if (_showingApp || _webView.CoreWebView2 is null)
        {
            return;
        }

        if (!_pageLoaded)
        {
            if (_pendingScripts.Count < 300)
            {
                _pendingScripts.Add(script);
            }

            return;
        }

        _ = _webView.CoreWebView2.ExecuteScriptAsync(script);
    });

    private void FlushPendingScripts()
    {
        if (_webView.CoreWebView2 is null || _showingApp)
        {
            _pendingScripts.Clear();
            return;
        }

        foreach (var script in _pendingScripts.ToList())
        {
            _ = _webView.CoreWebView2.ExecuteScriptAsync(script);
        }

        _pendingScripts.Clear();
    }

    private void PageStep(string step, string state, string text) =>
        PageCall($"window.sonetto && window.sonetto.step({Js(step)}, {Js(state)}, {Js(text)})");

    private void PageLog(string line)
    {
        if (!_webViewInitialized || _fallbackPanel.Visible)
        {
            Ui(() => _fallbackText.AppendText(line + Environment.NewLine));
            return;
        }

        // 任务控制台占据主视野时，不再往日志框里塞内容，避免两处刷屏互相干扰
        if (_terminalOpen)
        {
            return;
        }

        PageCall($"window.sonetto && window.sonetto.log({Js(line)})");
    }

    private void PageBanner(string kind, string title, string text, IEnumerable<PageAction> actions)
    {
        var payload = JsonSerializer.Serialize(actions.Select(action => new
        {
            id = action.Id,
            label = action.Label,
            primary = action.Primary,
        }));

        PageCall($"window.sonetto && window.sonetto.banner({Js(kind)}, {Js(title)}, {Js(text)}, {payload})");
    }

    private static string Js(string value) => JsonSerializer.Serialize(value);

    private sealed record PageAction(string Id, string Label, bool Primary = false);

    // ── 外部打开 ────────────────────────────────────────────

    private void OpenPath(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _log.Write($"[启动器] 打开 {path} 失败：{ex.Message}");
        }
    }

    private void OpenInBrowser(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _log.Write($"[启动器] 打开浏览器失败：{ex.Message}");
        }
    }

    private void OpenExternalAppWindow(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        var edge = ProcessUtil.FindEdge();
        if (edge is null)
        {
            OpenInBrowser(url);
            return;
        }

        try
        {
            var psi = new ProcessStartInfo(edge)
            {
                UseShellExecute = false,
                Arguments = $"--app={url} --user-data-dir=\"{LauncherPaths.EdgeProfileDir}\" --window-size={_config.WindowWidth},{Math.Max(600, _config.WindowHeight - 160)}",
            };

            Process.Start(psi);
            _log.Write($"[启动器] 已在独立窗口打开：{url}");
        }
        catch (Exception ex)
        {
            _log.Write($"[启动器] 独立窗口打开失败，改用默认浏览器：{ex.Message}");
            OpenInBrowser(url);
        }
    }

    // ── 关闭 ────────────────────────────────────────────────

    private async void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_shutdownComplete)
        {
            return;
        }

        e.Cancel = true;

        if (_closing)
        {
            return;
        }

        _closing = true;

        try
        {
            var servicesRunning = await AnyServiceAliveAsync();

            if (servicesRunning && _config.ConfirmOnClose)
            {
                using var dialog = new ConfirmCloseDialog(
                    "关闭窗口会优雅停止 SonettoHere 的前后端服务（等同关闭 start.bat 的两个窗口，但走的是优雅退出）。");
                if (dialog.ShowDialog(this) != DialogResult.OK)
                {
                    _closing = false;
                    return;
                }

                if (dialog.DontAskAgain)
                {
                    _config.ConfirmOnClose = false;
                    _config.Save();
                }
            }

            _statusTimer.Stop();
            ShowOverlay("正在停止服务…", "前端 → 后端，逐级优雅退出");
            await ShutdownAsync();
        }
        catch (Exception ex)
        {
            _log.Write($"[启动器] 关闭流程出错：{ex}");
        }

        _shutdownComplete = true;
        Close();
    }

    private async Task ShutdownAsync()
    {
        try
        {
            if (_scriptRunner is { IsRunning: true })
            {
                _log.Write("[启动器] 关闭时终止仍在运行的脚本");
                _scriptRunner.Terminate();
            }

            if (_services is not null)
            {
                _services.Progress += SetOverlayDetail;
                await _services.StopAllAsync();
            }
        }
        catch (Exception ex)
        {
            _log.Write($"[启动器] 停止服务时出错：{ex.Message}");
        }
        finally
        {
            // 兜底：job 里若还有漏网进程（例如强杀失败的），整树收掉
            _job.Terminate();
            _services?.Dispose();
            _log.Write("[启动器] 已退出");
            _log.Dispose();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _statusTimer.Dispose();
            _job.Dispose();
        }

        base.Dispose(disposing);
    }
}

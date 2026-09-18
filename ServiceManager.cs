using System.Diagnostics;
using System.IO;
using System.Text.Json;
using SonettoHere.Launcher.Native;

namespace SonettoHere.Launcher;

internal sealed class StartReport
{
    public bool Ok { get; init; }
    public string? Error { get; init; }
    public string? FrontendUrl { get; init; }
}

internal enum PortConflictChoice
{
    /// <summary>停掉已有服务后重新启动（受管）。</summary>
    StopAndRestart,

    /// <summary>沿用已在运行的服务，只负责显示（不管生命周期）。</summary>
    Adopt,

    /// <summary>取消启动。</summary>
    Cancel,
}

internal sealed class PortConflict
{
    public bool BackendBusy { get; init; }
    public int? BackendPid { get; init; }
    public bool BackendLooksLikeApp { get; init; }
    public bool FrontendBusy { get; init; }
    public int? FrontendPid { get; init; }

    public bool Any => BackendBusy || FrontendBusy;

    public string Describe()
    {
        var lines = new List<string>();
        if (BackendBusy)
        {
            lines.Add($"· 后端端口 8000 已被占用（PID {BackendPid?.ToString() ?? "?"}"
                      + (BackendLooksLikeApp ? "，健康检查通过，像是 SonettoHere 后端" : "，未通过健康检查") + "）");
        }

        if (FrontendBusy)
        {
            lines.Add($"· 前端端口 5173 已被占用（PID {FrontendPid?.ToString() ?? "?"}）");
        }

        return string.Join(Environment.NewLine, lines);
    }
}

/// <summary>
/// 后端 + 前端的启动编排：定位 → 环境自检 → 顺序拉起 → 就绪轮询 → 优雅停止。
/// </summary>
internal sealed class ServiceManager : IDisposable
{
    public const int BackendPort = 8000;
    public const int FrontendPort = 5173;

    private readonly LauncherConfig _config;
    private readonly LogBus _log;
    private readonly JobObject _job;
    private readonly string _root;
    private readonly string _stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");

    private string? _frontendUrlFromWrapper;

    public ServiceManager(string root, LauncherConfig config, LogBus log, JobObject job)
    {
        _root = root;
        _config = config;
        _log = log;
        _job = job;

        CleanupOldLogs();

        Backend = new ServiceProcess("后端", log, Path.Combine(LauncherPaths.LogDir, $"backend-{_stamp}.log"));
        Frontend = new ServiceProcess("前端", log, Path.Combine(LauncherPaths.LogDir, $"frontend-{_stamp}.log"));

        Backend.Output += (service, line) => OnOutput(service, line);
        Frontend.Output += (service, line) => OnOutput(service, line);
        Backend.Exited += (service, intentional) => OnExited(service, intentional);
        Frontend.Exited += (service, intentional) => OnExited(service, intentional);
    }

    public ServiceProcess Backend { get; }

    public ServiceProcess Frontend { get; }

    public bool BackendReady { get; private set; }

    public bool FrontendReady { get; private set; }

    public bool BackendAdopted { get; private set; }

    public bool FrontendAdopted { get; private set; }

    public string BackendBaseUrl => $"http://127.0.0.1:{BackendPort}";

    /// <summary>轻量探活地址：根路径不经 /api 中间件，也不会触发任何 LLM 检查。</summary>
    public string BackendProbeUrl => $"{BackendBaseUrl}/";

    public string BackendHealthUrl => $"{BackendBaseUrl}/api/health";

    public string FrontendUrl =>
        _frontendUrlFromWrapper
        ?? _config.LastFrontendUrl
        ?? $"http://localhost:{FrontendPort}/";

    /// <summary>人类可读的进度文本。</summary>
    public event Action<string>? Progress;

    /// <summary>带服务前缀的输出行（给 UI 日志面板）。</summary>
    public event Action<string>? OutputLine;

    public event Action<string>? BackendVanished;

    public event Action<string>? FrontendVanished;

    // ── 端口冲突检测 ────────────────────────────────────────

    public async Task<PortConflict> DetectPortConflictAsync()
    {
        var backendBusy = await Net.HttpRespondsAsync($"{BackendBaseUrl}/", 1500).ConfigureAwait(false);
        var frontendBusy = await Net.HttpRespondsAsync($"http://localhost:{FrontendPort}/", 1500).ConfigureAwait(false);

        var backendLooksLikeApp = backendBusy && await Net.LooksLikeSonettoBackendAsync(BackendBaseUrl).ConfigureAwait(false);

        return new PortConflict
        {
            BackendBusy = backendBusy,
            BackendPid = backendBusy ? Net.FindPidListeningOn(BackendPort) : null,
            BackendLooksLikeApp = backendLooksLikeApp,
            FrontendBusy = frontendBusy,
            FrontendPid = frontendBusy ? Net.FindPidListeningOn(FrontendPort) : null,
        };
    }

    /// <summary>停掉占用端口的已有服务（先 Ctrl+C 优雅，再兜底强杀）。</summary>
    public async Task StopConflictingAsync(PortConflict conflict)
    {
        if (conflict.FrontendBusy && conflict.FrontendPid is { } frontendPid)
        {
            await StopExternalAsync("前端", FrontendPort, frontendPid).ConfigureAwait(false);
        }

        if (conflict.BackendBusy && conflict.BackendPid is { } backendPid)
        {
            await StopExternalAsync("后端", BackendPort, backendPid).ConfigureAwait(false);
        }
    }

    private async Task StopExternalAsync(string name, int port, int pid)
    {
        _log.Write($"[启动器] 停掉占用 {port} 端口的已有{name}进程（PID {pid}）");
        ConsoleSignal.SendCtrlC(pid);

        for (var i = 0; i < 40; i++)
        {
            if (!await Net.HttpRespondsAsync($"http://127.0.0.1:{port}/", 1200).ConfigureAwait(false))
            {
                _log.Write($"[启动器] 端口 {port} 已释放");
                return;
            }

            await Task.Delay(250).ConfigureAwait(false);
        }

        _log.Write($"[启动器] 端口 {port} 仍被占用，强制结束 PID {pid}");
        ProcessUtil.KillTree(pid, _log);
        await Task.Delay(800).ConfigureAwait(false);
    }

    /// <summary>只显示、不管理：把已在运行的服务登记为「外部服务」。</summary>
    public void AdoptExisting(PortConflict conflict)
    {
        if (conflict.BackendBusy)
        {
            BackendAdopted = true;
            BackendReady = true;
            Backend.MarkExternal(conflict.BackendPid ?? 0);
            _log.Write("[启动器] 沿用已在运行的后端（不由本启动器管理）");
        }

        if (conflict.FrontendBusy)
        {
            FrontendAdopted = true;
            FrontendReady = true;
            Frontend.MarkExternal(conflict.FrontendPid ?? 0);
            _log.Write("[启动器] 沿用已在运行的前端（不由本启动器管理）");
        }
    }

    /// <summary>
    /// 后端被应用自身的 /api/restart 重新拉起（不再是启动器的子进程）时登记为外部服务，
    /// 界面继续可用；关闭时仍会尝试按端口优雅停止它。
    /// </summary>
    public void MarkRecoveredExternal(bool isBackend)
    {
        if (isBackend)
        {
            BackendAdopted = true;
            BackendReady = true;
            Backend.MarkExternal(Net.FindPidListeningOn(BackendPort) ?? 0);
        }
        else
        {
            FrontendAdopted = true;
            FrontendReady = true;
            Frontend.MarkExternal(Net.FindPidListeningOn(FrontendPort) ?? 0);
        }
    }

    // ── 启动 ────────────────────────────────────────────────

    public async Task<StartReport> StartAllAsync(CancellationToken cancellationToken)
    {
        if (!BackendReady)
        {
            Progress?.Invoke("正在启动后端（:8000）…");
            var backendError = await StartBackendAsync(cancellationToken).ConfigureAwait(false);
            if (backendError is not null)
            {
                return new StartReport { Ok = false, Error = backendError };
            }
        }

        if (!FrontendReady)
        {
            Progress?.Invoke("正在启动前端（:5173）…");
            var frontendError = await StartFrontendAsync(cancellationToken).ConfigureAwait(false);
            if (frontendError is not null)
            {
                return new StartReport { Ok = false, Error = frontendError };
            }
        }

        if (FrontendUrl is { } url)
        {
            _config.LastFrontendUrl = url;
            _config.Save();
        }

        return new StartReport { Ok = true, FrontendUrl = FrontendUrl };
    }

    private async Task<string?> StartBackendAsync(CancellationToken cancellationToken)
    {
        var useWrapper = _config.UseWrappers;
        string? lastError = null;

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var spec = BuildBackendSpec(useWrapper);
            Backend.Start(spec, _job);

            var deadline = DateTime.UtcNow.AddSeconds(Math.Max(30, _config.BackendReadyTimeoutSeconds));
            var exitedEarly = false;

            while (DateTime.UtcNow < deadline)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return "启动已取消";
                }

                if (Backend.HasExited)
                {
                    exitedEarly = true;
                    lastError = $"后端进程启动后立即退出（exit={Backend.ExitCode}）。最后输出：{Environment.NewLine}{Backend.RecentOutput()}";
                    _log.Write($"[启动器] 后端提前退出（exit={Backend.ExitCode}）");
                    break;
                }

                if (await Net.HttpRespondsAsync(BackendProbeUrl, 1500).ConfigureAwait(false))
                {
                    BackendReady = true;
                    Progress?.Invoke("后端已就绪（:8000 已响应）");
                    _log.Write("[启动器] 后端就绪");
                    _ = LogHealthReportAsync();
                    return null;
                }

                await Task.Delay(800, cancellationToken).ConfigureAwait(false);
            }

            if (!exitedEarly)
            {
                lastError = $"后端在 {_config.BackendReadyTimeoutSeconds} 秒内未就绪（端口 {BackendPort} 无健康响应）。"
                            + $"最后输出：{Environment.NewLine}{Backend.RecentOutput()}";
                return lastError;
            }

            await Backend.StopAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

            if (useWrapper)
            {
                // wrapper 起不来（例如项目日后改了入口）：回退直启，关闭时改用控制台信号
                _log.Write("[启动器] 包装器启动失败，回退为直接运行 main.py");
                Progress?.Invoke("后端包装器不可用，改用直接启动…");
                useWrapper = false;
                continue;
            }

            return lastError;
        }

        return lastError ?? "后端启动失败";
    }

    private async Task<string?> StartFrontendAsync(CancellationToken cancellationToken)
    {
        var useWrapper = _config.UseWrappers;
        string? lastError = null;

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var spec = BuildFrontendSpec(useWrapper);
            _frontendUrlFromWrapper = null;
            Frontend.Start(spec, _job);

            var deadline = DateTime.UtcNow.AddSeconds(Math.Max(30, _config.FrontendReadyTimeoutSeconds));
            var exitedEarly = false;

            while (DateTime.UtcNow < deadline)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return "启动已取消";
                }

                if (Frontend.HasExited)
                {
                    exitedEarly = true;
                    lastError = $"前端进程启动后立即退出（exit={Frontend.ExitCode}）。最后输出：{Environment.NewLine}{Frontend.RecentOutput()}";
                    _log.Write($"[启动器] 前端提前退出（exit={Frontend.ExitCode}）");
                    break;
                }

                if (await Net.HttpRespondsAsync(FrontendUrl, 5000).ConfigureAwait(false))
                {
                    FrontendReady = true;
                    Progress?.Invoke("前端已就绪");
                    _log.Write($"[启动器] 前端就绪：{FrontendUrl}");
                    return null;
                }

                await Task.Delay(700, cancellationToken).ConfigureAwait(false);
            }

            if (!exitedEarly)
            {
                lastError = $"前端在 {_config.FrontendReadyTimeoutSeconds} 秒内未就绪（端口 {FrontendPort} 无响应）。"
                            + $"最后输出：{Environment.NewLine}{Frontend.RecentOutput()}";
                return lastError;
            }

            await Frontend.StopAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

            if (useWrapper)
            {
                _log.Write("[启动器] 前端包装器启动失败，回退为 npm run dev");
                Progress?.Invoke("前端包装器不可用，改用 npm run dev…");
                useWrapper = false;
                continue;
            }

            return lastError;
        }

        return lastError ?? "前端启动失败";
    }

    private ServiceLaunchSpec BuildBackendSpec(bool useWrapper)
    {
        if (useWrapper && File.Exists(LauncherPaths.BackendWrapper))
        {
            return new ServiceLaunchSpec
            {
                Mode = "包装器",
                FileName = ProjectLocator.VenvPython(_root),
                Arguments = new[] { "-u", LauncherPaths.BackendWrapper, _root },
                WorkingDirectory = _root,
                UsesControlChannel = true,
            };
        }

        return new ServiceLaunchSpec
        {
            Mode = "直启",
            FileName = ProjectLocator.VenvPython(_root),
            Arguments = new[] { "-u", "main.py", "web" },
            WorkingDirectory = _root,
            UsesControlChannel = false,
        };
    }

    private ServiceLaunchSpec BuildFrontendSpec(bool useWrapper)
    {
        var node = ProcessUtil.Which("node");
        var viteModule = ProjectLocator.ViteModuleEntry(_root);

        if (useWrapper
            && node is not null
            && File.Exists(viteModule)
            && File.Exists(LauncherPaths.ViteWrapper))
        {
            return new ServiceLaunchSpec
            {
                Mode = "包装器",
                FileName = node,
                Arguments = new[] { LauncherPaths.ViteWrapper },
                WorkingDirectory = ProjectLocator.WebDir(_root),
                Environment = new Dictionary<string, string>
                {
                    ["SONETTO_VITE_MODULE"] = new Uri(viteModule).AbsoluteUri,
                },
                UsesControlChannel = true,
            };
        }

        // 回退：npm run dev —— Vite 未监听 SIGINT，关闭时只能硬退（日志中会说明）
        return new ServiceLaunchSpec
        {
            Mode = "直启（npm run dev）",
            FileName = "cmd.exe",
            Arguments = new[] { "/c", "npm", "run", "dev" },
            WorkingDirectory = ProjectLocator.WebDir(_root),
            UsesControlChannel = false,
        };
    }

    // ── 停止 ────────────────────────────────────────────────

    public async Task<IReadOnlyList<StopOutcome>> StopAllAsync()
    {
        var outcomes = new List<StopOutcome>();
        var graceful = TimeSpan.FromSeconds(Math.Max(5, _config.GracefulShutdownSeconds));

        // 先丢掉自己的 keep-alive 连接，别让它拖着后端的优雅关闭
        Net.ResetConnections();

        Progress?.Invoke("正在停止前端…");
        if (FrontendAdopted)
        {
            _log.Write("[启动器] 前端为外部服务，尝试按端口优雅停止");
            if (Net.FindPidListeningOn(FrontendPort) is { } adoptedFrontendPid)
            {
                ConsoleSignal.SendCtrlC(adoptedFrontendPid);
                await Task.Delay(1500).ConfigureAwait(false);
            }
        }
        else
        {
            outcomes.Add(await Frontend.StopAsync(graceful).ConfigureAwait(false));
        }

        Progress?.Invoke("正在停止后端…");
        if (BackendAdopted)
        {
            _log.Write("[启动器] 后端为外部服务，尝试按端口优雅停止");
            if (Net.FindPidListeningOn(BackendPort) is { } adoptedBackendPid)
            {
                ConsoleSignal.SendCtrlC(adoptedBackendPid);
                await Task.Delay(1500).ConfigureAwait(false);
            }
        }
        else
        {
            outcomes.Add(await Backend.StopAsync(graceful).ConfigureAwait(false));
        }

        BackendReady = false;
        FrontendReady = false;

        foreach (var outcome in outcomes)
        {
            _log.Write($"[启动器] {outcome.Detail}");
        }

        return outcomes;
    }

    /// <summary>重启后端：应用内有 /api/restart 会另起进程，这里只处理启动器自己拉起的部分。</summary>
    public async Task RestartBackendAsync(CancellationToken cancellationToken)
    {
        if (!BackendAdopted)
        {
            await Backend.StopAsync(TimeSpan.FromSeconds(Math.Max(5, _config.GracefulShutdownSeconds))).ConfigureAwait(false);
        }

        BackendReady = false;
        var error = await StartBackendAsync(cancellationToken).ConfigureAwait(false);
        if (error is not null)
        {
            _log.Write($"[启动器] 后端重启失败：{error}");
        }
    }

    public void KillAllHard()
    {
        _job.Terminate();
    }

    // ── 存活探测（状态栏用） ────────────────────────────────

    public Task<bool> IsBackendAliveAsync() => Net.HttpRespondsAsync(BackendProbeUrl, 2500);

    public Task<bool> IsFrontendAliveAsync() => Net.HttpRespondsAsync(FrontendUrl, 2500);

    /// <summary>
    /// 后端就绪后取一次健康报告写日志。/api/health 会比较慢（内部要跑 LLM 连通性检查），
    /// 所以只在就绪时调一次、给足超时，绝不用它轮询。
    /// </summary>
    private async Task LogHealthReportAsync()
    {
        try
        {
            var body = await Net.HttpBodyAsync(BackendHealthUrl, 90000).ConfigureAwait(false);
            if (body is null)
            {
                _log.Write("[启动器] 健康报告获取失败（不影响使用）");
                return;
            }

            using var document = JsonDocument.Parse(body);
            var status = document.RootElement.TryGetProperty("status", out var statusElement) ? statusElement.GetString() : "?";
            var version = document.RootElement.TryGetProperty("version", out var versionElement) ? versionElement.GetString() : "?";
            _log.Write($"[启动器] 后端健康报告：status={status}, version={version}");
        }
        catch (Exception ex)
        {
            _log.Write($"[启动器] 健康报告解析失败：{ex.Message}");
        }
    }

    // ── 内部事件 ────────────────────────────────────────────

    private void OnOutput(ServiceProcess service, string line)
    {
        if (service == Frontend && line.Contains("::SONETTO_URL::", StringComparison.Ordinal))
        {
            _frontendUrlFromWrapper = line[(line.IndexOf("::SONETTO_URL::", StringComparison.Ordinal) + "::SONETTO_URL::".Length)..].Trim();
            return;
        }

        OutputLine?.Invoke($"[{service.Name}] {line}");
    }

    private void OnExited(ServiceProcess service, bool intentional)
    {
        if (intentional)
        {
            return;
        }

        var isBackend = ReferenceEquals(service, Backend);
        var name = isBackend ? "后端" : "前端";
        var message = $"{name}进程意外退出（exit={service.ExitCode}）";

        _log.Write($"[启动器] {message}");

        if (isBackend)
        {
            BackendReady = false;
            BackendVanished?.Invoke(message);
        }
        else
        {
            FrontendReady = false;
            FrontendVanished?.Invoke(message);
        }
    }

    private void CleanupOldLogs()
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-7);
            foreach (var file in Directory.EnumerateFiles(LauncherPaths.LogDir, "*.log"))
            {
                if (File.GetLastWriteTime(file) < cutoff)
                {
                    File.Delete(file);
                }
            }
        }
        catch
        {
            // 清理失败无关紧要
        }
    }

    public void Dispose()
    {
        Backend.Dispose();
        Frontend.Dispose();
    }
}

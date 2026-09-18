using System.Diagnostics;
using System.IO;
using System.Text;
using SonettoHere.Launcher.Native;

namespace SonettoHere.Launcher;

internal enum ServiceState
{
    Stopped,
    Running,
    Stopping,
    Exited,
}

/// <summary>一次启动所需的全部信息。</summary>
internal sealed class ServiceLaunchSpec
{
    public required string Mode { get; init; }
    public required string FileName { get; init; }
    public required string WorkingDirectory { get; init; }
    public IReadOnlyList<string> Arguments { get; init; } = Array.Empty<string>();
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();

    /// <summary>true = 通过 stdin 控制通道优雅关闭；false = 只能靠控制台信号。</summary>
    public required bool UsesControlChannel { get; init; }

    public string CommandLine => $"{Path.GetFileName(FileName)} {string.Join(' ', Arguments)}";
}

internal sealed class StopOutcome
{
    public bool Stopped { get; init; }
    public bool WasGraceful { get; init; }
    public string Detail { get; init; } = string.Empty;

    public static StopOutcome NotRunning() => new()
    {
        Stopped = true,
        WasGraceful = true,
        Detail = "进程已不在运行",
    };
}

/// <summary>
/// 单个受管服务（后端 / 前端）。负责拉起进程、捕获输出、以及分级优雅停止：
///   1) 控制通道（wrapper）或 Ctrl+C → 2) 控制台中断信号 → 3) 整树强杀。
/// </summary>
internal sealed class ServiceProcess : IDisposable
{
    private const int RecentLineLimit = 200;

    private readonly LogBus _log;
    private readonly object _gate = new();
    private readonly Queue<string> _recent = new();
    private readonly StreamWriter? _file;
    private Process? _process;
    private JobObject? _job;
    private bool _intentionalStop;

    public ServiceProcess(string name, LogBus log, string logFilePath)
    {
        Name = name;
        _log = log;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(logFilePath)!);
            _file = new StreamWriter(
                new FileStream(logFilePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                AutoFlush = true,
            };
        }
        catch
        {
            _file = null;
        }
    }

    public string Name { get; }

    public ServiceState State { get; private set; } = ServiceState.Stopped;

    public ServiceLaunchSpec? Spec { get; private set; }

    /// <summary>是否由本启动器拉起（false 表示端口上已有别人启动的服务）。</summary>
    public bool StartedByLauncher { get; private set; }

    public int Pid { get; private set; }

    public DateTime? ExitedAt { get; private set; }

    public bool HasExited
    {
        get
        {
            var process = _process;
            if (process is null)
            {
                return true;
            }

            try
            {
                return process.HasExited;
            }
            catch
            {
                return true;
            }
        }
    }

    public int ExitCode
    {
        get
        {
            try
            {
                return _process is { HasExited: true } process ? process.ExitCode : 0;
            }
            catch
            {
                return 0;
            }
        }
    }

    /// <summary>原始输出行（供 UI 与控制通道解析使用）。</summary>
    public event Action<ServiceProcess, string>? Output;

    /// <summary>进程退出；参数为「是否属于启动器主动停止」。</summary>
    public event Action<ServiceProcess, bool>? Exited;

    public event Action<ServiceProcess>? StateChanged;

    public void Start(ServiceLaunchSpec spec, JobObject? job)
    {
        Spec = spec;
        _intentionalStop = false;
        ExitedAt = null;

        var psi = new ProcessStartInfo
        {
            FileName = spec.FileName,
            WorkingDirectory = spec.WorkingDirectory,
            UseShellExecute = false,
            // CREATE_NO_WINDOW：子进程各自拿到一个隐藏控制台。
            // 既不会闪出黑窗口，又保留「控制台信号」这条兜底通道。
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };

        foreach (var argument in spec.Arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        foreach (var (key, value) in spec.Environment)
        {
            psi.Environment[key] = value;
        }

        // Python 侧统一 UTF-8，避免中文输出在管道里被按 GBK 解码成乱码
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUNBUFFERED"] = "1";

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => OnOutputLine(e.Data);
        process.ErrorDataReceived += (_, e) => OnOutputLine(e.Data);
        process.Exited += (_, _) => OnProcessExited();

        WriteLine($"[启动器] 启动{Name}（{spec.Mode}）：{spec.CommandLine}");

        process.Start();
        _process = process;
        _job = job;
        Pid = process.Id;
        StartedByLauncher = true;
        State = ServiceState.Running;

        job?.Assign(process.Handle, _log);

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        WriteLine($"[启动器] {Name} PID = {Pid}");
        StateChanged?.Invoke(this);
    }

    /// <summary>标记为「外部已在运行、启动器不管理其生命周期」。</summary>
    public void MarkExternal(int pid)
    {
        Pid = pid;
        StartedByLauncher = false;
        State = ServiceState.Running;
        StateChanged?.Invoke(this);
    }

    public async Task<bool> WaitForExitAsync(TimeSpan timeout)
    {
        var process = _process;
        if (process is null)
        {
            return true;
        }

        try
        {
            using var cts = new CancellationTokenSource(timeout);
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return HasExited;
        }
        catch
        {
            return HasExited;
        }
    }

    public async Task<StopOutcome> StopAsync(TimeSpan gracefulTimeout)
    {
        var process = _process;
        if (process is null)
        {
            State = ServiceState.Stopped;
            StateChanged?.Invoke(this);
            return StopOutcome.NotRunning();
        }

        if (process.HasExited)
        {
            State = ServiceState.Stopped;
            StateChanged?.Invoke(this);
            return StopOutcome.NotRunning();
        }

        _intentionalStop = true;
        State = ServiceState.Stopping;
        StateChanged?.Invoke(this);

        var pid = process.Id;
        var usesChannel = Spec?.UsesControlChannel == true;

        // ── 第一级：优雅关闭 ───────────────────────────────
        if (usesChannel)
        {
            WriteLine($"[启动器] 请{Name}优雅退出（控制通道）…");
            await SendControlCommandAsync(process, "shutdown").ConfigureAwait(false);
        }
        else
        {
            WriteLine($"[启动器] 请{Name}优雅退出（Ctrl+C）…");
            ConsoleSignal.SendCtrlC(pid);
        }

        if (await WaitForExitAsync(gracefulTimeout).ConfigureAwait(false))
        {
            return Graceful($"{Name}已在 {gracefulTimeout.TotalSeconds:0} 秒内优雅退出");
        }

        // ── 第二级：控制台中断信号 ─────────────────────────
        WriteLine($"[启动器] {Name}优雅关闭超时，发送中断信号");
        ConsoleSignal.SendCtrlC(pid);
        if (await WaitForExitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false))
        {
            return Graceful($"{Name}在收到中断信号后退出");
        }

        // ── 第三级：整树强杀 ──────────────────────────────
        WriteLine($"[启动器] {Name}仍未退出，强制结束进程树（PID {pid}）");
        ProcessUtil.KillTree(pid, _log);
        var killed = await WaitForExitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);

        return new StopOutcome
        {
            Stopped = killed && HasExited,
            WasGraceful = false,
            Detail = killed ? $"{Name}被强制终止" : $"{Name}强杀后仍未退出",
        };
    }

    private async Task SendControlCommandAsync(Process process, string command)
    {
        try
        {
            await process.StandardInput.WriteLineAsync(command).ConfigureAwait(false);
            await process.StandardInput.FlushAsync().ConfigureAwait(false);

            // 再关掉 stdin：wrapper 收到 EOF 也会走同一条优雅关闭路径（双保险）
            process.StandardInput.Close();
        }
        catch (Exception ex)
        {
            WriteLine($"[启动器] 写入{Name}控制通道失败：{ex.Message}");
        }
    }

    private static StopOutcome Graceful(string detail) => new()
    {
        Stopped = true,
        WasGraceful = true,
        Detail = detail,
    };

    private void OnOutputLine(string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (_gate)
        {
            try
            {
                _file?.WriteLine(line);
            }
            catch
            {
                // 忽略写盘失败
            }

            _recent.Enqueue(line);
            while (_recent.Count > RecentLineLimit)
            {
                _recent.Dequeue();
            }
        }

        Output?.Invoke(this, line);
    }

    private void WriteLine(string message)
    {
        _log.Write(message);
        OnOutputLine(message);
    }

    private void OnProcessExited()
    {
        var intentional = _intentionalStop;
        State = intentional ? ServiceState.Stopped : ServiceState.Exited;
        ExitedAt = DateTime.Now;
        StateChanged?.Invoke(this);
        Exited?.Invoke(this, intentional);
    }

    /// <summary>由启动器拉起、且进程仍在运行。</summary>
    public bool IsManagedAndAlive => StartedByLauncher && !HasExited;

    /// <summary>
    /// 状态栏文案：把「进程已经没了」和「进程还在但没应答」区分开 ——
    /// 后者（例如 uvicorn 监听被打挂、端口无响应）不去误报成「已停止」。
    /// </summary>
    public string DescribeRuntime(bool responding)
    {
        if (responding)
        {
            return StartedByLauncher ? "运行中" : "运行中（外部）";
        }

        if (IsManagedAndAlive)
        {
            return "无响应（进程仍在）";
        }

        if (!StartedByLauncher && Pid > 0)
        {
            return "无响应（外部进程）";
        }

        return "已停止";
    }

    public string RecentOutput(int maxLines = 12)
    {
        lock (_gate)
        {
            return string.Join(Environment.NewLine, _recent.TakeLast(maxLines));
        }
    }

    public void Dispose()
    {
        try
        {
            _process?.Dispose();
        }
        catch
        {
            // 忽略
        }

        try
        {
            _file?.Dispose();
        }
        catch
        {
            // 忽略
        }
    }
}

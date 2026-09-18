using System.Diagnostics;
using System.IO;
using System.Text;
using SonettoHere.Launcher.Native;

namespace SonettoHere.Launcher;

/// <summary>
/// 在启动器窗口内运行 setup_guide.py / upgrade.py 这类交互式脚本。
///
/// 不开独立控制台窗口：标准输入输出全部接管到管道 ——
/// 输出按「文本块」实时推给界面上的任务控制台（保留回车覆盖与提示符不带换行的行为），
/// 用户在界面输入行敲的内容写回脚本 stdin，因此脚本里的 input() 提示照常工作。
/// 脚本本身无需任何改动。
/// </summary>
internal sealed class ScriptRunner : IDisposable
{
    private readonly LogBus _log;
    private readonly StringBuilder _rawOutput = new();
    private Process? _process;
    private bool _terminated;

    public ScriptRunner(LogBus log)
    {
        _log = log;
    }

    /// <summary>原始输出块（可能不含换行，例如 input() 的提示符）。</summary>
    public event Action<string>? Output;

    public bool IsRunning
    {
        get
        {
            try
            {
                return _process is { HasExited: false };
            }
            catch
            {
                return false;
            }
        }
    }

    public int ExitCode { get; private set; }

    public bool Start(string fileName, IReadOnlyList<string> arguments, string workingDirectory, JobObject? job)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };

        foreach (var argument in arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        // 让 Python 逐字符刷出提示符（否则 input() 的提示会卡在缓冲区里不显示）
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUNBUFFERED"] = "1";

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { };
        process.ErrorDataReceived += (_, e) => { };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            _log.Write($"[启动器] 无法启动脚本：{ex.Message}");
            return false;
        }

        _process = process;
        job?.Assign(process.Handle, _log);

        _ = PumpAsync(process.StandardOutput);
        _ = PumpAsync(process.StandardError);

        _log.Write($"[启动器] 已启动脚本（PID {process.Id}）：{Path.GetFileName(fileName)} {string.Join(' ', arguments)}");
        return true;
    }

    private async Task PumpAsync(StreamReader reader)
    {
        var buffer = new char[1024];
        try
        {
            while (true)
            {
                var read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length)).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                var text = new string(buffer, 0, read);
                _rawOutput.Append(text);
                Output?.Invoke(text);
            }
        }
        catch (Exception ex)
        {
            _log.Write($"[启动器] 读取脚本输出中断：{ex.Message}");
        }
    }

    /// <summary>把用户输入写回脚本。空串也会发送（等价于直接回车，脚本自己取默认值）。</summary>
    public void WriteInput(string text)
    {
        var process = _process;
        if (process is null || !IsRunning)
        {
            return;
        }

        try
        {
            process.StandardInput.Write(text);
            process.StandardInput.Write('\n');
            process.StandardInput.Flush();
        }
        catch (Exception ex)
        {
            _log.Write($"[启动器] 向脚本写入输入失败：{ex.Message}");
        }
    }

    /// <summary>终止脚本（含它拉起的 pip / npm 等子进程）。</summary>
    public void Terminate()
    {
        var process = _process;
        if (process is null || !IsRunning)
        {
            return;
        }

        _terminated = true;
        _log.Write($"[启动器] 终止脚本进程树（PID {process.Id}）");
        ProcessUtil.KillTree(process.Id, _log);
    }

    public async Task<int> WaitAsync()
    {
        var process = _process;
        if (process is null)
        {
            return -1;
        }

        try
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
            ExitCode = _terminated ? -2 : process.ExitCode;
        }
        catch (Exception ex)
        {
            _log.Write($"[启动器] 等待脚本结束失败：{ex.Message}");
            ExitCode = -1;
        }

        return ExitCode;
    }

    public string RecentOutput(int maxChars = 4000)
    {
        var text = _rawOutput.ToString();
        return text.Length <= maxChars ? text : text[^maxChars..];
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
    }
}

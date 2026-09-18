using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace SonettoLauncher;

/// <summary>进程相关小工具：查找可执行文件、整树强杀。</summary>
internal static class ProcessUtil
{
    /// <summary>在 PATH 中查找可执行文件（等价于 where / which）。</summary>
    public static string? Which(string executable)
    {
        var extensions = new List<string> { string.Empty };
        var pathExt = Environment.GetEnvironmentVariable("PATHEXT");
        if (!string.IsNullOrEmpty(pathExt))
        {
            extensions.AddRange(pathExt.Split(';', StringSplitOptions.RemoveEmptyEntries));
        }

        var searchDirs = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        foreach (var dir in searchDirs)
        {
            foreach (var extension in extensions)
            {
                try
                {
                    var candidate = Path.Combine(dir.Trim().Trim('"'), executable + extension);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch
                {
                    // 非法路径忽略
                }
            }
        }

        return null;
    }

    /// <summary>整树强杀（taskkill /T /F）—— 仅用于优雅关闭失败后的最后兜底。</summary>
    public static void KillTree(int pid, LogBus? log = null)
    {
        if (pid <= 0)
        {
            return;
        }

        try
        {
            var psi = new ProcessStartInfo("taskkill.exe", $"/PID {pid} /T /F")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.Latin1,
                StandardErrorEncoding = System.Text.Encoding.Latin1,
            };

            using var process = Process.Start(psi);
            process?.WaitForExit(8000);
        }
        catch (Exception ex)
        {
            log?.Write($"[启动器] 强制结束进程 {pid} 失败：{ex.Message}");
        }
    }

    public static bool IsRunning(int pid)
    {
        if (pid <= 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (Win32Exception)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>查找 Edge 可执行文件（WebView2 不可用时的回退显示方案）。</summary>
    public static string? FindEdge()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "Edge", "Application", "msedge.exe"),
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint dwProcessId);
}

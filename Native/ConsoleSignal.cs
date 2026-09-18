using System.Runtime.InteropServices;

namespace SonettoLauncher.Native;

/// <summary>
/// Windows 控制台信号投递。用途有两条：
///   1) 兜底关闭：wrapper 通道失效时，用 CTRL_C_EVENT 让后端走 uvicorn 自己的
///      Ctrl+C 优雅关闭路径（第二次信号等价于 force）。
///   2) 收拾启动器「没启动过、但占着端口」的后端进程（例如应用内 /api/restart 拉起的那个）。
///
/// 前提：目标进程必须和调用方共享同一个控制台。启动器的子进程是用
/// CREATE_NO_WINDOW 拉起的，各自拥有一个隐藏控制台，因此必须先 AttachConsole。
/// </summary>
internal static class ConsoleSignal
{
    private const uint CtrlCEvent = 0;

    private static bool _selfIgnoresCtrlC;

    /// <summary>让启动器自身忽略 Ctrl+C，避免附加到目标控制台后被一起干掉。</summary>
    public static void EnsureSelfIgnoresCtrlC()
    {
        if (_selfIgnoresCtrlC)
        {
            return;
        }

        SetConsoleCtrlHandler(IntPtr.Zero, add: true);
        _selfIgnoresCtrlC = true;
    }

    public static bool SendCtrlC(int pid, int settleMilliseconds = 1200)
    {
        if (pid <= 0)
        {
            return false;
        }

        EnsureSelfIgnoresCtrlC();

        FreeConsole();

        if (!AttachConsole((uint)pid))
        {
            return false;
        }

        var sent = false;
        try
        {
            sent = GenerateConsoleCtrlEvent(CtrlCEvent, 0);
            if (sent)
            {
                // 保持附加状态一会儿，确保目标进程有时间收到并处理该事件
                Thread.Sleep(settleMilliseconds);
            }
        }
        finally
        {
            FreeConsole();
        }

        return sent;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleCtrlHandler(IntPtr handlerRoutine, bool add);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GenerateConsoleCtrlEvent(uint dwCtrlEvent, uint dwProcessGroupId);
}

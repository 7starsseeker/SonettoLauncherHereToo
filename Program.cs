using System.Diagnostics;
using System.Reflection;
using System.Windows.Forms;

namespace SonettoLauncher;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        // 启动器自身忽略 Ctrl+C：向目标控制台投递信号时不会被一起带走
        Native.ConsoleSignal.EnsureSelfIgnoresCtrlC();

        try
        {
            Application.Run(new MainForm(args));
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"启动器发生未处理错误：{Environment.NewLine}{Environment.NewLine}{ex}",
                "SonettoLauncher",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    /// <summary>
    /// 启动器版本（显示在窗口标题与日志里）。
    /// 用**文件版本**而不是程序集版本：程序集版本固定在 1.0.0.0 以保持绑定稳定，
    /// 真正对外表示版本的是 &lt;Version&gt;/&lt;FileVersion&gt;（也就是资源管理器属性里看到的那个）。
    /// </summary>
    public static string Version { get; } = ResolveVersion();

    private static string ResolveVersion()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path))
            {
                var fileVersion = FileVersionInfo.GetVersionInfo(path).FileVersion;
                if (!string.IsNullOrWhiteSpace(fileVersion))
                {
                    var parts = fileVersion.Split('.');
                    return string.Join('.', parts.Take(Math.Min(3, parts.Length)));
                }
            }
        }
        catch
        {
            // 读不到就用程序集版本兜底
        }

        return Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
    }
}

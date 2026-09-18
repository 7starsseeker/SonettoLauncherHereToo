using System.Windows.Forms;

namespace SonettoHere.Launcher;

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
                "SonettoHere 启动器",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    public static string Version =>
        typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}

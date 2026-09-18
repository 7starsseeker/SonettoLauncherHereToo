using System.IO;

namespace SonettoHere.Launcher;

/// <summary>
/// 启动器自身的文件位置 —— 全部位于 %LOCALAPPDATA%，绝不写入项目目录。
/// </summary>
internal static class LauncherPaths
{
    public static string BaseDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SonettoHereLauncher");

    public static string ConfigFile => Path.Combine(BaseDir, "launcher.config.json");
    public static string WrapperDir => Path.Combine(BaseDir, "wrappers");
    public static string ScriptDir => Path.Combine(BaseDir, "scripts");
    public static string LogDir => Path.Combine(BaseDir, "logs");
    public static string WebViewDataDir => Path.Combine(BaseDir, "webview2");
    public static string EdgeProfileDir => Path.Combine(BaseDir, "edge-profile");

    public static string BackendWrapper => Path.Combine(WrapperDir, "backend_wrapper.py");
    public static string ViteWrapper => Path.Combine(WrapperDir, "vite_run.mjs");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(BaseDir);
        Directory.CreateDirectory(WrapperDir);
        Directory.CreateDirectory(ScriptDir);
        Directory.CreateDirectory(LogDir);
    }
}

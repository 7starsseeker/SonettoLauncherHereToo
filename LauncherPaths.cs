using System.IO;

namespace SonettoLauncher;

/// <summary>
/// 启动器自身的文件位置 —— 全部位于 %LOCALAPPDATA%，绝不写入项目目录。
/// </summary>
internal static class LauncherPaths
{
    public static string BaseDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SonettoLauncher");

    public static string ConfigFile => Path.Combine(BaseDir, "launcher.config.json");
    public static string WrapperDir => Path.Combine(BaseDir, "wrappers");
    public static string ScriptDir => Path.Combine(BaseDir, "scripts");
    public static string LogDir => Path.Combine(BaseDir, "logs");
    public static string WebViewDataDir => Path.Combine(BaseDir, "webview2");
    public static string EdgeProfileDir => Path.Combine(BaseDir, "edge-profile");

    public static string BackendWrapper => Path.Combine(WrapperDir, "backend_wrapper.py");
    public static string ViteWrapper => Path.Combine(WrapperDir, "vite_run.mjs");

    /// <summary>数据目录迁移的结果说明（非空时由界面在启动时写进日志）。</summary>
    public static string? MigrationNote { get; private set; }

    public static void EnsureCreated()
    {
        MigrateLegacyDirectory();
        Directory.CreateDirectory(BaseDir);
        Directory.CreateDirectory(WrapperDir);
        Directory.CreateDirectory(ScriptDir);
        Directory.CreateDirectory(LogDir);
    }

    /// <summary>
    /// 早期版本用的是 SonettoHereLauncher 目录；改名为 SonettoLauncher 后，
    /// 首次启动时把旧目录搬过来。
    ///
    /// 注意：整体改名经常会被拒（残留的 <c>msedgewebview2.exe</c> 还占着旧目录里的 WebView2 数据），
    /// 所以失败时退回「逐个复制关键数据」（配置、备份、旧日志），保证用户的东西不丢。
    /// </summary>
    private static void MigrateLegacyDirectory()
    {
        try
        {
            var legacy = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SonettoHereLauncher");

            if (!Directory.Exists(legacy))
            {
                return;
            }

            if (!Directory.Exists(BaseDir))
            {
                try
                {
                    Directory.Move(legacy, BaseDir);
                    MigrationNote = $"数据目录已迁移：{legacy} → {BaseDir}";
                    return;
                }
                catch (Exception ex)
                {
                    MigrationNote = $"旧数据目录整体迁移失败（{ex.Message.Trim()}），改为逐个复制关键数据";
                }
            }

            Directory.CreateDirectory(BaseDir);
            var copied = new List<string>();

            var legacyConfig = Path.Combine(legacy, "launcher.config.json");
            if (File.Exists(legacyConfig) && !File.Exists(ConfigFile))
            {
                File.Copy(legacyConfig, ConfigFile);
                copied.Add("launcher.config.json");
            }

            foreach (var name in new[] { "backups", "logs" })
            {
                var source = Path.Combine(legacy, name);
                var target = Path.Combine(BaseDir, name);
                if (Directory.Exists(source) && !Directory.Exists(target))
                {
                    CopyDirectory(source, target);
                    copied.Add(name + "\\");
                }
            }

            var prefix = MigrationNote is null ? string.Empty : MigrationNote + "；";
            MigrationNote = copied.Count > 0
                ? $"{prefix}已从旧目录复制：{string.Join('、', copied)}（旧目录 {legacy} 可手动删除）"
                : prefix.TrimEnd('；');
        }
        catch (Exception ex)
        {
            MigrationNote = $"旧数据目录迁移出错：{ex.Message.Trim()}";
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);

        foreach (var file in Directory.GetFiles(source))
        {
            var destination = Path.Combine(target, Path.GetFileName(file));
            if (!File.Exists(destination))
            {
                File.Copy(file, destination);
            }
        }

        foreach (var directory in Directory.GetDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(target, Path.GetFileName(directory)));
        }
    }
}

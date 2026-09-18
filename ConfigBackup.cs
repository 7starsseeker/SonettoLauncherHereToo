using System.IO;
using System.Text;

namespace SonettoHere.Launcher;

/// <summary>
/// 执行 setup / upgrade 前的配置备份。
///
/// 为什么需要：<c>setup_guide.py</c> 会用模板覆盖 <c>config/personas/USER.md</c> 与
/// <c>SOUL.md</c>（这两类文件在 .gitignore 里，git 救不回来），并会重跑依赖安装；
/// <c>upgrade.py</c> 的迁移脚本也可能改写配置。所以动它们之前先原样拷一份到
/// %LOCALAPPDATA%\SonettoHereLauncher\backups\ 下，随时可以人工回滚。
/// </summary>
internal static class ConfigBackup
{
    private static readonly string[] BackupFiles =
    {
        Path.Combine("config", "providers.yaml"),
        Path.Combine("config", "auth_token.yaml"),
        Path.Combine("config", "path_whitelist.yaml"),
        Path.Combine("config", "sonetto_blocker.yaml"),
        Path.Combine("config", "mcp_servers.yaml"),
        Path.Combine("config", "model_context_windows.yaml"),
        Path.Combine("config", ".env"),
        Path.Combine("config", "personas", "USER.md"),
        Path.Combine("config", "personas", "SOUL.md"),
        Path.Combine("config", "personas", "MEMORY.md"),
        Path.Combine("config", "personas", "memory.yaml"),
        Path.Combine("config", "personas", "memory_operations.yaml"),
    };

    /// <summary>把关键配置拷到时间戳目录，返回备份目录（无文件可备份时返回 null）。</summary>
    public static string? Create(string projectRoot, string label, LogBus log)
    {
        try
        {
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var target = Path.Combine(LauncherPaths.BaseDir, "backups", $"{label}-{stamp}");
            var copied = 0;

            foreach (var relative in BackupFiles)
            {
                var source = Path.Combine(projectRoot, relative);
                if (!File.Exists(source))
                {
                    continue;
                }

                var destination = Path.Combine(target, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination, overwrite: true);
                copied++;
            }

            if (copied == 0)
            {
                return null;
            }

            // 附一份清单，便于人工确认备份内容
            var manifest = new StringBuilder();
            manifest.AppendLine($"备份时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            manifest.AppendLine($"来源项目: {projectRoot}");
            manifest.AppendLine($"用途: 执行{label}前的自动备份");
            manifest.AppendLine($"文件数: {copied}");
            File.WriteAllText(Path.Combine(target, "backup-info.txt"), manifest.ToString(), new UTF8Encoding(false));

            PruneOldBackups();
            log.Write($"[启动器] 已备份 {copied} 个配置文件 → {target}");
            return target;
        }
        catch (Exception ex)
        {
            log.Write($"[启动器] 配置备份失败（继续执行）：{ex.Message}");
            return null;
        }
    }

    /// <summary>只保留最近 10 份备份，避免无限堆积。</summary>
    private static void PruneOldBackups()
    {
        try
        {
            var root = Path.Combine(LauncherPaths.BaseDir, "backups");
            if (!Directory.Exists(root))
            {
                return;
            }

            foreach (var directory in Directory.GetDirectories(root)
                         .OrderByDescending(path => Directory.GetCreationTime(path))
                         .Skip(10))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch
        {
            // 清理失败无关紧要
        }
    }
}

using System.IO;
using System.Text.RegularExpressions;

namespace SonettoLauncher;

/// <summary>
/// 项目根目录定位与环境自检。启动器只读取项目文件，不做任何写入。
/// </summary>
internal static class ProjectLocator
{
    private const string MainPy = "main.py";

    public static bool IsProjectRoot(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
        {
            return false;
        }

        return File.Exists(Path.Combine(dir, MainPy))
               && File.Exists(Path.Combine(dir, "web", "package.json"));
    }

    /// <summary>按「命令行参数 → 配置 → exe 所在目录 → 逐级向上」的顺序定位项目根。</summary>
    public static string? Resolve(string? explicitRoot, string? configuredRoot, string startDir)
    {
        foreach (var candidate in Candidates(explicitRoot, configuredRoot, startDir))
        {
            if (IsProjectRoot(candidate))
            {
                return Path.GetFullPath(candidate!);
            }
        }

        return null;
    }

    private static IEnumerable<string?> Candidates(string? explicitRoot, string? configuredRoot, string startDir)
    {
        yield return explicitRoot;
        yield return configuredRoot;

        var dir = new DirectoryInfo(startDir);
        for (var depth = 0; dir is not null && depth < 5; depth++, dir = dir.Parent)
        {
            yield return dir.FullName;
        }
    }

    public static string VenvPython(string root) => Path.Combine(root, ".venv", "Scripts", "python.exe");

    public static string WebDir(string root) => Path.Combine(root, "web");

    public static string ViteModuleEntry(string root) =>
        Path.Combine(root, "web", "node_modules", "vite", "dist", "node", "index.js");

    /// <summary>返回缺失的初始化项；为空表示环境已就绪。</summary>
    public static IReadOnlyList<string> MissingPrerequisites(string root)
    {
        var missing = new List<string>();

        if (!File.Exists(VenvPython(root)))
        {
            missing.Add("Python 虚拟环境（.venv\\Scripts\\python.exe）");
        }

        if (!Directory.Exists(Path.Combine(WebDir(root), "node_modules")))
        {
            missing.Add("前端依赖（web\\node_modules）");
        }
        else if (!File.Exists(ViteModuleEntry(root)))
        {
            missing.Add("前端 Vite 包（web\\node_modules\\vite）");
        }

        return missing;
    }

    /// <summary>读取项目版本号（version.py 的 __version__）。</summary>
    public static string? ReadAppVersion(string root)
    {
        try
        {
            var path = Path.Combine(root, "version.py");
            if (!File.Exists(path))
            {
                return null;
            }

            var match = Regex.Match(File.ReadAllText(path), "__version__\\s*=\\s*[\"']([^\"']+)[\"']");
            return match.Success ? match.Groups[1].Value : null;
        }
        catch
        {
            return null;
        }
    }
}

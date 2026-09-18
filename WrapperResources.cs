using System.IO;
using System.Reflection;
using System.Text;

namespace SonettoHere.Launcher;

/// <summary>
/// 把内嵌的 wrapper 脚本释放到 %LOCALAPPDATA%\SonettoHereLauncher\wrappers\。
/// 这样 exe 是自包含的单文件，项目目录也不会多出任何文件。
/// </summary>
internal static class WrapperResources
{
    private const string BackendWrapperResource = "backend_wrapper.py";
    private const string ViteWrapperResource = "vite_run.mjs";
    private const string LoadingPageResource = "loading.html";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static string ExtractAll(LogBus log)
    {
        LauncherPaths.EnsureCreated();

        WriteResource(BackendWrapperResource, LauncherPaths.BackendWrapper, log);
        WriteResource(ViteWrapperResource, LauncherPaths.ViteWrapper, log);

        return LauncherPaths.WrapperDir;
    }

    public static string ReadLoadingPage()
    {
        using var stream = OpenResource(LoadingPageResource)
                           ?? throw new InvalidOperationException("内嵌加载页资源缺失");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static void WriteResource(string name, string targetPath, LogBus log)
    {
        using var stream = OpenResource(name);
        if (stream is null)
        {
            throw new InvalidOperationException($"内嵌资源缺失: {name}");
        }

        using var reader = new StreamReader(stream, Encoding.UTF8);
        var content = reader.ReadToEnd();

        // 内容一致就不重复写盘（避免无谓的文件时间戳变动）
        if (File.Exists(targetPath) && File.ReadAllText(targetPath, Encoding.UTF8) == content)
        {
            return;
        }

        File.WriteAllText(targetPath, content, Utf8NoBom);
        log.Write($"[启动器] 已释放 {Path.GetFileName(targetPath)} → {targetPath}");
    }

    private static Stream? OpenResource(string name) =>
        Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
}

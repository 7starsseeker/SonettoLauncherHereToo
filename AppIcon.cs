using System.Drawing;
using System.IO;
using System.Reflection;

namespace SonettoHere.Launcher;

/// <summary>
/// 窗口图标（标题栏 / 任务栏）。
///
/// 为什么要显式设置：exe 的 <c>ApplicationIcon</c> 只负责「资源管理器里看到的文件图标」，
/// WinForms 的窗口不会自动继承它 —— 不设 <c>Form.Icon</c> 就一直是系统默认图标。
/// 这里优先用内嵌的 <c>app.ico</c>（单文件发布也稳定），退一步从 exe 资源里提取。
/// </summary>
internal static class AppIcon
{
    private static readonly byte[]? IconBytes = LoadBytes();

    public static Icon Create()
    {
        if (IconBytes is not null)
        {
            try
            {
                using var stream = new MemoryStream(IconBytes);
                return new Icon(stream);
            }
            catch
            {
                // 落到下面的兜底
            }
        }

        try
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path) && Icon.ExtractAssociatedIcon(path) is { } extracted)
            {
                return extracted;
            }
        }
        catch
        {
            // 忽略
        }

        return SystemIcons.Application;
    }

    private static byte[]? LoadBytes()
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.ico");
            if (stream is null)
            {
                return null;
            }

            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            return memory.ToArray();
        }
        catch
        {
            return null;
        }
    }
}

using System.IO;
using System.Text;

namespace SonettoLauncher;

/// <summary>
/// 启动器日志：写文件（%LOCALAPPDATA%\SonettoLauncher\logs）并广播给 UI。
/// </summary>
internal sealed class LogBus : IDisposable
{
    private readonly object _gate = new();
    private readonly StreamWriter? _writer;

    public LogBus(string filePath)
    {
        FilePath = filePath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            _writer = new StreamWriter(
                new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                AutoFlush = true,
            };
        }
        catch
        {
            _writer = null;
        }
    }

    public string FilePath { get; }

    public event Action<string>? Line;

    public void Write(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss} {message}";

        lock (_gate)
        {
            try
            {
                _writer?.WriteLine(line);
            }
            catch
            {
                // 日志写盘失败不影响主流程
            }
        }

        Line?.Invoke(line);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            try
            {
                _writer?.Dispose();
            }
            catch
            {
                // 忽略
            }
        }
    }
}

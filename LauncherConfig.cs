using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SonettoLauncher;

/// <summary>
/// 启动器本地配置（%LOCALAPPDATA%\SonettoLauncher\launcher.config.json）。
/// 不放在项目目录里，避免污染仓库。
/// </summary>
internal sealed class LauncherConfig
{
    /// <summary>项目根目录；空则自动探测。</summary>
    public string? ProjectRoot { get; set; }

    /// <summary>关闭窗口时是否弹确认框。</summary>
    public bool ConfirmOnClose { get; set; } = true;

    /// <summary>是否使用 wrapper（优雅关闭通道）；关掉则退回直启 + 控制台信号。</summary>
    public bool UseWrappers { get; set; } = true;

    /// <summary>后端就绪等待上限（秒）。首次启动要加载 MCP/工具链，留足时间。</summary>
    public int BackendReadyTimeoutSeconds { get; set; } = 240;

    /// <summary>前端就绪等待上限（秒）。</summary>
    public int FrontendReadyTimeoutSeconds { get; set; } = 180;

    /// <summary>优雅关闭等待上限（秒），超时才升级为强杀。</summary>
    public int GracefulShutdownSeconds { get; set; } = 30;

    public int WindowWidth { get; set; } = 1440;

    public int WindowHeight { get; set; } = 920;

    /// <summary>上次就绪的前端地址（探测失败时的提示用）。</summary>
    public string? LastFrontendUrl { get; set; }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static LauncherConfig Load()
    {
        try
        {
            if (File.Exists(LauncherPaths.ConfigFile))
            {
                var json = File.ReadAllText(LauncherPaths.ConfigFile);
                var loaded = JsonSerializer.Deserialize<LauncherConfig>(json, Options);
                if (loaded is not null)
                {
                    return loaded;
                }
            }
        }
        catch
        {
            // 配置损坏时退回默认值，不影响启动
        }

        return new LauncherConfig();
    }

    public void Save()
    {
        try
        {
            LauncherPaths.EnsureCreated();
            File.WriteAllText(LauncherPaths.ConfigFile, JsonSerializer.Serialize(this, Options));
        }
        catch
        {
            // 写配置失败不阻断流程
        }
    }
}

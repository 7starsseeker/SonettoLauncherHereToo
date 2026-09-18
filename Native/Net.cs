using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace SonettoHere.Launcher.Native;

/// <summary>
/// HTTP 探活工具。
///
/// ⚠️ 实测踩坑（勿改回去）：
/// 1) 不要用「裸 TCP 连接后立刻关闭」当探活。Windows 上客户端在服务端 accept 之前断开，
///    会让 asyncio proactor 的 accept 抛 WinError 64；而 CPython 的 accept 循环捕获
///    OSError 时会直接 close 掉监听 socket —— 后端进程还活着，但从此不再监听端口。
/// 2) 不要拿 /api/health 当轮询接口：它要做 LLM 连通性检查，单次可能十几秒，
///    短超时轮询等于不断中途掐断在途请求。
/// 所以：探活用一次正常的 HTTP 请求，就绪判断用轻量路径（/ 或 /openapi.json）。
/// </summary>
internal static class Net
{
    private static HttpClient _client = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectTimeout = TimeSpan.FromSeconds(2),
            MaxConnectionsPerServer = 8,
        };

        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>丢弃连接池（停止服务前调用，避免 keep-alive 连接拖着优雅关闭）。</summary>
    public static void ResetConnections()
    {
        var old = _client;
        _client = CreateClient();

        try
        {
            old.CancelPendingRequests();
            old.Dispose();
        }
        catch
        {
            // 忽略
        }
    }

    /// <summary>只要收到任何 HTTP 响应（含 401/404）就认为服务活着。</summary>
    public static async Task<bool> HttpRespondsAsync(string url, int timeoutMilliseconds = 3000)
    {
        var result = await GetAsync(url, timeoutMilliseconds).ConfigureAwait(false);
        return result is not null;
    }

    /// <summary>取响应体；失败返回 null。</summary>
    public static async Task<string?> HttpBodyAsync(string url, int timeoutMilliseconds = 10000)
    {
        var result = await GetAsync(url, timeoutMilliseconds).ConfigureAwait(false);
        return result?.Body;
    }

    /// <summary>确认端口上确实是 SonettoHere 后端（用几乎没有成本的 /openapi.json 标题判断）。</summary>
    public static async Task<bool> LooksLikeSonettoBackendAsync(string baseUrl, int timeoutMilliseconds = 8000)
    {
        var body = await HttpBodyAsync($"{baseUrl.TrimEnd('/')}/openapi.json", timeoutMilliseconds).ConfigureAwait(false);
        return body is not null && body.Contains("SonettoHere", StringComparison.Ordinal);
    }

    private static async Task<(int Status, string Body)?> GetAsync(string url, int timeoutMilliseconds)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeoutMilliseconds);
            using var response = await _client.GetAsync(url, cts.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            return ((int)response.StatusCode, body);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>解析 netstat -ano，找出监听指定端口（127.0.0.1）的进程 PID。不发任何探测包。</summary>
    public static int? FindPidListeningOn(int port)
    {
        try
        {
            var psi = new ProcessStartInfo("netstat.exe", "-ano")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                StandardOutputEncoding = Encoding.Latin1,
            };

            using var process = Process.Start(psi);
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);

            var pattern = new Regex($@"^\s*TCP\s+\S+:{port}\s+\S+\s+LISTENING\s+(\d+)\s*$", RegexOptions.Multiline);
            var match = pattern.Match(output);
            if (match.Success && int.TryParse(match.Groups[1].Value, out var pid))
            {
                return pid;
            }
        }
        catch
        {
            // 查询失败按「没找到」处理
        }

        return null;
    }
}

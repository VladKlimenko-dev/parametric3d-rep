using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Parametric3D.App.Views;

namespace Parametric3D.App.Services.Mcp;

/// <summary>
/// k154: СЕРВЕР MCP В ПРИЛОЖЕНИИ — HTTP на 127.0.0.1 (только этот компьютер), путь /mcp, POST с сообщением JSON-RPC, ключ
/// доступа в заголовке Authorization: Bearer (ключ — случайный при каждом запуске). Порт и ключ пишутся в файл
/// %APPDATA%/Parametric3D/mcp.json — по нему мост stdio (Parametric3D --mcp-stdio) находит запущенное приложение, а
/// клиенты HTTP (Claude Code: transport http) берут адрес и ключ. Мутация P3D_MCP=0 — сервер не запускается.
/// Свой небольшой разбор HTTP на TcpListener: HttpListener в Windows требует прав на адрес, ASP.NET — отдельной среды.
/// </summary>
public sealed class McpHost : IDisposable
{
    public static readonly bool Off = Environment.GetEnvironmentVariable("P3D_MCP") == "0";
    public const int BasePort = 47853;
    public static McpHost? Current { get; private set; }

    public int Port { get; private set; }
    public string Token { get; } = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
    /// <summary>Разбор: обработано вызовов инструментов.</summary>
    public static long ToolCalls;

    private readonly Func<MainWindow?> _window;
    private TcpListener? _listener;
    private readonly CancellationTokenSource _cts = new();

    private McpHost(Func<MainWindow?> window) { _window = window; }

    /// <summary>Каталог файла сведений (переопределяется проверками).</summary>
    public static string InfoDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Parametric3D");
    public static string InfoPath => Path.Combine(InfoDir, "mcp.json");

    public static McpHost? Start(Func<MainWindow?> window, int basePort = BasePort)
    {
        if (Off) return null;
        Current?.Dispose();
        var h = new McpHost(window);
        for (int p = basePort; p < basePort + 20; p++)
        {
            try { var l = new TcpListener(IPAddress.Loopback, p); l.Start(); h._listener = l; h.Port = p; break; }
            catch (SocketException) { }
        }
        if (h._listener == null) { Core.AppLog.Write("MCP: no free port", null); return null; }
        try
        {
            Directory.CreateDirectory(InfoDir);
            File.WriteAllText(InfoPath, new JsonObject { ["url"] = $"http://127.0.0.1:{h.Port}/mcp", ["port"] = h.Port, ["token"] = h.Token, ["pid"] = Environment.ProcessId }.ToJsonString());
        }
        catch (Exception ex) { Core.AppLog.Write("MCP: info file", ex); }
        _ = Task.Run(() => h.AcceptLoop(h._cts.Token));
        Current = h;
        return h;
    }

    private async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient c;
            try { c = await _listener!.AcceptTcpClientAsync(ct); }
            catch { return; }
            _ = Task.Run(() => Serve(c, ct));
        }
    }

    private async Task Serve(TcpClient client, CancellationToken ct)
    {
        using var _ = client;
        try
        {
            var s = client.GetStream();
            while (!ct.IsCancellationRequested)
            {
                var req = await ReadRequest(s, ct);
                if (req == null) return;
                var (method, path, headers, body) = req.Value;
                if (path.Split('?')[0] != "/mcp") { await Respond(s, 404, "text/plain", "not found"); continue; }
                if (method == "GET") { await Respond(s, 405, "text/plain", "method not allowed"); continue; }
                if (method == "DELETE") { await Respond(s, 200, "text/plain", ""); continue; }
                if (method != "POST") { await Respond(s, 405, "text/plain", "method not allowed"); continue; }
                string auth = headers.GetValueOrDefault("authorization") ?? "";
                if (auth != "Bearer " + Token) { await Respond(s, 401, "text/plain", "unauthorized"); continue; }
                string? reply = await McpProtocol.HandleTextAsync(body, (name, args) => { Interlocked.Increment(ref ToolCalls); return McpTools.CallAsync(_window, name, args); });
                if (reply == null) await Respond(s, 202, "text/plain", "");
                else await Respond(s, 200, "application/json", reply);
                if (headers.GetValueOrDefault("connection")?.Equals("close", StringComparison.OrdinalIgnoreCase) == true) return;
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException) { }
        catch (Exception ex) { Core.AppLog.Write("MCP: request", ex); }
    }

    /// <summary>Запрос HTTP/1.1: строка запроса, заголовки, тело по Content-Length. null — соединение закрыто.</summary>
    private static async Task<(string Method, string Path, Dictionary<string, string> Headers, string Body)?> ReadRequest(Stream s, CancellationToken ct)
    {
        var head = new List<byte>();
        var one = new byte[1];
        while (true)
        {
            int n = await s.ReadAsync(one, ct);
            if (n == 0) return null;
            head.Add(one[0]);
            int c = head.Count;
            if (c >= 4 && head[c - 4] == '\r' && head[c - 3] == '\n' && head[c - 2] == '\r' && head[c - 1] == '\n') break;
            if (c > 65536) return null;
        }
        var lines = Encoding.ASCII.GetString(head.ToArray()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var first = lines[0].Split(' ');
        if (first.Length < 2) return null;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in lines.Skip(1)) { int k = l.IndexOf(':'); if (k > 0) headers[l[..k].Trim()] = l[(k + 1)..].Trim(); }
        int len = int.TryParse(headers.GetValueOrDefault("content-length"), out var cl) ? cl : 0;
        if (len < 0 || len > 64 * 1024 * 1024) return null;
        var body = new byte[len];
        int got = 0;
        while (got < len) { int n = await s.ReadAsync(body.AsMemory(got, len - got), ct); if (n == 0) return null; got += n; }
        return (first[0].ToUpperInvariant(), first[1], headers, Encoding.UTF8.GetString(body));
    }

    private static async Task Respond(Stream s, int code, string type, string text)
    {
        var body = Encoding.UTF8.GetBytes(text);
        string reason = code switch { 200 => "OK", 202 => "Accepted", 401 => "Unauthorized", 404 => "Not Found", _ => "Method Not Allowed" };
        var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {code} {reason}\r\nContent-Type: {type}; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: keep-alive\r\n\r\n");
        await s.WriteAsync(head);
        await s.WriteAsync(body);
        await s.FlushAsync();
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        try
        {
            if (File.Exists(InfoPath) && JsonNode.Parse(File.ReadAllText(InfoPath))?["pid"]?.GetValue<int>() == Environment.ProcessId) File.Delete(InfoPath);
        }
        catch { }
        if (ReferenceEquals(Current, this)) Current = null;
    }
}

/// <summary>
/// k154: МОСТ STDIO для клиентов, запускающих MCP-сервер командой (Claude Desktop): «Parametric3D --mcp-stdio». Сообщения
/// JSON-RPC — по строкам stdin/stdout. initialize, tools/list, ping — отвечает сам (список инструментов — тот же), tools/call
/// передаёт запущенному приложению (адрес и ключ — из mcp.json). Приложение не запущено — ответ инструмента с причиной.
/// </summary>
public static class McpBridge
{
    public static int Run(TextReader input, TextWriter output)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        string? line;
        while ((line = input.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            string? reply = McpProtocol.HandleTextAsync(line, (name, args) => Forward(http, name, args)).GetAwaiter().GetResult();
            if (reply != null) { output.WriteLine(reply); output.Flush(); }
        }
        return 0;
    }

    static async Task<JsonObject> Forward(HttpClient http, string name, JsonObject args)
    {
        JsonNode? info;
        try { info = JsonNode.Parse(File.ReadAllText(McpHost.InfoPath)); }
        catch { return McpProtocol.ToolText("Parametric3D is not running (start the app; it serves the tools while open).", isError: true); }
        var msg = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call", ["params"] = new JsonObject { ["name"] = name, ["arguments"] = args } };
        var req = new HttpRequestMessage(HttpMethod.Post, (string)info!["url"]!) { Content = new StringContent(msg.ToJsonString(), Encoding.UTF8, "application/json") };
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", (string)info["token"]!);
        try
        {
            var resp = await http.SendAsync(req);
            var text = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode) return McpProtocol.ToolText($"Parametric3D answered {(int)resp.StatusCode}: {text}", isError: true);
            var node = JsonNode.Parse(text);
            if (node?["result"] is JsonObject r) return (JsonObject)r.DeepClone();
            return McpProtocol.ToolText(node?["error"]?["message"]?.GetValue<string>() ?? "no result", isError: true);
        }
        catch (HttpRequestException) { return McpProtocol.ToolText("Parametric3D is not running (start the app; it serves the tools while open).", isError: true); }
    }
}

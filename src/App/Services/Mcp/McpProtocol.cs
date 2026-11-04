using System.Text.Json;
using System.Text.Json.Nodes;

namespace Parametric3D.App.Services.Mcp;

/// <summary>
/// k154: ПРОТОКОЛ MCP (Model Context Protocol) — JSON-RPC 2.0: initialize, tools/list, tools/call, ping; уведомления
/// (notifications/*) ответа не требуют. Инструменты — McpTools. Ответ инструмента — content (text / image) и isError.
/// Один и тот же разбор — у сервера в приложении (HTTP на localhost) и у моста stdio (Parametric3D --mcp-stdio): мост
/// отвечает на initialize и tools/list сам (список инструментов — в той же сборке), tools/call передаёт приложению.
/// </summary>
public static class McpProtocol
{
    public const string ProtocolVersion = "2025-06-18";
    public const string ServerName = "parametric3d";

    /// <summary>Ответ на одно сообщение (null — уведомление, ответа нет). callTool — исполнитель tools/call.</summary>
    public static async Task<JsonNode?> HandleAsync(JsonNode msg, Func<string, JsonObject, Task<JsonObject>> callTool)
    {
        var id = msg["id"]?.DeepClone();
        string method = msg["method"]?.GetValue<string>() ?? "";
        if (id == null) return null;   // уведомление (notifications/initialized и др.)
        try
        {
            switch (method)
            {
                case "initialize":
                {
                    string ver = msg["params"]?["protocolVersion"]?.GetValue<string>() ?? ProtocolVersion;
                    return Result(id, new JsonObject
                    {
                        ["protocolVersion"] = ver,
                        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
                        ["serverInfo"] = new JsonObject { ["name"] = ServerName, ["version"] = AppVersion() },
                        ["instructions"] = "Parametric3D — parametric 3D CAD with its own kernel. Tools read and edit the document open in the running app: the build tree, parameters, mass properties, a picture of the view. Every edit is one undo step in the app.",
                    });
                }
                case "ping": return Result(id, new JsonObject());
                case "tools/list":
                    return Result(id, new JsonObject { ["tools"] = new JsonArray(McpTools.All.Select(t => (JsonNode)t.Describe()).ToArray()) });
                case "tools/call":
                {
                    string name = msg["params"]?["name"]?.GetValue<string>() ?? "";
                    var args = msg["params"]?["arguments"] as JsonObject ?? new JsonObject();
                    if (McpTools.Find(name) == null) return Error(id, -32602, $"unknown tool '{name}'");
                    return Result(id, await callTool(name, (JsonObject)args.DeepClone()));
                }
                default: return Error(id, -32601, $"method '{method}' is not supported");
            }
        }
        catch (Exception ex) { return Error(id, -32603, ex.Message); }
    }

    /// <summary>Сообщение или пакет сообщений; null — отвечать нечего.</summary>
    public static async Task<string?> HandleTextAsync(string text, Func<string, JsonObject, Task<JsonObject>> callTool)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(text); }
        catch (JsonException ex) { return Error(null, -32700, "parse error: " + ex.Message).ToJsonString(); }
        if (node is JsonArray batch)
        {
            var outs = new JsonArray();
            foreach (var m in batch) if (m != null && await HandleAsync(m, callTool) is { } r) outs.Add(r);
            return outs.Count > 0 ? outs.ToJsonString() : null;
        }
        return node == null ? null : (await HandleAsync(node, callTool))?.ToJsonString();
    }

    static JsonObject Result(JsonNode? id, JsonObject result) => new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };
    public static JsonObject Error(JsonNode? id, int code, string message) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };

    /// <summary>Ответ инструмента: текст (и, если есть, картинка PNG).</summary>
    public static JsonObject ToolText(string text, bool isError = false, byte[]? png = null)
    {
        var content = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = text } };
        if (png != null) content.Add(new JsonObject { ["type"] = "image", ["data"] = Convert.ToBase64String(png), ["mimeType"] = "image/png" });
        return new JsonObject { ["content"] = content, ["isError"] = isError };
    }

    static string AppVersion() => typeof(McpProtocol).Assembly.GetName().Version?.ToString() ?? "1.0";
}

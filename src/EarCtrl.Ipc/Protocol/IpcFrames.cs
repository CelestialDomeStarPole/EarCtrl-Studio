using System.Text.Json;
using System.Text.Json.Serialization;

namespace EarCtrl.Ipc.Protocol;

/// <summary>IPC 帧类型（《软件实现计划》§4 契约）。</summary>
public enum IpcFrameKind
{
    Request,
    Response,
    Event,
}

public sealed record IpcError(string Code, string Message);

/// <summary>请求（主程序 → Agent）：{"id":1,"method":"...","params":{...}}</summary>
public sealed record IpcRequest(int Id, string Method, JsonElement? Params);

/// <summary>响应：{"id":1,"ok":true,"result":{...}} 或 {"id":1,"ok":false,"error":{code,message}}</summary>
public sealed record IpcResponse(int Id, bool Ok, JsonElement? Result, IpcError? Error)
{
    public static IpcResponse Success(int id, object? result) => new(
        id, true,
        result is null ? null : JsonSerializer.SerializeToElement(result, IpcJson.Options),
        null);

    public static IpcResponse Fail(int id, string code, string message) => new(
        id, false, null, new IpcError(code, message));
}

/// <summary>事件（Agent → 主程序，无 id）：{"event":"...","payload":{...}}</summary>
public sealed record IpcEvent(string Event, JsonElement? Payload);

/// <summary>
/// 解析结果：单帧 NDJSON 行 → 三类帧之一。无法识别时抛 <see cref="IpcProtocolException"/>。
/// </summary>
public static class IpcFrames
{
    public static (IpcFrameKind Kind, object Frame) Parse(string line)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new IpcProtocolException("帧必须是 JSON 对象");
        }

        if (root.TryGetProperty("event", out var ev))
        {
            JsonElement? payload = root.TryGetProperty("payload", out var p) ? p.Clone() : null;
            return (IpcFrameKind.Event, new IpcEvent(ev.GetString() ?? "", payload));
        }

        if (root.TryGetProperty("method", out var method))
        {
            var id = root.TryGetProperty("id", out var i) ? i.GetInt32() : 0;
            JsonElement? param = root.TryGetProperty("params", out var pr) ? pr.Clone() : null;
            return (IpcFrameKind.Request, new IpcRequest(id, method.GetString() ?? "", param));
        }

        if (root.TryGetProperty("id", out var rid) && root.TryGetProperty("ok", out var ok))
        {
            JsonElement? result = root.TryGetProperty("result", out var res) ? res.Clone() : null;
            IpcError? error = root.TryGetProperty("error", out var er)
                ? new IpcError(
                    er.TryGetProperty("code", out var c) ? c.GetString() ?? "" : "",
                    er.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "")
                : null;
            return (IpcFrameKind.Response, new IpcResponse(rid.GetInt32(), ok.GetBoolean(), result, error));
        }

        throw new IpcProtocolException($"无法识别的帧: {line}");
    }

    public static string Serialize(object frame) => JsonSerializer.Serialize(frame, IpcJson.Options);
}

public sealed class IpcProtocolException(string message) : Exception(message);

/// <summary>IPC JSON 序列化约定：camelCase、忽略 null。</summary>
public static class IpcJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}

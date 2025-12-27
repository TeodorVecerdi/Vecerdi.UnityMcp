using System.Text.Json;
using System.Text.Json.Serialization;

namespace UnityMcp.Mcp;

/// <summary>
/// JSON-RPC 2.0 request message.
/// </summary>
public sealed class JsonRpcRequest {
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; set; } = "2.0";

    [JsonPropertyName("id")]
    public JsonElement? Id { get; set; }

    [JsonPropertyName("method")]
    public string Method { get; set; } = string.Empty;

    [JsonPropertyName("params")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Params { get; set; }

    public bool IsNotification => Id is null;
}

/// <summary>
/// JSON-RPC 2.0 response message.
/// </summary>
public sealed class JsonRpcResponse {
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; set; } = "2.0";

    [JsonPropertyName("id")]
    public JsonElement? Id { get; set; }

    [JsonPropertyName("result")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Result { get; set; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonRpcError? Error { get; set; }

    public static JsonRpcResponse Success(JsonElement? id, object? result) => new() {
        Id = id,
        Result = result,
    };

    public static JsonRpcResponse Failure(JsonElement? id, int code, string message, object? data = null) => new() {
        Id = id,
        Error = new JsonRpcError { Code = code, Message = message, Data = data },
    };
}

/// <summary>
/// JSON-RPC 2.0 error object.
/// </summary>
public sealed class JsonRpcError {
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("data")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Data { get; set; }
}

/// <summary>
/// Standard JSON-RPC error codes.
/// </summary>
public static class JsonRpcErrorCodes {
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;
}

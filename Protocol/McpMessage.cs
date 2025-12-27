using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vecerdi.UnityMcp.Protocol;

/// <summary>
/// Request message from MCP client to Unity.
/// </summary>
public sealed class McpRequest {
    /// <summary>Correlation ID to match request with response.</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>Command to execute (e.g., "unity.debug.getLogs").</summary>
    [JsonPropertyName("command")]
    public string Command { get; set; } = string.Empty;

    /// <summary>Command parameters as raw JSON.</summary>
    [JsonPropertyName("params")]
    public JsonElement? Params { get; set; }

    /// <summary>Get a typed parameter value.</summary>
    public T? GetParam<T>(string name, T? defaultValue = default) {
        if (Params is not { ValueKind: JsonValueKind.Object } paramsObj) {
            return defaultValue;
        }

        if (!paramsObj.TryGetProperty(name, out var value)) {
            return defaultValue;
        }

        try {
            return value.Deserialize<T>();
        } catch {
            return defaultValue;
        }
    }

    /// <summary>Check if a parameter exists.</summary>
    public bool HasParam(string name) {
        return Params is { ValueKind: JsonValueKind.Object } paramsObj
            && paramsObj.TryGetProperty(name, out _);
    }
}

/// <summary>
/// Response message from Unity to MCP client.
/// </summary>
public sealed class McpResponse {
    /// <summary>Correlation ID matching the request.</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>Whether the command succeeded.</summary>
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    /// <summary>Result data on success.</summary>
    [JsonPropertyName("result")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Result { get; set; }

    /// <summary>Error details on failure.</summary>
    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public McpError? Error { get; set; }

    public static McpResponse Ok(string id, object? result = null) => new() {
        Id = id,
        Success = true,
        Result = result,
    };

    public static McpResponse Fail(string id, string code, string message, object? details = null) => new() {
        Id = id,
        Success = false,
        Error = new McpError {
            Code = code,
            Message = message,
            Details = details,
        },
    };

    public static McpResponse Fail(string id, McpError error) => new() {
        Id = id,
        Success = false,
        Error = error,
    };
}

/// <summary>
/// Error details for failed commands.
/// </summary>
public sealed class McpError {
    /// <summary>Error code (e.g., "COMMAND_NOT_FOUND", "INVALID_PARAMS").</summary>
    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    /// <summary>Human-readable error message.</summary>
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    /// <summary>Additional error context.</summary>
    [JsonPropertyName("details")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Details { get; set; }
}

/// <summary>
/// Common error codes.
/// </summary>
public static class McpErrorCodes {
    public const string CommandNotFound = "COMMAND_NOT_FOUND";
    public const string InvalidParams = "INVALID_PARAMS";
    public const string ExecutionFailed = "EXECUTION_FAILED";
    public const string Timeout = "TIMEOUT";
    public const string NotSupported = "NOT_SUPPORTED";
    public const string MenuItemNotFound = "MENU_ITEM_NOT_FOUND";
}

using System.Text.Json.Serialization;

namespace UnityMcp.Mcp;

/// <summary>
/// MCP server information returned during initialization.
/// </summary>
public sealed class ServerInfo {
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;
}

/// <summary>
/// MCP server capabilities.
/// </summary>
public sealed class ServerCapabilities {
    [JsonPropertyName("tools")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ToolsCapability? Tools { get; set; }
}

public sealed class ToolsCapability {
    [JsonPropertyName("listChanged")]
    public bool ListChanged { get; set; }
}

/// <summary>
/// MCP initialize request params.
/// </summary>
public sealed class InitializeParams {
    [JsonPropertyName("protocolVersion")]
    public string ProtocolVersion { get; set; } = string.Empty;

    [JsonPropertyName("capabilities")]
    public ClientCapabilities Capabilities { get; set; } = new();

    [JsonPropertyName("clientInfo")]
    public ClientInfo ClientInfo { get; set; } = new();
}

public sealed class ClientCapabilities { }

public sealed class ClientInfo {
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;
}

/// <summary>
/// MCP initialize response result.
/// </summary>
public sealed class InitializeResult {
    [JsonPropertyName("protocolVersion")]
    public string ProtocolVersion { get; set; } = "2024-11-05";

    [JsonPropertyName("capabilities")]
    public ServerCapabilities Capabilities { get; set; } = new();

    [JsonPropertyName("serverInfo")]
    public ServerInfo ServerInfo { get; set; } = new();
}

/// <summary>
/// MCP tool definition.
/// </summary>
public sealed class ToolDefinition {
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("inputSchema")]
    public InputSchema InputSchema { get; set; } = new();
}

/// <summary>
/// JSON Schema for tool input parameters.
/// </summary>
public sealed class InputSchema {
    [JsonPropertyName("type")]
    public string Type { get; set; } = "object";

    [JsonPropertyName("properties")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, PropertySchema>? Properties { get; set; }

    [JsonPropertyName("required")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Required { get; set; }
}

public sealed class PropertySchema {
    [JsonPropertyName("type")]
    public string Type { get; set; } = "string";

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    [JsonPropertyName("enum")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Enum { get; set; }

    [JsonPropertyName("default")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Default { get; set; }
}

/// <summary>
/// MCP tools/list response.
/// </summary>
public sealed class ToolsListResult {
    [JsonPropertyName("tools")]
    public List<ToolDefinition> Tools { get; set; } = [];
}

/// <summary>
/// MCP tools/call request params.
/// </summary>
public sealed class ToolCallParams {
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("arguments")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, object>? Arguments { get; set; }
}

/// <summary>
/// MCP tools/call response result.
/// </summary>
public sealed class ToolCallResult {
    [JsonPropertyName("content")]
    public List<ContentBlock> Content { get; set; } = [];

    [JsonPropertyName("isError")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsError { get; set; }
}

/// <summary>
/// Content block in tool response.
/// </summary>
public sealed class ContentBlock {
    [JsonPropertyName("type")]
    public string Type { get; set; } = "text";

    [JsonPropertyName("text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TextContent { get; set; }

    public static ContentBlock Text(string text) => new() { Type = "text", TextContent = text };
}

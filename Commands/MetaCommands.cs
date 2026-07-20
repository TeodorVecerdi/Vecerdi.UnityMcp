using System.Collections.Generic;
using System.Text.Json;
using Vecerdi.UnityMcp.Protocol;

namespace Vecerdi.UnityMcp.Commands;

/// <summary>
/// Command: unity.meta.listTools - Enumerate the commands this editor exposes as agent-facing
/// MCP tools (todo #243). The stdio bridge calls this when it (re)connects and registers the
/// results as dynamic tools, so tool additions on the Unity side need no bridge rebuild.
/// </summary>
public sealed class ListToolsCommand(McpCommandRegistry registry) : IMcpCommandHandler {
    public string Command => "unity.meta.listTools";

    public McpResponse Execute(McpRequest request) {
        var tools = new List<object>();
        foreach (var handler in registry.GetHandlers()) {
            if (handler is not IMcpToolProvider provider) {
                continue;
            }

            var descriptor = provider.ToolDescriptor;
            using var schema = JsonDocument.Parse(descriptor.InputSchemaJson);
            tools.Add(new Dictionary<string, object?> {
                ["name"] = descriptor.Name,
                ["description"] = descriptor.Description,
                ["inputSchema"] = schema.RootElement.Clone(),
                ["command"] = handler.Command,
            });
        }

        return McpResponse.Ok(request.Id, new Dictionary<string, object?> { ["tools"] = tools });
    }
}

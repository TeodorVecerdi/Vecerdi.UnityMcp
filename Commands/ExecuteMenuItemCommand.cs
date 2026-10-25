using UnityEditor;
using Vecerdi.UnityMcp.Protocol;

namespace Vecerdi.UnityMcp.Commands;

/// <summary>
/// Command: unity.editor.executeMenuItem - Execute a Unity menu item by path.
/// </summary>
public sealed class ExecuteMenuItemCommand : IMcpCommandHandler, IMcpToolProvider {
    public string Command => "unity.editor.executeMenuItem";

    public McpToolDescriptor ToolDescriptor { get; } = new(
        "execute_menu_item",
        "Execute a Unity Editor menu item by its path (e.g., 'File/Save Project', 'Edit/Project Settings...', 'Window/General/Console').",
        """{"type":"object","properties":{"menuItem":{"type":"string","description":"The menu item path to execute (e.g., 'File/Save Project')"}},"required":["menuItem"]}""");

    public McpResponse Execute(McpRequest request) {
        var menuItem = request.GetParam<string>("menuItem");

        if (string.IsNullOrEmpty(menuItem)) {
            return McpResponse.Fail(request.Id, McpErrorCodes.InvalidParams, "menuItem parameter is required");
        }

        var executed = EditorApplication.ExecuteMenuItem(menuItem);

        if (!executed) {
            return McpResponse.Fail(request.Id, McpErrorCodes.MenuItemNotFound, $"Menu item not found or could not be executed: {menuItem}");
        }

        return McpResponse.Ok(request.Id, new { executed = true, menuItem });
    }
}
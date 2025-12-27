using UnityEditor;
using UnityEngine;

namespace Vecerdi.UnityMcp;

/// <summary>
/// Editor window for monitoring and controlling the MCP server.
/// </summary>
public sealed class McpServerWindow : EditorWindow {
    private Vector2 m_ScrollPosition;

    [MenuItem("Window/Unity MCP Server")]
    public static void ShowWindow() {
        var window = GetWindow<McpServerWindow>();
        window.titleContent = new GUIContent("MCP Server");
        window.minSize = new Vector2(300, 200);
        window.Show();
    }

    private void OnEnable() {
        // Repaint periodically to show updated status
        EditorApplication.update += Repaint;
    }

    private void OnDisable() {
        EditorApplication.update -= Repaint;
    }

    private void OnGUI() {
        EditorGUILayout.Space(10);

        // Server Status Section
        DrawServerStatus();

        EditorGUILayout.Space(10);

        // Commands Section
        DrawCommands();
    }

    private void DrawServerStatus() {
        EditorGUILayout.LabelField("Server Status", EditorStyles.boldLabel);

        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox)) {
            var isRunning = McpEditorServer.IsRunning;

            // Status indicator
            using (new EditorGUILayout.HorizontalScope()) {
                var statusColor = isRunning ? Color.green : Color.red;
                var statusText = isRunning ? "Running" : "Stopped";

                var originalColor = GUI.color;
                GUI.color = statusColor;
                GUILayout.Label("\u25cf", GUILayout.Width(20)); // Circle indicator
                GUI.color = originalColor;

                EditorGUILayout.LabelField(statusText);
            }

            if (isRunning) {
                EditorGUILayout.LabelField($"Port: {McpEditorServer.Port}");
                EditorGUILayout.LabelField($"Connections: {McpEditorServer.ConnectionCount}");
                EditorGUILayout.LabelField($"URL: ws://localhost:{McpEditorServer.Port}/");

                EditorGUILayout.Space(5);

                if (GUILayout.Button("Stop Server")) {
                    McpEditorServer.Instance.Stop();
                }

                if (GUILayout.Button("Copy URL")) {
                    EditorGUIUtility.systemCopyBuffer = $"ws://localhost:{McpEditorServer.Port}/";
                    Debug.Log("[UnityMcp] URL copied to clipboard");
                }
            } else {
                EditorGUILayout.Space(5);

                if (GUILayout.Button("Start Server")) {
                    McpEditorServer.Instance.Start();
                }
            }
        }
    }

    private void DrawCommands() {
        EditorGUILayout.LabelField("Available Commands", EditorStyles.boldLabel);

        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox)) {
            m_ScrollPosition = EditorGUILayout.BeginScrollView(m_ScrollPosition, GUILayout.Height(200));

            var commands = new[] {
                ("unity.listCommands", "List all available commands"),
                ("unity.debug.getLogs", "Get recent console logs (params: count, minLevel, filter)"),
                ("unity.debug.clearLogs", "Clear the log buffer"),
                ("unity.editor.recompile", "Force script recompilation"),
                ("unity.editor.getCompilationStatus", "Check compilation state"),
                ("unity.editor.isPlaying", "Check play mode state"),
                ("unity.editor.enterPlayMode", "Enter play mode"),
                ("unity.editor.exitPlayMode", "Exit play mode"),
                ("unity.editor.pausePlayMode", "Pause play mode"),
                ("unity.editor.resumePlayMode", "Resume play mode"),
                ("unity.editor.getOpenScenes", "Get currently open scenes"),
                ("unity.editor.saveAll", "Save all scenes and assets"),
                ("unity.editor.refreshAssets", "Refresh asset database"),
            };

            foreach (var (command, description) in commands) {
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox)) {
                    EditorGUILayout.SelectableLabel(command, EditorStyles.boldLabel, GUILayout.Height(18));
                    EditorGUILayout.LabelField(description, EditorStyles.wordWrappedMiniLabel);
                }
            }

            EditorGUILayout.EndScrollView();
        }
    }
}

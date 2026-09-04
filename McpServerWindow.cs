using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Vecerdi.UnityMcp.Commands;

namespace Vecerdi.UnityMcp;

/// <summary>
/// Status window for the MCP server: what this editor advertises to agents, which other editors are
/// visible in the discovery file, and the live set of registered commands. Ideally never opened —
/// the server starts on its own — but when something looks off this is where to look first.
/// </summary>
public sealed class McpServerWindow : EditorWindow {
    private const double RepaintIntervalSeconds = 0.5;
    private const double InstancesRefreshIntervalSeconds = 2.0;

    private static readonly Color s_Green = new(0.35f, 0.8f, 0.4f);
    private static readonly Color s_Amber = new(0.95f, 0.7f, 0.25f);
    private static readonly Color s_Red = new(0.9f, 0.35f, 0.35f);

    private Vector2 m_ScrollPosition;
    private bool m_ShowCommands;
    private bool m_ShowOtherEditors = true;
    private double m_LastRepaintTime;
    private double m_LastInstancesRefreshTime;
    private List<EditorInstance> m_Instances = [];
    private GUIStyle? m_DotStyle;
    private GUIStyle? m_MonoStyle;

    private GUIStyle DotStyle => m_DotStyle ??= new GUIStyle(EditorStyles.boldLabel) { fontSize = 16, alignment = TextAnchor.MiddleCenter, fixedWidth = 18 };
    private GUIStyle MonoStyle => m_MonoStyle ??= new GUIStyle(EditorStyles.label) { font = EditorStyles.miniFont, wordWrap = true };

    [MenuItem("Window/Unity MCP Server")]
    public static void ShowWindow() {
        var window = GetWindow<McpServerWindow>();
        window.titleContent = new GUIContent("MCP Server");
        window.minSize = new Vector2(360, 240);
        window.Show();
    }

    private void OnEnable() {
        EditorApplication.update += OnEditorUpdate;
        RefreshInstances();
    }

    private void OnDisable() {
        EditorApplication.update -= OnEditorUpdate;
    }

    private void OnEditorUpdate() {
        var now = EditorApplication.timeSinceStartup;
        if (now - m_LastInstancesRefreshTime > InstancesRefreshIntervalSeconds) {
            RefreshInstances();
        }

        if (now - m_LastRepaintTime > RepaintIntervalSeconds) {
            m_LastRepaintTime = now;
            Repaint();
        }
    }

    private void RefreshInstances() {
        m_LastInstancesRefreshTime = EditorApplication.timeSinceStartup;
        try {
            m_Instances = EditorInstanceRegistry.GetInstances();
        } catch (Exception) {
            m_Instances = [];
        }
    }

    private void OnGUI() {
        m_ScrollPosition = EditorGUILayout.BeginScrollView(m_ScrollPosition);
        EditorGUILayout.Space(6);

        DrawThisEditor();
        EditorGUILayout.Space(8);
        DrawOtherEditors();
        EditorGUILayout.Space(8);
        DrawCommands();

        EditorGUILayout.Space(6);
        EditorGUILayout.EndScrollView();
    }

    private void DrawThisEditor() {
        var isRunning = McpEditorServer.IsRunning;
        var state = McpEditorServer.AdvertisedState;

        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox)) {
            using (new EditorGUILayout.HorizontalScope()) {
                DrawDot(!isRunning ? s_Red : state == EditorInstanceState.Ready ? s_Green : s_Amber);
                var headline = !isRunning ? "Stopped" : $"Listening on port {McpEditorServer.Port}";
                EditorGUILayout.LabelField(headline, EditorStyles.boldLabel);

                GUILayout.FlexibleSpace();
                if (isRunning) {
                    if (GUILayout.Button("Stop", GUILayout.Width(60))) {
                        McpEditorServer.Instance.Stop();
                    }
                } else if (GUILayout.Button("Start", GUILayout.Width(60))) {
                    McpEditorServer.Instance.Start();
                }
            }

            if (!isRunning) {
                EditorGUILayout.HelpBox("The server starts automatically with the editor. It only stays stopped if it was stopped here, or if this is an asset import worker process.", MessageType.Info);
                return;
            }

            EditorGUILayout.Space(4);
            DrawRow("State", DescribeState(state));
            DrawRow("Connections", McpEditorServer.ConnectionCount.ToString());

            var (logCount, logDropped) = McpEditorServer.LogBufferStats;
            DrawRow("Log buffer", logDropped > 0 ? $"{logCount} entries ({logDropped} dropped since startup)" : $"{logCount} entries");

            var pending = PendingInvocationRegistry.Count;
            if (pending > 0) {
                DrawRow("Pending invocations", pending.ToString());
            }

            DrawRow("Process", $"{Process.GetCurrentProcess().Id}");

            EditorGUILayout.Space(4);
            using (new EditorGUILayout.HorizontalScope()) {
                EditorGUILayout.LabelField("Discovery file");
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Reveal", GUILayout.Width(60))) {
                    EditorUtility.RevealInFinder(EditorInstanceRegistry.DiscoveryFilePath);
                }
            }

            var path = EditorInstanceRegistry.DiscoveryFilePath;
            var pathHeight = MonoStyle.CalcHeight(new GUIContent(path), EditorGUIUtility.currentViewWidth - 40);
            EditorGUILayout.SelectableLabel(path, MonoStyle, GUILayout.Height(pathHeight));
        }
    }

    private void DrawOtherEditors() {
        var currentPort = McpEditorServer.Port;
        var others = m_Instances.Where(i => i.Port != currentPort).OrderBy(i => i.Port).ToList();

        m_ShowOtherEditors = EditorGUILayout.Foldout(m_ShowOtherEditors, $"Other editors in the discovery file ({others.Count})", true);
        if (!m_ShowOtherEditors) {
            return;
        }

        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox)) {
            if (others.Count == 0) {
                EditorGUILayout.LabelField("None. Agents auto-connect to this editor.", EditorStyles.miniLabel);
                return;
            }

            EditorGUILayout.LabelField("With several editors running, agents must pick one with list_editors / select_editor.", EditorStyles.wordWrappedMiniLabel);
            foreach (var instance in others) {
                using (new EditorGUILayout.HorizontalScope()) {
                    DrawDot(instance.State == EditorInstanceState.Ready ? s_Green : s_Amber);
                    EditorGUILayout.LabelField($"{instance.ProjectName}  ·  port {instance.Port}  ·  {DescribeState(instance.State)}  ·  pid {instance.ProcessId}");
                }

                EditorGUILayout.LabelField(instance.ProjectPath, MonoStyle);
            }
        }
    }

    private void DrawCommands() {
        var handlers = McpEditorServer.RegisteredHandlers.OrderBy(h => h.Command, StringComparer.Ordinal).ToList();
        var toolCount = handlers.Count(h => h is IMcpToolProvider);

        m_ShowCommands = EditorGUILayout.Foldout(m_ShowCommands, $"Registered commands ({handlers.Count}, {toolCount} exposed as agent tools)", true);
        if (!m_ShowCommands) {
            return;
        }

        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox)) {
            EditorGUILayout.LabelField(
                "Read live from the command registry. Commands with a tool descriptor are advertised to the bridge as dynamic MCP tools; the rest are used by the bridge's own native tools.",
                EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.Space(2);

            foreach (var handler in handlers) {
                using (new EditorGUILayout.HorizontalScope()) {
                    EditorGUILayout.SelectableLabel(handler.Command, EditorStyles.boldLabel, GUILayout.Height(EditorGUIUtility.singleLineHeight), GUILayout.ExpandWidth(true));
                    if (handler is IMcpToolProvider { ToolDescriptor: var descriptor }) {
                        EditorGUILayout.LabelField(new GUIContent($"tool: {descriptor.Name}", descriptor.Description), EditorStyles.miniLabel, GUILayout.Width(200));
                    }
                }
            }
        }
    }

    private void DrawDot(Color color) {
        var previous = GUI.color;
        GUI.color = color;
        GUILayout.Label("●", DotStyle);
        GUI.color = previous;
    }

    private static void DrawRow(string label, string value) {
        using (new EditorGUILayout.HorizontalScope()) {
            EditorGUILayout.PrefixLabel(label);
            EditorGUILayout.LabelField(value);
        }
    }

    private static string DescribeState(string state) => state switch {
        EditorInstanceState.Ready => "ready",
        EditorInstanceState.Compiling => "compiling (agents' calls wait for the reload)",
        EditorInstanceState.Reloading => "reloading scripts",
        _ => state,
    };
}

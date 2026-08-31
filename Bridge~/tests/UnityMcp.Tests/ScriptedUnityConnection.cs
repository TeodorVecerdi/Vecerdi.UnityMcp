using System.Text.Json;
using UnityMcp;

namespace UnityMcp.Tests;

/// <summary>
/// An <see cref="IUnityConnection"/> whose <see cref="SendAsync"/> is driven by a per-command script,
/// so orchestration logic (fresh-diagnostics assembly) can be tested without a live editor.
/// </summary>
internal sealed class ScriptedUnityConnection : IUnityConnection {
    private readonly Func<string, object?, UnityResponse> m_Responder;

    public ScriptedUnityConnection(Func<string, object?, UnityResponse> responder) {
        m_Responder = responder;
    }

    public event Action? Connected;
    public bool IsConnected { get; set; } = true;
    public string CurrentUri => "ws://scripted/";

    public Task ConnectAsync(CancellationToken ct = default) {
        IsConnected = true;
        Connected?.Invoke();
        return Task.CompletedTask;
    }

    public Task DisconnectAsync() {
        IsConnected = false;
        return Task.CompletedTask;
    }

    public Task<bool> WaitForConnectionAsync(TimeSpan timeout, TimeSpan pollInterval, CancellationToken ct = default) {
        IsConnected = true;
        return Task.FromResult(true);
    }

    public Task<UnityResponse> SendAsync(string command, object? parameters = null, CancellationToken ct = default) =>
        Task.FromResult(m_Responder(command, parameters));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>Build a <c>unity.debug.getLogs</c>-shaped response from the given entries.</summary>
    public static UnityResponse LogsResponse(params (string Level, string Message, DateTimeOffset? Timestamp)[] entries) {
        var logs = entries.Select(e => new Dictionary<string, object?> {
            ["level"] = e.Level,
            ["message"] = e.Message,
            ["stackTrace"] = (string?)null,
            ["timestamp"] = e.Timestamp?.ToString("o"),
        }).ToArray();

        var payload = new { logs, total = logs.Length };
        var json = JsonSerializer.Serialize(payload);
        using var doc = JsonDocument.Parse(json);
        return new UnityResponse { Id = "1", Success = true, Result = doc.RootElement.Clone() };
    }

    /// <summary>Build a <c>unity.editor.getCompilationStatus</c>-shaped response.</summary>
    public static UnityResponse CompilationStatusResponse(bool isCompiling, bool isUpdating) {
        var json = JsonSerializer.Serialize(new { isCompiling, isUpdating });
        using var doc = JsonDocument.Parse(json);
        return new UnityResponse { Id = "1", Success = true, Result = doc.RootElement.Clone() };
    }
}

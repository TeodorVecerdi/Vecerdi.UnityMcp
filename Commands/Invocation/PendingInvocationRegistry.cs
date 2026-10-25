using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Unity.Scripting.LifecycleManagement;

namespace Vecerdi.UnityMcp.Commands;

/// <summary>
/// Tasks returned by invoked methods that outlived the bounded wait. Entries are handed out
/// once (removed when a completed/faulted result is retrieved) and expire after an hour or on
/// domain reload — a pending invocation is a live poll target, not durable storage.
/// </summary>
[NoAutoStaticsCleanup]
internal static class PendingInvocationRegistry {
    private sealed record Entry(Task Task, string Method, DateTime StartedUtc);

    private static readonly ConcurrentDictionary<string, Entry> s_Entries = new();
    private static readonly TimeSpan s_MaxAge = TimeSpan.FromHours(1);

    public static string Register(Task task, string method) {
        foreach (var (key, entry) in s_Entries) {
            if (DateTime.UtcNow - entry.StartedUtc > s_MaxAge) {
                s_Entries.TryRemove(key, out _);
            }
        }

        var id = Guid.NewGuid().ToString("N")[..12];
        s_Entries[id] = new Entry(task, method, DateTime.UtcNow);
        return id;
    }

    public static bool TryGet(string id, out Task task, out string method, out TimeSpan elapsed) {
        if (s_Entries.TryGetValue(id, out var entry)) {
            task = entry.Task;
            method = entry.Method;
            elapsed = DateTime.UtcNow - entry.StartedUtc;
            return true;
        }

        task = null!;
        method = string.Empty;
        elapsed = default;
        return false;
    }

    public static void Remove(string id) => s_Entries.TryRemove(id, out _);

    /// <summary>Invocations still being polled for (or never claimed and not yet expired).</summary>
    public static int Count => s_Entries.Count;
}

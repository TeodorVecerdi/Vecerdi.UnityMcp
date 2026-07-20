using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Vecerdi.UnityMcp.Protocol;
using Object = UnityEngine.Object;

namespace Vecerdi.UnityMcp.Commands;

/// <summary>
/// Tasks returned by invoked methods that outlived the bounded wait. Entries are handed out
/// once (removed when a completed/faulted result is retrieved) and expire after an hour or on
/// domain reload — a pending invocation is a live poll target, not durable storage.
/// </summary>
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
}

/// <summary>
/// Command: unity.managed.getInvocationResult - Poll a pending invocation started by
/// unity.managed.invokeMethod (todo #387).
/// </summary>
public sealed class GetInvocationResultCommand : IMcpCommandHandler, IMcpToolProvider {
    public string Command => "unity.managed.getInvocationResult";

    public McpToolDescriptor ToolDescriptor { get; } = new(
        "get_invocation_result",
        "Poll the outcome of a backgrounded invoke_managed_method call. When invoke_managed_method returns {pending: true, invocationId}, the invoked Task kept running without blocking the editor; call this with that invocationId until status is 'completed' (includes returnValue) or the call reports the failure. Results are handed out once - the pending entry is removed on a completed/faulted poll, and unclaimed entries expire after ~1 hour or on domain reload.",
        """{"type":"object","properties":{"invocationId":{"type":"string","description":"The invocationId returned by a pending invoke_managed_method call"}},"required":["invocationId"]}""");

    public McpResponse Execute(McpRequest request) {
        var invocationId = request.GetParam<string>("invocationId");
        if (string.IsNullOrWhiteSpace(invocationId)) {
            return McpResponse.Fail(request.Id, McpErrorCodes.InvalidParams, "invocationId is required");
        }

        if (!PendingInvocationRegistry.TryGet(invocationId, out var task, out var method, out var elapsed)) {
            return McpResponse.Fail(request.Id, McpErrorCodes.InvalidParams, $"Unknown or expired invocationId '{invocationId}' (results are handed out once; entries expire after an hour or on domain reload).");
        }

        if (!task.IsCompleted) {
            return McpResponse.Ok(request.Id, new Dictionary<string, object?> {
                ["status"] = "running",
                ["method"] = method,
                ["elapsedSeconds"] = Math.Round(elapsed.TotalSeconds, 1),
            });
        }

        PendingInvocationRegistry.Remove(invocationId);

        if (task.IsCanceled) {
            return McpResponse.Fail(request.Id, McpErrorCodes.ExecutionFailed, $"Invocation '{method}' was cancelled.");
        }

        if (task.IsFaulted) {
            var inner = task.Exception?.GetBaseException() ?? new Exception("Unknown failure");
            return McpResponse.Fail(
                request.Id,
                McpErrorCodes.ExecutionFailed,
                $"Invocation '{method}' failed: {inner.Message}",
                new {
                    exception = inner.GetType().FullName,
                    stackTrace = inner.StackTrace,
                }
            );
        }

        return McpResponse.Ok(request.Id, new Dictionary<string, object?> {
            ["status"] = "completed",
            ["method"] = method,
            ["returnValue"] = InvokeManagedMethodCommand.SerializeResult(InvokeManagedMethodCommand.ExtractTaskResult(task)),
        });
    }
}

/// <summary>
/// Command: unity.managed.invokeMethod - Invoke a managed method via reflection.
/// </summary>
public sealed class InvokeManagedMethodCommand : IMcpCommandHandler {
    public string Command => "unity.managed.invokeMethod";

    /// <summary>Default bounded wait for Task-returning methods. Must stay well under the
    /// bridge's 30s per-request timeout.</summary>
    private const int DefaultTaskWaitMs = 2000;
    private const int MaxTaskWaitMs = 25000;

    private static readonly JsonSerializerOptions s_JsonOptions = new() {
        PropertyNameCaseInsensitive = true,
    };

    public McpResponse Execute(McpRequest request) {
        var typeName = request.GetParam<string>("typeName");
        var methodName = request.GetParam<string>("methodName");
        var assemblyName = request.GetParam<string>("assemblyName");
        var parameterTypeNames = request.GetParam<string[]>("parameterTypeNames") ?? [];
        var genericTypeNames = request.GetParam<string[]>("genericTypeNames") ?? [];
        var argumentElements = request.GetParam<JsonElement[]>("arguments") ?? [];
        var constructorArgumentElements = request.GetParam<JsonElement[]>("constructorArguments") ?? [];
        var includeNonPublic = request.GetParam("includeNonPublic", false);
        var invokeOnInstance = request.GetParam("invokeOnInstance", false);

        if (string.IsNullOrWhiteSpace(typeName)) {
            return McpResponse.Fail(request.Id, McpErrorCodes.InvalidParams, "typeName is required");
        }

        if (string.IsNullOrWhiteSpace(methodName)) {
            return McpResponse.Fail(request.Id, McpErrorCodes.InvalidParams, "methodName is required");
        }

        if (!TryResolveType(typeName, assemblyName, out var targetType, out var typeResolveError)) {
            return McpResponse.Fail(request.Id, McpErrorCodes.InvalidParams, typeResolveError ?? $"Unable to resolve type '{typeName}'");
        }

        var bindingFlags = BindingFlags.FlattenHierarchy
                         | (includeNonPublic ? BindingFlags.Public | BindingFlags.NonPublic : BindingFlags.Public)
                         | (invokeOnInstance ? BindingFlags.Instance : BindingFlags.Static);

        MethodInfo? resolvedMethod = null;
        object?[]? convertedArguments = null;
        string? resolutionError = null;

        var candidateMethods = targetType.GetMethods(bindingFlags)
            .Where(m => string.Equals(m.Name, methodName, StringComparison.Ordinal))
            .ToList();

        if (candidateMethods.Count == 0) {
            return McpResponse.Fail(
                request.Id,
                McpErrorCodes.CommandNotFound,
                $"No method named '{methodName}' found on type '{targetType.FullName}' with the requested visibility/static settings."
            );
        }

        foreach (var candidate in candidateMethods) {
            var method = candidate;

            if (genericTypeNames.Length > 0) {
                if (!candidate.IsGenericMethodDefinition || candidate.GetGenericArguments().Length != genericTypeNames.Length) {
                    continue;
                }

                if (!TryResolveTypes(genericTypeNames, null, out var genericTypes, out var genericTypeError)) {
                    return McpResponse.Fail(request.Id, McpErrorCodes.InvalidParams, genericTypeError ?? "Failed to resolve generic type arguments.");
                }

                method = candidate.MakeGenericMethod(genericTypes!);
            } else if (candidate.IsGenericMethodDefinition) {
                continue;
            }

            var parameters = method.GetParameters();

            if (parameterTypeNames.Length > 0) {
                if (!TryResolveTypes(parameterTypeNames, null, out var requestedParameterTypes, out var parameterTypeError)) {
                    return McpResponse.Fail(request.Id, McpErrorCodes.InvalidParams, parameterTypeError ?? "Failed to resolve parameterTypeNames.");
                }

                if (!ParameterTypesMatch(parameters, requestedParameterTypes!)) {
                    continue;
                }
            }

            if (!TryConvertArguments(argumentElements, parameters, out var candidateArguments, out var conversionError)) {
                resolutionError = conversionError;
                continue;
            }

            resolvedMethod = method;
            convertedArguments = candidateArguments;
            resolutionError = null;
            break;
        }

        if (resolvedMethod is null || convertedArguments is null) {
            // Fold the candidate signatures into the message itself — clients that only surface the error text (not
            // the structured data) still need to see the shapes the caller can actually target.
            var overloads = string.Join(", ", candidateMethods.Select(DescribeMethod));
            var baseMessage = resolutionError ?? $"Unable to resolve a matching overload for '{targetType.FullName}.{methodName}'.";

            return McpResponse.Fail(
                request.Id,
                McpErrorCodes.InvalidParams,
                $"{baseMessage} Available overload(s): {overloads}.",
                new {
                    type = targetType.FullName,
                    method = methodName,
                    argumentCount = argumentElements.Length,
                    requestedParameterTypeCount = parameterTypeNames.Length,
                }
            );
        }

        object? instance = null;
        if (invokeOnInstance) {
            if (!TryCreateInstance(targetType, constructorArgumentElements, includeNonPublic, out instance, out var createError)) {
                return McpResponse.Fail(request.Id, McpErrorCodes.ExecutionFailed, createError ?? $"Failed to create instance of '{targetType.FullName}'.");
            }
        }

        var waitMs = Math.Clamp(request.GetParam("waitMs", DefaultTaskWaitMs), 0, MaxTaskWaitMs);

        try {
            var invocationResult = resolvedMethod.Invoke(instance, convertedArguments);
            if (invocationResult is not null and not Task && TryConvertUniTaskToTask(invocationResult) is { } convertedTask) {
                invocationResult = convertedTask;
            }

            var awaitedTask = false;
            object? returnValue = invocationResult;

            if (invocationResult is Task task) {
                awaitedTask = true;

                // Bounded wait, never GetResult(): this runs on the editor main thread, and an
                // unbounded block deadlocks any task whose continuations need that same thread
                // (UniTask/PlayerLoop, Unity sync context — todo #387). If the task outlives
                // the window, hand back a poll handle instead; once this method returns, the
                // main thread resumes pumping and the stuck continuations can actually run.
                bool completedInTime;
                try {
                    completedInTime = task.Wait(waitMs);
                } catch (AggregateException aggregate) {
                    var inner = aggregate.GetBaseException();
                    return McpResponse.Fail(
                        request.Id,
                        McpErrorCodes.ExecutionFailed,
                        $"Method invocation failed: {inner.Message}",
                        new {
                            exception = inner.GetType().FullName,
                            stackTrace = inner.StackTrace,
                        }
                    );
                }

                if (!completedInTime) {
                    var invocationId = PendingInvocationRegistry.Register(task, DescribeMethod(resolvedMethod));
                    // The task may still be using the instance — leave disposal to the task's
                    // owner rather than yanking it mid-flight (clears the finally's dispose).
                    instance = null;

                    return McpResponse.Ok(request.Id, new Dictionary<string, object?> {
                        ["pending"] = true,
                        ["invocationId"] = invocationId,
                        ["returnType"] = FriendlyTypeName(resolvedMethod.ReturnType),
                        ["message"] = $"Task still running after {waitMs}ms; poll get_invocation_result with this invocationId.",
                    });
                }

                returnValue = ExtractTaskResult(task);
            }

            var serializedValue = SerializeResult(returnValue);

            // Keep the payload lean: the caller already knows the type/method/flags it requested, so echoing them
            // back is noise. Surface only what interprets the result (its type) plus anything the caller couldn't
            // predict — whether a Task was awaited, and which overload ran when the method name was ambiguous.
            var payload = new Dictionary<string, object?> {
                ["returnValue"] = serializedValue,
                ["returnType"] = FriendlyTypeName(resolvedMethod.ReturnType),
            };

            if (awaitedTask) {
                payload["awaitedTask"] = true;
            }

            if (candidateMethods.Count > 1) {
                payload["resolvedOverload"] = DescribeMethod(resolvedMethod);
            }

            return McpResponse.Ok(request.Id, payload);
        } catch (TargetInvocationException ex) {
            var inner = ex.InnerException ?? ex;
            return McpResponse.Fail(
                request.Id,
                McpErrorCodes.ExecutionFailed,
                $"Method invocation failed: {inner.Message}",
                new {
                    exception = inner.GetType().FullName,
                    stackTrace = inner.StackTrace,
                }
            );
        } catch (Exception ex) {
            return McpResponse.Fail(
                request.Id,
                McpErrorCodes.ExecutionFailed,
                $"Method invocation failed: {ex.Message}",
                new {
                    exception = ex.GetType().FullName,
                    stackTrace = ex.StackTrace,
                }
            );
        } finally {
            if (instance is IDisposable disposable) {
                disposable.Dispose();
            }
        }
    }

    private static bool TryResolveType(string typeName, string? assemblyName, out Type type, out string? error) {
        if (!string.IsNullOrWhiteSpace(assemblyName)) {
            try {
                var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => string.Equals(a.GetName().Name, assemblyName, StringComparison.OrdinalIgnoreCase))
                            ?? Assembly.Load(assemblyName);

                var explicitType = assembly.GetType(typeName, false, true);
                if (explicitType is not null) {
                    type = explicitType;
                    error = null;
                    return true;
                }
            } catch (Exception ex) {
                type = null!;
                error = $"Failed to load assembly '{assemblyName}': {ex.Message}";
                return false;
            }
        }

        var direct = Type.GetType(typeName, false, true);
        if (direct is not null) {
            type = direct;
            error = null;
            return true;
        }

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()) {
            var found = assembly.GetType(typeName, false, true);
            if (found is null) continue;
            type = found;
            error = null;
            return true;
        }

        type = null!;
        error = $"Unable to resolve type '{typeName}'.";
        return false;
    }

    private static bool TryResolveTypes(string[] typeNames, string? assemblyName, out Type[]? types, out string? error) {
        var resolved = new List<Type>(typeNames.Length);

        foreach (var typeName in typeNames) {
            if (!TryResolveType(typeName, assemblyName, out var type, out error)) {
                types = null;
                return false;
            }

            resolved.Add(type);
        }

        types = [.. resolved];
        error = null;
        return true;
    }

    private static bool ParameterTypesMatch(ParameterInfo[] parameters, Type[] requestedTypes) {
        if (parameters.Length != requestedTypes.Length) return false;

        for (var i = 0; i < parameters.Length; i++) {
            if (parameters[i].ParameterType != requestedTypes[i]) {
                return false;
            }
        }

        return true;
    }

    private static bool TryConvertArguments(
        JsonElement[] rawArguments,
        ParameterInfo[] parameters,
        out object?[]? convertedArguments,
        out string? error
    ) {
        if (rawArguments.Length > parameters.Length) {
            convertedArguments = null;
            error = $"Too many arguments. Method expects {parameters.Length}, received {rawArguments.Length}.";
            return false;
        }

        var args = new object?[parameters.Length];

        for (var i = 0; i < parameters.Length; i++) {
            var parameter = parameters[i];
            var parameterType = parameter.ParameterType;

            if (parameterType.IsByRef || parameterType.IsPointer) {
                convertedArguments = null;
                error = $"Unsupported parameter type for '{parameter.Name}': by-ref and pointer parameters are not supported.";
                return false;
            }

            if (i >= rawArguments.Length) {
                if (parameter.HasDefaultValue) {
                    args[i] = parameter.DefaultValue;
                    continue;
                }

                convertedArguments = null;
                error = $"Missing required argument for parameter '{parameter.Name}'.";
                return false;
            }

            if (!TryConvertArgument(rawArguments[i], parameterType, out var convertedValue, out error)) {
                convertedArguments = null;
                error = $"Failed to convert argument {i} for parameter '{parameter.Name}': {error}";
                return false;
            }

            args[i] = convertedValue;
        }

        convertedArguments = args;
        error = null;
        return true;
    }

    private static bool TryConvertArgument(JsonElement value, Type targetType, out object? result, out string? error) {
        if (targetType == typeof(JsonElement)) {
            result = value;
            error = null;
            return true;
        }

        if (value.ValueKind == JsonValueKind.Null) {
            var underlyingNullableType = Nullable.GetUnderlyingType(targetType);
            if (!targetType.IsValueType || underlyingNullableType is not null) {
                result = null;
                error = null;
                return true;
            }

            result = null;
            error = $"Cannot assign null to non-nullable value type '{targetType.FullName}'.";
            return false;
        }

        var effectiveType = Nullable.GetUnderlyingType(targetType) ?? targetType;

        if (effectiveType.IsEnum) {
            try {
                if (value.ValueKind == JsonValueKind.String) {
                    var enumText = value.GetString();
                    result = Enum.Parse(effectiveType, enumText!, true);
                    error = null;
                    return true;
                }

                if (value.ValueKind == JsonValueKind.Number) {
                    var raw = value.GetInt32();
                    result = Enum.ToObject(effectiveType, raw);
                    error = null;
                    return true;
                }
            } catch (Exception ex) {
                result = null;
                error = ex.Message;
                return false;
            }
        }

        try {
            if (targetType == typeof(object)) {
                result = JsonSerializer.Deserialize<object>(value.GetRawText(), s_JsonOptions);
                error = null;
                return true;
            }

            result = JsonSerializer.Deserialize(value.GetRawText(), targetType, s_JsonOptions);
            error = null;
            return true;
        } catch (Exception ex) {
            result = null;
            error = ex.Message;
            return false;
        }
    }

    private static bool TryCreateInstance(
        Type targetType,
        JsonElement[] constructorArguments,
        bool includeNonPublic,
        out object? instance,
        out string? error
    ) {
        if (targetType.IsAbstract && targetType.IsSealed) {
            instance = null;
            error = $"Type '{targetType.FullName}' is static and cannot be instantiated.";
            return false;
        }

        var constructorFlags = BindingFlags.Instance | (includeNonPublic ? BindingFlags.Public | BindingFlags.NonPublic : BindingFlags.Public);
        var constructors = targetType.GetConstructors(constructorFlags);

        if (constructors.Length == 0) {
            if (constructorArguments.Length == 0) {
                try {
                    instance = Activator.CreateInstance(targetType, includeNonPublic);
                    error = null;
                    return instance is not null;
                } catch (Exception ex) {
                    instance = null;
                    error = ex.Message;
                    return false;
                }
            }

            instance = null;
            error = $"Type '{targetType.FullName}' does not have accessible constructors.";
            return false;
        }

        foreach (var constructor in constructors.OrderBy(c => c.GetParameters().Length)) {
            if (!TryConvertArguments(constructorArguments, constructor.GetParameters(), out var args, out _)) {
                continue;
            }

            try {
                instance = constructor.Invoke(args);
                error = null;
                return true;
            } catch (Exception ex) {
                instance = null;
                error = ex.Message;
                return false;
            }
        }

        instance = null;
        error = $"No constructor overload matched {constructorArguments.Length} constructor argument(s).";
        return false;
    }

    /// <summary>Result value of a completed <see cref="Task"/> (null for non-generic tasks and
    /// async-void-shaped <c>Task&lt;VoidTaskResult&gt;</c>).</summary>
    internal static object? ExtractTaskResult(Task task) {
        var taskType = task.GetType();
        if (!taskType.IsGenericType) {
            return null;
        }

        var resultProperty = taskType.GetProperty("Result", BindingFlags.Public | BindingFlags.Instance);
        var result = resultProperty?.GetValue(task);
        return result?.GetType().Name == "VoidTaskResult" ? null : result;
    }

    /// <summary>
    /// Converts a boxed <c>UniTask</c>/<c>UniTask&lt;T&gt;</c> return value to a <see cref="Task"/>
    /// via Cysharp's <c>UniTaskExtensions.AsTask</c>, resolved by reflection so this assembly
    /// needs no UniTask reference. Returns null for anything else (including <c>UniTaskVoid</c>,
    /// which is fire-and-forget by contract and cannot be observed).
    /// </summary>
    private static Task? TryConvertUniTaskToTask(object invocationResult) {
        var type = invocationResult.GetType();
        var fullName = type.IsGenericType ? type.GetGenericTypeDefinition().FullName : type.FullName;
        if (fullName is not "Cysharp.Threading.Tasks.UniTask" and not "Cysharp.Threading.Tasks.UniTask`1") {
            return null;
        }

        var extensions = type.Assembly.GetType("Cysharp.Threading.Tasks.UniTaskExtensions");
        if (extensions is null) {
            return null;
        }

        try {
            if (type.IsGenericType) {
                var generic = extensions.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => m.Name == "AsTask" && m.IsGenericMethod && m.GetParameters() is { Length: 1 } p && p[0].ParameterType.IsGenericType);
                return generic is null ? null : (Task?)generic.MakeGenericMethod(type.GetGenericArguments()).Invoke(null, [invocationResult]);
            }

            var nonGeneric = extensions.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "AsTask" && !m.IsGenericMethod && m.GetParameters() is { Length: 1 } p && p[0].ParameterType == type);
            return (Task?)nonGeneric?.Invoke(null, [invocationResult]);
        } catch {
            return null; // fall back to serializing the raw struct, as before
        }
    }

    /// <summary>
    /// Renders a method as a compact, human-readable signature, e.g. <c>FloatSeeded(Int64)</c> or
    /// <c>Element&lt;String&gt;(String, String)</c>. Used to disambiguate overloads in responses.
    /// </summary>
    private static string DescribeMethod(MethodInfo method) {
        var parameters = string.Join(", ", method.GetParameters().Select(p => FriendlyTypeName(p.ParameterType)));

        if (!method.IsGenericMethod) {
            return $"{method.Name}({parameters})";
        }

        var genericArgs = string.Join(", ", method.GetGenericArguments().Select(FriendlyTypeName));
        return $"{method.Name}<{genericArgs}>({parameters})";
    }

    /// <summary>
    /// Short, readable type name — unwraps <see cref="Nullable{T}"/> and renders generics as
    /// <c>List&lt;Int32&gt;</c> rather than the mangled reflection form.
    /// </summary>
    private static string FriendlyTypeName(Type type) {
        if (Nullable.GetUnderlyingType(type) is { } underlying) {
            return FriendlyTypeName(underlying) + "?";
        }

        if (!type.IsGenericType) {
            return type.Name;
        }

        var name = type.Name;
        var tick = name.IndexOf('`');
        if (tick >= 0) {
            name = name[..tick];
        }

        var args = string.Join(", ", type.GetGenericArguments().Select(FriendlyTypeName));
        return $"{name}<{args}>";
    }

    internal static object? SerializeResult(object? value, int depth = 0) {
        if (value is null) return null;
        if (depth > 4) return value.ToString();

        var type = value.GetType();

        if (type.IsPrimitive || value is string || value is decimal || value is DateTime || value is DateTimeOffset || value is TimeSpan || value is Guid) {
            return value;
        }

        if (type.IsEnum) {
            return value.ToString();
        }

        if (value is Object unityObject) {
            return new {
                type = type.FullName ?? type.Name,
                name = unityObject.name,
                entityId = unityObject.GetEntityId(),
            };
        }

        if (value is IDictionary dictionary) {
            var entries = new List<object?>();
            var count = 0;

            foreach (DictionaryEntry entry in dictionary) {
                entries.Add(new {
                    key = SerializeResult(entry.Key, depth + 1),
                    value = SerializeResult(entry.Value, depth + 1),
                });

                count++;
                if (count >= 100) break;
            }

            return new {
                count = dictionary.Count,
                entries,
                truncated = dictionary.Count > count,
            };
        }

        if (value is IEnumerable enumerable) {
            var items = new List<object?>();
            var count = 0;

            foreach (var item in enumerable) {
                items.Add(SerializeResult(item, depth + 1));
                count++;
                if (count >= 100) break;
            }

            return new {
                count,
                items,
                truncated = count >= 100,
            };
        }

        try {
            var json = JsonSerializer.Serialize(value, type, s_JsonOptions);
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        } catch {
            return value.ToString();
        }
    }
}

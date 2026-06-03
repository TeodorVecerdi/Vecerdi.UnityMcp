using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Vecerdi.UnityMcp.Protocol;
using Object = UnityEngine.Object;

namespace Vecerdi.UnityMcp.Commands;

/// <summary>
/// Command: unity.managed.invokeMethod - Invoke a managed method via reflection.
/// </summary>
public sealed class InvokeManagedMethodCommand : IMcpCommandHandler {
    public string Command => "unity.managed.invokeMethod";

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
            return McpResponse.Fail(
                request.Id,
                McpErrorCodes.InvalidParams,
                resolutionError ?? $"Unable to resolve a matching overload for '{targetType.FullName}.{methodName}'.",
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

        try {
            var invocationResult = resolvedMethod.Invoke(instance, convertedArguments);
            var returnValue = AwaitIfTask(invocationResult, out var awaitedTask);
            var serializedValue = SerializeResult(returnValue);

            return McpResponse.Ok(request.Id, new {
                typeName = targetType.FullName ?? targetType.Name,
                methodName = resolvedMethod.Name,
                isStatic = resolvedMethod.IsStatic,
                invokedOnInstance = invokeOnInstance,
                awaitedTask,
                returnType = resolvedMethod.ReturnType.FullName ?? resolvedMethod.ReturnType.Name,
                returnValue = serializedValue,
            });
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

    private static object? AwaitIfTask(object? invocationResult, out bool awaitedTask) {
        awaitedTask = false;

        if (invocationResult is not Task task) {
            return invocationResult;
        }

        awaitedTask = true;
        task.GetAwaiter().GetResult();

        var taskType = task.GetType();
        if (!taskType.IsGenericType) {
            return null;
        }

        var resultProperty = taskType.GetProperty("Result", BindingFlags.Public | BindingFlags.Instance);
        return resultProperty?.GetValue(task);
    }

    private static object? SerializeResult(object? value, int depth = 0) {
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
                instanceId = unityObject.GetInstanceID(),
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

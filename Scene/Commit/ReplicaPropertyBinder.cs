using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using Ape.Core.Logging;
using Ape.Core.Scene;
using MessagePack;

namespace Ape.Core.Scene.Commit;

/// <summary>
/// Applies property writes by name using reflection with <see cref="KeyAttribute"/> filtering (same idea as <see cref="Ape.Core.Replication.Replica"/> snapshot/deserialize).
/// PropertyInfo is cached per (runtime type, name).
/// </summary>
public static class ReplicaPropertyBinder
{
    private static readonly ConcurrentDictionary<(Type DeclaringType, string Name), PropertyInfo?> Cache = new();

    /// <summary>
    /// Sets a public instance property marked with <see cref="KeyAttribute"/> on <paramref name="instance"/>.
    /// </summary>
    /// <returns>False if property missing, not key-serialized, or assignment/conversion failed.</returns>
    public static bool TrySet(object instance, string propertyName, object? value, ILogger? logger)
    {
        var type = instance.GetType();
        var prop = Cache.GetOrAdd((type, propertyName), static key =>
        {
            var p = key.DeclaringType.GetProperty(key.Name, BindingFlags.Public | BindingFlags.Instance);
            if (p == null || !p.CanRead || !p.CanWrite || p.GetCustomAttribute<KeyAttribute>() == null)
                return null;
            return p;
        });

        if (prop == null)
        {
            logger?.LogWarning($"[ReplicaPropertyBinder] No [Key] property '{propertyName}' on {type.Name}");
            return false;
        }

        object? converted = value;
        var targetType = prop.PropertyType;
        if (value != null)
        {
            var vt = value.GetType();
            if (targetType != vt && !targetType.IsAssignableFrom(vt))
            {
                try
                {
                    converted = Convert.ChangeType(value, targetType, CultureInfo.InvariantCulture);
                }
                catch (Exception ex)
                {
                    logger?.LogWarning($"[ReplicaPropertyBinder] Cannot convert value for '{propertyName}' on {type.Name}: {ex.Message}");
                    return false;
                }
            }
        }
        else if (targetType.IsValueType && Nullable.GetUnderlyingType(targetType) == null)
        {
            logger?.LogWarning($"[ReplicaPropertyBinder] Cannot set null to value-type property '{propertyName}' on {type.Name}");
            return false;
        }

        try
        {
            prop.SetValue(instance, converted);
            return true;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is SceneMutationOutsideScopeException inner)
        {
            throw inner;
        }
        catch (SceneMutationOutsideScopeException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger?.LogWarning($"[ReplicaPropertyBinder] SetValue failed for '{propertyName}' on {type.Name}: {ex.Message}");
            return false;
        }
    }
}

using System.Collections.Generic;

namespace Ape.Core.Utils;

/// <summary>
/// Extension methods and utilities for Dictionary operations.
/// </summary>
public static class DictionaryExtensions
{
    /// <summary>
    /// Safely appends data to a dictionary value, handling cases where the key doesn't exist yet.
    /// If the key doesn't exist, it will be created with the new data as the initial value.
    /// If the key exists, the new data will be appended to the existing value.
    /// </summary>
    /// <param name="dictionary">The dictionary to update</param>
    /// <param name="key">The key to append to</param>
    /// <param name="data">The data to append (will be converted to string)</param>
    /// <example>
    /// <code>
    /// var deviceData = new Dictionary&lt;string, object&gt;();
    /// deviceData.AppendValue("lastData", "Hello ");
    /// deviceData.AppendValue("lastData", "World!");
    /// // Result: deviceData["lastData"] = "Hello World!"
    /// </code>
    /// </example>
    public static void AppendValue(this Dictionary<string, object> dictionary, string key, object? data)
    {
        var existingValue = dictionary.TryGetValue(key, out var existingObj) ? existingObj?.ToString() ?? "" : "";
        dictionary[key] = existingValue + data?.ToString();
    }

    /// <summary>
    /// Safely gets a string value from the dictionary, returning empty string if key doesn't exist.
    /// </summary>
    /// <param name="dictionary">The dictionary to read from</param>
    /// <param name="key">The key to get</param>
    /// <returns>The string value, or empty string if key doesn't exist</returns>
    public static string GetStringValue(this Dictionary<string, object> dictionary, string key)
    {
        return dictionary.TryGetValue(key, out var value) ? value?.ToString() ?? "" : "";
    }

    /// <summary>
    /// Safely sets a value in the dictionary, handling null values gracefully.
    /// </summary>
    /// <param name="dictionary">The dictionary to update</param>
    /// <param name="key">The key to set</param>
    /// <param name="value">The value to set</param>
    public static void SetValue(this Dictionary<string, object> dictionary, string key, object? value)
    {
        dictionary[key] = value ?? "";
    }
}

using System.Diagnostics.CodeAnalysis;

namespace Ape.Core.Config.Models;

/// <summary>
/// Hierarchical configuration node: primitives, nested objects, and arrays of nodes or strings.
/// </summary>
public interface IConfigNode
{
    string GetString(string key, string defaultValue = "");

    int GetInt(string key, int defaultValue = 0);

    double GetDouble(string key, double defaultValue = 0.0);

    bool GetBool(string key, bool defaultValue = false);

    IConfigNode GetObject(string key);

    bool TryGetChildObject(string key, [NotNullWhen(true)] out IConfigNode? child);

    /// <summary>Array of object nodes; elements are <see cref="ConfigNode"/> instances.</summary>
    List<ConfigNode> GetArray(string key);

    List<string> GetStringArray(string key);

    void SetString(string key, string value);

    void SetInt(string key, int value);

    void SetDouble(string key, double value);

    void SetBool(string key, bool value);

    void SetArray(string key, List<string> values);

    void SetObjectArray(string key, List<ConfigNode> values);

    IConfigNode this[string key] { get; }

    IConfigNode this[int index] { get; }

    void PushArrayElement(string key, ConfigNode element);

    bool HasKey(string key);

    IEnumerable<string> Keys { get; }

    void Print(int indent = 0);
}

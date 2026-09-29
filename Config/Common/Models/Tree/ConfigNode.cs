using System.Diagnostics.CodeAnalysis;
using MessagePack;

namespace Ape.Core.Config.Models;

/// <summary>
/// Hierarchical configuration node that can hold various types of values.
/// Similar to JSON structure with support for primitives, objects, and arrays.
/// </summary>
[MessagePackObject(AllowPrivate = true)]
public partial class ConfigNode : IConfigNode
{
    [Key(0)]
    private Dictionary<string, object> _data = new();

    public ConfigNode()
    {
    }

    // ============================================================
    // GET methods
    // ============================================================

    public string GetString(string key, string defaultValue = "")
    {
        if (_data.TryGetValue(key, out var value) && value is string str)
            return str;
        return defaultValue;
    }

    public int GetInt(string key, int defaultValue = 0)
    {
        if (_data.TryGetValue(key, out var value))
        {
            if (value is int intVal) return intVal;
            if (value is long longVal) return (int)longVal;
            if (value is double dblVal) return (int)dblVal;
        }
        return defaultValue;
    }

    public double GetDouble(string key, double defaultValue = 0.0)
    {
        if (_data.TryGetValue(key, out var value))
        {
            if (value is double dblVal) return dblVal;
            if (value is int intVal) return intVal;
            if (value is long longVal) return longVal;
        }
        return defaultValue;
    }

    public bool GetBool(string key, bool defaultValue = false)
    {
        if (_data.TryGetValue(key, out var value) && value is bool boolVal)
            return boolVal;
        return defaultValue;
    }

    public ConfigNode GetObject(string key)
    {
        if (!_data.TryGetValue(key, out var value) || value is not ConfigNode)
        {
            var node = new ConfigNode();
            _data[key] = node;
            return node;
        }
        return (ConfigNode)value;
    }

    /// <summary>
    /// Returns an existing child object node without creating a placeholder (unlike <see cref="GetObject"/>).
    /// </summary>
    public bool TryGetChildObject(string key, [NotNullWhen(true)] out ConfigNode? child)
    {
        if (_data.TryGetValue(key, out var value) && value is ConfigNode cn)
        {
            child = cn;
            return true;
        }

        child = null;
        return false;
    }

    public List<ConfigNode> GetArray(string key)
    {
        if (!_data.TryGetValue(key, out var value) || value is not List<ConfigNode>)
        {
            var list = new List<ConfigNode>();
            _data[key] = list;
            return list;
        }
        return (List<ConfigNode>)value;
    }

    public List<string> GetStringArray(string key)
    {
        if (!_data.TryGetValue(key, out var value) || value is not List<string>)
        {
            return new List<string>();
        }
        return (List<string>)value;
    }

    // ============================================================
    // SET methods
    // ============================================================

    public void SetString(string key, string value)
    {
        _data[key] = value;
    }

    public void SetInt(string key, int value)
    {
        _data[key] = value;
    }

    public void SetDouble(string key, double value)
    {
        _data[key] = value;
    }

    public void SetBool(string key, bool value)
    {
        _data[key] = value;
    }

    public void SetArray(string key, List<string> values)
    {
        _data[key] = values;
    }

    public void SetObjectArray(string key, List<ConfigNode> values)
    {
        _data[key] = values;
    }

    // ============================================================
    // Operators for easy access
    // ============================================================

    /// <summary>
    /// Access nested object with indexer syntax: config["audio"]["volume"]
    /// </summary>
    public ConfigNode this[string key]
    {
        get => GetObject(key);
    }

    /// <summary>
    /// Access array element by index
    /// </summary>
    public ConfigNode this[int index]
    {
        get
        {
            var array = GetArray("array");
            while (array.Count <= index)
            {
                array.Add(new ConfigNode());
            }
            return array[index];
        }
    }

    // ============================================================
    // Utility methods
    // ============================================================

    public void PushArrayElement(string key, ConfigNode element)
    {
        GetArray(key).Add(element);
    }

    public bool HasKey(string key)
    {
        return _data.ContainsKey(key);
    }

    [IgnoreMember]
    public IEnumerable<string> Keys => _data.Keys;

    /// <summary>
    /// Print config structure to console (for debugging)
    /// </summary>
    public void Print(int indent = 0)
    {
        string prefix = new string(' ', indent);
        Console.WriteLine($"{prefix}{{");

        var items = _data.ToList();
        for (int i = 0; i < items.Count; i++)
        {
            var kvp = items[i];
            Console.Write($"{prefix}  \"{kvp.Key}\": ");

            switch (kvp.Value)
            {
                case string str:
                    Console.Write($"\"{str}\"");
                    break;
                case int intVal:
                    Console.Write(intVal);
                    break;
                case long longVal:
                    Console.Write(longVal);
                    break;
                case double dblVal:
                    Console.Write(dblVal);
                    break;
                case bool boolVal:
                    Console.Write(boolVal ? "true" : "false");
                    break;
                case List<string> strArray:
                    Console.WriteLine("[");
                    for (int j = 0; j < strArray.Count; j++)
                    {
                        Console.Write($"{prefix}    \"{strArray[j]}\"");
                        if (j < strArray.Count - 1) Console.Write(",");
                        Console.WriteLine();
                    }
                    Console.Write($"{prefix}  ]");
                    break;
                case List<ConfigNode> nodeArray:
                    Console.WriteLine("[");
                    for (int j = 0; j < nodeArray.Count; j++)
                    {
                        nodeArray[j].Print(indent + 4);
                        if (j < nodeArray.Count - 1) Console.Write(",");
                        Console.WriteLine();
                    }
                    Console.Write($"{prefix}  ]");
                    break;
                case ConfigNode node:
                    Console.WriteLine();
                    node.Print(indent + 4);
                    break;
            }

            if (i < items.Count - 1) Console.Write(",");
            Console.WriteLine();
        }

        Console.Write($"{prefix}}}");
    }

    public override string ToString()
    {
        return $"ConfigNode({_data.Count} keys)";
    }

    // ============================================================
    // IConfigNode (explicit where signatures differ)
    // ============================================================

    IConfigNode IConfigNode.GetObject(string key) => GetObject(key);

    bool IConfigNode.TryGetChildObject(string key, [NotNullWhen(true)] out IConfigNode? child)
    {
        if (TryGetChildObject(key, out var cn))
        {
            child = cn;
            return true;
        }

        child = null;
        return false;
    }

    IConfigNode IConfigNode.this[string key] => GetObject(key);

    IConfigNode IConfigNode.this[int index] => this[index];
}

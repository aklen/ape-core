using Ape.Core.Config;
using Ape.Core.Logging;
using Ape.Core.Runtime.Service;
using Ape.Core.Config.Models;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

namespace Ape.Core.Config.Services;

/// <summary>
/// Implementation of IConfigManager using System.Text.Json for parsing.
/// Loads JSON configuration files into hierarchical ConfigNode structures.
/// Also implements ICoreService for direct registration in the DI container.
/// </summary>
public class ConfigManager : IConfigManager, ICoreService
{
    public string ServiceId => "core-config-manager";
    public string Name => "Config Manager";

    private ILogger? _logger;
    private ConfigNode? _startupRoot;

    /// <inheritdoc />
    public IConfigNode? StartupRoot => _startupRoot;

    // ICoreService implementation
    public void Register(IServiceCollection serviceCollection)
    {
        serviceCollection.AddSingleton<IConfigManager>(this);
    }

    public void Initialize(IServiceProvider services)
    {
        _logger = services.GetRequiredService<ILogger>();
        _startupRoot = services.GetService<IStartupConfig>()?.Root as ConfigNode;
    }

    public void Start(CancellationToken cancellationToken)
    {
    }

    public void Stop()
    {
    }

    // IConfigManager implementation
    public bool LoadJson(string filePath, out ConfigNode config)
    {
        config = new ConfigNode();
        return LoadJson(filePath, config);
    }

    public bool LoadJson(string filePath, ConfigNode config)
    {
        _logger?.LogInfo($"[ConfigManager] Loading config file: {filePath}");

        if (!File.Exists(filePath))
        {
            _logger?.LogError($"[ConfigManager] Config file not found: {filePath}");
            return false;
        }

        try
        {
            string jsonText = File.ReadAllText(filePath);
            using var jsonDocument = JsonDocument.Parse(jsonText);

            ParseJsonToConfigNode(jsonDocument.RootElement, config);

            _logger?.LogInfo($"[ConfigManager] Successfully loaded config: {filePath}");
            return true;
        }
        catch (JsonException ex)
        {
            _logger?.LogError($"[ConfigManager] JSON parsing error in {filePath}: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            _logger?.LogError($"[ConfigManager] Error loading config {filePath}: {ex.Message}");
            return false;
        }
    }

    private void ParseJsonToConfigNode(JsonElement jsonElement, ConfigNode node, string key = "")
    {
        switch (jsonElement.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in jsonElement.EnumerateObject())
                {
                    var propertyKey = property.Name;
                    var propertyValue = property.Value;

                    switch (propertyValue.ValueKind)
                    {
                        case JsonValueKind.Object:
                            var childNode = node.GetObject(propertyKey);
                            ParseJsonToConfigNode(propertyValue, childNode);
                            break;

                        case JsonValueKind.Array:
                            ParseArrayToConfigNode(propertyValue, node, propertyKey);
                            break;

                        case JsonValueKind.String:
                            node.SetString(propertyKey, propertyValue.GetString() ?? "");
                            break;

                        case JsonValueKind.Number:
                            if (propertyValue.TryGetInt32(out int intVal))
                            {
                                node.SetInt(propertyKey, intVal);
                            }
                            else if (propertyValue.TryGetDouble(out double dblVal))
                            {
                                node.SetDouble(propertyKey, dblVal);
                            }
                            break;

                        case JsonValueKind.True:
                            node.SetBool(propertyKey, true);
                            break;

                        case JsonValueKind.False:
                            node.SetBool(propertyKey, false);
                            break;

                        case JsonValueKind.Null:
                            break;
                    }
                }
                break;

            case JsonValueKind.Array:
                if (!string.IsNullOrEmpty(key))
                {
                    ParseArrayToConfigNode(jsonElement, node, key);
                }
                break;
        }
    }

    private void ParseArrayToConfigNode(JsonElement arrayElement, ConfigNode parentNode, string key)
    {
        if (arrayElement.GetArrayLength() == 0)
        {
            parentNode.SetObjectArray(key, new List<ConfigNode>());
            return;
        }

        var firstElement = arrayElement.EnumerateArray().FirstOrDefault();

        if (firstElement.ValueKind == JsonValueKind.String)
        {
            var stringArray = new List<string>();
            foreach (var element in arrayElement.EnumerateArray())
            {
                if (element.ValueKind == JsonValueKind.String)
                {
                    stringArray.Add(element.GetString() ?? "");
                }
            }
            parentNode.SetArray(key, stringArray);
        }
        else if (firstElement.ValueKind == JsonValueKind.Object)
        {
            var nodeArray = parentNode.GetArray(key);
            foreach (var element in arrayElement.EnumerateArray())
            {
                if (element.ValueKind == JsonValueKind.Object)
                {
                    var childNode = new ConfigNode();
                    ParseJsonToConfigNode(element, childNode);
                    nodeArray.Add(childNode);
                }
            }
        }
        else if (firstElement.ValueKind == JsonValueKind.Number)
        {
            var nodeArray = parentNode.GetArray(key);
            foreach (var element in arrayElement.EnumerateArray())
            {
                if (element.ValueKind == JsonValueKind.Number)
                {
                    var childNode = new ConfigNode();
                    if (element.TryGetInt32(out int intVal))
                    {
                        childNode.SetInt("value", intVal);
                    }
                    else if (element.TryGetDouble(out double dblVal))
                    {
                        childNode.SetDouble("value", dblVal);
                    }
                    nodeArray.Add(childNode);
                }
            }
        }
        else if (firstElement.ValueKind == JsonValueKind.True || firstElement.ValueKind == JsonValueKind.False)
        {
            var nodeArray = parentNode.GetArray(key);
            foreach (var element in arrayElement.EnumerateArray())
            {
                var childNode = new ConfigNode();
                childNode.SetBool("value", element.GetBoolean());
                nodeArray.Add(childNode);
            }
        }
    }
}

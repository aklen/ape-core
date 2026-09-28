namespace Ape.Core.Runtime.Plugin;

/// <summary>
/// Fail-fast when a plugin still needs direct Scene writes. Plugin DI does not resolve <c>ISceneManager</c>.
/// </summary>
public static class PluginDirectSceneWrite
{
    public static InvalidOperationException Unavailable(string pluginName) =>
        new(
            $"[{pluginName}] ISceneManager is not available to plugins. " +
            "Look up with ISceneRead and enqueue ISceneCommitRequest on IFrameCommitBatch in OnHostFrame.");
}

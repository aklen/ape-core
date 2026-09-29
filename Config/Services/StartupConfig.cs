using Ape.Core.Config.Models;

namespace Ape.Core.Config;

/// <inheritdoc />
public sealed class StartupConfig : IStartupConfig
{
    public IConfigNode? Root { get; }

    public StartupConfig(ConfigNode? root) => Root = root;
}

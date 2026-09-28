using Ape.Core.Scene;
using Microsoft.Extensions.DependencyInjection;
using System.Runtime.Versioning;

namespace Ape.Core.Tests;

[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("windows")]
public class SceneReadRegistrationTests
{
    [Fact]
    public void ISceneRead_is_registered_for_observers()
    {
        var sp = SceneIntegrationServices.Build();
        var read = sp.GetService<ISceneRead>();
        Assert.NotNull(read);
        Assert.Same(sp.GetRequiredService<ISceneManager>(), read);
    }
}

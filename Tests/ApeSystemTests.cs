using Ape.Core;

namespace Ape.Core.Tests;

[Collection(nameof(ApeSystemCollection))]
public sealed class ApeSystemTests
{
    [Fact]
    public void Start_non_blocking_then_Stop_round_trips()
    {
        ApeSystem.Start(configPath: null, blocking: false);
        try
        {
            Assert.NotNull(ApeSystem.Services);
        }
        finally
        {
            ApeSystem.Stop();
        }

        Assert.Null(ApeSystem.Services);
    }

    [Fact]
    public void Start_twice_without_Stop_throws()
    {
        ApeSystem.Start(configPath: null, blocking: false);
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                ApeSystem.Start(configPath: null, blocking: false));
            Assert.Contains("already been called", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            ApeSystem.Stop();
        }
    }
}

[CollectionDefinition(nameof(ApeSystemCollection), DisableParallelization = true)]
public sealed class ApeSystemCollection
{
}

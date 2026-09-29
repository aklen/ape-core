using Ape.Core.Scene.Models;
using MessagePack;

namespace Ape.Core.Tests;

/// <summary>Minimal <see cref="Entity"/> for registry / commit pipeline tests (type id <c>tests.stub.v1</c>).</summary>
[MessagePackObject(AllowPrivate = true)]
internal sealed partial class TestStubEntity : Entity
{
    internal const string RegistryTypeId = "tests.stub.v1";

    public TestStubEntity()
        : base(RegistryTypeId)
    {
    }

    [Key(103)]
    public int Counter { get; set; }
}

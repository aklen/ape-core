using Ape.Core.Determinism;
using Ape.Core.Scene.Commit;
using System.Numerics;

namespace Ape.Core.Tests;

public class SceneCommitConflictsTests
{
    [Fact]
    public void FindMultiWriterKeys_reports_same_node_property_from_two_participants()
    {
        var batches = new List<(string, IReadOnlyList<ISceneCommitRequest>)>
        {
            ("ingest", new ISceneCommitRequest[]
            {
                new SetSceneNodePropertyCommitRequest("/Cube", "Position", new Vector3(1, 0, 0))
            }),
            ("fusion", new ISceneCommitRequest[]
            {
                new SetSceneNodePositionCommitRequest("/Cube", new Vector3(2, 0, 0))
            })
        };

        var conflicts = SceneCommitConflicts.FindMultiWriterKeys(batches);
        Assert.Single(conflicts);
        Assert.Contains("node:/Cube.Position", conflicts[0]);
        Assert.Contains("fusion", conflicts[0]);
        Assert.Contains("ingest", conflicts[0]);
    }

    [Fact]
    public void FindMultiWriterKeys_ignores_disjoint_properties()
    {
        var batches = new List<(string, IReadOnlyList<ISceneCommitRequest>)>
        {
            ("a", new ISceneCommitRequest[] { new SetSceneNodePositionCommitRequest("/A", Vector3.One) }),
            ("b", new ISceneCommitRequest[] { new SetSceneNodePositionCommitRequest("/B", Vector3.Zero) })
        };
        Assert.Empty(SceneCommitConflicts.FindMultiWriterKeys(batches));
    }
}

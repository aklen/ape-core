using System.Numerics;
using Ape.Core.Determinism;
using Ape.Core.Runtime.Plugin;
using Ape.Core.Scene;
using Ape.Core.Scene.Commit;
using Microsoft.Extensions.DependencyInjection;
using System.Runtime.Versioning;

namespace Ape.Core.Tests;

[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("windows")]
public class SceneMutationScopeTests
{
    [Fact]
    public void Setter_from_ISceneRead_throws_outside_apply_scope()
    {
        var root = SceneIntegrationServices.Build(includeSceneCommitPipeline: true);
        var scene = root.GetRequiredService<ISceneManager>();
        using (SceneMutationScope.Begin())
            scene.CreateNode("n", "o");

        var plugins = new PluginServiceProvider(root);
        var read = (ISceneRead)plugins.GetService(typeof(ISceneRead))!;
        var node = read.GetNode("n")!;

        Assert.Throws<SceneMutationOutsideScopeException>(() => node.Position = Vector3.UnitX);
        Assert.Equal(Vector3.Zero, node.Position);
    }

    [Fact]
    public void CreateNode_throws_outside_apply_scope()
    {
        var scene = SceneIntegrationServices.Build().GetRequiredService<ISceneManager>();
        Assert.Throws<SceneMutationOutsideScopeException>(() => scene.CreateNode("n", "o"));
    }

    [Fact]
    public void Binder_rethrows_outside_apply_scope()
    {
        var scene = SceneIntegrationServices.Build().GetRequiredService<ISceneManager>();
        INode node;
        using (SceneMutationScope.Begin())
            node = scene.CreateNode("n", "o");

        Assert.Throws<SceneMutationOutsideScopeException>(
            () => ReplicaPropertyBinder.TrySet(node, nameof(INode.Position), Vector3.One, null));
    }

    [Fact]
    public void Applicator_can_create_and_set_position()
    {
        var sp = SceneIntegrationServices.Build();
        var scene = sp.GetRequiredService<ISceneManager>();
        var log = sp.GetRequiredService<Ape.Core.Logging.ILogger>();

        SceneCommitApplicator.Apply(scene, new CreateSceneNodeCommitRequest("n", "o", new Vector3(1, 2, 3)), log);
        var node = scene.GetNode("n");
        Assert.NotNull(node);
        Assert.Equal(new Vector3(1, 2, 3), node.Position);

        SceneCommitApplicator.Apply(scene, new SetSceneNodePositionCommitRequest("n", Vector3.UnitY), log);
        Assert.Equal(Vector3.UnitY, node.Position);
    }

    [Fact]
    public void Nested_Begin_keeps_outer_scope_after_inner_dispose()
    {
        var scene = SceneIntegrationServices.Build().GetRequiredService<ISceneManager>();
        using (SceneMutationScope.Begin())
        {
            scene.CreateNode("outer", "o");
            using (SceneMutationScope.Begin())
                scene.CreateNode("inner", "o");

            Assert.True(SceneMutationScope.IsActive);
            scene.CreateNode("after-inner", "o");
        }

        Assert.False(SceneMutationScope.IsActive);
        Assert.Throws<SceneMutationOutsideScopeException>(() => scene.CreateNode("after-outer", "o"));
    }

    [Fact]
    public void Task_Run_from_inside_scope_does_not_inherit_write_rights()
    {
        var scene = SceneIntegrationServices.Build().GetRequiredService<ISceneManager>();
        INode node;
        using (SceneMutationScope.Begin())
        {
            node = scene.CreateNode("n", "o");

            Exception? caught = null;
            var ownerId = Environment.CurrentManagedThreadId;
            var workerId = ownerId;
            // LongRunning: dedicated thread, so a parallel test's ThreadStatic depth cannot leak onto the worker.
            Task.Factory.StartNew(() =>
            {
                workerId = Environment.CurrentManagedThreadId;
                try
                {
                    node.Position = Vector3.UnitZ;
                }
                catch (Exception ex)
                {
                    caught = ex;
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)
                .GetAwaiter().GetResult();

            Assert.NotEqual(ownerId, workerId);
            Assert.IsType<SceneMutationOutsideScopeException>(caught);
            node.Position = Vector3.UnitX;
        }

        Assert.Equal(Vector3.UnitX, node.Position);
    }

    [Fact]
    public void Thread_started_from_inside_scope_does_not_inherit_write_rights()
    {
        var scene = SceneIntegrationServices.Build().GetRequiredService<ISceneManager>();
        INode node;
        using (SceneMutationScope.Begin())
        {
            node = scene.CreateNode("n", "o");

            Exception? caught = null;
            var thread = new Thread(() =>
            {
                try
                {
                    node.Position = Vector3.UnitZ;
                }
                catch (Exception ex)
                {
                    caught = ex;
                }
            });
            thread.Start();
            thread.Join();

            Assert.IsType<SceneMutationOutsideScopeException>(caught);
            Assert.Equal(Vector3.Zero, node.Position);
        }
    }

    [Fact]
    public void Disposing_the_same_lease_twice_does_not_close_outer_scope()
    {
        var scene = SceneIntegrationServices.Build().GetRequiredService<ISceneManager>();
        var outer = SceneMutationScope.Begin();
        try
        {
            scene.CreateNode("outer", "o");
            var inner = SceneMutationScope.Begin();
            inner.Dispose();
            inner.Dispose();

            Assert.True(SceneMutationScope.IsActive);
            scene.CreateNode("after-double-inner-dispose", "o");
        }
        finally
        {
            outer.Dispose();
        }

        Assert.False(SceneMutationScope.IsActive);
        Assert.Throws<SceneMutationOutsideScopeException>(() => scene.CreateNode("after-outer", "o"));
    }

    [Fact]
    public void Dispose_on_another_thread_throws_and_leaves_owner_scope_active()
    {
        var scene = SceneIntegrationServices.Build().GetRequiredService<ISceneManager>();
        using (var lease = SceneMutationScope.Begin())
        {
            scene.CreateNode("n", "o");

            Exception? caught = null;
            var thread = new Thread(() =>
            {
                try
                {
                    lease.Dispose();
                }
                catch (Exception ex)
                {
                    caught = ex;
                }
            });
            thread.Start();
            thread.Join();

            var ex = Assert.IsType<InvalidOperationException>(caught);
            Assert.Contains("creating thread", ex.Message, StringComparison.Ordinal);
            Assert.True(SceneMutationScope.IsActive);
            scene.CreateNode("after-foreign-dispose", "o");
        }

        Assert.False(SceneMutationScope.IsActive);
    }

    [Fact]
    public void OnHostFrame_direct_setter_fails_the_frame()
    {
        var sp = SceneIntegrationServices.Build(includeSceneCommitPipeline: true);
        var scene = sp.GetRequiredService<ISceneManager>();
        using (SceneMutationScope.Begin())
            scene.CreateNode("n", "o");

        sp.GetRequiredService<IFrameParticipantRegistry>().Register(
            new DirectSetParticipant(sp.GetRequiredService<ISceneRead>()));
        var result = sp.GetRequiredService<IHostFrameRunner>().RunNextFrame();
        Assert.Equal(HostFrameStatus.Failed, result.Status);
        Assert.Equal(Vector3.Zero, scene.GetNode("n")!.Position);
    }

    private sealed class DirectSetParticipant : IDeterministicFrameParticipant
    {
        private readonly ISceneRead _scene;

        public DirectSetParticipant(ISceneRead scene) => _scene = scene;

        public string ParticipantId => "direct-set";
        public FramePhase Phase => FramePhase.Publish;
        public int Order => 0;

        public void OnHostFrame(in FrameContext context, IFrameCommitBatch commits)
        {
            _scene.GetNode("n")!.Position = Vector3.UnitX;
        }
    }
}

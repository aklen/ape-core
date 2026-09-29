namespace Ape.Core.Tests;

public sealed class GraphCompilerTests
{
    private struct DecodeScratch
    {
        public int Frames;
    }

    private struct DemoScratch
    {
        public int Value;
        public int Frames;
        public DecodeScratch Decode;
    }

    private sealed class RaiseOnExecuteStage : Ape.Core.Graph.IStage<DemoScratch>
    {
        public string Id { get; }

        public Ape.Core.Graph.PlanRuntime? Runtime { get; set; }

        public string EventId { get; init; } = "open";

        public RaiseOnExecuteStage(string id) => Id = id;

        public void Execute(ref DemoScratch scratch)
        {
            scratch.Value++;
            Runtime?.EnqueueEvent(EventId);
        }
    }

    private sealed class IncStage : Ape.Core.Graph.IStage<DemoScratch>
    {
        public string Id { get; }

        public IncStage(string id) => Id = id;

        public void Execute(ref DemoScratch scratch) => scratch.Value++;
    }

    [Fact]
    public void Compile_flattens_nested_graph_in_declaration_order()
    {
        var plan = new Ape.Core.Graph.ComponentGraph<DemoScratch>("demo")
            .Add(new IncStage("watch"))
            .Add(new Ape.Core.Graph.ComponentGraph<DemoScratch>("decode")
                .Add(new IncStage("parse"))
                .Add(new IncStage("demux")))
            .Add(new IncStage("publish"))
            .Compile();

        Assert.Equal(["watch", "decode/parse", "decode/demux", "publish"], plan.StageOrder);
    }

    [Fact]
    public void Compile_rejects_unknown_hsm_enable_target()
    {
        var ex = Assert.Throws<Ape.Core.Graph.GraphCompileException>(() =>
            new Ape.Core.Graph.ComponentGraph<DemoScratch>("demo")
                .Add(new IncStage("watch"))
                .Hsm(h => h
                    .State("Idle")
                    .State("Run", "missing")
                    .On("Idle", "go", "Run"))
                .Compile());

        Assert.Equal("CG102", ex.Code);
        Assert.Contains("missing", ex.Message);
    }

    [Fact]
    public void Compile_rejects_undeclared_hsm_state()
    {
        var ex = Assert.Throws<Ape.Core.Graph.GraphCompileException>(() =>
            new Ape.Core.Graph.ComponentGraph<DemoScratch>("demo")
                .Add(new IncStage("watch"))
                .Hsm(h => h
                    .State("Idle")
                    .On("Idle", "go", "Run"))
                .Compile());

        Assert.Equal("CG103", ex.Code);
        Assert.Contains("Run", ex.Message);
    }

    [Fact]
    public void Describe_lists_flattened_order_and_state_activation()
    {
        var plan = new Ape.Core.Graph.ComponentGraph<DemoScratch>("demo")
            .Add(new IncStage("watch"))
            .Add(new Ape.Core.Graph.ComponentGraph<DemoScratch>("decode")
                .Add(new IncStage("parse")))
            .Add(new IncStage("publish"))
            .Hsm(h => h
                .State("Idle")
                .State("Run", "decode", "publish")
                .On("Idle", "go", "Run"))
            .Compile();

        var text = plan.Describe();
        Assert.Contains("FrozenPlan: demo", text);
        Assert.Contains("00 watch", text);
        Assert.Contains("01 decode/parse", text);
        Assert.Contains("02 publish", text);
        Assert.Contains("Idle", text);
        Assert.Contains("Run", text);

        var parse = plan.ExplainStage("decode/parse");
        Assert.Contains("index: 1", parse);
        Assert.Contains("Run", parse);
        Assert.DoesNotContain("Idle", parse.Split("declared active in:")[1]);

        var dot = plan.ToDot();
        Assert.Contains("digraph \"demo\"", dot);
        Assert.Contains("decode/parse", dot);
    }

    [Fact]
    public void Tick_runs_only_hsm_enabled_stages()
    {
        const string EvGo = "go";

        var plan = new Ape.Core.Graph.ComponentGraph<DemoScratch>("demo")
            .Add(new IncStage("a"))
            .Add(new IncStage("b"))
            .Hsm(h => h
                .State("Idle")
                .State("Run", "a", "b")
                .On("Idle", EvGo, "Run"))
            .Compile();

        var scratch = new DemoScratch();
        var rt = new Ape.Core.Graph.PlanRuntime();
        plan.InitRuntime(ref rt);

        plan.Tick(ref scratch, ref rt);
        Assert.Equal(0, scratch.Value);

        rt.EnqueueEvent(EvGo);
        plan.Tick(ref scratch, ref rt);
        Assert.Equal(2, scratch.Value);
    }

    [Fact]
    public void Tick_replaces_mask_from_destination_state()
    {
        var plan = new Ape.Core.Graph.ComponentGraph<DemoScratch>("demo")
            .Add(new IncStage("watch"))
            .Add(new IncStage("decode"))
            .Hsm(h => h
                .State("Opening", "watch")
                .State("Streaming", "decode")
                .State("Fault")
                .On("Opening", "open", "Streaming")
                .On("Streaming", "lost", "Fault")
                .On("Fault", "retry", "Opening"))
            .Compile();

        var scratch = new DemoScratch();
        var rt = new Ape.Core.Graph.PlanRuntime();
        plan.InitRuntime(ref rt);

        plan.Tick(ref scratch, ref rt);
        Assert.Equal(1, scratch.Value);
        Assert.Equal("Opening", rt.CurrentState);
        Assert.True(rt.IsStageEnabled(0));
        Assert.False(rt.IsStageEnabled(1));

        rt.EnqueueEvent("open");
        plan.Tick(ref scratch, ref rt);
        Assert.Equal(2, scratch.Value);
        Assert.Equal("Streaming", rt.CurrentState);
        Assert.False(rt.IsStageEnabled(0));
        Assert.True(rt.IsStageEnabled(1));

        rt.EnqueueEvent("lost");
        plan.Tick(ref scratch, ref rt);
        Assert.Equal(2, scratch.Value);
        Assert.Equal("Fault", rt.CurrentState);
        Assert.False(rt.IsStageEnabled(0));
        Assert.False(rt.IsStageEnabled(1));

        rt.EnqueueEvent("retry");
        plan.Tick(ref scratch, ref rt);
        Assert.Equal(3, scratch.Value);
        Assert.Equal("Opening", rt.CurrentState);
        Assert.True(rt.IsStageEnabled(0));
        Assert.False(rt.IsStageEnabled(1));
    }

    [Fact]
    public void Compile_rejects_duplicate_hsm_state()
    {
        var ex = Assert.Throws<Ape.Core.Graph.GraphCompileException>(() =>
            new Ape.Core.Graph.ComponentGraph<DemoScratch>("demo")
                .Add(new IncStage("watch"))
                .Hsm(h => h
                    .State("Idle")
                    .State("Idle", "watch"))
                .Compile());

        Assert.Equal("CG105", ex.Code);
    }

    [Fact]
    public void Tick_drains_all_events_before_any_stage()
    {
        var plan = new Ape.Core.Graph.ComponentGraph<DemoScratch>("demo")
            .Add(new IncStage("watch"))
            .Add(new IncStage("decode"))
            .Hsm(h => h
                .State("Idle")
                .State("Opening", "watch")
                .State("Streaming", "decode")
                .On("Idle", "match", "Opening")
                .On("Opening", "open", "Streaming"))
            .Compile();

        var scratch = new DemoScratch();
        var rt = new Ape.Core.Graph.PlanRuntime();
        plan.InitRuntime(ref rt);

        rt.EnqueueEvent("match");
        rt.EnqueueEvent("open");
        plan.Tick(ref scratch, ref rt);

        Assert.Equal("Streaming", rt.CurrentState);
        Assert.Equal(1, scratch.Value);
        Assert.False(rt.IsStageEnabled(0));
        Assert.True(rt.IsStageEnabled(1));
    }

    [Fact]
    public void Compile_rejects_unresolved_connection()
    {
        var ex = Assert.Throws<Ape.Core.Graph.GraphCompileException>(() =>
            new Ape.Core.Graph.ComponentGraph<DemoScratch>("demo")
                .Add(new IncStage("parse"))
                .Connect<int>("parse/frames", "demux/frames")
                .Compile());

        Assert.Equal("CG209", ex.Code);
    }

    [Fact]
    public void Compile_rejects_consumer_active_while_producer_idle()
    {
        var ex = Assert.Throws<Ape.Core.Graph.GraphCompileException>(() =>
            new Ape.Core.Graph.ComponentGraph<DemoScratch>("demo")
                .Add(new IncStage("parse"))
                .Add(new IncStage("demux"))
                .ExposeOut("parse", s => s.Frames, "frames")
                .ExposeIn("demux", s => s.Frames, "frames")
                .Connect<int>("parse/frames", "demux/frames")
                .Hsm(h => h
                    .State("Run", "demux"))
                .Compile());

        Assert.Equal("CG104", ex.Code);
        Assert.Contains("Run", ex.Message);
    }

    [Fact]
    public void Compile_accepts_connection_when_both_ends_share_activation()
    {
        var plan = new Ape.Core.Graph.ComponentGraph<DemoScratch>("demo")
            .Add(new IncStage("parse"))
            .Add(new IncStage("demux"))
            .ExposeOut("parse", s => s.Frames, "frames")
            .ExposeIn("demux", s => s.Frames, "frames")
            .Connect<int>("parse/frames", "demux/frames")
            .Hsm(h => h.State("Run", "parse", "demux"))
            .Compile();

        Assert.Contains("parse/frames -> demux/frames", plan.Describe());
        Assert.Contains("scratch.Frames", plan.Describe());
    }

    [Fact]
    public void Compile_rejects_incompatible_port_types()
    {
        var ex = Assert.Throws<Ape.Core.Graph.GraphCompileException>(() =>
            new Ape.Core.Graph.ComponentGraph<DemoScratch>("demo")
                .Add(new IncStage("parse"))
                .Add(new IncStage("demux"))
                .ExposeOut("parse", s => s.Frames, "frames")
                .ExposeIn("demux", s => s.Value, "frames")
                .Connect<int>("parse/frames", "demux/frames")
                .Hsm(h => h.State("Run", "parse", "demux"))
                .Compile());

        Assert.Equal("CG201", ex.Code);
    }

    [Fact]
    public void Compile_rejects_producer_after_consumer_in_stage_order()
    {
        var ex = Assert.Throws<Ape.Core.Graph.GraphCompileException>(() =>
            new Ape.Core.Graph.ComponentGraph<DemoScratch>("demo")
                .Add(new IncStage("demux"))
                .Add(new IncStage("parse"))
                .ExposeOut("parse", s => s.Frames, "frames")
                .ExposeIn("demux", s => s.Frames, "frames")
                .Connect<int>("parse/frames", "demux/frames")
                .Hsm(h => h.State("Run", "parse", "demux"))
                .Compile());

        Assert.Equal("CG208", ex.Code);
        Assert.Contains("demux", ex.Message);
    }

    [Fact]
    public void Compile_rejects_nested_scratch_slot_mismatch()
    {
        var ex = Assert.Throws<Ape.Core.Graph.GraphCompileException>(() =>
            new Ape.Core.Graph.ComponentGraph<DemoScratch>("demo")
                .Add(new IncStage("parse"))
                .Add(new IncStage("demux"))
                .ExposeOut("parse", s => s.Decode.Frames, "frames")
                .ExposeIn("demux", s => s.Frames, "frames")
                .Connect<int>("parse/frames", "demux/frames")
                .Hsm(h => h.State("Run", "parse", "demux"))
                .Compile());

        Assert.Equal("CG201", ex.Code);
        Assert.Contains("Decode.Frames", ex.Message);
    }

    [Fact]
    public void Tick_defers_events_raised_during_execute_to_next_frame()
    {
        var watch = new RaiseOnExecuteStage("watch");
        var plan = new Ape.Core.Graph.ComponentGraph<DemoScratch>("demo")
            .Add(watch)
            .Add(new IncStage("decode"))
            .Hsm(h => h
                .State("Opening", "watch")
                .State("Streaming", "decode")
                .On("Opening", "open", "Streaming"))
            .Compile();

        var scratch = new DemoScratch();
        var rt = new Ape.Core.Graph.PlanRuntime();
        watch.Runtime = rt;
        plan.InitRuntime(ref rt);

        plan.Tick(ref scratch, ref rt);
        Assert.Equal("Opening", rt.CurrentState);
        Assert.Equal(1, scratch.Value);
        Assert.True(rt.IsStageEnabled(0));
        Assert.False(rt.IsStageEnabled(1));

        plan.Tick(ref scratch, ref rt);
        Assert.Equal("Streaming", rt.CurrentState);
        Assert.Equal(2, scratch.Value);
        Assert.False(rt.IsStageEnabled(0));
        Assert.True(rt.IsStageEnabled(1));
    }

    [Fact]
    public void Compile_rejects_multiple_declared_writers_in_one_state()
    {
        var ex = Assert.Throws<Ape.Core.Graph.GraphCompileException>(() =>
            new Ape.Core.Graph.ComponentGraph<DemoScratch>("demo")
                .Add(new AccessStage("fuse", c => c.Writes(s => s.Frames)))
                .Add(new AccessStage("coast", c => c.Writes(s => s.Frames)))
                .Hsm(h => h.State("Run", "fuse", "coast"))
                .Compile());

        Assert.Equal("CG207", ex.Code);
        Assert.Contains("Frames", ex.Message);
        Assert.Contains("fuse", ex.Message);
        Assert.Contains("coast", ex.Message);
    }

    [Fact]
    public void Compile_leaf_access_refines_owner_span_order()
    {
        var plan = new Ape.Core.Graph.ComponentGraph<DemoScratch>("demo")
            .Add(new Ape.Core.Graph.ComponentGraph<DemoScratch>("decode")
                .Add(new AccessStage("parse", c => c.Writes(s => s.Frames)))
                .Add(new IncStage("extra"))
                .Add(new AccessStage("demux", c => c.Reads(s => s.Frames)))
                .ExposeOut(s => s.Frames, "frames")
                .ExposeIn("demux", s => s.Frames, "frames"))
            .Connect<int>("decode/frames", "decode/demux/frames")
            .Hsm(h => h.State("Run", "decode"))
            .Compile();

        Assert.Contains("decode/parse  W Frames", plan.Describe());
        Assert.Contains("decode/demux  R Frames", plan.Describe());
    }

    private sealed class AccessStage : Ape.Core.Graph.IStage<DemoScratch>
    {
        private readonly Action<Ape.Core.Graph.StageContract<DemoScratch>> _describe;

        public string Id { get; }

        public AccessStage(string id, Action<Ape.Core.Graph.StageContract<DemoScratch>> describe)
        {
            Id = id;
            _describe = describe;
        }

        public void Execute(ref DemoScratch scratch)
        {
        }

        public void Describe(Ape.Core.Graph.StageContract<DemoScratch> contract) => _describe(contract);
    }
}

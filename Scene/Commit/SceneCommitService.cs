using Ape.Core.Config;
using Ape.Core.Config.Models;
using Ape.Core.Determinism;
using Ape.Core.Network;
using Ape.Core.Replication;
using Ape.Core.Scene;
using Ape.Core.Logging;
using Ape.Core.Runtime.Service;
using Microsoft.Extensions.DependencyInjection;

namespace Ape.Core.Scene.Commit;

/// <summary>
/// Host tick: ordered participants each get a frame-scoped <see cref="IFrameCommitBatch"/>.
/// Production: <see cref="IHostFrameRunner.RunNextFrame"/>. Tests: <see cref="IDeterministicHostTick.RaiseHostFrame"/>.
/// Plugins receive <see cref="IFrameParticipantRegistry"/> only.
/// </summary>
public sealed class SceneCommitService : ICoreService, IDeterministicHostTick, IHostFrameRunner, IFrameParticipantRegistry
{
    private readonly List<IDeterministicFrameParticipant> _participants = new();
    private readonly List<HostFrameRecord> _records = new();
    private readonly object _sync = new();

    private ISceneManager? _scene;
    private ILogger? _logger;
    private IReplicaManager? _replicas;
    private bool _replicateAfterApply;
    private bool _logCommitsPerFrame;
    private SceneMultiWriterMode _multiWriterMode;
    private FailedFramePolicy _failedFramePolicy;
    private int _recordHistoryLimit = 1000;
    private bool _halted;
    private HostFrameOutcomeJournal.Writer? _outcomeJournal;
    private string? _lastFailureParticipant;
    private string? _lastFailureReason;

    internal SceneMultiWriterMode MultiWriterMode
    {
        get => _multiWriterMode;
        set => _multiWriterMode = value;
    }

    internal FailedFramePolicy FailedFramePolicy
    {
        get => _failedFramePolicy;
        set => _failedFramePolicy = value;
    }

    /// <summary>In-memory audit cap. 0 = unbounded (tests). Production default 1000; durable history is the journal.</summary>
    internal int RecordHistoryLimit
    {
        get => _recordHistoryLimit;
        set => _recordHistoryLimit = value < 0 ? 0 : value;
    }

    internal void SetReplication(IReplicaManager? replicas, bool enable)
    {
        _replicas = replicas;
        _replicateAfterApply = enable;
    }

    public string ServiceId => "core-scene-commit";
    public string Name => "Scene commit pipeline";

    public long FrameId { get; private set; }

    public HostFrameResult LastResult { get; private set; }

    public IReadOnlyList<HostFrameRecord> Records => _records;

    public void Register(IServiceCollection serviceCollection)
    {
        serviceCollection.AddSingleton(this);
        serviceCollection.AddSingleton<IFrameParticipantRegistry>(sp => sp.GetRequiredService<SceneCommitService>());
        serviceCollection.AddSingleton<IHostFrameRunner>(sp => sp.GetRequiredService<SceneCommitService>());
    }

    public void Initialize(IServiceProvider services)
    {
        _scene = services.GetRequiredService<ISceneManager>();
        _logger = services.GetRequiredService<ILogger>();
        _replicas = services.GetService<IReplicaManager>();
        var root = services.GetService<IStartupConfig>()?.Root;
        var moduleAware = services.GetRequiredService<IModuleTable>();
        var section = moduleAware.GetModuleSection(root, SceneModuleIds.ModuleId);
        _logCommitsPerFrame = section?.GetBool("logCommits") ?? false;
        var multi = section?.GetString("multiWriter");
        _multiWriterMode = string.Equals(multi, "strict", StringComparison.OrdinalIgnoreCase)
            ? SceneMultiWriterMode.Strict
            : SceneMultiWriterMode.LastWriterWins;
        var failed = section?.GetString("failedFrame");
        _failedFramePolicy = string.Equals(failed, "stop", StringComparison.OrdinalIgnoreCase)
            ? FailedFramePolicy.StopPipeline
            : FailedFramePolicy.ConsumeAndContinue;
        _recordHistoryLimit = section?.GetInt("frameRecordLimit", 1000) ?? 1000;
        if (_recordHistoryLimit < 0)
            _recordHistoryLimit = 0;
        var outcomePath = section?.GetString("outcomeJournalPath");
        if (!string.IsNullOrWhiteSpace(outcomePath))
            _outcomeJournal = HostFrameOutcomeJournal.CreateNew(outcomePath);
        var network = moduleAware.GetModuleSection(root, NetworkModuleIds.ModuleId);
        var networkOn = moduleAware.IsModuleEnabled(root, NetworkModuleIds.ModuleId)
            && (network?.GetBool("enabled") ?? false);
        _replicateAfterApply = networkOn;
        _logger.LogDebug(
            $"[SceneCommitService] Initialized logCommits={_logCommitsPerFrame} multiWriter={_multiWriterMode} failedFrame={_failedFramePolicy} replicate={_replicateAfterApply}");
    }

    public void Start(CancellationToken cancellationToken)
    {
    }

    public void Stop()
    {
        _outcomeJournal?.Dispose();
        _outcomeJournal = null;
    }

    internal void SetOutcomeJournal(HostFrameOutcomeJournal.Writer? journal) =>
        _outcomeJournal = journal;

    /// <summary>Only an isolated replay host may align its first tick to a captured frame id.</summary>
    internal void AlignFirstReplayFrame(long firstFrameId)
    {
        if (firstFrameId < 1 || FrameId != 0 || _records.Count != 0)
            throw new InvalidOperationException("Replay frame alignment is allowed only before the first tick.");
        FrameId = firstFrameId - 1;
    }

    public void Register(IDeterministicFrameParticipant participant)
    {
        ArgumentNullException.ThrowIfNull(participant);
        if (string.IsNullOrWhiteSpace(participant.ParticipantId))
            throw new ArgumentException("ParticipantId is required.", nameof(participant));

        lock (_sync)
        {
            foreach (var existing in _participants)
            {
                if (string.Equals(existing.ParticipantId, participant.ParticipantId, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Duplicate frame participant id '{participant.ParticipantId}' (Phase+Order+Id must be unique).");
                }
            }

            _participants.Add(participant);
        }

        _logger?.LogDebug(
            $"[SceneCommitService] Registered frame participant: {participant.ParticipantId} phase={participant.Phase} order={participant.Order}");
    }

    HostFrameResult IHostFrameRunner.RunNextFrame(DateTimeOffset? logicalTime) => RunNextFrame(logicalTime);

    bool IDeterministicHostTick.RaiseHostFrame(long frameId, DateTimeOffset? logicalTime) =>
        RaiseHostFrame(frameId, logicalTime);

    void IDeterministicHostTick.Register(IDeterministicFrameParticipant participant) => Register(participant);

    internal HostFrameResult RunNextFrame(DateTimeOffset? logicalTime = null)
    {
        if (_halted)
        {
            throw new InvalidOperationException(
                "Host frame pipeline is stopped after a failed/discarded frame or journal error.");
        }

        FrameId++;
        var applied = RaiseHostFrame(FrameId, logicalTime);
        var replicated = false;
        if (applied && _replicateAfterApply && _replicas != null)
        {
            _replicas.Tick();
            replicated = true;
        }

        var record = _records[^1];
        LastResult = new HostFrameResult(
            record.FrameId,
            record.LogicalTime,
            record.Status,
            replicated,
            record.FailureParticipant,
            record.FailureReason,
            record.MayHavePartialSceneWrites);

        if (!applied && _failedFramePolicy == FailedFramePolicy.StopPipeline)
            _halted = true;

        return LastResult;
    }

    internal bool RaiseHostFrame(long frameId, DateTimeOffset? logicalTime = null)
    {
        _lastFailureParticipant = null;
        _lastFailureReason = null;

        IDeterministicFrameParticipant[] snapshot;
        lock (_sync)
        {
            snapshot = _participants
                .OrderBy(p => (int)p.Phase)
                .ThenBy(p => p.Order)
                .ThenBy(p => p.ParticipantId, StringComparer.Ordinal)
                .ToArray();
        }

        var time = logicalTime ?? LogicalFrameTime.FromFrameId(frameId);
        var hostThreadId = Environment.CurrentManagedThreadId;
        var batches = new List<(string ParticipantId, IReadOnlyList<ISceneCommitRequest> Ops)>(snapshot.Length);
        string? failedId = null;
        string? failedReason = null;

        foreach (var p in snapshot)
        {
            var batch = new FrameCommitBatch(p.ParticipantId, hostThreadId);
            var ctx = new FrameContext(frameId, time);
            try
            {
                p.OnHostFrame(in ctx, batch);
            }
            catch (Exception ex)
            {
                failedId = p.ParticipantId;
                failedReason = ex.Message;
                _logger?.LogError($"[SceneCommitService] OnHostFrame failed for {p.ParticipantId}: {ex.Message}", ex);
            }
            finally
            {
                batch.Seal();
            }

            if (failedId != null)
                break;

            batches.Add((p.ParticipantId, batch.Operations.ToArray()));
        }

        if (failedId != null)
        {
            _lastFailureParticipant = failedId;
            _lastFailureReason = failedReason;
            _logger?.LogError(
                $"[SceneCommitService] frame {frameId} failed (participant '{failedId}'); scene unchanged; input consumed");
            AppendRecord(frameId, time, HostFrameStatus.Failed, failedId, failedReason);
            return false;
        }

        var conflicts = SceneCommitConflicts.FindMultiWriterKeys(batches);
        if (conflicts.Count > 0)
        {
            foreach (var line in conflicts)
            {
                if (_multiWriterMode == SceneMultiWriterMode.Strict)
                    _logger?.LogError($"[SceneCommitService] Multi-writer (strict): {line}");
                else
                    _logger?.LogWarning($"[SceneCommitService] Multiple participants wrote the same property this frame (last-writer-wins): {line}");
            }

            if (_multiWriterMode == SceneMultiWriterMode.Strict)
            {
                _lastFailureParticipant = "*multi-writer*";
                _lastFailureReason = string.Join("; ", conflicts);
                _logger?.LogError($"[SceneCommitService] frame {frameId} discarded (strict multi-writer)");
                AppendRecord(frameId, time, HostFrameStatus.Discarded, _lastFailureParticipant, _lastFailureReason);
                return false;
            }
        }

        try
        {
            ApplyPending(frameId, batches);
        }
        catch (Exception ex)
        {
            _lastFailureParticipant = "*applicator*";
            _lastFailureReason = ex.Message;
            _halted = true;
            _logger?.LogError(
                $"[SceneCommitService] frame {frameId} applicator failed; Scene may contain partial writes. Pipeline stopped.", ex);
            AppendRecord(frameId, time, HostFrameStatus.Failed,
                _lastFailureParticipant, _lastFailureReason, mayHavePartialSceneWrites: true);
            return false;
        }
        AppendRecord(frameId, time, HostFrameStatus.Applied, null, null);
        return true;
    }

    private void AppendRecord(
        long frameId,
        DateTimeOffset time,
        HostFrameStatus status,
        string? failureParticipant,
        string? failureReason,
        bool mayHavePartialSceneWrites = false)
    {
        if (_recordHistoryLimit > 0)
        {
            while (_records.Count >= _recordHistoryLimit)
                _records.RemoveAt(0);
        }

        var record = new HostFrameRecord(frameId, time, status,
            failureParticipant, failureReason, mayHavePartialSceneWrites);
        _records.Add(record);
        try
        {
            _outcomeJournal?.Append(record);
        }
        catch (Exception ex)
        {
            // An applied frame cannot be rolled back if its durable outcome write fails.
            _halted = true;
            throw new IOException(
                $"Host outcome journal failed after frame {frameId}; Scene may have changed. Pipeline stopped.", ex);
        }
    }

    private void ApplyPending(
        long frameId,
        List<(string ParticipantId, IReadOnlyList<ISceneCommitRequest> Ops)> batches)
    {
        // Collection is atomic (this method is not reached on participant failure).
        // Apply is not a Scene transaction: an exception mid-loop can leave a partial frame.
        if (_scene == null)
            return;

        var applied = 0;
        foreach (var (participantId, ops) in batches)
        {
            if (ops.Count == 0)
                continue;
            applied += ops.Count;
            if (_logCommitsPerFrame)
            {
                var detail = string.Join("; ", ops.Select(r => r.ToString()));
                _logger?.LogInfo(
                    $"[SceneCommitService] frame {frameId} participant {participantId}: {ops.Count} commit(s) — {detail}");
            }

            foreach (var op in ops)
                SceneCommitApplicator.Apply(_scene, op, _logger);
        }

        if (_logCommitsPerFrame && applied == 0)
            _logger?.LogDebug($"[SceneCommitService] frame {frameId}: 0 commits");
    }
}

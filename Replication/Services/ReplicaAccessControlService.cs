using Ape.Core.Runtime.Service;
using Ape.Core.Config.Models;
using Ape.Core.Logging;
using Ape.Core.Replication;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Concurrent;

namespace Ape.Core.Replication.Services;

/// <summary>
/// Implements replica access control: subscription, authentication, authorization.
/// When no config: no-op (GetSubscribedPeers returns null = broadcast).
/// When config present: filters who receives replica updates; enforces auth at Subscribe time.
/// </summary>
public class ReplicaAccessControlService : IReplicaAccessControlService, ICoreService
{
    public string ServiceId => "core-replica-access-control";
    public string Name => "Replica Access Control";

    private ILogger? _logger;
    /// <summary><c>modules["Ape.Core.Replica"]</c>, or null when omitted (broadcast).</summary>
    private readonly IConfigNode? _moduleSection;

    /// <summary>peerId -> path patterns this peer subscribed to</summary>
    private readonly ConcurrentDictionary<string, HashSet<string>> _peerToPatterns = new();

    /// <summary>participantId -> peerId (for auth lookup; peerId used as participantId when no mapping)</summary>
    private readonly ConcurrentDictionary<string, string> _registeredPeers = new();

    private List<AuthRule> _authRules = new();
    private bool _hasSubscriptionConfig;
    private bool _hasAuthConfig;
    private bool _subscriptionExcludeMode;
    private List<string> _subscriptionPaths = new();

    /// <param name="modulesReplicaSection"><c>modules["Ape.Core.Replica"]</c> subtree, or null if omitted.</param>
    public ReplicaAccessControlService(IConfigNode? modulesReplicaSection = null)
    {
        _moduleSection = modulesReplicaSection;
    }

    public void Register(IServiceCollection serviceCollection)
    {
        serviceCollection.AddSingleton<IReplicaAccessControlService>(this);
    }

    public void Initialize(IServiceProvider services)
    {
        _logger = services.GetService<ILogger>();
        LoadConfig();
    }

    public void Start(CancellationToken cancellationToken) { }
    public void Stop() { }

    private void LoadConfig()
    {
        var cfg = _moduleSection;
        if (cfg == null)
        {
            _logger?.LogInfo("[ReplicaAccessControl] No config - broadcast mode (allow all)");
            return;
        }

        // Use TryGetChildObject so missing keys do not mutate the tree (GetObject creates empty nodes).
        if (!cfg.TryGetChildObject("subscription", out var subscriptionNode))
        {
            _subscriptionPaths = new List<string>();
            _subscriptionExcludeMode = false;
            _hasSubscriptionConfig = false;
        }
        else
        {
            _subscriptionPaths = subscriptionNode.GetStringArray("paths");
            _subscriptionExcludeMode = string.Equals(subscriptionNode.GetString("mode", "include"), "exclude", StringComparison.OrdinalIgnoreCase);
            _hasSubscriptionConfig = _subscriptionPaths.Count > 0;
            if (_hasSubscriptionConfig)
                _logger?.LogInfo($"[ReplicaAccessControl] Subscription config: {_subscriptionPaths.Count} path(s), mode={(_subscriptionExcludeMode ? "exclude" : "include")}");
        }

        _authRules = new List<AuthRule>();
        if (cfg.TryGetChildObject("authentication", out var authNode) && authNode.HasKey("rules"))
        {
            var rulesArray = authNode.GetArray("rules");
            foreach (var ruleNode in rulesArray)
            {
                var path = ruleNode.GetString("path");
                if (string.IsNullOrEmpty(path)) continue;

                var whitelist = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
                if (ruleNode.TryGetChildObject("whitelist", out var whitelistNode))
                {
                    foreach (var key in whitelistNode.Keys)
                    {
                        var participantNode = whitelistNode.GetObject(key);
                        var privs = participantNode.GetStringArray("privileges");
                        whitelist[key] = new HashSet<string>(privs, StringComparer.OrdinalIgnoreCase);
                    }
                }

                var blacklist = new HashSet<string>(ruleNode.GetStringArray("blacklist"), StringComparer.OrdinalIgnoreCase);
                _authRules.Add(new AuthRule { Path = path, Whitelist = whitelist, Blacklist = blacklist });
            }
        }

        _hasAuthConfig = _authRules.Count > 0;
        if (_hasAuthConfig)
            _logger?.LogInfo($"[ReplicaAccessControl] Auth config: {_authRules.Count} rule(s)");
    }

    public void RegisterPeer(string peerId)
    {
        _registeredPeers[peerId] = peerId;
        _peerToPatterns.TryAdd(peerId, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        _logger?.LogDebug($"[ReplicaAccessControl] Registered peer: {peerId}");
    }

    public void UnregisterPeer(string peerId)
    {
        UnsubscribeAll(peerId);
        _registeredPeers.TryRemove(peerId, out _);
        _logger?.LogDebug($"[ReplicaAccessControl] Unregistered peer: {peerId}");
    }

    public bool TrySubscribe(string peerId, string pathPattern, out string? denyReason, string requiredPrivilege = "read")
    {
        denyReason = null;
        if (!_registeredPeers.ContainsKey(peerId))
        {
            denyReason = "Peer not registered";
            return false;
        }

        if (_hasAuthConfig && !HasPrivilege(peerId, pathPattern, requiredPrivilege))
        {
            denyReason = $"No {requiredPrivilege} privilege for path pattern: {pathPattern}";
            return false;
        }

        var patterns = _peerToPatterns.GetOrAdd(peerId, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        lock (patterns) patterns.Add(pathPattern);
        _logger?.LogDebug($"[ReplicaAccessControl] Peer {peerId} subscribed to {pathPattern}");
        return true;
    }

    public void Unsubscribe(string peerId, string pathPattern)
    {
        if (_peerToPatterns.TryGetValue(peerId, out var patterns))
        {
            lock (patterns) patterns.Remove(pathPattern);
        }
    }

    public void UnsubscribeAll(string peerId)
    {
        if (_peerToPatterns.TryRemove(peerId, out _))
            _logger?.LogDebug($"[ReplicaAccessControl] Unsubscribed all for peer: {peerId}");
    }

    public void ApplyImplicitSubscribe(string peerId)
    {
        if (!_hasSubscriptionConfig || !_registeredPeers.ContainsKey(peerId)) return;

        if (_subscriptionExcludeMode)
        {
            _peerToPatterns.GetOrAdd(peerId, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            return;
        }

        foreach (var path in _subscriptionPaths)
        {
            TrySubscribe(peerId, path, out _, "read");
        }
    }

    public bool HasPrivilege(string peerId, string replicaPath, string privilege)
    {
        foreach (var rule in _authRules)
        {
            if (!PathMatches(replicaPath, rule.Path)) continue;

            if (rule.Blacklist.Contains(peerId)) return false;
            if (rule.Whitelist.TryGetValue(peerId, out var privs) && privs.Contains(privilege))
                return true;
        }
        return !_hasAuthConfig;
    }

    public IReadOnlyList<string>? GetSubscribedPeers(string replicaPath)
    {
        if (!_hasSubscriptionConfig)
            return null;

        if (_subscriptionExcludeMode)
        {
            foreach (var pattern in _subscriptionPaths)
            {
                if (PathMatches(replicaPath, pattern))
                    return new List<string>();
            }
            return _peerToPatterns.Keys.ToList();
        }

        var result = new List<string>();
        foreach (var kvp in _peerToPatterns)
        {
            var peerId = kvp.Key;
            HashSet<string> patterns;
            lock (kvp.Value) patterns = new HashSet<string>(kvp.Value);
            foreach (var pattern in patterns)
            {
                if (PathMatches(replicaPath, pattern))
                {
                    result.Add(peerId);
                    break;
                }
            }
        }
        return result;
    }

    /// <summary>
    /// Path pattern matching: * = one segment, ** = zero or more segments.
    /// </summary>
    internal static bool PathMatches(string replicaPath, string pattern)
    {
        var pathParts = replicaPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var patternParts = pattern.Split('/', StringSplitOptions.RemoveEmptyEntries);

        int pi = 0, pp = 0;
        while (pp < patternParts.Length)
        {
            if (pi >= pathParts.Length)
                return patternParts[pp] == "**" && pp == patternParts.Length - 1;

            if (patternParts[pp] == "**")
                return true;

            if (patternParts[pp] == "*")
            {
                pi++;
                pp++;
                continue;
            }

            if (!string.Equals(patternParts[pp], pathParts[pi], StringComparison.OrdinalIgnoreCase))
                return false;

            pi++;
            pp++;
        }

        return pi >= pathParts.Length;
    }

    private sealed class AuthRule
    {
        public string Path { get; set; } = "";
        public Dictionary<string, HashSet<string>> Whitelist { get; set; } = new();
        public HashSet<string> Blacklist { get; set; } = new();
    }
}

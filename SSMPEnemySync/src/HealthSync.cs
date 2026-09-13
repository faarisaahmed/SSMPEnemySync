using System;
using System.Collections.Generic;
using SSMP.Api.Client.Networking;
using UnityEngine;

namespace SSMPEnemySync;

/// <summary>
/// Gives every synced enemy a single pool of health shared by both players.
///
/// SSMP's entity system never synchronised health — <c>HealthManagerComponent</c> carries the
/// comment "TODO: periodically (or on hit) sync the health of the entity" and only replicates death
/// and invincibility. The consequence is that a scene client's hits land on an inert puppet and are
/// discarded, so only the scene host can actually kill anything.
///
/// This closes both halves of that gap by watching health rather than patching the damage path.
/// Polling is deliberate: damage in this game arrives from nail hits, spells, tools, poison and
/// recoil through several unrelated code paths, and watching the resulting health value catches all
/// of them without a patch per source.
/// <list type="bullet">
/// <item>On the scene host, any change to a host object's health is broadcast.</item>
/// <item>On a scene client, a drop in a puppet's health is a local hit: it is reported to the host
/// and immediately undone locally, leaving the host's value authoritative.</item>
/// </list>
/// </summary>
internal class HealthSync : MonoBehaviour {
    /// <summary>The internal EntityManager instance, held as object since the type is internal.</summary>
    private object _entityManager;

    private IClientAddonNetworkSender<EnemySyncPacketId> _sender;

    private Func<bool> _isConnected;

    /// <summary>
    /// Last health this client observed for each entity.
    ///
    /// On the host this is the last value broadcast; on a client it is the last authoritative value,
    /// which is also what a puppet gets restored to after a local hit.
    /// </summary>
    private readonly Dictionary<ushort, int> _knownHp = new();

    /// <summary>Health changes waiting to be broadcast this frame (scene host only).</summary>
    private readonly List<EntityHealth> _pendingHealth = new();

    /// <summary>Damage waiting to be reported to the scene host (scene client only).</summary>
    private readonly List<EntityDamage> _pendingDamage = new();

    /// <summary>Health received from the host that has not been applied to a puppet yet.</summary>
    private readonly Dictionary<ushort, int> _receivedHp = new();

    /// <summary>Damage received from clients that the host has not applied yet.</summary>
    private readonly List<EntityDamage> _receivedDamage = new();

    /// <summary>Whether this client was the scene host on the previous frame.</summary>
    private bool _wasSceneHost;

    /// <summary>
    /// How often the scene host re-sends every entity's health rather than only what changed.
    ///
    /// Changes alone are not enough: a player who walks into a fight already in progress has missed
    /// every change that happened before they arrived, and since an undamaged enemy never changes
    /// again until it is hit, they would keep the authored maximum indefinitely. This also repairs
    /// any update lost in transit.
    /// </summary>
    private const float FullResyncIntervalSeconds = 2f;

    private float _nextFullResync;

    public void Configure(
        object entityManager,
        IClientAddonNetworkSender<EnemySyncPacketId> sender,
        Func<bool> isConnected
    ) {
        _entityManager = entityManager;
        _sender = sender;
        _isConnected = isConnected;
    }

    /// <summary>
    /// Forget all cached health. Called on scene change, on disconnect, and when the scene host role
    /// changes, since entity IDs are only unique within one scene and are reused across scenes.
    /// </summary>
    public void Reset() {
        _knownHp.Clear();
        _receivedHp.Clear();
        _receivedDamage.Clear();
        _pendingHealth.Clear();
        _pendingDamage.Clear();

        // Resync on the next frame rather than up to an interval later, so a newly loaded scene
        // starts from the host's values immediately.
        _nextFullResync = 0f;
    }

    #region Network callbacks

    /// <summary>Queue authoritative health from the scene host, applied on the next frame.</summary>
    public void OnHealthUpdate(HealthUpdatePacket packet) {
        foreach (var entry in packet.Entries) {
            _receivedHp[entry.EntityId] = entry.Hp;
        }
    }

    /// <summary>Queue damage reported by another client, applied on the next frame if we host.</summary>
    public void OnDamageReport(DamageReportPacket packet) {
        _receivedDamage.AddRange(packet.Entries);
    }

    #endregion

    private void Update() {
        if (_entityManager == null || _isConnected == null || !_isConnected()) {
            return;
        }

        // Until SSMP has decided who hosts the scene, neither branch below is meaningful: acting as
        // a client would report damage to nobody, acting as host would broadcast health nobody owns.
        if (!SsmpReflection.IsSceneHostDetermined(_entityManager)) {
            return;
        }

        var isSceneHost = SsmpReflection.IsSceneHost(_entityManager);

        // Scene host can change mid-scene when the previous host leaves. The incoming host's host
        // objects start at whatever health they were authored with, so cached values from the other
        // role would be applied to the wrong object.
        if (isSceneHost != _wasSceneHost) {
            Log.Info($"Scene host role changed: now {(isSceneHost ? "scene host" : "scene client")}");
            _wasSceneHost = isSceneHost;
            Reset();
            return;
        }

        var entities = SsmpReflection.GetEntities(_entityManager);

        if (isSceneHost) {
            ApplyReportedDamage(entities);
            BroadcastHealthChanges(entities);
        } else {
            ApplyAuthoritativeHealth(entities);
            ReportLocalDamage(entities);
        }
    }

    #region Scene host

    /// <summary>
    /// Apply damage other players dealt to their puppets onto the real enemy.
    ///
    /// This is what actually makes a second player able to kill something.
    /// </summary>
    private void ApplyReportedDamage(List<EntityView> entities) {
        if (_receivedDamage.Count == 0) {
            return;
        }

        foreach (var damage in _receivedDamage) {
            var healthManager = FindHealthManager(entities, damage.EntityId, host: true);
            if (healthManager == null) {
                continue;
            }

            // Already dead and waiting to be cleaned up; applying more damage would re-trigger death.
            if (healthManager.hp <= 0) {
                continue;
            }

            healthManager.hp -= damage.Amount;

            Log.Debug($"Applied {damage.Amount} remote damage to entity {damage.EntityId} (hp now {healthManager.hp})");

            if (healthManager.hp <= 0) {
                // Decrementing health alone does not kill anything: the enemy would sit at zero with
                // its AI still running. Die is what SSMP's own death component observes and
                // replicates to the other client, so the kill has to go through it.
                healthManager.hp = 0;

                try {
                    healthManager.Die(null, AttackTypes.Generic, true);
                } catch (Exception e) {
                    Log.Warn($"Die failed for entity {damage.EntityId}: {e.Message}");
                }
            }
        }

        _receivedDamage.Clear();
    }

    /// <summary>Broadcast any host-side health that changed since the last frame.</summary>
    private void BroadcastHealthChanges(List<EntityView> entities) {
        _pendingHealth.Clear();

        var fullResync = Time.unscaledTime >= _nextFullResync;
        if (fullResync) {
            _nextFullResync = Time.unscaledTime + FullResyncIntervalSeconds;
        }

        foreach (var entity in entities) {
            var healthManager = GetHealthManager(entity.Host);
            if (healthManager == null) {
                continue;
            }

            var hp = healthManager.hp;

            if (!fullResync && _knownHp.TryGetValue(entity.Id, out var previous) && previous == hp) {
                continue;
            }

            _knownHp[entity.Id] = hp;
            _pendingHealth.Add(new EntityHealth { EntityId = entity.Id, Hp = hp });
        }

        if (_pendingHealth.Count > 0) {
            _sender.SendSingleData(
                EnemySyncPacketId.HealthUpdate,
                new HealthUpdatePacket { Entries = new List<EntityHealth>(_pendingHealth) }
            );
        }
    }

    #endregion

    #region Scene client

    /// <summary>Write host-authoritative health onto the local puppets.</summary>
    private void ApplyAuthoritativeHealth(List<EntityView> entities) {
        if (_receivedHp.Count == 0) {
            return;
        }

        List<ushort> applied = null;

        foreach (var pair in _receivedHp) {
            var healthManager = FindHealthManager(entities, pair.Key, host: false);
            if (healthManager == null) {
                // The entity has not been discovered locally yet, which is normal for the first
                // frames after a scene load. Keep the value and try again next frame rather than
                // dropping it: the host only re-sends health when it changes.
                continue;
            }

            // Zero would kill the puppet locally. Death arrives separately, through SSMP's own death
            // replication, which plays the correct effects and respects the fight's scripting.
            healthManager.hp = Mathf.Max(pair.Value, 1);

            // Record what was actually written, not what was received, so the damage detection below
            // compares against the same number it will read back next frame.
            _knownHp[pair.Key] = healthManager.hp;

            (applied ??= new List<ushort>()).Add(pair.Key);
        }

        if (applied == null) {
            return;
        }

        foreach (var id in applied) {
            _receivedHp.Remove(id);
        }
    }

    /// <summary>
    /// Detect hits this player landed on a puppet, report them, and undo them locally.
    /// </summary>
    private void ReportLocalDamage(List<EntityView> entities) {
        _pendingDamage.Clear();

        foreach (var entity in entities) {
            var healthManager = GetHealthManager(entity.Client);
            if (healthManager == null) {
                continue;
            }

            var hp = healthManager.hp;

            if (!_knownHp.TryGetValue(entity.Id, out var previous)) {
                // First sight of this entity. Take the local value as the baseline; the host's next
                // broadcast corrects it if the enemy was already damaged before we arrived.
                _knownHp[entity.Id] = hp;
                continue;
            }

            if (hp >= previous) {
                _knownHp[entity.Id] = hp;
                continue;
            }

            var damage = previous - hp;

            // Restore immediately. The hit flash and stagger have already played, so the hit still
            // reads as landing, but the number stays under the host's control and the puppet cannot
            // die locally and desync the fight.
            healthManager.hp = previous;

            _pendingDamage.Add(new EntityDamage { EntityId = entity.Id, Amount = damage });

            Log.Debug($"Reporting {damage} damage on entity {entity.Id} to the scene host");
        }

        if (_pendingDamage.Count > 0) {
            _sender.SendSingleData(
                EnemySyncPacketId.DamageReport,
                new DamageReportPacket { Entries = new List<EntityDamage>(_pendingDamage) }
            );
        }
    }

    #endregion

    private static HealthManager FindHealthManager(List<EntityView> entities, ushort id, bool host) {
        foreach (var entity in entities) {
            if (entity.Id == id) {
                return GetHealthManager(host ? entity.Host : entity.Client);
            }
        }

        return null;
    }

    /// <summary>
    /// Get the HealthManager on the given object, treating a destroyed object as absent. Entities
    /// outlive their objects briefly when a scene unloads, so the Unity null check matters here.
    /// </summary>
    private static HealthManager GetHealthManager(GameObject gameObject) =>
        gameObject == null ? null : gameObject.GetComponent<HealthManager>();
}

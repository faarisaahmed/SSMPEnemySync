using System;
using SSMP.Api.Client;
using SSMP.Networking.Packet;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SSMPEnemySync;

/// <summary>
/// Turns SSMP's dormant entity system on for Silksong and adds the health synchronisation it never had.
///
/// SSMP ships a complete host/client entity system that replicates enemy position, animation, FSM
/// state and death, but three calls that start it are commented out in ClientManager
/// (<c>_entityManager.Initialize()</c>, <c>RegisterHooks()</c>, <c>DeregisterHooks()</c>) and its
/// entity registry is Hollow Knight 1's. This addon supplies the missing pieces from outside:
/// <list type="number">
/// <item>Enables full synchronisation, which gates every entity packet handler on both ends.</item>
/// <item>Registers Silksong's enemies so they qualify as entities at all
/// (<see cref="EntityRegistryPopulator"/>).</item>
/// <item>Starts the entity manager and its hooks.</item>
/// <item>Shares one pool of health per enemy across both players (<see cref="HealthSync"/>).</item>
/// </list>
/// </summary>
public class EnemySyncClientAddon : ClientAddon {
    /// <inheritdoc />
    protected override string Name => "EnemySync";

    /// <inheritdoc />
    protected override string Version => "0.1.0";

    /// <inheritdoc />
    public override bool NeedsNetwork => true;

    /// <inheritdoc />
    /// <remarks>Deliberately a literal, not Addon.CurrentApiVersion: it records the API this addon
    /// was written against, so a future SSMP can refuse to load it rather than load it broken.</remarks>
    public override uint ApiVersion => 1;

    /// <summary>The internal EntityManager, held as object because the type is internal.</summary>
    private object _entityManager;

    private HealthSync _healthSync;

    private IClientApi _api;

    /// <summary>
    /// Whether <c>EntityManager.Initialize</c> has run. It wires up static state and is not written
    /// to be run twice, so it must survive reconnects.
    /// </summary>
    private bool _entityManagerInitialized;

    /// <summary>Whether entity hooks are currently registered, to keep register/deregister balanced.</summary>
    private bool _hooksRegistered;

    /// <inheritdoc />
    public override void Initialize(IClientApi clientApi) {
        _api = clientApi;

        Log.Initialize(Logger);

        SsmpReflection.Initialize();
        if (!SsmpReflection.Available) {
            Log.Error(
                $"Cannot read SSMP's internals ({SsmpReflection.UnavailableReason}). " +
                "This build of SSMP is not compatible with this addon; enemy sync stays off."
            );
            return;
        }

        _entityManager = SsmpReflection.GetEntityManager(clientApi.ClientManager);
        if (_entityManager == null) {
            Log.Error("SSMP has no entity manager; enemy sync stays off.");
            return;
        }

        if (!EntityRegistryPopulator.Initialize()) {
            Log.Error("Could not reach SSMP's entity registry; enemy sync stays off.");
            return;
        }

        // Has to happen before a server is started: SSMP reads this setting when hosting and uses it
        // to decide whether to register entity packet handlers at all.
        SsmpReflection.EnableFullSynchronisationSetting(clientApi.ClientManager);

        // Health sync first: the packet handlers registered below close over it.
        SetUpHealthSync();
        SetUpNetworking();

        clientApi.ClientManager.ConnectEvent += OnConnect;
        clientApi.ClientManager.DisconnectEvent += OnDisconnect;

        Log.Info("Initialised. Enemy sync starts when you connect to a server.");
    }

    private void SetUpNetworking() {
        var receiver = _api.NetClient.GetNetworkReceiver<EnemySyncPacketId>(this, InstantiatePacket);

        receiver.RegisterPacketHandler<HealthUpdatePacket>(
            EnemySyncPacketId.HealthUpdate,
            packet => _healthSync.OnHealthUpdate(packet)
        );
        receiver.RegisterPacketHandler<DamageReportPacket>(
            EnemySyncPacketId.DamageReport,
            packet => _healthSync.OnDamageReport(packet)
        );
    }

    private static IPacketData InstantiatePacket(EnemySyncPacketId packetId) => packetId switch {
        EnemySyncPacketId.HealthUpdate => new HealthUpdatePacket(),
        EnemySyncPacketId.DamageReport => new DamageReportPacket(),
        _ => null
    };

    /// <summary>
    /// Host the health sync behaviour on its own object so it gets an Update tick independent of
    /// SSMP's own update plumbing.
    /// </summary>
    private void SetUpHealthSync() {
        var gameObject = new GameObject("SSMPEnemySync");
        UnityEngine.Object.DontDestroyOnLoad(gameObject);

        _healthSync = gameObject.AddComponent<HealthSync>();
        _healthSync.Configure(
            _entityManager,
            _api.NetClient.GetNetworkSender<EnemySyncPacketId>(this),
            () => _api.NetClient.IsConnected
        );
    }

    private void OnConnect() {
        // The client-side branches that handle entity packets check this flag. It normally comes from
        // the server's handshake; forcing it covers a server that reported false.
        SsmpReflection.ForceClientFullSynchronisation(_api.ClientManager);

        if (!_entityManagerInitialized) {
            try {
                SsmpReflection.EntityManagerInitialize(_entityManager);
                _entityManagerInitialized = true;
            } catch (Exception e) {
                Log.Error($"Could not initialise the entity manager: {e.Message}");
                return;
            }
        }

        if (_hooksRegistered) {
            return;
        }

        // Order matters. Enemies have to be in the registry before SSMP's discovery walks the scene,
        // and Unity runs scene callbacks in subscription order, so ours must be registered first.
        SceneManager.sceneLoaded += OnSceneLoaded;
        SceneManager.activeSceneChanged += OnActiveSceneChanged;

        try {
            SsmpReflection.EntityManagerRegisterHooks(_entityManager);
            _hooksRegistered = true;
        } catch (Exception e) {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.activeSceneChanged -= OnActiveSceneChanged;

            Log.Error($"Could not register entity hooks: {e.Message}");
            return;
        }

        // The scene the player is already standing in raises no load event, so it is registered now.
        EntityRegistryPopulator.RegisterEnemiesInScene(SceneManager.GetActiveScene());

        Log.Info("Enemy sync active.");
    }

    private void OnDisconnect() {
        if (!_hooksRegistered) {
            return;
        }

        SceneManager.sceneLoaded -= OnSceneLoaded;
        SceneManager.activeSceneChanged -= OnActiveSceneChanged;

        try {
            SsmpReflection.EntityManagerDeregisterHooks(_entityManager);
        } catch (Exception e) {
            Log.Warn($"Could not deregister entity hooks: {e.Message}");
        }

        _hooksRegistered = false;
        _healthSync.Reset();

        Log.Info("Enemy sync stopped.");
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode) =>
        EntityRegistryPopulator.RegisterEnemiesInScene(scene);

    private void OnActiveSceneChanged(Scene from, Scene to) {
        // Health cached for the previous scene's entity IDs would otherwise be applied to whichever
        // enemies happen to reuse those IDs here.
        _healthSync.Reset();

        EntityRegistryPopulator.RegisterEnemiesInScene(to);
    }
}

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using SSMP.Api.Client;
using UnityEngine;

namespace SSMPEnemySync;

/// <summary>
/// Typed access to the parts of SSMP that are declared <c>internal</c>.
///
/// The entity system is entirely internal, and the addon API deliberately exposes nothing about
/// entities, so every member below has to be reached by reflection. Everything is resolved once and
/// cached; a member that cannot be resolved leaves this class <see cref="Available"/> = false rather
/// than throwing, so a future SSMP version renaming a field disables the addon instead of spraying
/// exceptions through the game loop.
/// </summary>
internal static class SsmpReflection {
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    /// <summary>Whether every member this addon needs was resolved successfully.</summary>
    public static bool Available { get; private set; }

    /// <summary>Description of the first member that failed to resolve, for logging.</summary>
    public static string UnavailableReason { get; private set; } = "not initialised";

    // The SSMP assembly, taken from a public type we do bind to at compile time.
    private static Assembly _ssmp;

    // ClientManager members.
    private static FieldInfo _cmEntityManager;
    private static FieldInfo _cmModSettings;
    private static FieldInfo _cmFullSync;

    // EntityManager members.
    private static FieldInfo _emEntities;
    private static PropertyInfo _emIsSceneHost;
    private static PropertyInfo _emIsSceneHostDetermined;
    private static MethodInfo _emInitialize;
    private static MethodInfo _emRegisterHooks;
    private static MethodInfo _emDeregisterHooks;

    // Entity members.
    private static PropertyInfo _eId;
    private static PropertyInfo _eObject;

    // HostClientPair<GameObject> members.
    private static PropertyInfo _pairHost;
    private static PropertyInfo _pairClient;

    // ModSettings members.
    private static PropertyInfo _msFullSync;

    /// <summary>Types used by <see cref="RegistryFallbackPatch"/>.</summary>
    public static Type EntityRegistryType { get; private set; }

    /// <inheritdoc cref="EntityRegistryType"/>
    public static Type EntityRegistryEntryType { get; private set; }

    /// <inheritdoc cref="EntityRegistryType"/>
    public static Type EntityTypeEnum { get; private set; }

    private static PropertyInfo _entryBaseObjectName;
    private static PropertyInfo _entryType;
    private static PropertyInfo _entryFsmName;

    /// <summary>
    /// Resolve every member. Safe to call more than once; only the first call does work.
    /// </summary>
    public static void Initialize() {
        if (Available || UnavailableReason != "not initialised") {
            return;
        }

        try {
            // ClientAddon is public, so this binds at compile time and gives us the assembly without
            // having to guess at how it was loaded.
            _ssmp = typeof(ClientAddon).Assembly;

            var clientManagerType = Require(_ssmp.GetType("SSMP.Game.Client.ClientManager"), "ClientManager type");
            var entityManagerType = Require(_ssmp.GetType("SSMP.Game.Client.Entity.EntityManager"), "EntityManager type");
            var entityType = Require(_ssmp.GetType("SSMP.Game.Client.Entity.Entity"), "Entity type");
            var modSettingsType = Require(_ssmp.GetType("SSMP.Game.Settings.ModSettings"), "ModSettings type");

            EntityRegistryType = Require(_ssmp.GetType("SSMP.Game.Client.Entity.EntityRegistry"), "EntityRegistry type");
            EntityRegistryEntryType = Require(_ssmp.GetType("SSMP.Game.Client.Entity.EntityRegistryEntry"), "EntityRegistryEntry type");
            EntityTypeEnum = Require(_ssmp.GetType("SSMP.Game.Client.Entity.EntityType"), "EntityType enum");

            _cmEntityManager = Require(clientManagerType.GetField("_entityManager", Instance), "ClientManager._entityManager");
            _cmModSettings = Require(clientManagerType.GetField("_modSettings", Instance), "ClientManager._modSettings");
            _cmFullSync = Require(clientManagerType.GetField("_fullSynchronisation", Instance), "ClientManager._fullSynchronisation");

            _emEntities = Require(entityManagerType.GetField("_entities", Instance), "EntityManager._entities");
            _emIsSceneHost = Require(entityManagerType.GetProperty("IsSceneHost", Instance), "EntityManager.IsSceneHost");
            _emIsSceneHostDetermined = Require(entityManagerType.GetProperty("IsSceneHostDetermined", Instance), "EntityManager.IsSceneHostDetermined");
            _emInitialize = Require(entityManagerType.GetMethod("Initialize", Instance, null, Type.EmptyTypes, null), "EntityManager.Initialize");
            _emRegisterHooks = Require(entityManagerType.GetMethod("RegisterHooks", Instance, null, Type.EmptyTypes, null), "EntityManager.RegisterHooks");
            _emDeregisterHooks = Require(entityManagerType.GetMethod("DeregisterHooks", Instance, null, Type.EmptyTypes, null), "EntityManager.DeregisterHooks");

            _eId = Require(entityType.GetProperty("Id", Instance), "Entity.Id");
            _eObject = Require(entityType.GetProperty("Object", Instance), "Entity.Object");

            var pairType = Require(_eObject.PropertyType, "HostClientPair<GameObject> type");
            _pairHost = Require(pairType.GetProperty("Host", Instance), "HostClientPair.Host");
            _pairClient = Require(pairType.GetProperty("Client", Instance), "HostClientPair.Client");

            _msFullSync = Require(modSettingsType.GetProperty("FullSynchronisation", Instance), "ModSettings.FullSynchronisation");

            _entryBaseObjectName = Require(EntityRegistryEntryType.GetProperty("BaseObjectName", Instance), "EntityRegistryEntry.BaseObjectName");
            _entryType = Require(EntityRegistryEntryType.GetProperty("Type", Instance), "EntityRegistryEntry.Type");
            _entryFsmName = Require(EntityRegistryEntryType.GetProperty("FsmName", Instance), "EntityRegistryEntry.FsmName");

            Available = true;
            UnavailableReason = null;
        } catch (Exception e) {
            Available = false;
            UnavailableReason = e.Message;
        }
    }

    private static T Require<T>(T member, string description) where T : class {
        if (member == null) {
            throw new MissingMemberException($"could not resolve {description}");
        }

        return member;
    }

    #region ClientManager

    /// <summary>Get the internal EntityManager held by the given ClientManager.</summary>
    public static object GetEntityManager(IClientManager clientManager) =>
        _cmEntityManager.GetValue(clientManager);

    /// <summary>
    /// Turn on full synchronisation for servers hosted by this client.
    ///
    /// SSMP reads <c>ModSettings.FullSynchronisation</c> when starting a hosted server
    /// (ModServerManager.cs:63) and it gates every entity packet handler on the server. It defaults
    /// to false and the menu toggle that used to set it is commented out (ModMenu.cs:266), so
    /// without this the server drops entity traffic on the floor.
    /// </summary>
    public static void EnableFullSynchronisationSetting(IClientManager clientManager) {
        var modSettings = _cmModSettings.GetValue(clientManager);
        if (modSettings != null) {
            _msFullSync.SetValue(modSettings, true);
        }
    }

    /// <summary>
    /// Mirror the flag onto the connected-client side so the entity branches in ClientManager run.
    /// Normally set from the server's handshake; we force it for servers that report false.
    /// </summary>
    public static void ForceClientFullSynchronisation(IClientManager clientManager) =>
        _cmFullSync.SetValue(clientManager, true);

    /// <inheritdoc cref="ForceClientFullSynchronisation"/>
    public static bool GetClientFullSynchronisation(IClientManager clientManager) =>
        (bool) _cmFullSync.GetValue(clientManager);

    #endregion

    #region EntityManager

    public static void EntityManagerInitialize(object entityManager) => _emInitialize.Invoke(entityManager, null);

    public static void EntityManagerRegisterHooks(object entityManager) => _emRegisterHooks.Invoke(entityManager, null);

    public static void EntityManagerDeregisterHooks(object entityManager) => _emDeregisterHooks.Invoke(entityManager, null);

    public static bool IsSceneHost(object entityManager) => (bool) _emIsSceneHost.GetValue(entityManager);

    public static bool IsSceneHostDetermined(object entityManager) =>
        (bool) _emIsSceneHostDetermined.GetValue(entityManager);

    /// <summary>
    /// Snapshot the entity table as a list of lightweight views.
    ///
    /// A snapshot rather than a live view: the dictionary is mutated from scene load and network
    /// callbacks, so iterating it directly risks an InvalidOperationException mid-frame.
    /// </summary>
    public static List<EntityView> GetEntities(object entityManager) {
        var result = new List<EntityView>();
        if (_emEntities.GetValue(entityManager) is not IDictionary entities) {
            return result;
        }

        foreach (DictionaryEntry pair in entities) {
            var entity = pair.Value;
            if (entity == null) {
                continue;
            }

            var pairObject = _eObject.GetValue(entity);
            if (pairObject == null) {
                continue;
            }

            result.Add(new EntityView {
                Id = (ushort) _eId.GetValue(entity),
                Host = _pairHost.GetValue(pairObject) as GameObject,
                Client = _pairClient.GetValue(pairObject) as GameObject
            });
        }

        return result;
    }

    #endregion

    #region EntityRegistryEntry

    /// <summary>
    /// Build an <c>EntityRegistryEntry</c> for an object the shipped registry does not know about.
    /// </summary>
    public static object CreateRegistryEntry(string baseObjectName, string fsmName, ushort entityTypeValue) {
        var entry = Activator.CreateInstance(EntityRegistryEntryType);

        _entryBaseObjectName.SetValue(entry, baseObjectName);
        _entryFsmName.SetValue(entry, fsmName);
        _entryType.SetValue(entry, Enum.ToObject(EntityTypeEnum, entityTypeValue));

        return entry;
    }

    #endregion
}

/// <summary>
/// A flattened view of one SSMP entity: its network ID and the two game objects it switches between.
/// </summary>
internal struct EntityView {
    /// <summary>Network ID, identical on every client for a given enemy.</summary>
    public ushort Id;

    /// <summary>The object that runs real AI. Active only on the scene host.</summary>
    public GameObject Host;

    /// <summary>The puppet driven by network updates. Active only on scene clients.</summary>
    public GameObject Client;
}

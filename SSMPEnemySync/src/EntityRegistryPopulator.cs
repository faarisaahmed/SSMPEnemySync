using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SSMPEnemySync;

/// <summary>
/// Adds Silksong enemies to SSMP's entity registry.
///
/// SSMP only syncs a game object if it matches an entry in the registry
/// (<c>EntityProcessor.Process</c> returns early otherwise), and the registry it ships is Hollow
/// Knight 1's: Zombie Runner, False Knight, Mawlek, Gruz. Almost nothing in Silksong matches, which
/// is why enabling full synchronisation on its own syncs nothing.
///
/// Two sources fill the gap:
/// <list type="bullet">
/// <item>An embedded table generated from EnemyBehaviorApi.Survey dumps, which carries the correct
/// primary FSM name per enemy.</item>
/// <item>A scan of each loaded scene that registers anything carrying a <see cref="HealthManager"/>,
/// so enemies and bosses that have never been surveyed still sync.</item>
/// </list>
/// </summary>
internal static class EntityRegistryPopulator {
    private const string EmbeddedTablePath = "SSMPEnemySync.res.silksong-enemies.json";

    /// <summary>
    /// Strips the suffixes Unity appends to instantiated objects, so "Bone Goomba (Clone) (1)"
    /// registers under the authored name the other client will also see.
    /// </summary>
    private static readonly Regex InstanceSuffix = new(@"\s*\((Clone|\d+)\)\s*$", RegexOptions.Compiled);

    /// <summary>
    /// Names that carry a HealthManager but must never become entities: the player, and the remote
    /// player objects SSMP spawns for other clients. Registering those would have SSMP fight itself
    /// over who controls the hero.
    /// </summary>
    private static readonly string[] ExcludedNames = ["Knight", "Hero", "Player Prefab", "PlayerPrefab"];

    /// <summary>The live registry list, reached by reflection because the registry is internal.</summary>
    private static IList _entries;

    /// <summary>Base names already registered, so repeated scene loads do not add duplicates.</summary>
    private static readonly HashSet<string> Registered = new(StringComparer.Ordinal);

    /// <summary>Enemy name -> primary FSM name, from the embedded survey table.</summary>
    private static readonly Dictionary<string, string> SurveyFsmNames = new(StringComparer.Ordinal);

    public static int RegisteredCount => Registered.Count;

    /// <summary>
    /// Bind to the registry and load the survey table. Returns false if the registry could not be
    /// reached, in which case the addon cannot do anything useful.
    /// </summary>
    public static bool Initialize() {
        var entriesProperty = SsmpReflection.EntityRegistryType.GetProperty(
            "Entries",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public
        );

        if (entriesProperty?.GetValue(null) is not IList entries) {
            return false;
        }

        _entries = entries;

        LoadSurveyTable();

        return true;
    }

    /// <summary>
    /// Read the embedded survey-derived table. A failure here is not fatal: without it every enemy
    /// simply falls back to its discovered FSM instead of a known-good one.
    /// </summary>
    private static void LoadSurveyTable() {
        try {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(EmbeddedTablePath);
            if (stream == null) {
                return;
            }

            using var reader = new StreamReader(stream);
            var records = JsonConvert.DeserializeObject<List<SurveyEnemy>>(reader.ReadToEnd());
            if (records == null) {
                return;
            }

            foreach (var record in records) {
                if (!string.IsNullOrEmpty(record.Name) && !string.IsNullOrEmpty(record.Fsm)) {
                    SurveyFsmNames[record.Name] = record.Fsm;
                }
            }
        } catch (Exception e) {
            Log.Warn($"Could not read the embedded enemy table: {e.Message}");
        }
    }

    /// <summary>
    /// Register every enemy in the given scene.
    ///
    /// Must run before SSMP's own scene handler, which is why the addon registers its scene callback
    /// before calling EntityManager.RegisterHooks: Unity invokes sceneLoaded handlers in
    /// registration order, and an enemy registered after discovery has already run is missed until
    /// the next scene load.
    /// </summary>
    public static void RegisterEnemiesInScene(Scene scene) {
        if (_entries == null) {
            return;
        }

        var added = 0;

        // Inactive objects included: plenty of enemies and every boss start inactive and are woken
        // by an FSM, and SSMP's own discovery looks at inactive objects too. Order does not matter,
        // since each object is registered by name, so the unsorted query is the cheaper one.
        var healthManagers = UnityEngine.Object.FindObjectsByType<HealthManager>(
            FindObjectsInactive.Include,
            FindObjectsSortMode.None
        );

        foreach (var healthManager in healthManagers) {
            var gameObject = healthManager.gameObject;
            if (!gameObject.scene.IsValid() || gameObject.scene != scene) {
                continue;
            }

            if (TryRegister(gameObject)) {
                added++;
            }
        }

        if (added > 0) {
            Log.Info($"Registered {added} enemy name(s) from scene '{scene.name}' ({Registered.Count} total)");
        }
    }

    /// <summary>
    /// Add a registry entry for the given object's authored name, if it qualifies and is new.
    /// </summary>
    /// <returns>true if an entry was added.</returns>
    private static bool TryRegister(GameObject gameObject) {
        var baseName = InstanceSuffix.Replace(gameObject.name, string.Empty).Trim();

        if (baseName.Length == 0 || Registered.Contains(baseName)) {
            return false;
        }

        if (ExcludedNames.Any(excluded => string.Equals(baseName, excluded, StringComparison.Ordinal))) {
            return false;
        }

        // An FSM is what SSMP actually synchronises. Without one there is no behaviour to replicate,
        // and registering the object would only add a puppet that never does anything.
        var fsms = gameObject.GetComponents<PlayMakerFSM>();
        if (fsms.Length == 0) {
            return false;
        }

        // Prefer the FSM the survey identified as primary; otherwise "Control", which is the
        // overwhelmingly common convention in this game; otherwise whatever is on the object.
        string fsmName;
        if (SurveyFsmNames.TryGetValue(baseName, out var surveyed)
            && fsms.Any(fsm => fsm.Fsm.Name == surveyed)) {
            fsmName = surveyed;
        } else if (fsms.Any(fsm => fsm.Fsm.Name == "Control")) {
            fsmName = "Control";
        } else {
            fsmName = fsms[0].Fsm.Name;
        }

        try {
            _entries.Add(SsmpReflection.CreateRegistryEntry(baseName, fsmName, SyntheticEntityType(baseName)));
        } catch (Exception e) {
            Log.Warn($"Could not register '{baseName}': {e.Message}");
            return false;
        }

        Registered.Add(baseName);
        Log.Debug($"Registered enemy '{baseName}' (FSM '{fsmName}')");

        return true;
    }

    /// <summary>
    /// Derive a stable <c>EntityType</c> value for an enemy the enum has no name for.
    ///
    /// Both clients must agree on the value, so it is hashed from the name rather than assigned in
    /// discovery order. The range sits far above the ~236 real enum values so it can never collide
    /// with a type SSMP special-cases (MantisLord and CityElevator skip a physics reset, Tiktik and
    /// Garpede require particular components), and it stays inside ushort because that is how
    /// EntitySpawn serialises the type.
    /// </summary>
    private static ushort SyntheticEntityType(string baseName) {
        // FNV-1a: stable across processes and runtimes, unlike string.GetHashCode.
        unchecked {
            var hash = 2166136261;
            foreach (var c in baseName) {
                hash = (hash ^ c) * 16777619;
            }

            const ushort rangeStart = 40000;
            const ushort rangeSize = 25000;

            return (ushort) (rangeStart + hash % rangeSize);
        }
    }

    /// <summary>One row of the embedded survey-derived table.</summary>
    private class SurveyEnemy {
        [JsonProperty("name")] public string Name { get; set; }

        [JsonProperty("fsm")] public string Fsm { get; set; }

        [JsonProperty("maxHealth")] public int MaxHealth { get; set; }
    }
}

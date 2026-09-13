using System.Reflection;

// Verifies, without running the game, that every member SSMPEnemySync reaches by reflection
// actually exists in the installed SSMP.dll. Reflection strings are the addon's main fragility:
// a rename turns into a silent runtime failure, so they get checked against real metadata here.

var game = Environment.GetEnvironmentVariable("GAME_DIR")!;
var ssmpDll = Path.Combine(game, "BepInEx/plugins/ssmp-fixed/SSMP.dll");
var managed = Path.Combine(game, "Hollow Knight Silksong_Data/Managed");

var paths = new List<string> { ssmpDll };
paths.AddRange(Directory.GetFiles(managed, "*.dll"));
paths.AddRange(Directory.GetFiles(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "*.dll"));
paths.AddRange(Directory.GetFiles(Path.GetDirectoryName(ssmpDll)!, "*.dll"));

// PathAssemblyResolver rejects two assemblies with the same simple name, and both the game's
// Managed folder and the host runtime ship an mscorlib. First wins, and the search order above puts
// the game's own assemblies first, which are the ones SSMP was compiled against.
var unique = paths
    .GroupBy(Path.GetFileNameWithoutExtension, StringComparer.OrdinalIgnoreCase)
    .Select(group => group.First());

using var mlc = new MetadataLoadContext(new PathAssemblyResolver(unique));
var ssmp = mlc.LoadFromAssemblyPath(ssmpDll);

const BindingFlags Inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
const BindingFlags Stat = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

var failures = 0;

Type T(string name) {
    var t = ssmp.GetType(name);
    Report(t is not null, $"type   {name}");
    return t;
}

void Member(Type t, string kind, string name, bool ok) =>
    Report(ok, $"{kind,-6} {t?.Name}.{name}");

void Report(bool ok, string what) {
    Console.WriteLine($"  {(ok ? "ok  " : "MISS")}  {what}");
    if (!ok) failures++;
}

Console.WriteLine($"SSMP: {ssmpDll}");
Console.WriteLine($"Version: {ssmp.GetName().Version}\n");

var clientManager = T("SSMP.Game.Client.ClientManager");
var entityManager = T("SSMP.Game.Client.Entity.EntityManager");
var entity = T("SSMP.Game.Client.Entity.Entity");
var modSettings = T("SSMP.Game.Settings.ModSettings");
var registry = T("SSMP.Game.Client.Entity.EntityRegistry");
var registryEntry = T("SSMP.Game.Client.Entity.EntityRegistryEntry");
var entityTypeEnum = T("SSMP.Game.Client.Entity.EntityType");

Console.WriteLine();

Member(clientManager, "field", "_entityManager", clientManager?.GetField("_entityManager", Inst) is not null);
Member(clientManager, "field", "_modSettings", clientManager?.GetField("_modSettings", Inst) is not null);
Member(clientManager, "field", "_fullSynchronisation", clientManager?.GetField("_fullSynchronisation", Inst) is not null);

Member(entityManager, "field", "_entities", entityManager?.GetField("_entities", Inst) is not null);
Member(entityManager, "prop", "IsSceneHost", entityManager?.GetProperty("IsSceneHost", Inst) is not null);
Member(entityManager, "prop", "IsSceneHostDetermined", entityManager?.GetProperty("IsSceneHostDetermined", Inst) is not null);
foreach (var m in new[] { "Initialize", "RegisterHooks", "DeregisterHooks" }) {
    Member(entityManager, "method", m, entityManager?.GetMethod(m, Inst, null, Type.EmptyTypes, null) is not null);
}

Member(entity, "prop", "Id", entity?.GetProperty("Id", Inst) is not null);
var objectProp = entity?.GetProperty("Object", Inst);
Member(entity, "prop", "Object", objectProp is not null);

var pair = objectProp?.PropertyType;
Member(pair, "prop", "Host", pair?.GetProperty("Host", Inst) is not null);
Member(pair, "prop", "Client", pair?.GetProperty("Client", Inst) is not null);

Member(modSettings, "prop", "FullSynchronisation", modSettings?.GetProperty("FullSynchronisation", Inst) is not null);

Member(registry, "prop", "Entries", registry?.GetProperty("Entries", Stat) is not null);
foreach (var p in new[] { "BaseObjectName", "Type", "FsmName" }) {
    Member(registryEntry, "prop", p, registryEntry?.GetProperty(p, Inst) is not null);
}

// The synthetic EntityType values must not collide with real ones, and must fit the ushort that
// EntitySpawn serialises them as.
if (entityTypeEnum is not null) {
    var max = entityTypeEnum.GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(f => Convert.ToInt64(f.GetRawConstantValue())).Max();
    Console.WriteLine();
    Report(max < 40000, $"EntityType max value {max} < synthetic range start 40000");
    Report(40000 + 25000 <= ushort.MaxValue, "synthetic range fits in ushort");
}

Console.WriteLine();
Console.WriteLine(failures == 0
    ? "All reflection targets resolved."
    : $"{failures} reflection target(s) MISSING - the addon would disable itself at runtime.");

return failures == 0 ? 0 : 1;

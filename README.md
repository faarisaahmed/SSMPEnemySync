# SSMPEnemySync

An addon for [SSMP](https://github.com/Extremelyd1/SSMP) (Hollow Knight: Silksong multiplayer) that
makes enemies **shared between both players**.

One enemy. One pool of health. Either player can hit it, both hits come off the same health bar, and
it dies once for both of you. Bosses included.

Without this, each player fights their own private copy of every enemy, and only one of you can
actually kill anything.

> [!IMPORTANT]
> **Both players need this addon installed.** SSMP pairs networked addons by name and version, so a
> mismatched pair will not connect.

---

## Requirements

| | |
|---|---|
| Game | Hollow Knight: Silksong |
| Mod loader | [BepInExPack for Silksong](https://thunderstore.io/c/hollow-knight-silksong/p/BepInEx/BepInExPack_Silksong/) |
| Required mod | [SSMP](https://github.com/Extremelyd1/SSMP) **0.3.1.0** |

The addon reads SSMP's internals directly, so it is tied to that version. On a different SSMP build
it disables itself at startup and logs why, rather than breaking your game.

## Install

1. Install BepInEx and SSMP first, and confirm multiplayer works on its own.
2. Download `SSMPEnemySync.zip` from the [latest release](../../releases/latest).
3. Drop `SSMPEnemySync.dll` into the folder that already contains `SSMP.dll`:

```
Hollow Knight Silksong/
└── BepInEx/plugins/
    └── SSMP/                 ← whatever folder your SSMP.dll lives in
        ├── SSMP.dll
        └── SSMPEnemySync.dll ← here
```

**That exact folder matters.** SSMP's addon loader only scans the directory its own assembly sits in.
Anywhere else and the addon is silently never loaded.

4. Start the game. The BepInEx log should show `[EnemySync] Initialised.`

### Uninstalling

Delete `SSMPEnemySync.dll`. SSMP returns to exactly its previous behaviour — this addon modifies no
SSMP file and changes nothing on disk.

SSMP's `/addon disable` will not work on it. That command only handles addons deriving from
`TogglableClientAddon`, and this one deliberately does not: switching the entity system back off
mid-session would leave enemies half-replicated.

---

## Status

> [!WARNING]
> **This compiles and every reflection target is verified against SSMP 0.3.1.0, but it has not yet
> been run in a live game.** Testing needs two players, so the first real playtest is yours.

Expect breakage, for a specific and knowable reason: SSMP's `EntityFsmActions.cs` is ~1,900 lines of
per-action serialisers written against **Hollow Knight 1's** FSMs. Silksong actions it does not cover
will not replicate. This is realistically a mod that gets working enemy-by-enemy rather than all at
once.

Start with ordinary enemies, watch the log for `[EnemySync]` lines, and expect bosses — multi-FSM,
phase transitions, arena gates — to need the most work.

### Known limitation: enemies only chase the scene host

Enemies target the scene host's hero, because the other player is a ghost object rather than a real
`HeroController`. The non-host player can hit enemies and now genuinely damages them, but enemies
will not turn to face or chase them.

Fixing that is a separate mod: it means rewriting the target references *inside* enemy FSMs, which is
a different problem from networking and touches nothing this addon does.

---

## Why this is needed

SSMP already contains a complete host/client entity system that replicates enemy position, animation,
FSM state and death — about 3,400 lines under `Game/Client/Entity/`. **It is switched off, and
switching it on alone would not do much.** Three separate things stand between it and working
Silksong enemies:

| Problem | Where | What this addon does |
|---|---|---|
| The entity manager is never started | `ClientManager.cs:263`, `:315`, `:343` — `Initialize()`, `RegisterHooks()`, `DeregisterHooks()` are all commented out | Calls them by reflection on connect |
| Full synchronisation defaults off, and it gates every entity packet handler on both ends | `ModSettings.FullSynchronisation`; the menu toggle is commented out at `ModMenu.cs:266` | Enables it before a server starts |
| The entity registry is **Hollow Knight 1's** — an object only syncs if it matches an entry (`EntityProcessor.cs:105`), and ~108 of its 255 entries are HK1 enemies against ~8 Silksong ones | `Resource/entity-registry.json` | Registers Silksong's enemies at scene load |

Plus one gap that exists even in the Hollow Knight version:

> `HealthManagerComponent.cs:11` — *"TODO: periodically (or on hit) sync the health of the entity"*

Health was never synchronised at all; only death and invincibility were. So a scene client's hits
land on an inert puppet and are thrown away. This addon implements that TODO.

## How it works

SSMP elects one client as **scene host** per scene. That client's enemies run real AI
(`Object.Host`); everyone else sees a puppet driven by network updates (`Object.Client`).

```
        scene client                    server                    scene host
             │                            │                            │
   hit lands on puppet                    │                            │
   health drops locally                   │                            │
             │──── DamageReport ─────────▶│──────────────────────────▶ │
   health restored locally                │                   applied to the real
   (puppet cannot die early)              │                   enemy; Die() if it hits 0
             │                            │                            │
             │◀─────────────────────────  │◀───── HealthUpdate ────────│
   authoritative health applied           │                            │
```

Damage is detected by **watching health**, not by patching the damage path. Damage in this game
arrives from nail hits, spells, tools, poison and recoil through several unrelated code paths;
watching the resulting value catches all of them without needing a patch per source.

The server holds no health state of its own — it only routes. SSMP's entity model already makes one
client the authority for a scene, and a second authority on the server would mean two sources of
truth to reconcile.

Position, animation, FSM state and death are **not** implemented here. Those are SSMP's existing
entity system, which this addon switches on.

### Enemy registration

Two sources, in `EntityRegistryPopulator`:

1. **An embedded table** (`res/silksong-enemies.json`) carrying the correct primary FSM name per
   enemy, generated from `EnemyBehaviorApi.Survey` dumps.
2. **A scan of every loaded scene** registering anything with a `HealthManager` and a `PlayMakerFSM`,
   so enemies and bosses that were never surveyed still sync.

Each enemy gets a synthetic `EntityType` hashed from its name (FNV-1a, range 40000–65000). Hashed
rather than assigned in discovery order so both clients independently agree on the same value, and
far above the ~236 real enum values so it can never collide with a type SSMP special-cases.

---

## Building from source

```bash
dotnet build -c Release
```

Builds against the game's own assemblies and deploys the DLL next to `SSMP.dll`.

```bash
# Different install location
dotnet build -c Release -p:GameDir="/path/to/Hollow Knight Silksong"

# Build without deploying
dotnet build -c Release -p:NoDeploy=true
```

### Verifying the reflection targets

The addon reaches SSMP's internals by reflection, so a rename becomes a silent runtime failure rather
than a compile error. `tools/verify-reflection` checks all 25 members against the installed
`SSMP.dll` metadata without launching the game:

```bash
cd tools/verify-reflection
GAME_DIR="/path/to/Hollow Knight Silksong" dotnet run
```

Run it after any SSMP update. Anything reporting `MISS` is a member the addon can no longer reach.

## License

[LGPL-2.1-or-later](LICENSE), matching SSMP, which this addon builds on.

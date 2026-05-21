# ZombieMod for Counter-Strike 2

A classic outbreak / infection survival game mode for **Counter-Strike 2** dedicated servers.
Built on top of [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp), JSON-configured, Linux dedicated server target, .NET 8.

This repository ships **two projects** under one roof:

| Project | Description | Path |
| --- | --- | --- |
| **ZombieMod plugin** | The CS2 gameplay mod — infection, classes, knockback, props, sounds, admin | `ZombieMod/` + `ZombieMod.Api/` |
| **WebCon** | Self-hosted web RCON dashboard + multi-server control panel | `webcon/` |

Both are independent. You can run the plugin without WebCon, and vice versa.

---

## Contents

- [Features](#features)
- [Roadmap](#roadmap)
- [Required mods](#required-mods)
- [Optional mods](#optional-mods)
- [Installation](#installation)
- [Configuration](#configuration)
- [Commands](#commands)
- [Workshop addons in use](#workshop-addons-in-use)
- [Public plugin API](#public-plugin-api)
- [Building from source](#building-from-source)
- [License](#license)

---

## Features

### Gameplay
- Round start → freezetime → Patient Zero(s) identified after `FirstInfectionTimer` seconds
- Patient Zero count scales with player count via `PatientZeroRatio`
- Patient Zeros are stripped to knife-only and get a buffed class (custom model, more HP)
- Survivors (CT) vs Infected (T); knife-infect flips a survivor's team for the rest of the round
- Round-end detection: all survivors dead → outbreak wins; all infected dead → survivors win
- `mp_roundtime` expiry → `TimeoutWinner` policy (infected or survivors)
- Per-round cash floor — players below get topped up, players above keep earnings
- Cash reward per successful knife-transmission
- Map rotation after a configurable round count — supports vanilla map names AND Steam Workshop IDs

### Classes
- Per-class Health, Model, Speed, RenderRGB tint, regen, knockback resistance
- Custom infected models via mounted Workshop addon (zombie_basic, chris_walker, cultist, frozen)
- Class applies on infect AND on respawn

### Knockback
- Direction × damage × class.Kb × weapon.Kb × hitgroup.Kb
- HE-grenade explosion knockback path included
- Knife knockback ×3, Zeus ×2 by default

### Weapons
- 40 weapons in `weapons.json` — entity name, buy price, purchase command alias, knockback multiplier, restriction, MaxPurchase per life
- Dynamic purchase command registration (`!ak`, `!awp`, `!deagle`, `!p90`, …)
- `mp_buy_anywhere 1` for the first 50 seconds of each round
- Infected are stripped to knife-only on infect; auto-strip on ground pickup
- Infinite reserve ammo, normal reload mechanic preserved

### Props (`!prop` menu)
- WasdMenu prop spawner, costs in-game cash
- 5 default props (cabinets, crates, fridge, dumpster) using `COLLISION_GROUP_PROPS` so they actually block players
- VPHYSICS props with `EnableMotion + Wake` — fall correctly, respond to bullets
- Per-slot tracking, cleaned up on disconnect / round-end / map change

### Sounds
- Configurable via `configs/sounds.json` — map event keys to lists of `.vsnd` paths
- Events: `round_ambient`, `patient_zero`, `infected_death`, `infected_idle`, `survivor_death`
- `stopsound` issued on round end so background tracks cut cleanly
- Round-active gate prevents idle audio from bleeding into freezetime / post-round
- Custom sound packs ship via Workshop addons; see [Workshop addons in use](#workshop-addons-in-use)

### Teleport (`!ztele`)
- Captures spawn origin per-round, teleports back on command
- Configurable uses per round + cooldown

### Admin
- `admins.json` populated with the user's SteamID64, `@css/root`
- `!admin` opens a WasdMenu (restart round, force-end round, skip map, reload configs, end warmup)
- `!infect`, `!human` (restores a survivor), `!zreload` gated on `@css/admin`
- Standard CSSharp target syntax: `@me`, `@all`, `@t`, `@ct`, `#userid`, partial name

### Reliability
- `game_type 0 game_mode 0` pinned + re-applied on every map start so changelevel can't drop the server into Deathmatch
- 5-tick warmup killer with `mp_restartgame 1` fallback if `mp_warmup_end` doesn't take
- Workshop addon mounting via MultiAddonManager
- Hot-reloadable JSON configs via `!zreload`

### WebCon
- FastAPI + WebSocket app in its own Docker container
- Token-gated dashboard at `http://<host>:8088/`
- Multi-server: lists every configured server with status pill, click-through to per-server console
- **Manual server CRUD from the UI** — add/edit/delete servers without editing JSON; passwords write to `.env` atomically
- Per-server console: live `docker logs` tail merged with RCON command/response in one feed, up/down command history, quick-action buttons, workshop-map ID input, ANSI codes stripped
- Broadcast mode: fan a command to every server in parallel, responses tagged per server
- Cyberpunk-HUD theme (black background + neon orange accents)

---

## Roadmap

### Shipped
See [Features](#features).

### In progress
| Item | Status |
| --- | --- |
| `.vsndevts` soundevents with per-event volume baked in | User-authored .vsndevts in the custom Workshop addon; plugin switch from `play <path>` to `EmitSound("ZombieMod.<Event>")` queued |
| Positional audio for bite + death | Depends on above |

### Planned
| Item | Notes |
| --- | --- |
| `!zclass` picker UI | WasdMenu listing available classes for the calling player. Currently a stub |
| `RandomClassesOnConnect` / `RandomClassesOnSpawn` | Config fields exist; class picker not wired |
| Napalm fire VFX | Class config tracks state, particle attachment not implemented |
| Localizer / language file support | Strings currently English-inline |
| Live knockback-provider runtime probe | Filesystem heuristic doesn't catch loaded-but-disabled providers |
| `v0.1.0` git tag | After soundevents migration + smoke test |
| License | Currently TBD — must pick before any public release |

### Stretch
| Item | Notes |
| --- | --- |
| Custom per-class HUD (HP bar, ability cooldown) | Possible via CenterHtmlMenu; deferred |
| ZE-style escape-map win condition | Map rotation already supports it; would need exit-zone trigger entities |
| Infected-vision POV shader | Investigated, shelved — Source 2 doesn't expose enough hooks from CSSharp; would require a side Metamod plugin |

---

## Required mods

These must be installed for ZombieMod to function:

| Mod | Type | Source | Why |
| --- | --- | --- | --- |
| **Metamod:Source** | Engine plugin | https://www.sourcemm.net/ | Loader for everything below |
| **CounterStrikeSharp** ≥ v1.0.355 | Metamod plugin | https://github.com/roflmuffin/CounterStrikeSharp | Plugin runtime ZombieMod targets |
| **MovementUnlocker** | Metamod plugin | https://github.com/Source2ZE/MovementUnlocker | Enables velocity writes — needed for knockback to actually push players |
| **MultiAddonManager** | Metamod plugin | https://github.com/Source2ZE/MultiAddonManager | Mounts Steam Workshop content (models, sounds, maps) at runtime |

CS2 auto-updates occasionally overwrite `gameinfo.gi`, removing the Metamod search-path line. If `meta` becomes an "Unknown command" after a patch, re-inject the line and restart.

## Optional mods

You can layer any of these on top:

| Mod / addon | Purpose |
| --- | --- |
| **WebCon** (in this repo) | Browser-based RCON console + multi-server dashboard. See [Installation](#installation) |
| Custom Workshop sound pack | Drop your own `.vsndevts` + `.vsnd` files into a private Workshop addon, add the ID to `mm_extra_addons`, reference paths/events in `sounds.json` |
| Custom Workshop maps | Add IDs to `gamesettings.json → MapRotation`; the plugin invokes `host_workshop_map <id>` |
| Custom player models | Drop model paths into `classes.json → Model` for any class |

---

## Installation

### Repository layout (post-install)

```
counterstrike/
├── ZombieMod/                ← plugin source
├── ZombieMod.Api/            ← public API source
├── configs/                  ← canonical configs (deployed via cp)
│   ├── classes.json
│   ├── gamesettings.json
│   ├── hitgroups.json
│   ├── props.json
│   ├── sounds.json
│   └── weapons.json
├── gameserver/
│   ├── compose.yaml          ← joedwards32/cs2 image
│   ├── .env                  ← SRCDS_TOKEN + STEAM_API_KEY (gitignored)
│   └── data/                 ← CS2 install root (bind-mounted)
└── webcon/
    ├── compose.yaml
    ├── .env                  ← WEBCON_TOKEN + RCON_PASSWORD (gitignored)
    └── servers.json          ← runtime-editable via the UI
```

### Plugin deploy

1. Build (see [Building from source](#building-from-source))
2. Copy artifacts into the CS2 install:
   ```bash
   \cp -f ZombieMod/bin/Release/net8.0/ZombieMod.dll        gameserver/data/game/csgo/addons/counterstrikesharp/plugins/ZombieMod/
   \cp -f ZombieMod.Api/bin/Release/net8.0/ZombieMod.Api.dll gameserver/data/game/csgo/addons/counterstrikesharp/plugins/ZombieMod/
   \cp -f configs/*.json                                     gameserver/data/game/csgo/addons/counterstrikesharp/configs/ZombieMod/
   ```
3. Restart the server (or `css_plugins reload ZombieMod` for DLL-only changes).

The backslash before `cp` bypasses the interactive `cp -i` alias common on Synology DSM — without it, overwrites silently no-op.

### WebCon deploy

```bash
cd webcon
cp .env.example .env       # set WEBCON_TOKEN + RCON_PASSWORD
docker compose up -d --build
```

Open `http://<host>:8088/`, enter the token. Add your servers via the **+ Add Server** button on the dashboard.

---

## Configuration

All six JSON files live in `configs/` and deploy to `addons/counterstrikesharp/configs/ZombieMod/`. They accept C++-style comments and trailing commas (`System.Text.Json` is configured accordingly).

| File | Purpose |
| --- | --- |
| `gamesettings.json` | Round timings, Patient Zero ratio, respawn policy, start money, kill rewards, map rotation, master toggles |
| `classes.json` | Per-class Health, Model, Speed, RenderRGB, regen, napalm, knockback resist |
| `weapons.json` | Per-weapon entity name, buy price, purchase command alias, knockback multiplier, restriction, MaxPurchase |
| `hitgroups.json` | Per-hitbox knockback multipliers |
| `props.json` | Spawnable props for `!prop` — display name, model path, cost |
| `sounds.json` | Event → (Volume, Files[]) — paths reference MAM-mounted Workshop content |

`!zreload` hot-reloads all six. Plugin DLL changes still need `css_plugins reload ZombieMod`.

---

## Commands

### Player
| Command | Effect |
| --- | --- |
| `!zhelp` | Open the help menu |
| `!ztele` | Teleport to your round-spawn (limited uses + cooldown) |
| `!zspawn` | Respawn into the current round (when dead, respawn enabled) |
| `!zclass` | Class picker (stub — planned) |
| `!prop` | Open the prop-spawn menu |
| `!ak`, `!awp`, `!deagle`, `!p90`, … | Per-weapon buy commands |

### Admin (`@css/admin` or `@css/root`)
| Command | Effect |
| --- | --- |
| `!admin` | Open the admin WasdMenu (restart round, force win, skip map, reload configs, end warmup) |
| `!infect <target>` | Force-infect a target |
| `!human <target>` | Restore a target to survivor |
| `!zreload` | Reload all configs |

Target syntax: `@me` / `@all` / `@t` / `@ct` / `#userid` / partial name.

---

## Workshop addons in use

Currently mounted via `mm_extra_addons`:

| ID | Name | Purpose |
| --- | --- | --- |
| `3160448201` | GFL Zombie Escape Content | Custom infected models + 26 infected sounds (idle voices, deaths, pain) + soundevent overrides |
| `3183164171`, `3215759704` | Player-model packs | Additional character models |
| `3730087911` | Zombiemod Sounds (custom) | Custom round-ambient + mother screams + bite effect |

Workshop map rotation is configured via `gamesettings.json → MapRotation` — numeric entries load via `host_workshop_map`, everything else via `changelevel`.

---

## Public plugin API

Downstream plugins consume `IZombieModAPI` via CSSharp's `PluginCapability`:

```csharp
var caps = new PluginCapability<IZombieModAPI>("zombiemod:core");
var api = caps.Get();

api.OnClientInfect += (client, attacker, patientZero, force) =>
{
    return HookResult.Continue; // or HookResult.Stop to cancel
};

api.InfectClient(controller, attacker: null, patientZero: true, force: true);
```

Reference `ZombieMod.Api.dll` from your plugin — **never** reference `ZombieMod.dll` directly.

Events:
- `OnClientInfect(client, attacker, patientZero, force) → HookResult`
- `OnClientHumanize(client, respawn) → HookResult`
- `OnPatientZeroSelected(patientZeroList)`
- `OnOutbreakRoundStart()`

---

## Building from source

Requirements: `dotnet` SDK 8.0.

```bash
cd ZombieMod
dotnet build -c Release
```

Output: `ZombieMod/bin/Release/net8.0/ZombieMod.dll`.

If you don't have `dotnet` on the host (e.g. Synology NAS), build inside the official image:

```bash
docker run --rm --user "$(id -u):$(id -g)" \
  -v "$PWD":/src -w /src \
  mcr.microsoft.com/dotnet/sdk:8.0 \
  dotnet build -c Release ZombieMod.sln
```

---

## License

TBD — to be decided before any public release.

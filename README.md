# ZombieMod for Counter-Strike 2

A classic infection / survival zombie game mode for **Counter-Strike 2** dedicated servers,
implemented as a [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp) plugin
with JSON-only configuration. Target: Linux dedicated server, .NET 8.

This repository is **two products** under one roof:

| Project | Purpose | Path |
| --- | --- | --- |
| **ZombieMod plugin** | The CS2 gameplay mod — infection, classes, knockback, props, sounds | `ZombieMod/` + `ZombieMod.Api/` |
| **WebCon** | A self-hosted web RCON console + multi-server dashboard | `webcon/` |

The plugin runs inside the CS2 dedicated server container; WebCon is a separate small FastAPI
container next to it. They are independent — you can use the mod without WebCon and vice versa.

---

## Quick links

- [What's working today](#whats-working-today)
- [Roadmap](#roadmap)
- [Setup](#setup)
- [Configuration](#configuration)
- [Commands](#commands)
- [Workshop addons](#workshop-addons)
- [Public plugin API](#public-plugin-api)
- [Building](#building)

---

## What's working today

### Gameplay loop
- Round start → freezetime → mother zombie(s) selected after `FirstInfectionTimer` seconds
- Mother count = `ceil(alive_players / MotherZombieRatio)`
- Mother zombie auto-strips weapons (knife only), gets the `motherzombie` class (custom model + buffed health)
- Humans (CT) vs zombies (T). Knife-infect knocks a human onto the zombie team; their pawn keeps its model swap for the rest of the round
- Round-end detection: all humans dead → zombies win; all zombies dead → humans win
- `mp_roundtime` expiry → `TimeoutWinner` policy (0 = zombies, 1 = humans)
- Per-round cash floor (`StartMoney`) — players below get topped up, players above keep their earnings
- Cash reward per successful infect (`InfectKillReward`)
- Map rotation after `MaxRoundsPerMap` — supports vanilla map names and Steam Workshop IDs

### Classes
- Three default classes in `classes.json`: `human_default`, `zombie_default`, `motherzombie`
- Per-class Health, Model, Speed multiplier, RenderR/G/B tint, health regen, knockback resist
- Custom zombie models loaded via the GFL ZE Content workshop addon (`zombie_basic`, `chris_walker`, `cultist`, `frozen`)
- Classes apply on infect AND on respawn

### Knockback
- Powered by **MovementUnlocker** (Metamod plugin, installed alongside the server)
- Formula: `direction × dmg × class.Kb × weapon.Kb × hitgroup.Kb`
- HE-grenade explosion knockback path included
- Knife knockback ×3, Zeus ×2 by default

### Weapons
- 40 weapon entries in `weapons.json`, each with entity name, buy price, purchase command alias, knockback multiplier, restriction toggle, MaxPurchase per life
- Purchase commands dynamically registered: `!ak`, `!awp`, `!deagle`, `!p90`, …
- `mp_buy_anywhere 1` enforced + re-applied at +5s / +15s after map start (casual gamemode cfg clobbers it otherwise)
- `mp_buytime 50` for the first 50 seconds of each round
- Zombies are stripped to knife-only on infect; if they pick up a dropped weapon it's auto-stripped
- `sv_infinite_ammo 2` (infinite reserve, normal reload mechanic preserved)

### Props (`!prop` menu)
- WasdMenu-based prop spawner for in-game cash
- 5 default props from `props.json`: File Cabinet ($400), Wooden Crate S ($400), Fridge ($700), Wooden Crate L ($800), Dumpster ($1500)
- Props use `COLLISION_GROUP_PROPS` so they actually block players (not pushaway)
- Physics props with `EnableMotion` + `Wake` so they fall + respond to bullets
- Per-slot tracking → cleaned up on disconnect / round-end / map change

### Teleport (`!ztele`)
- Captures each player's spawn origin per-round, teleports them back on command
- Uses per round + cooldown configurable in `gamesettings.json`

### Sounds
- Configurable via `configs/sounds.json` — map event keys to lists of `.vsnd` file paths
- Current events: `round_ambient` (fires on `OnRoundFreezeEnd`), `mother_zombie` (round-start scream), `zombie_death`, `zombie_idle` (15–25s cadence), `human_death` (zombie kill on human)
- Custom sound pack via Workshop addon (`3730087911`) for ambient / screams / bite
- GFL ZE Content (`3160448201`) provides zombie idle voices + death sounds
- `stopsound` issued on `OnRoundEnd` so background tracks cut cleanly at the round boundary
- HE-explosion sound on infection removed (visual particle FX still plays) — the in-house scream covers the "you got infected" cue now
- Round-active gate prevents idle music from firing during freezetime / post-round

### Admin
- `admins.json` populated; `@css/root` flag on the user's SteamID64
- `!admin` opens a WasdMenu panel with: Restart Round / End Round Humans Win / End Round Zombies Win / Reload Configs / Skip to Next Map / End Warmup Now
- `!infect`, `!human`, `!zreload` require `@css/admin`
- Standard CSSharp target resolution: `@me`, `@all`, `@t`, `@ct`, `#userid`, partial name match

### Reliability / quality-of-life
- `game_type 0 game_mode 0` pinned + re-applied on every map start (changelevel was occasionally dropping us into Deathmatch)
- Warmup killer: 5-tick fallback to `mp_restartgame 1` if `mp_warmup_end` doesn't take
- Workshop addon download via MultiAddonManager (`mm_extra_addons` cvar)
- Hot-reloadable JSON configs via `!zreload` — no plugin restart needed for config tweaks
- `\cp -f` discipline in our deploy scripts (the `cp -i` alias on Synology was silently swallowing updates)

### WebCon
- FastAPI + WebSocket app, lives in `webcon/`, runs in its own Docker container
- Token-gated dashboard at `http://<host>:8088/`
- Multi-server: lists every server in `servers.json`, status pill per server, click-through to per-server console
- **Manual server CRUD from the UI** — add / edit / delete servers without editing JSON files (passwords land in `.env` under per-server keys; `servers.json` written atomically)
- Per-server console: live `docker logs` tail merged with RCON command/response in one feed, up/down arrow command history, quick-action buttons, workshop map ID input + button, ANSI color codes stripped
- Broadcast mode: send a single command to every configured server in parallel, each line server-prefixed
- Cyberpunk-HUD visual theme (black + neon orange + scanline overlay)
- Tails container logs via mounted Docker socket (read-only)

---

## Roadmap

### Working today
*(see ["What's working today"](#whats-working-today) for the full list)*

### In progress
| Item | Status |
| --- | --- |
| `.vsndevts` soundevents with baked-in per-sound volume | User is authoring the file in their workshop addon; plugin swap from `play <path>` to `EmitSound("ZombieMod.<Event>")` queued |
| Positional audio for bite + death sounds | Depends on above |
| WebCon "skill"-driven UI iteration | Cyberpunk-HUD theme just landed — iterating on the look-and-feel |

### Planned / not yet started
| Item | Notes |
| --- | --- |
| `!zclass` picker UI | ChatMenu / WasdMenu listing classes for the calling player; currently a stub message. Class is set by gamesettings only |
| `RandomClassesOnConnect` / `RandomClassesOnSpawn` | Config fields exist; class picker not wired |
| Napalm fire VFX | Class config tracks state; particle attachment not implemented |
| Localizer / language file support | All strings currently English-inline |
| Auto-strip restricted-weapon ground pickups | Currently only strips on purchase + on infect |
| Live knockback-provider runtime probe | Filesystem heuristic works but doesn't catch loaded-but-disabled providers |
| Smoke-test suite | Live verification on the dedicated server lives in the user's hands today |
| `v0.1.0` git tag | Awaits the soundevents migration + smoke test |
| License decision | Currently TBD — must pick before any public release |

### Stretch ideas
| Item | Notes |
| --- | --- |
| Zombie-vision shader / POV effect | Investigated and shelved — Source 2 doesn't expose enough hooks from CSSharp to recolor the player's view; would need a Metamod side plugin |
| Custom per-class HUD (HP bar, ability cooldown) | Could use CenterHtmlMenu; deferred |
| ZE-style escape map rotation logic | Map list already supports it; would need win conditions tied to a player reaching an exit zone |

---

## Setup

### Server-side bind mount layout

```
counterstrike/
├── ZombieMod/                ← plugin source (this repo)
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
│   └── data/                 ← CS2 install root (bind-mounted at /home/steam/cs2-dedicated)
│       └── game/csgo/addons/counterstrikesharp/{plugins,configs}/ZombieMod/
└── webcon/
    ├── compose.yaml
    ├── .env                  ← WEBCON_TOKEN + RCON_PASSWORD (gitignored)
    └── servers.json          ← runtime-editable from the UI
```

### Install the plugin

1. Build (see [Building](#building))
2. Deploy:
   ```bash
   \cp -f ZombieMod/bin/Release/net8.0/ZombieMod.dll    gameserver/data/game/csgo/addons/counterstrikesharp/plugins/ZombieMod/
   \cp -f ZombieMod.Api/bin/Release/net8.0/ZombieMod.Api.dll gameserver/data/game/csgo/addons/counterstrikesharp/plugins/ZombieMod/
   \cp -f configs/*.json gameserver/data/game/csgo/addons/counterstrikesharp/configs/ZombieMod/
   ```
3. Restart the server (or `css_plugins reload ZombieMod` if no schema changes)

(Use `\cp -f` to bypass the `cp -i` interactive prompt — without the backslash on Synology, copies silently fail to overwrite.)

### Install WebCon

```bash
cd webcon
cp .env.example .env       # set WEBCON_TOKEN + RCON_PASSWORD
docker compose up -d --build
```

Open `http://<host>:8088/`, enter the token. Add your servers via the dashboard's **+ Add Server** button.

---

## Configuration

All five JSON files live in `configs/` and are deployed to the server's
`addons/counterstrikesharp/configs/ZombieMod/`. All accept comments (`//`) and trailing commas
(System.Text.Json is configured with `ReadCommentHandling = Skip` and `AllowTrailingCommas = true`).

| File | Purpose |
| --- | --- |
| `gamesettings.json` | Round timings, mother-zombie ratio, respawn policy, start money, kill rewards, map rotation, master toggles |
| `classes.json` | Per-class Health, Model, Speed, RenderRGB, regen, napalm, knockback resist |
| `weapons.json` | Per-weapon entity name, buy price, purchase command alias, knockback multiplier, restriction, MaxPurchase |
| `hitgroups.json` | Per-hitbox knockback multipliers |
| `props.json` | Spawnable props for `!prop` menu — display name, model path, cost |
| `sounds.json` | Event → (Volume, Files[]) — paths from MAM-mounted addons |

`!zreload` hot-reloads all six files. Plugin DLL changes still need `css_plugins reload ZombieMod`.

---

## Commands

### Player
| Command | Effect |
| --- | --- |
| `!zhelp` | Open the help menu |
| `!ztele` | Teleport to your round-spawn (limited uses + cooldown) |
| `!zspawn` | Respawn into the current round (when dead and respawn is enabled) |
| `!zclass` | Class picker — currently a stub |
| `!prop` | Open the prop-spawn menu (costs in-game cash) |
| `!ak`, `!awp`, `!deagle`, `!p90`, … | Per-weapon buy commands |

### Admin (`@css/admin` or `@css/root`)
| Command | Effect |
| --- | --- |
| `!admin` | Open the admin WasdMenu (restart round, force win, skip map, reload configs, end warmup) |
| `!infect <target>` | Force-infect a target |
| `!human <target>` | Force-humanize a target |
| `!zreload` | Reload all configs |

Target syntax: `@me` / `@all` / `@t` / `@ct` / `#userid` / partial name.

---

## Workshop addons

Currently mounted via `mm_extra_addons` (MultiAddonManager):

| ID | Name | Purpose |
| --- | --- | --- |
| `3160448201` | GFL Zombie Escape Content | Custom zombie models (`zombie_basic`, `chris_walker`, `cultist`, `frozen`) + 26 GFL zombie sounds (idle voices, deaths, pain) + soundevent overrides |
| `3183164171`, `3215759704` | Player model packs | Additional character models |
| `3730087911` | Zombiemod Sounds (custom) | Custom round-ambient + mother screams + bite effect |

Workshop map rotation is configured in `gamesettings.json → MapRotation` — numeric entries are
loaded via `host_workshop_map`, anything else via `changelevel`.

---

## Public plugin API

Downstream plugins consume `IZombieModAPI` via CSSharp's `PluginCapability`:

```csharp
var caps = new PluginCapability<IZombieModAPI>("zombiemod:core");
var api = caps.Get();

api.OnClientInfect += (client, attacker, mother, force) =>
{
    return HookResult.Continue; // or HookResult.Stop to cancel
};

api.InfectClient(controller, attacker: null, motherZombie: true, force: true);
```

Reference `ZombieMod.Api.dll` from your plugin — **never** reference `ZombieMod.dll` directly.

Events:
- `OnClientInfect(client, attacker, motherZombie, force) → HookResult`
- `OnClientHumanize(client, respawn) → HookResult`
- `OnMotherZombieSelected(motherList)`
- `OnZombieRoundStart()`

---

## Building

`dotnet` SDK 8.0 required. From the repo root:

```bash
cd ZombieMod
dotnet build -c Release
```

Output: `ZombieMod/bin/Release/net8.0/ZombieMod.dll`.

If you don't have `dotnet` on the host (e.g. Synology NAS), build through the official image:

```bash
docker run --rm --user "$(id -u):$(id -g)" \
  -v "$PWD":/src -w /src \
  mcr.microsoft.com/dotnet/sdk:8.0 \
  dotnet build -c Release ZombieMod.sln
```

---

## Compatibility caveats

### Knockback requires MovementUnlocker
Vanilla CounterStrikeSharp cannot push players around reliably — direct writes to `pawn.AbsVelocity`
either no-op or get clobbered by the player movement code each tick. ZombieMod relies on the
**MovementUnlocker** Metamod plugin (https://github.com/Source2ZE/MovementUnlocker) for the
velocity-write path. Install it alongside Metamod:Source before knockback will work.

### CS2 patch updates can break Metamod
Steam auto-updates may overwrite `gameinfo.gi`, removing the metamod search-path line. Symptom: `meta` is "Unknown command", `css_plugins list` doesn't work. Fix: re-inject the line.

---

## License

TBD — pick before any public release.

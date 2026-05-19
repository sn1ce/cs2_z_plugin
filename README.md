# ZombieMod

Classic infection / survival zombie gameplay for **Counter-Strike 2**, implemented as a
[CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp) plugin. Linux dedicated
server target. JSON-only configuration.

Status: **scaffold only**. Subsystems land phase-by-phase — see [`TODO.md`](TODO.md).

---

## ⚠️ Knockback requires a third-party patch

Vanilla CounterStrikeSharp **cannot push players around reliably** — direct writes to
`pawn.AbsVelocity` either no-op or get clobbered each tick by the player movement code.
Pick **one** of the following before knockback will work:

| Provider | Type | Source |
| --- | --- | --- |
| **CSSharpFixes** | Metamod plugin | https://github.com/Source2ZE/CSSharpFixes |
| **MovementUnlocker** | Metamod plugin | https://github.com/Source2ZE/MovementUnlocker |
| **CS2-SigPatcher** | CSSharp plugin | https://github.com/oylsister/CS2-SigPatcher |

ZombieMod auto-detects whichever is loaded at startup. If none is present it logs a Warning and
**all knockback is disabled** (the plugin still runs — humans just can't push zombies).

## ⚠️ Incompatible with CS2Fixes + Zombie:Reborn

CS2Fixes ships its own Zombie:Reborn module. Running both at once will fight over team/infect
state. Pick one — if you keep CS2Fixes, disable its `zr_*` module.

---

## Dependencies

- CS2 Linux dedicated server
- [Metamod:Source](https://www.sourcemm.net/) (latest stable)
- [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp) **≥ v1.0.355**
  (nested-`NextFrame` fix and `Vector`/`QAngle` leak fix shipped between v1.0.354 and v1.0.362;
  scaffold is built against v1.0.368)
- One knockback provider (above) if you want functional knockback
- `dotnet` SDK 8.0 to build

---

## Build

```bash
cd ZombieMod
dotnet build -c Release
```

Output: `ZombieMod/bin/Release/net8.0/ZombieMod.dll`.

If you don't have `dotnet` on the host (e.g. Synology NAS), build through Docker:

```bash
docker run --rm -u "$(id -u):$(id -g)" \
  -v "$PWD":/src -w /src \
  mcr.microsoft.com/dotnet/sdk:8.0 \
  dotnet build -c Release ZombieMod.sln
```

---

## Install

1. Copy the plugin folder into your server:
   ```
   game/csgo/addons/counterstrikesharp/plugins/ZombieMod/
   ├── ZombieMod.dll
   └── ZombieMod.Api.dll
   ```
2. Copy the configs:
   ```
   game/csgo/addons/counterstrikesharp/configs/ZombieMod/
   ├── gamesettings.json
   ├── weapons.json
   ├── classes.json
   └── hitgroups.json
   ```
3. Copy the cvar file so the server applies our required cvars each map:
   ```
   game/csgo/cfg/zombiemod/zombiemod.cfg
   ```
   And add `exec zombiemod/zombiemod.cfg` to your `gamemode_*.cfg` (or `server.cfg`).
4. Start the server. First-load logs go to `addons/counterstrikesharp/logs/`.

---

## Required cvars

Set in `cfg/zombiemod/zombiemod.cfg` (provided):

```
mp_limitteams 0
mp_autoteambalance 0
mp_disconnect_kills_players 1
mp_roundtime 3
mp_roundtime_hostage 0
mp_roundtime_defuse 0
mp_solid_teammates 1
mp_teammates_are_enemies 0  // we manage cross-team infection ourselves
```

`mp_teammates_are_enemies 0` is intentional — the plugin handles damage between T (zombies) and
CT (humans) directly via `EventPlayerHurt`. Setting it to `1` will double-dip.

---

## Configuration overview

All four files are JSON-with-comments + trailing commas friendly (`System.Text.Json` configured
with `ReadCommentHandling = Skip`, `AllowTrailingCommas = true`).

| File | Purpose |
| --- | --- |
| `gamesettings.json` | Round timings, mother-zombie ratio, respawn policy, master toggles |
| `weapons.json` | Per-weapon entity name, knockback multiplier, buy command, restriction |
| `classes.json` | Zombie / human classes — health, model, regen, napalm, knockback resist |
| `hitgroups.json` | Per-hitbox knockback multipliers |

Hot reload via `!zreload` (admin).

---

## Admin commands

All require `@css/admin` flag.

| Command | Effect |
| --- | --- |
| `!infect <player>` | Force-infect a player |
| `!human <player>` | Force-humanize a player |
| `!zspawn` | Respawn (yourself or target) into current round |
| `!zclass` | Open class picker for the calling player |
| `!zreload` | Reload all four JSON configs |

Player commands:

| Command | Effect |
| --- | --- |
| `!ztele` | Teleport back to your map spawn (uses + cooldown per round) |
| `!ak`, `!awp`, …   | Per-weapon purchase commands (driven by `weapons.json`) |

---

## Public API

Downstream plugins consume `IZombieModAPI` via CSSharp's `PluginCapability`:

```csharp
var caps = new PluginCapability<IZombieModAPI>("zombiemod:core");
var api = caps.Get();
api.OnClientInfect += (client, attacker, mother, force) => {
    return HookResult.Continue; // or HookResult.Stop to cancel
};
api.InfectClient(controller, attacker: null, motherZombie: true, force: true);
```

Reference `ZombieMod.Api.dll` from your plugin — do **not** reference `ZombieMod.dll`.

---

## License

TBD — pick one before publishing.

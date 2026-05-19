# ZombieMod TODO

Tracker. One line per task. Tick as you go.

## Phase 0 — Scaffold
- [x] Confirm latest CSSharp release (v1.0.368, 2026-05-17)
- [x] Mine ZombieSharp config schemas from archived repo
- [x] Solution + two csprojs (ZombieMod, ZombieMod.Api)
- [x] Service stubs + record-based config types
- [x] Initial configs (gamesettings, weapons, classes, hitgroups)
- [x] Required cvar file `cfg/zombiemod/zombiemod.cfg`
- [x] `dotnet build -c Release` passes with zero warnings

## Phase 1 — Config
- [x] `ConfigService` load all four JSON files at startup
- [x] `JsonSerializerOptions`: comments skip, trailing commas, case-insensitive
- [x] Schema validation: file+key error logs, cross-references (DefaultHumanBuffer etc.)
- [x] WeaponsByEntity + HitgroupsByIndex auxiliary lookups
- [x] `!zreload` admin command for hot-reload

## Phase 2 — Infection
- [x] `EventRoundStart` → reset state, shuffle all to CT
- [x] `EventRoundFreezeEnd` → schedule first infection (`AddTimer FirstInfectionTimer`)
- [x] Mother zombie selection: `ceil(playerCount / MotherZombieRatio)`, random
- [x] `InfectClient(controller, attacker, motherZombie, force)`: team swap + class + event
- [x] `HumanizeClient(controller, respawn)`: reverse infect
- [x] Knife-only infection in `OnPlayerHurt`
- [x] Round-end check: all-humans-dead → zombies win; all-zombies-dead → humans win
- [ ] Round-timer expires (`TimeoutWinner`) — deferred; needs `mp_roundtime` timer hook

## Phase 3 — Respawn
- [x] `EventPlayerDeath` → schedule respawn after `RespawnDelay`
- [x] `RespawnTeam` policy (0/1/2) via `ResolvePostSpawnAction`
- [x] `AllowRespawnJoinLate` via `EventPlayerTeam`
- [x] Skip when round-end conditions reached (no scheduling guard needed; pawn already gone)

## Phase 4 — Class
- [x] Load `classes.json` into `Dictionary<string, ClassConfig>` (Phase 1)
- [x] Apply Health on infect/spawn (deferred via 0.3s timer so spawn doesn't clobber)
- [x] Apply Model — defaults to phoenix/sas if `default`
- [x] Health regen: per-player `AddTimer(Regen_Interval, REPEAT)`
- [x] ArmorValue = 0, helmet = false for zombies
- [x] Speed: gated behind `EnableClassSpeed` (default false — CSSharp limitation)
- [x] Re-apply velocity modifier on hurt to counter freezetime resets
- [ ] Napalm visual (fire particles) — state tracked but no VFX yet
- [ ] `!zclass` picker UI (ChatMenu) — deferred; Phase 1 sets via config only
- [ ] `RandomClassesOnConnect` / `RandomClassesOnSpawn` — config exists, picker not wired

## Phase 5 — Knockback
- [x] `KnockbackProviderDetector` filesystem heuristic (CSSharpFixes / MovementUnlocker / CS2-SigPatcher)
- [x] Logs Warning and disables knockback if none found
- [x] `EventPlayerHurt` → zombie-victim + human-attacker filter
- [x] direction × dmgHealth × class.Kb × weapon.Kb × hitgroup.Kb formula
- [x] HE grenade explosion knockback path
- [x] One `Vector` allocation per hurt event (not per tick)
- [ ] Live verification on a test server — providers are filesystem-detected, not runtime-probed

## Phase 6 — Weapons
- [x] `weapons.json` keyed by short name, indexed by entity name (Phase 1)
- [x] Dynamic registration of `PurchaseCommand` aliases (e.g. `!ak`, `!awp`)
- [x] `WeaponBuyZoneOnly` toggle
- [x] `MaxPurchase` per life tracking via `PlayerState.PurchaseCounts`
- [x] Restriction check on purchase
- [ ] Auto-strip restricted weapons picked up off the ground — deferred; needs `OnEntitySpawned`

## Phase 7 — Teleport
- [x] `!ztele` → capture spawn origin, teleport back
- [x] Uses per round + cooldown
- [x] `TeleportAllow` master toggle
- [x] Reset on round start (via `ResetForRound` on PlayerState)

## Phase 8 — API
- [x] `IZombieModAPI` interface in `ZombieMod.Api/`
- [x] `ZombieClass` DTO mirrors `ClassConfig`
- [x] Implementation in `ZombieMod/Api/ZombieModApi.cs`
- [x] Registered via `Capabilities.RegisterPluginCapability("zombiemod:core", () => _api)`
- [x] Internal hooks: `Raise*` on Api wired into services to avoid double-firing
- [x] `HookResult.Stop` cancellation respected in `InfectClient`/`HumanizeClient`

## Phase 9 — Admin
- [x] `[RequiresPermissions("@css/admin")]` on `!infect`, `!human`, `!zreload`
- [x] `!zspawn`, `!ztele` are player commands (no perm gate)
- [x] Target resolution via `info.GetArgTargetResult(1)` (handles `@me`/`@all`/`@t`/`@ct`/`#userid`/partial)
- [ ] `!zclass` shows a stub message — picker UI deferred (see Phase 4)

## Phase 10 — Polish
- [x] README install path + cvar block
- [x] No warnings on `dotnet build -c Release`
- [ ] Smoke test on Linux dedicated server (cannot do from build host)
- [ ] Tag v0.1.0 once subsystems land *and* the smoke test passes
- [ ] Localizer / language file support — currently English strings inline
- [ ] Round-timer / `TimeoutWinner` enforcement
- [ ] Napalm fire VFX
- [ ] `!zclass` chat menu
- [ ] Auto-strip of restricted-weapon ground pickups

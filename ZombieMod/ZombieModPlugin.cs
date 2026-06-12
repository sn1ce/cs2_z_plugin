using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using ZombieMod.Api;
using ZombieMod.ApiImpl;
using ZombieMod.Services;
using ZombieMod.Util;
using static CounterStrikeSharp.API.Core.Listeners;

namespace ZombieMod;

public sealed class ZombieModPlugin : BasePlugin
{
    public override string ModuleName => "ZombieMod";
    public override string ModuleVersion => "0.1.0";
    public override string ModuleAuthor => "sn1ce";
    public override string ModuleDescription => "Outbreak — classic infection/survival mode for CS2.";

    public static readonly PluginCapability<IZombieModAPI> Capability = new("zombiemod:core");

    internal ConfigService Config { get; private set; } = null!;
    internal InfectionService Infection { get; private set; } = null!;
    internal ClassService Classes { get; private set; } = null!;
    internal WeaponService Weapons { get; private set; } = null!;
    internal RespawnService Respawn { get; private set; } = null!;
    internal TeleportService Teleport { get; private set; } = null!;
    internal KnockbackService Knockback { get; private set; } = null!;
    internal CommandService Commands { get; private set; } = null!;
    internal PropService Props { get; private set; } = null!;
    internal SoundService Sounds { get; private set; } = null!;
    internal FlashlightService Flashlight { get; private set; } = null!;
    internal KnockbackProviderDetector KnockbackDetector { get; private set; } = null!;
    internal ZombieModApi Api { get; private set; } = null!;

    // Throttle counter for the OnTick reserve-ammo refill (acts every 16th tick ≈ 4×/sec).
    private int _ammoRefillTick;

    public override void Load(bool hotReload)
    {
        var configDir = ResolveConfigDir();

        Config            = new ConfigService(Logger, configDir);
        KnockbackDetector = new KnockbackProviderDetector(Logger);

        Config.LoadAll();
        KnockbackDetector.Detect(ResolveAddonsDir());

        Infection = new InfectionService(Logger, Config) { Host = this };
        Classes   = new ClassService(Logger, Config, Infection) { Host = this };
        Weapons   = new WeaponService(Logger, Config, Infection) { Host = this };
        Knockback = new KnockbackService(Logger, Config, KnockbackDetector, Infection);
        Respawn   = new RespawnService(Logger, Config, Infection) { Host = this };
        Teleport  = new TeleportService(Logger, Config, Infection) { Host = this };
        Props     = new PropService(Logger, Config);
        Sounds    = new SoundService(Logger, Config);
        Flashlight = new FlashlightService(Logger) { Host = this };
        Commands  = new CommandService(Logger, Config, Infection, Respawn, Classes, Teleport, Weapons, Props, Sounds, Flashlight) { Host = this };

        Api = new ZombieModApi(Infection, Classes);

        // Wire service → API event-firing. Internal callers do the firing; external API calls
        // route straight to the service (which fires) so events never raise twice.
        Infection.FireInfectHook         = (c, a, pz, f) =>
        {
            // Patient Zero scream spatializes from the chosen client's pawn (so other players
            // hear the direction). The "infect" event (regular knife-infect) isn't defined
            // in sounds.json today — Broadcast no-ops on missing keys.
            Sounds.Broadcast(pz ? "patient_zero" : "infect", c.PlayerPawn.Value);
            return Api.RaiseClientInfect(c, a, pz, f);
        };
        Infection.FireHumanizeHook       = Api.RaiseClientHumanize;
        Infection.FirePatientZeroSelectedHook = Api.RaisePatientZeroSelected;
        Infection.FireRoundStartHook          = Api.RaiseOutbreakRoundStart;
        Infection.ApplyClassHook         = (c, cls) => Classes.ApplyClass(c, cls);

        ScheduleInfectedIdleSounds();
        ScheduleFlashlightHint();

        Capabilities.RegisterPluginCapability(Capability, () => Api);

        Weapons.RegisterPurchaseCommands();

        // Required cvars: loaded from configs/cvars.json. Self-applying at Load + every map
        // start keeps gameplay stable as a safety net. The authoritative pinning happens via
        // gameserver/.../cfg/gamemode_casual_server.cfg — CS2 execs that file as the LAST
        // step of casual-mode bootstrap, so it cleanly wins over gamemode_casual.cfg's
        // sv_infinite_ammo=0 / mp_buy_anywhere=0 clobber. Keep the two in sync.
        ApplyRequiredCvars();
        EnsureWarmupEnded();
        RegisterListener<OnMapStart>(_ =>
        {
            ApplyRequiredCvars();
            EnsureWarmupEnded();
            // Precache happens via the OnServerPrecacheResources listener (manifest.AddResource).
        });

        RegisterListener<OnServerPrecacheResources>(manifest =>
        {
            var n = 0;
            manifest.AddResource("particles/explosions_fx/explosion_hegrenade.vpcf"); n++;

            // Custom class models from classes.json (if user replaces "default" with a path).
            foreach (var cls in Config.Classes.Values)
            {
                if (!string.IsNullOrEmpty(cls.Model) && cls.Model != "default")
                { manifest.AddResource(cls.Model); n++; }
            }

            // Prop catalog models.
            foreach (var prop in Config.Props.Values)
            {
                if (!string.IsNullOrEmpty(prop.Model))
                { manifest.AddResource(prop.Model); n++; }
            }

            Logger.LogInformation("[Precache] OnServerPrecacheResources fired — added {N} resources", n);
        });

        // Per-weapon Clip + Reserve must be applied at entity-creation time. Hooking
        // EventItemPickup is too late — CS2's pickup logic overrides our writes.
        // Also handles RemoveHostages on cs_* maps: kills hostage_entity 0.1s after spawn.
        RegisterListener<OnEntityCreated>(entity =>
        {
            if (entity is null || !entity.IsValid) return;

            // Hostage strip — runs on every map. cs_* maps spawn hostage_entity instances at
            // round start; we kill them so CTs can't auto-win on rescue and the AI doesn't
            // wander into infected paths. Toggle via gamesettings.RemoveHostages.
            if (Config.GameSettings.RemoveHostages
                && entity.DesignerName.Equals("hostage_entity", StringComparison.OrdinalIgnoreCase))
            {
                var hostage = entity;
                AddTimer(0.1f, () =>
                {
                    if (hostage.IsValid)
                    {
                        try { hostage.AddEntityIOEvent("Kill", hostage, null, "", 0.0f); } catch { }
                    }
                });
                return;
            }

            if (!entity.DesignerName.StartsWith("weapon_", StringComparison.OrdinalIgnoreCase)) return;
            if (entity.DesignerName.Contains("knife", StringComparison.OrdinalIgnoreCase)) return;

            var name = entity.DesignerName;
            if (!Config.WeaponsByEntity.TryGetValue(name, out var cfg)) return;

            Server.NextFrame(() =>
            {
                if (!entity.IsValid) return;
                var weapon = new CCSWeaponBase(entity.Handle);
                if (!weapon.IsValid || weapon.VData is null) return;

                var clipTarget = cfg.Clip > 0 ? cfg.Clip : weapon.VData.MaxClip1 * 2;
                if (clipTarget > 0)
                {
                    if (weapon.VData.MaxClip1 < clipTarget) weapon.VData.MaxClip1 = clipTarget;
                    if (weapon.Clip1 < clipTarget) weapon.Clip1 = clipTarget;
                }
                Utilities.SetStateChanged(weapon, "CBasePlayerWeapon", "m_iClip1");

                // Reserve (spare) ammo pinned to 2 magazines. The engine clamps reserve to the
                // weapon's VData cap, so raise that cap first, then fill. Weapons.TickRefillReserves
                // keeps it topped after reloads so it never drains to 0 (no sv_infinite_ammo / cheats).
                var reserveTarget = cfg.Reserve > 0 ? cfg.Reserve : clipTarget * 2;
                if (reserveTarget > 0)
                {
                    weapon.VData.PrimaryReserveAmmoMax = reserveTarget;
                    weapon.ReserveAmmo[0] = reserveTarget;
                    Utilities.SetStateChanged(weapon, "CBasePlayerWeapon", "m_pReserveAmmo");
                }
            });
        });

        RegisterListener<OnClientPutInServer>(slot =>
        {
            var c = Utilities.GetPlayerFromSlot(slot);
            if (c is not null)
            {
                Infection.OnClientPutInServer(c);
                Sounds.ApplyClientCvarsTo(c);
            }
        });
        RegisterListener<OnClientDisconnect>(slot =>
        {
            Classes.OnPlayerDisconnect(slot);
            Infection.OnClientDisconnect(slot);
            Flashlight.Cleanup(slot);
        });

        // Per-tick: re-position any active flashlights to follow their owners.
        // Cheap when nobody has a flashlight on (early-return on _wantOn.Count == 0).
        // Classes.Tick handles molotov-slow expiry (restores class speed when no longer burning).
        // Weapons.TickRefillReserves (throttled to ~4x/sec) keeps spare ammo pinned at 2 mags.
        RegisterListener<OnTick>(() =>
        {
            Flashlight.Tick();
            Classes.Tick();
            if (++_ammoRefillTick >= 16) { _ammoRefillTick = 0; Weapons.TickRefillReserves(); }
        });

        // sv_cheats has to stay on for sv_infinite_ammo (cheat-protected), but that means
        // any player can normally run noclip / god / ent_* and break the round. Block these
        // commands from non-admins. Admins (@css/root) keep dev access.
        foreach (var cmd in new[] { "noclip", "god", "ent_create", "ent_remove", "ent_fire",
                                    "ent_setpos", "ent_teleport", "impulse" })
        {
            AddCommandListener(cmd, (client, info) =>
            {
                if (client is null || !client.IsValid) return HookResult.Continue;
                if (CounterStrikeSharp.API.Modules.Admin.AdminManager.PlayerHasPermissions(client, "@css/root"))
                    return HookResult.Continue;
                client.PrintToChat($" \x04[ZombieMod]\x01 \x07{cmd}\x01 is admin-only on this server.");
                return HookResult.Stop;
            });
        }

        // changelevel intercept: CS2 picks the destination map's metadata-declared gamemode
        // at swap time, so 'changelevel de_dust2' from RCON drops the server into competitive
        // (de_dust2 defaults to competitive in gamemodes_server.txt). Pinning game_type/
        // game_mode just BEFORE the changelevel forces CS2 to exec gamemode_casual.cfg →
        // gamemode_casual_server.cfg on the new map, keeping us in casual.
        AddCommandListener("changelevel", (_, _) =>
        {
            Server.ExecuteCommand("game_type 0");
            Server.ExecuteCommand("game_mode 0");
            return HookResult.Continue;
        });

        // mp_restartgame intercept: resets infection state the instant the command is issued
        // (before the engine's restart delay), so a player who runs it mid-game doesn't linger
        // as a zombie. The subsequent EventRoundStart also calls OnRoundStart, so this is a
        // belt-and-suspenders reset — but it only fires on the explicit command, never during
        // normal round flow, so it can't kill a live first-infection timer.
        AddCommandListener("mp_restartgame", (_, _) =>
        {
            Infection.OnRoundStart();
            return HookResult.Continue;
        });

        // Block weapon pickup for infected at the engine level. The previous reactive
        // approach (OnItemPickup → drop) caused a pickup-drop loop: the zombie picked
        // up → we dropped at their feet → they walked over it next tick → loop. During
        // the loop the pickup animation kept restarting and zombies couldn't knife.
        // CanAcquire fires BEFORE the engine adds the weapon to inventory; returning
        // anything non-Allowed cleanly rejects the pickup with no animation, no inventory
        // touch, and the weapon stays on the ground for survivors. Buy path (method=Buy)
        // is untouched so survivors keep purchasing normally.
        VirtualFunctions.CCSPlayer_ItemServices_CanAcquireFunc.Hook(hook =>
        {
            if (hook.GetParam<AcquireMethod>(2) != AcquireMethod.PickUp) return HookResult.Continue;
            var services = hook.GetParam<CCSPlayer_ItemServices>(0);
            var pawn = services?.Pawn?.Value;
            var controllerHandle = pawn?.Controller;
            if (controllerHandle is null || controllerHandle.Value is null) return HookResult.Continue;
            var controller = new CCSPlayerController(controllerHandle.Value.Handle);
            if (!controller.IsValid) return HookResult.Continue;
            if (!Infection.IsClientInfected(controller)) return HookResult.Continue;
            hook.SetReturn(AcquireResult.NotAllowedByMode);
            return HookResult.Stop;
        }, HookMode.Pre);

        Logger.LogInformation(
            "ZombieMod {Version} loaded (hotReload={HotReload}, configDir={Dir})",
            ModuleVersion, hotReload, configDir);
    }

    // ─── event handlers ───────────────────────────────────────────────────────

    [GameEventHandler]
    public HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        // Fires on every round start, INCLUDING the round restart that mp_restartgame
        // triggers — so this one handler resets infection state for both normal rounds and
        // restarts. (An earlier "defense-in-depth" set of extra restart hooks — CsPreRestart,
        // RoundPrestart, BeginNewMatch, AnnounceMatchStart — was removed: they fired AFTER
        // OnRoundFreezeEnd scheduled the first-infection timer and each called OnRoundStart →
        // KillRoundTimers(), nuking the timer so nobody ever got infected.)
        Infection.OnRoundStart();
        Props.CleanupAll();
        Flashlight.CleanupAll();
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnRoundFreezeEnd(EventRoundFreezeEnd @event, GameEventInfo info)
    {
        Infection.OnRoundFreezeEnd();
        // Kick off the round-long ambient track. OnRoundEnd already calls
        // Sounds.StopAllForEveryone(), so this one cuts cleanly at round end.
        Sounds.Broadcast("round_ambient");
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnRoundEnd(EventRoundEnd @event, GameEventInfo info)
    {
        Infection.OnRoundEnd();
        // Kill any in-flight ambient music track so it doesn't bleed into post-round/freezetime.
        Sounds.StopAllForEveryone();
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnPlayerHurt(EventPlayerHurt @event, GameEventInfo info)
    {
        var victim = @event.Userid;
        var attacker = @event.Attacker;

        Infection.OnPlayerHurt(victim, attacker, @event.Weapon);

        if (victim is { IsValid: true } v)
        {
            Classes.OnPlayerHurt(v);
            // Inferno/molotov fire damage refreshes the molotov-slow timer on infected.
            // CS2 reports the burn-area weapon as "inferno"; the projectile itself can also
            // appear as "molotov" / "incgrenade" on the first impact tick. Catch all three.
            var w = @event.Weapon ?? string.Empty;
            if (w.Contains("inferno", StringComparison.OrdinalIgnoreCase)
                || w.Contains("molotov", StringComparison.OrdinalIgnoreCase)
                || w.Contains("incgrenade", StringComparison.OrdinalIgnoreCase))
            {
                Classes.ApplyMolotovBurn(v);
            }
            if (attacker is { IsValid: true } a)
                Knockback.ApplyHurtKnockback(v, a, w, @event.DmgHealth, @event.Hitgroup);
        }
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnHegrenadeDetonate(EventHegrenadeDetonate @event, GameEventInfo info)
    {
        // Record the detonation origin so the matching EventPlayerHurt(weapon=hegrenade) tick
        // can apply directional knockback away from this point. Without this hook, HE damage
        // events had no grenade reference and knockback silently skipped.
        var attacker = @event.Userid;
        if (attacker is null || !attacker.IsValid) return HookResult.Continue;
        Knockback.RememberHeDetonate(attacker.Slot,
            new CounterStrikeSharp.API.Modules.Utils.Vector(@event.X, @event.Y, @event.Z));
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
    {
        var victim = @event.Userid;
        Infection.OnPlayerDeath(victim);
        if (victim is not null && victim.IsValid)
        {
            Classes.OnPlayerDeath(victim);
            Respawn.ScheduleRespawn(victim);
            Flashlight.Cleanup(victim.Slot);
            // Survivor death (bite) spatializes from the dying player's pawn so nearby
            // zombies hear the directional cue. Infected death uses the GFL path-based
            // sounds (no SoundEvent yet) — sourceEntity is moot for play <path> fallback.
            Sounds.Broadcast(Infection.IsClientInfected(victim) ? "infected_death" : "survivor_death",
                             victim.PlayerPawn.Value);
        }
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnPlayerSpawn(EventPlayerSpawn @event, GameEventInfo info)
    {
        var client = @event.Userid;
        if (client is null || !client.IsValid) return HookResult.Continue;
        if (client.Team is CsTeam.None or CsTeam.Spectator) return HookResult.Continue;

        // Defer the team/class apply by ~0.2s so the pawn finishes its own spawn pipeline
        // first. Without this, SetModel inside ApplyClass runs before CS2 has fully initialized
        // the pawn and the swap doesn't stick — observed on respawned infected getting the
        // default agent model instead of the zombie one.
        var action = Respawn.ResolvePostSpawnAction(client);
        var deferredClient = client;
        AddTimer(0.2f, () =>
        {
            if (!deferredClient.IsValid) return;
            switch (action)
            {
                case RespawnService.PostSpawnAction.Infect:
                    if (deferredClient.PawnIsAlive)
                        Infection.InfectClient(deferredClient, attacker: null, patientZero: false, force: true);
                    break;
                case RespawnService.PostSpawnAction.Humanize:
                    Infection.HumanizeClient(deferredClient, respawn: false);
                    break;
            }
        });

        // Capture spawn position after CS2 finishes placing the pawn.
        var captured = client;
        AddTimer(0.2f, () => Teleport.OnPlayerSpawn(captured));

        // Auto-enable flashlight on spawn (config-gated). Tiny delay so the pawn is fully
        // alive when EnsureOn runs its IsValid/PawnIsAlive check.
        if (Config.GameSettings.FlashlightDefaultOn && !client.IsBot)
        {
            AddTimer(0.3f, () =>
            {
                if (captured.IsValid && captured.PawnIsAlive)
                    Flashlight.EnsureOn(captured);
            });
        }
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnItemPickup(EventItemPickup @event, GameEventInfo info)
    {
        var client = @event.Userid;
        if (client is null || !client.IsValid) return HookResult.Continue;

        // Infected: strip non-knife pickups. (Ammo/clip applied via OnEntityCreated above.)
        if (Infection.IsClientInfected(client)
            && @event.Item is not null
            && !@event.Item.Contains("knife", StringComparison.OrdinalIgnoreCase))
        {
            Infection.StripWeaponsKeepKnife(client);
        }
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnPlayerTeam(EventPlayerTeam @event, GameEventInfo info)
    {
        if (@event.Isbot) return HookResult.Continue;
        var client = @event.Userid;
        if (client is null || !client.IsValid) return HookResult.Continue;

        // EventPlayerTeam.Team is an int: 0=none, 1=spectator, 2=T, 3=CT.
        if (@event.Team is 2 or 3)
            Respawn.OnPlayerJoinedTeam(client);

        return HookResult.Continue;
    }

    // ─── console commands ─────────────────────────────────────────────────────

    [ConsoleCommand("css_infect", "Force-infect a player.")]
    [CommandHelper(1, "<target>")]
    [RequiresPermissions("@css/admin")]
    public void Cmd_Infect(CCSPlayerController? caller, CommandInfo info)
        => Commands.HandleInfect(caller, info);

    [ConsoleCommand("css_human", "Force-restore a player to survivor.")]
    [CommandHelper(1, "<target>")]
    [RequiresPermissions("@css/admin")]
    public void Cmd_Human(CCSPlayerController? caller, CommandInfo info)
        => Commands.HandleHuman(caller, info);

    [ConsoleCommand("css_zspawn", "Respawn yourself into the current round.")]
    [CommandHelper(0, "", CommandUsage.CLIENT_ONLY)]
    public void Cmd_ZSpawn(CCSPlayerController? caller, CommandInfo info)
        => Commands.HandleZSpawn(caller, info);

    [ConsoleCommand("css_zclass", "Open the class picker.")]
    [CommandHelper(0, "", CommandUsage.CLIENT_ONLY)]
    public void Cmd_ZClass(CCSPlayerController? caller, CommandInfo info)
        => Commands.HandleZClass(caller, info);

    [ConsoleCommand("css_zreload", "Reload all ZombieMod configs.")]
    [CommandHelper(0, "")]
    [RequiresPermissions("@css/admin")]
    public void Cmd_ZReload(CCSPlayerController? caller, CommandInfo info)
        => Commands.HandleReload(caller, info);

    [ConsoleCommand("css_ztele", "Teleport back to your spawn point.")]
    [CommandHelper(0, "", CommandUsage.CLIENT_ONLY)]
    public void Cmd_ZTele(CCSPlayerController? caller, CommandInfo info)
        => Commands.HandleZTele(caller, info);

    [ConsoleCommand("css_zhelp", "Open the ZombieMod help menu (unstuck + commands).")]
    [ConsoleCommand("css_z_help", "Open the ZombieMod help menu (unstuck + commands).")]
    [CommandHelper(0, "", CommandUsage.CLIENT_ONLY)]
    public void Cmd_ZHelp(CCSPlayerController? caller, CommandInfo info)
        => Commands.HandleZHelp(caller, info);

    [ConsoleCommand("css_prop", "Open the prop-spawn menu (costs in-game cash).")]
    [CommandHelper(0, "", CommandUsage.CLIENT_ONLY)]
    public void Cmd_Prop(CCSPlayerController? caller, CommandInfo info)
        => Commands.HandleProp(caller, info);

    [ConsoleCommand("css_flashlight", "Toggle your flashlight (light_dynamic attached to your pawn).")]
    [ConsoleCommand("css_fl", "Toggle your flashlight.")]
    [CommandHelper(0, "", CommandUsage.CLIENT_ONLY)]
    public void Cmd_Flashlight(CCSPlayerController? caller, CommandInfo info)
        => Commands.HandleFlashlight(caller, info);

    [ConsoleCommand("css_admin", "Open the ZombieMod admin panel (root admins only).")]
    [CommandHelper(0, "", CommandUsage.CLIENT_ONLY)]
    [RequiresPermissions("@css/root")]
    public void Cmd_Admin(CCSPlayerController? caller, CommandInfo info)
        => Commands.HandleAdmin(caller, info);

    // ─── config resolution ────────────────────────────────────────────────────

    /// <summary>
    /// Plugin DLL lives at <c>addons/counterstrikesharp/plugins/ZombieMod/</c>;
    /// configs ship at <c>addons/counterstrikesharp/configs/ZombieMod/</c>. Two levels up,
    /// then into configs.
    /// </summary>
    private string ResolveConfigDir()
    {
        var canonical = Path.GetFullPath(
            Path.Combine(ModuleDirectory, "..", "..", "configs", "ZombieMod"));
        if (Directory.Exists(canonical)) return canonical;

        // Dev fallback: configs/ folder next to the dll.
        var nextToDll = Path.Combine(ModuleDirectory, "configs");
        if (Directory.Exists(nextToDll)) return nextToDll;

        Logger.LogWarning("[Config] No config directory found, expected {Path}. Defaults will be used.", canonical);
        return canonical;
    }

    /// <summary>Returns <c>game/csgo/addons/</c>. Three levels up from plugin DLL.</summary>
    private string ResolveAddonsDir()
        => Path.GetFullPath(Path.Combine(ModuleDirectory, "..", "..", ".."));


    private readonly Random _idleRng = new();

    /// <summary>
    /// Ambient infected-voice loop. Every 15–25s we broadcast one of the short GFL idle clips
    /// from sounds.json["infected_idle"], provided ≥1 alive infected is on the server and the
    /// round is active. Cadence assumes 1–2s clips — bump back to 60s+ if we ever wire long
    /// music tracks back in.
    /// </summary>
    private void ScheduleInfectedIdleSounds()
    {
        var delay = 15.0f + (float)(_idleRng.NextDouble() * 10.0);
        AddTimer(delay, () =>
        {
            try
            {
                // Only broadcast if a round is actually in progress — otherwise the track
                // bleeds into the post-round / freezetime screens and won't stop.
                if (Infection.RoundActive)
                {
                    var aliveInfected = Utilities.GetPlayers()
                        .Where(p => p is { IsValid: true } && p.PawnIsAlive && Infection.IsClientInfected(p))
                        .ToList();
                    if (aliveInfected.Count > 0)
                        Sounds.Broadcast("infected_idle");
                }
            }
            catch { /* don't let one bad tick kill the loop */ }
            ScheduleInfectedIdleSounds();
        });
    }

    /// <summary>
    /// Every 30s, remind alive humans about the flashlight controls. Suppressed when no
    /// humans are connected, so it doesn't spam an empty server.
    /// </summary>
    private void ScheduleFlashlightHint()
    {
        AddTimer(30.0f, () =>
        {
            try
            {
                var sent = 0;
                foreach (var p in Utilities.GetPlayers())
                {
                    if (p is null || !p.IsValid || p.IsBot || p.IsHLTV) continue;
                    p.PrintToChat(" \x04[ZombieMod]\x01 Press \x07E\x01 (or type \x07!fl\x01) to toggle your flashlight.");
                    sent++;
                }
            }
            catch { /* don't let one bad tick kill the loop */ }
            ScheduleFlashlightHint();
        });
    }

    private void ApplyRequiredCvars()
    {
        // Single-pass apply. Server.NextFrame turned out to silently drop its callback at
        // plugin-load and timer contexts, so the previous "split sv_cheats then nextframe
        // the rest" approach left everything except sv_cheats unset. Manual RCON proved a
        // same-frame `sv_cheats 1; sv_infinite_ammo 2; mp_buy_anywhere 1` sequence works,
        // so the queuing-race theory was wrong.
        Logger.LogInformation("[Cvars] Apply ({N})", Config.Cvars.RequiredCvars.Count);
        foreach (var cmd in Config.Cvars.RequiredCvars)
            Server.ExecuteCommand(cmd);
    }

    private CounterStrikeSharp.API.Modules.Timers.Timer? _warmupKiller;
    private int _warmupKillerTicks;

    /// <summary>
    /// Beat warmup down. <c>mp_warmup_end</c> alone is unreliable in casual mode (gamemode cfg
    /// keeps re-arming pausetimer), so we also unfreeze the timer, zero the duration, and
    /// finally fall back to a hard <c>mp_restartgame 1</c> if WarmupPeriod is still true after
    /// a handful of attempts.
    /// </summary>
    private void EnsureWarmupEnded()
    {
        _warmupKiller?.Kill();
        _warmupKillerTicks = 0;
        _warmupKiller = AddTimer(2.0f, () =>
        {
            if (!Util.GameRules.IsWarmup())
            {
                _warmupKiller?.Kill();
                _warmupKiller = null;
                return;
            }

            _warmupKillerTicks++;
            Server.ExecuteCommand("mp_warmup_pausetimer 0");
            Server.ExecuteCommand("mp_warmuptime 0");
            Server.ExecuteCommand("mp_warmup_end");

            // After 5 cadence ticks (~10s) of warmup still being on, force-restart the game.
            // mp_restartgame 1 cleanly bounces the round and bypasses any stuck warmup state.
            if (_warmupKillerTicks == 5)
            {
                Logger.LogWarning("[Warmup] Stuck after {N} ticks — falling back to mp_restartgame 1.", _warmupKillerTicks);
                Server.ExecuteCommand("mp_restartgame 1");
            }
        }, CounterStrikeSharp.API.Modules.Timers.TimerFlags.REPEAT
         | CounterStrikeSharp.API.Modules.Timers.TimerFlags.STOP_ON_MAPCHANGE);
    }
}

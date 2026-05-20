using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
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
    public override string ModuleAuthor => "ZombieMod contributors";
    public override string ModuleDescription => "Classic infection/survival zombie gameplay for CS2.";

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
    internal KnockbackProviderDetector KnockbackDetector { get; private set; } = null!;
    internal ZombieModApi Api { get; private set; } = null!;

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
        Teleport  = new TeleportService(Logger, Config, Infection);
        Props     = new PropService(Logger, Config);
        Commands  = new CommandService(Logger, Config, Infection, Respawn, Classes, Teleport, Weapons, Props) { Host = this };

        Api = new ZombieModApi(Infection, Classes);

        // Wire service → API event-firing. Internal callers do the firing; external API calls
        // route straight to the service (which fires) so events never raise twice.
        Infection.FireInfectHook         = Api.RaiseClientInfect;
        Infection.FireHumanizeHook       = Api.RaiseClientHumanize;
        Infection.FireMotherSelectedHook = Api.RaiseMotherZombieSelected;
        Infection.FireRoundStartHook     = Api.RaiseZombieRoundStart;
        Infection.ApplyClassHook         = (c, cls) => Classes.ApplyClass(c, cls);

        Capabilities.RegisterPluginCapability(Capability, () => Api);

        Weapons.RegisterPurchaseCommands();

        // Required cvars: our README documents these, but relying on the user to `exec` a
        // cfg file is brittle (e.g. casual mode's auto-balance fights with our team shuffle).
        // Self-applying at Load + every map start keeps gameplay stable.
        ApplyRequiredCvars();
        EnsureWarmupEnded();
        RegisterListener<OnMapStart>(_ =>
        {
            ApplyRequiredCvars();
            EnsureWarmupEnded();
            AddTimer(5.0f, () => Server.ExecuteCommand("sv_cheats 1"));
            AddTimer(15.0f, () => Server.ExecuteCommand("sv_cheats 1"));

            // Precache happens via the OnServerPrecacheResources listener (manifest.AddResource).
            // Earlier hidden-dummy hack didn't pin anything — verified via deep research against
            // CS2-Parachute / CS2PropHunt / ResourcePrecacher: manifest is the only path.
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

        RegisterListener<OnClientPutInServer>(slot =>
        {
            var c = Utilities.GetPlayerFromSlot(slot);
            if (c is not null) Infection.OnClientPutInServer(c);
        });
        RegisterListener<OnClientDisconnect>(slot =>
        {
            Classes.OnPlayerDisconnect(slot);
            Infection.OnClientDisconnect(slot);
        });

        Logger.LogInformation(
            "ZombieMod {Version} loaded (hotReload={HotReload}, configDir={Dir})",
            ModuleVersion, hotReload, configDir);
    }

    // ─── event handlers ───────────────────────────────────────────────────────

    [GameEventHandler]
    public HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        Infection.OnRoundStart();
        Props.CleanupAll();
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnRoundFreezeEnd(EventRoundFreezeEnd @event, GameEventInfo info)
    {
        Infection.OnRoundFreezeEnd();
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnRoundEnd(EventRoundEnd @event, GameEventInfo info)
    {
        Infection.OnRoundEnd();
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
            if (attacker is { IsValid: true } a)
                Knockback.ApplyHurtKnockback(v, a, @event.Weapon, @event.DmgHealth, @event.Hitgroup);
        }
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
        }
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnPlayerSpawn(EventPlayerSpawn @event, GameEventInfo info)
    {
        var client = @event.Userid;
        if (client is null || !client.IsValid) return HookResult.Continue;
        if (client.Team is CsTeam.None or CsTeam.Spectator) return HookResult.Continue;

        switch (Respawn.ResolvePostSpawnAction(client))
        {
            case RespawnService.PostSpawnAction.Infect:
                Infection.InfectClient(client, attacker: null, motherZombie: false, force: true);
                break;
            case RespawnService.PostSpawnAction.Humanize:
                Infection.HumanizeClient(client, respawn: false);
                break;
        }

        // Capture spawn position after CS2 finishes placing the pawn.
        var captured = client;
        AddTimer(0.2f, () => Teleport.OnPlayerSpawn(captured));
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnItemPickup(EventItemPickup @event, GameEventInfo info)
    {
        var client = @event.Userid;
        if (client is null || !client.IsValid) return HookResult.Continue;
        if (!Infection.IsClientInfected(client)) return HookResult.Continue;
        if (@event.Item is null) return HookResult.Continue;
        if (@event.Item.Contains("knife", StringComparison.OrdinalIgnoreCase)) return HookResult.Continue;

        // Zombie picked up a gun — strip the inventory back down to the knife.
        Infection.StripWeaponsKeepKnife(client);
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

    [ConsoleCommand("css_human", "Force-humanize a player.")]
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

    private static readonly string[] RequiredCvars =
    [
        "mp_limitteams 0",
        "mp_autoteambalance 0",
        "mp_disconnect_kills_players 1",
        // 0 lets the newly-infected player phase through the zombie that knifed them, so they
        // never get stuck inside each other's hitbox at the moment of team-switch.
        "mp_solid_teammates 0",
        "mp_teammates_are_enemies 0",
        "mp_ignore_round_win_conditions 1",
        "mp_give_player_c4 0",
        // We rely on CS2's native round-end detection: when the last alive CT is infected we
        // SwitchTeam them to T, which makes CS2 see 0 CTs alive and end the round naturally.
        // Setting this to 1 (ZombieSharp's default) breaks that detection.
        "mp_ignore_round_win_conditions 0",
        // Disable warmup as aggressively as we can — casual mode's gamemode cfg re-enables
        // pausetimer, so we need to clear it explicitly each map start.
        "mp_warmuptime 0",
        "mp_warmup_pausetimer 0",
        "mp_warmup_offline_enabled 0",
        // Short freezetime for faster testing iteration.
        "mp_freezetime 1",
        // Bots: fill the server so workshop maps populate without players. Override your
        // compose's CS2_BOT_QUOTA=0; the cvar set runs after the image's startup.
        // Always 2 bots regardless of human count — gives a default testing buddy on solo.
        "bot_quota_mode normal",
        "bot_quota 2",
        "bot_join_after_player 0",
        // Cheats on by default so dev can use `thirdperson`, noclip, etc.
        // Overrides the image's CS2_CHEATS=0 startup arg — plugin applies after.
        "sv_cheats 1",
        // Start money per spawn — lets players afford props/weapons immediately.
        "mp_startmoney 4000",
        // Disable CS2's own match-end pipeline. The plugin owns map rotation via
        // GameSettings.MapRotation + MaxRoundsPerMap (see InfectionService.OnRoundEnd), so we
        // never want CS2 to auto-LOOPDEACTIVATE on its own round-count trigger.
        "mp_maxrounds 9999",
        "mp_match_can_clinch 0",
        "mp_match_end_changelevel 0",
        "mp_match_end_restart 1",
        "mp_halftime 0",
        "mp_overtime_enable 0",
        // mp_warmup_end fires at runtime from EnsureWarmupEnded() — issuing it inline at Load
        // is a no-op because warmup hasn't been entered yet.
        // (Frame-spike profiler spam can't be silenced via cvar in current CS2 builds —
        //  prof_dumpoverrun was removed/renamed. Lives in the log noise for now.)
        "developer 0",
    ];

    private void ApplyRequiredCvars()
    {
        foreach (var cmd in RequiredCvars)
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

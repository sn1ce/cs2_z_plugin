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
        Commands  = new CommandService(Logger, Config, Infection, Respawn, Classes, Teleport, Weapons);

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
}

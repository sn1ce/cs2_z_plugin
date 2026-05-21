using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using CS2MenuManager.API.Menu;
using Microsoft.Extensions.Logging;

namespace ZombieMod.Services;

public sealed class CommandService
{
    private readonly ILogger _logger;
    private readonly ConfigService _config;
    private readonly InfectionService _infection;
    private readonly RespawnService _respawn;
    private readonly ClassService _classes;
    private readonly TeleportService _teleport;
    private readonly WeaponService _weapons;
    private readonly PropService _props;

    /// <summary>Host plugin reference — required by CS2MenuManager.WasdMenu ctor.</summary>
    internal BasePlugin? Host { get; set; }

    public CommandService(
        ILogger logger,
        ConfigService config,
        InfectionService infection,
        RespawnService respawn,
        ClassService classes,
        TeleportService teleport,
        WeaponService weapons,
        PropService props)
    {
        _logger = logger;
        _config = config;
        _infection = infection;
        _respawn = respawn;
        _classes = classes;
        _teleport = teleport;
        _weapons = weapons;
        _props = props;
    }

    public void HandleProp(CCSPlayerController? caller, CommandInfo info) => OpenPropMenu(caller, null);

    private void OpenPropMenu(CCSPlayerController? caller, string? focusKey)
    {
        if (caller is null || !caller.IsValid || Host is null) return;
        var account = caller.InGameMoneyServices?.Account ?? 0;
        var menu = new WasdMenu($"Props — You have ${account}", Host);

        var ordered = _config.Props.OrderBy(p => p.Value.Cost).ToList();
        var startIdx = 0;

        for (var i = 0; i < ordered.Count; i++)
        {
            var (key, prop) = ordered[i];
            if (focusKey is not null && key == focusKey) startIdx = i;

            var capturedKey = key;
            var capturedProp = prop;
            var canAfford = account >= prop.Cost;
            var label = canAfford
                ? $"{prop.Name} — ${prop.Cost}"
                : $"{prop.Name} — ${prop.Cost} (insufficient)";
            menu.AddItem(label, (client, _) =>
            {
                if (_props.TrySpawn(client, capturedKey, out var reason))
                    client.PrintToChat($" \x04[ZombieMod]\x01 Spawned {capturedProp.Name} (-${capturedProp.Cost}).");
                else
                    client.PrintToChat($" \x04[ZombieMod]\x01 {reason}");
                // Keep the menu open AND remember which item was selected so cursor doesn't snap to top.
                Host?.AddTimer(0.1f, () => OpenPropMenu(client, capturedKey));
            });
        }
        menu.Display(caller, startIdx);
    }

    public void HandleInfect(CCSPlayerController? caller, CommandInfo info)
    {
        var targets = info.GetArgTargetResult(1);
        if (targets is null || !targets.Any())
        {
            info.ReplyToCommand(" [ZombieMod] No valid target.");
            return;
        }

        var mother = !_infection.InfectionStarted;
        var count = 0;
        foreach (var player in targets)
        {
            if (player is null || !player.IsValid || !player.PawnIsAlive) continue;
            if (_infection.IsClientInfected(player)) continue;
            _infection.InfectClient(player, attacker: null, motherZombie: mother, force: true);
            count++;
        }
        info.ReplyToCommand($" [ZombieMod] Infected {count} target(s).");
    }

    public void HandleHuman(CCSPlayerController? caller, CommandInfo info)
    {
        var targets = info.GetArgTargetResult(1);
        if (targets is null || !targets.Any())
        {
            info.ReplyToCommand(" [ZombieMod] No valid target.");
            return;
        }

        var count = 0;
        foreach (var player in targets)
        {
            if (player is null || !player.IsValid) continue;
            if (_infection.IsClientHuman(player) && player.PawnIsAlive) continue;
            _infection.HumanizeClient(player, respawn: !player.PawnIsAlive);
            count++;
        }
        info.ReplyToCommand($" [ZombieMod] Humanized {count} target(s).");
    }

    public void HandleZSpawn(CCSPlayerController? caller, CommandInfo info)
    {
        if (caller is null || !caller.IsValid) return;
        if (caller.PawnIsAlive)
        {
            info.ReplyToCommand(" [ZombieMod] You are already alive.");
            return;
        }
        if (!_config.GameSettings.RespawnEnable)
        {
            info.ReplyToCommand(" [ZombieMod] Respawn is disabled.");
            return;
        }
        _respawn.Respawn(caller);
    }

    public void HandleZClass(CCSPlayerController? caller, CommandInfo info)
    {
        if (caller is null || !caller.IsValid) return;
        info.ReplyToCommand(" [ZombieMod] Class picker is not implemented yet — set classes via gamesettings.");
    }

    public void HandleReload(CCSPlayerController? caller, CommandInfo info)
    {
        var ok = _config.Reload();
        info.ReplyToCommand(ok
            ? " [ZombieMod] Configs reloaded successfully."
            : " [ZombieMod] Config reload completed with validation warnings — see console.");
    }

    public void HandleZTele(CCSPlayerController? caller, CommandInfo info)
    {
        if (caller is null || !caller.IsValid) return;
        if (_teleport.TryTeleport(caller, out var reason))
            info.ReplyToCommand(" [ZombieMod] Teleported.");
        else
            info.ReplyToCommand($" [ZombieMod] {reason}");
    }

    public void HandleAdmin(CCSPlayerController? caller, CommandInfo info)
    {
        if (caller is null || !caller.IsValid || Host is null) return;
        if (!AdminManager.PlayerHasPermissions(caller, "@css/root"))
        {
            info.ReplyToCommand(" [ZombieMod] You are not an admin.");
            return;
        }

        var menu = new WasdMenu("Admin Panel", Host);
        menu.AddItem("Restart Round", (client, _) =>
        {
            Server.ExecuteCommand("mp_restartgame 1");
            client.PrintToChat(" \x04[ZombieMod]\x01 Round restarting…");
        });
        menu.AddItem("End Round — Humans Win", (client, _) =>
        {
            _infection.ForceEndRound(CsTeam.CounterTerrorist);
            client.PrintToChat(" \x04[ZombieMod]\x01 Forced humans win.");
        });
        menu.AddItem("End Round — Zombies Win", (client, _) =>
        {
            _infection.ForceEndRound(CsTeam.Terrorist);
            client.PrintToChat(" \x04[ZombieMod]\x01 Forced zombies win.");
        });
        menu.AddItem("Reload Configs", (client, _) =>
        {
            _config.Reload();
            client.PrintToChat(" \x04[ZombieMod]\x01 Configs reloaded.");
        });
        menu.AddItem("Skip to Next Map", (client, _) =>
        {
            _infection.ForceMapRotation();
            client.PrintToChat(" \x04[ZombieMod]\x01 Rotating to next map…");
        });
        menu.AddItem("End Warmup Now", (client, _) =>
        {
            Server.ExecuteCommand("mp_warmup_end");
            Server.ExecuteCommand("mp_warmup_pausetimer 0");
            client.PrintToChat(" \x04[ZombieMod]\x01 Warmup ended.");
        });
        menu.Display(caller, 0);
    }

    public void HandleZHelp(CCSPlayerController? caller, CommandInfo info)
    {
        if (caller is null || !caller.IsValid || Host is null) return;
        var menu = new WasdMenu("ZombieMod — Help", Host);
        menu.AddItem("Unstuck (teleport to your spawn)", (client, _) =>
        {
            if (_teleport.UnstuckClient(client, out var reason))
                client.PrintToChat(" \x04[ZombieMod]\x01 Unstuck — teleported to your spawn.");
            else
                client.PrintToChat($" \x04[ZombieMod]\x01 {reason}");
        });
        menu.AddItem("Open class picker", (client, _) =>
            client.PrintToChat(" \x04[ZombieMod]\x01 Class picker is a TODO — try !zclass."));
        menu.AddItem("Show commands", (client, _) =>
        {
            client.PrintToChat(" \x04[ZombieMod]\x01 Commands:");
            client.PrintToChat("   !zhelp — this menu");
            client.PrintToChat("   !ztele — teleport to spawn (uses/cooldown limited)");
            client.PrintToChat("   !zspawn — respawn (when dead)");
            client.PrintToChat("   !prop — spawn props from a menu");
            client.PrintToChat("   Buy commands: !ak !awp !deagle !p90 etc.");
        });
        menu.Display(caller, 0);
    }

}

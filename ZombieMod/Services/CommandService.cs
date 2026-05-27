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
    private readonly SoundService _sounds;
    private readonly FlashlightService _flashlight;

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
        PropService props,
        SoundService sounds,
        FlashlightService flashlight)
    {
        _logger = logger;
        _config = config;
        _infection = infection;
        _respawn = respawn;
        _classes = classes;
        _teleport = teleport;
        _weapons = weapons;
        _props = props;
        _sounds = sounds;
        _flashlight = flashlight;
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

        var patientZero = !_infection.InfectionStarted;
        var count = 0;
        foreach (var player in targets)
        {
            if (player is null || !player.IsValid || !player.PawnIsAlive) continue;
            if (_infection.IsClientInfected(player)) continue;
            _infection.InfectClient(player, attacker: null, patientZero: patientZero, force: true);
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
            if (_infection.IsClientSurvivor(player) && player.PawnIsAlive) continue;
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
        if (caller is null || !caller.IsValid || Host is null) return;

        // Picker shows every Team:0 class with PatientZero:false. Patient Zero is excluded
        // (it's auto-assigned at round start). The selection persists in PlayerState across
        // rounds and applies on the player's next infect/respawn.
        var picker = _config.Classes
            .Where(kv => kv.Value.Enable && kv.Value.Team == 0 && !kv.Value.PatientZero)
            .OrderBy(kv => kv.Value.Health)   // light → heavy reads nicely
            .ToList();

        if (picker.Count == 0)
        {
            info.ReplyToCommand(" [ZombieMod] No picker-eligible classes in classes.json.");
            return;
        }

        var state = _infection.GetState(caller);
        var currentId = state?.PreferredInfectedClass ?? _config.GameSettings.DefaultInfectedBuffer;
        var menu = new WasdMenu("Pick your infected class", Host);

        var startIdx = 0;
        for (var i = 0; i < picker.Count; i++)
        {
            var (id, cls) = picker[i];
            if (id == currentId) startIdx = i;
            var label = $"{cls.Name} — HP {cls.Health}, spd {cls.Speed:F0}, kb {cls.Knockback:F1}{(id == currentId ? " (current)" : "")}";
            var capturedId = id;
            var capturedName = cls.Name;
            menu.AddItem(label, (client, _) =>
            {
                var st = _infection.GetState(client);
                if (st is null)
                {
                    client.PrintToChat(" \x04[ZombieMod]\x01 Could not record class (no state).");
                    return;
                }
                st.PreferredInfectedClass = capturedId;
                client.PrintToChat(
                    $" \x04[ZombieMod]\x01 Class set to \x07{capturedName}\x01 — applies on your next infect/respawn.");
            });
        }
        menu.Display(caller, startIdx);
    }

    public void HandleReload(CCSPlayerController? caller, CommandInfo info)
    {
        var ok = _config.Reload();
        // Re-push ClientCvars (e.g. snd_toolvolume) so a value change in sounds.json
        // takes effect immediately for every connected player — no reconnect needed.
        _sounds.ApplyClientCvarsToAll();
        info.ReplyToCommand(ok
            ? " [ZombieMod] Configs reloaded successfully."
            : " [ZombieMod] Config reload completed with validation warnings — see console.");
    }

    public void HandleZTele(CCSPlayerController? caller, CommandInfo info)
    {
        if (caller is null || !caller.IsValid) return;
        // TeleportService prints "Teleporting in Xs..." (immediate) and "Teleported." (after the
        // delayed timer fires) itself, so on success we don't need to echo anything here.
        if (!_teleport.TryTeleport(caller, out var reason))
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
        menu.AddItem("End Round — Survivors Win", (client, _) =>
        {
            _infection.ForceEndRound(CsTeam.CounterTerrorist);
            client.PrintToChat(" \x04[ZombieMod]\x01 Forced survivors win.");
        });
        menu.AddItem("End Round — Outbreak Wins", (client, _) =>
        {
            _infection.ForceEndRound(CsTeam.Terrorist);
            client.PrintToChat(" \x04[ZombieMod]\x01 Forced outbreak wins.");
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

    public void HandleFlashlight(CCSPlayerController? caller, CommandInfo info)
    {
        if (caller is null || !caller.IsValid) return;
        if (!caller.PawnIsAlive)
        {
            info.ReplyToCommand(" [ZombieMod] Flashlight needs an alive pawn.");
            return;
        }
        var on = _flashlight.Toggle(caller);
        caller.PrintToChat($" \x04[ZombieMod]\x01 Flashlight \x07{(on ? "ON" : "OFF")}\x01.");
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

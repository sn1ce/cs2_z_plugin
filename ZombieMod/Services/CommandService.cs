using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
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

    public void HandleProp(CCSPlayerController? caller, CommandInfo info)
    {
        if (caller is null || !caller.IsValid || Host is null) return;
        var account = caller.InGameMoneyServices?.Account ?? 0;
        var menu = new WasdMenu($"Props — You have ${account}", Host);
        foreach (var (key, prop) in _config.Props.OrderBy(p => p.Value.Cost))
        {
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
            });
        }
        menu.Display(caller, 0);
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
        menu.AddItem("Try a zombie model", (client, _) => OpenModelMenu(client));
        menu.AddItem("Open class picker", (client, _) =>
            client.PrintToChat(" \x04[ZombieMod]\x01 Class picker is a TODO — try !zclass."));
        menu.AddItem("Show commands", (client, _) =>
        {
            client.PrintToChat(" \x04[ZombieMod]\x01 Commands:");
            client.PrintToChat("   !zhelp — this menu");
            client.PrintToChat("   !ztele — teleport to spawn (uses/cooldown limited)");
            client.PrintToChat("   !zspawn — respawn (when dead)");
            client.PrintToChat("   !prop — spawn props from a menu");
            client.PrintToChat("   !zmodels — model rotator (test which ones work)");
            client.PrintToChat("   Buy commands: !ak !awp !deagle !p90 etc.");
        });
        menu.Display(caller, 0);
    }

    public void HandleZModels(CCSPlayerController? caller, CommandInfo info) => OpenModelMenu(caller);

    /// <summary>
    /// Model rotator — covers workshop zombie packs (S2ZE, NOZB1) plus the vanilla heavy
    /// variants and a sampling of every base T/CT agent family. Pick one, see if the model
    /// renders and weapons attach correctly, then report back which ones work.
    /// </summary>
    private void OpenModelMenu(CCSPlayerController? caller)
    {
        if (caller is null || !caller.IsValid || Host is null) return;

        var paths = new[]
        {
            // Reset
            ("[Reset] Default (respawn to apply)", "default"),
            // Workshop zombie packs (purpose-built zombie models, mounted via MAM)
            ("[Workshop] S2ZE  zombie_frozen", "characters/models/s2ze/zombie_frozen/zombie_frozen.vmdl"),
            ("[Workshop] S2ZE  chris_walker",  "characters/models/s2ze/chris_walker/chris_walker.vmdl"),
            ("[Workshop] NOZB1 zombie_officer", "characters/models/nozb1/zombie_officer_player_model/zombie_officer_player_model.vmdl"),
            // Vanilla "heavy" agents — supposedly the safest swaps per CSSharp plugin lore
            ("[Vanilla] ctm_heavy",          "characters/models/ctm_heavy/ctm_heavy.vmdl"),
            ("[Vanilla] tm_phoenix_heavy",   "characters/models/tm_phoenix_heavy/tm_phoenix_heavy.vmdl"),
            // T-side base + variants
            ("[Vanilla T] tm_phoenix (base)",          "characters/models/tm_phoenix/tm_phoenix.vmdl"),
            ("[Vanilla T] tm_phoenix_variantg",        "characters/models/tm_phoenix/tm_phoenix_variantg.vmdl"),
            ("[Vanilla T] tm_balkan_variantf",         "characters/models/tm_balkan/tm_balkan_variantf.vmdl"),
            ("[Vanilla T] tm_leet_varianta",           "characters/models/tm_leet/tm_leet_varianta.vmdl"),
            ("[Vanilla T] tm_jumpsuit_varianta",       "characters/models/tm_jumpsuit/tm_jumpsuit_varianta.vmdl"),
            ("[Vanilla T] tm_jungle_raider_varianta",  "characters/models/tm_jungle_raider/tm_jungle_raider_varianta.vmdl"),
            // CT-side base + variants
            ("[Vanilla CT] ctm_sas (base)",           "characters/models/ctm_sas/ctm_sas.vmdl"),
            ("[Vanilla CT] ctm_sas_variantf",         "characters/models/ctm_sas/ctm_sas_variantf.vmdl"),
            ("[Vanilla CT] ctm_fbi (base)",           "characters/models/ctm_fbi/ctm_fbi.vmdl"),
            ("[Vanilla CT] ctm_st6_variantg",         "characters/models/ctm_st6/ctm_st6_variantg.vmdl"),
            ("[Vanilla CT] ctm_swat_variante",        "characters/models/ctm_swat/ctm_swat_variante.vmdl"),
            ("[Vanilla CT] ctm_diver_varianta",       "characters/models/ctm_diver/ctm_diver_varianta.vmdl"),
        };

        var menu = new WasdMenu("Model rotator — pick one to try", Host);
        foreach (var (label, path) in paths)
        {
            var capturedPath = path;
            menu.AddItem(label, (client, _) =>
            {
                if (capturedPath == "default")
                {
                    client.PrintToChat($" \x04[ZombieMod]\x01 Default — die + respawn to reset.");
                    return;
                }
                var pawn = client.PlayerPawn.Value;
                if (pawn is null) return;
                // Spawn-order-safe SetModel: defer one frame so the entity is settled.
                Server.NextFrame(() =>
                {
                    if (!pawn.IsValid) return;
                    pawn.SetModel(capturedPath);
                    client.PrintToChat($" \x04[ZombieMod]\x01 Model: {capturedPath}");
                });
            });
        }
        menu.Display(caller, 0);
    }
}

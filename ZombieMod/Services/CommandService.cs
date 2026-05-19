using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
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

    public CommandService(
        ILogger logger,
        ConfigService config,
        InfectionService infection,
        RespawnService respawn,
        ClassService classes,
        TeleportService teleport,
        WeaponService weapons)
    {
        _logger = logger;
        _config = config;
        _infection = infection;
        _respawn = respawn;
        _classes = classes;
        _teleport = teleport;
        _weapons = weapons;
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
}

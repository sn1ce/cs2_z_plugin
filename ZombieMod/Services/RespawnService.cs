using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

namespace ZombieMod.Services;

public sealed class RespawnService
{
    private readonly ILogger _logger;
    private readonly ConfigService _config;
    private readonly InfectionService _infection;

    internal BasePlugin? Host { get; set; }

    public RespawnService(ILogger logger, ConfigService config, InfectionService infection)
    {
        _logger = logger;
        _config = config;
        _infection = infection;
    }

    /// <summary>Schedules a respawn after <c>RespawnDelay</c>. No-op if <c>RespawnEnable=false</c>.</summary>
    public void ScheduleRespawn(CCSPlayerController client)
    {
        if (!_config.GameSettings.RespawnEnable) return;
        if (!client.IsValid) return;

        var delay = MathF.Max(0.1f, _config.GameSettings.RespawnDelay);
        var slot = client.Slot;

        if (Host is null)
        {
            _logger.LogError("[Respawn] Host plugin not set; cannot schedule.");
            return;
        }

        Host.AddTimer(delay, () =>
        {
            var fresh = CounterStrikeSharp.API.Utilities.GetPlayerFromSlot(slot);
            if (fresh is null || !fresh.IsValid) return;
            Respawn(fresh);
        });
    }

    public void Respawn(CCSPlayerController client)
    {
        if (!client.IsValid || client.PawnIsAlive) return;
        if (client.Team is CsTeam.Spectator or CsTeam.None) return;
        if (!_config.GameSettings.RespawnEnable) return;

        client.Respawn();
    }

    /// <summary>Called from EventPlayerTeam when a mid-round player joins T or CT.</summary>
    public void OnPlayerJoinedTeam(CCSPlayerController client)
    {
        if (!_config.GameSettings.AllowRespawnJoinLate) return;
        if (!client.IsValid) return;
        if (Host is null) return;
        if (client.Team is CsTeam.Spectator or CsTeam.None) return;

        // Tiny delay so CS2 finishes the team-change side effects before we respawn.
        Host.AddTimer(1.0f, () =>
        {
            var fresh = CounterStrikeSharp.API.Utilities.GetPlayerFromSlot(client.Slot);
            if (fresh is null || !fresh.IsValid || fresh.PawnIsAlive) return;
            Respawn(fresh);
        });
    }

    /// <summary>
    /// Resolve which team/state a freshly-spawned player should be in, given
    /// <c>RespawnTeam</c> policy and current infection state.
    /// </summary>
    public PostSpawnAction ResolvePostSpawnAction(CCSPlayerController client)
    {
        if (!_infection.InfectionStarted) return PostSpawnAction.Humanize;

        var pre = _infection.GetState(client);
        return _config.GameSettings.RespawnTeam switch
        {
            0 => PostSpawnAction.Infect,
            1 => PostSpawnAction.Humanize,
            2 => (pre is { IsInfected: true }) ? PostSpawnAction.Infect : PostSpawnAction.Humanize,
            _ => PostSpawnAction.Infect,
        };
    }

    public enum PostSpawnAction { Infect, Humanize }
}

using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

namespace ZombieMod.Services;

public sealed class TeleportService
{
    private readonly ILogger _logger;
    private readonly ConfigService _config;
    private readonly InfectionService _infection;

    public TeleportService(ILogger logger, ConfigService config, InfectionService infection)
    {
        _logger = logger;
        _config = config;
        _infection = infection;
    }

    /// <summary>Capture the player's spawn position on first spawn of the round.</summary>
    public void OnPlayerSpawn(CCSPlayerController client)
    {
        if (!client.IsValid || !client.PawnIsAlive) return;
        var pawn = client.PlayerPawn.Value;
        if (pawn is null) return;

        var state = _infection.GetState(client);
        if (state is null) return;

        if (state.SpawnPosition is null)
        {
            var origin = pawn.AbsOrigin;
            var angle = pawn.AbsRotation;
            if (origin is not null) state.SpawnPosition = new Vector(origin.X, origin.Y, origin.Z);
            if (angle is not null)  state.SpawnAngle    = new QAngle(angle.X, angle.Y, angle.Z);
        }
    }

    public bool TryTeleport(CCSPlayerController client, out string? denyReason)
    {
        denyReason = null;

        if (!_config.GameSettings.TeleportAllow)
        {
            denyReason = "Teleport is disabled on this server.";
            return false;
        }
        if (!client.IsValid || !client.PawnIsAlive)
        {
            denyReason = "You must be alive.";
            return false;
        }

        var state = _infection.GetState(client);
        if (state is null) { denyReason = "No state."; return false; }

        var maxUses = _config.GameSettings.TeleportUsesPerRound;
        if (maxUses > 0 && state.TeleportsUsedThisRound >= maxUses)
        {
            denyReason = $"You have used all {maxUses} teleport(s) this round.";
            return false;
        }

        var cooldown = _config.GameSettings.TeleportCooldownSeconds;
        var elapsed = (DateTime.UtcNow - state.LastTeleportAt).TotalSeconds;
        if (cooldown > 0 && elapsed < cooldown)
        {
            var remaining = (int)Math.Ceiling(cooldown - elapsed);
            denyReason = $"Teleport on cooldown — {remaining}s remaining.";
            return false;
        }

        if (state.SpawnPosition is null || state.SpawnAngle is null)
        {
            denyReason = "No spawn position recorded.";
            return false;
        }

        var pawn = client.PlayerPawn.Value;
        if (pawn is null) { denyReason = "No pawn."; return false; }

        pawn.Teleport(state.SpawnPosition, state.SpawnAngle, new Vector(0, 0, 0));
        state.TeleportsUsedThisRound++;
        state.LastTeleportAt = DateTime.UtcNow;
        return true;
    }

    public void ResetRound()
    {
        foreach (var state in _infection.Players.Values)
        {
            state.TeleportsUsedThisRound = 0;
            state.LastTeleportAt = DateTime.MinValue;
            state.SpawnPosition = null;
            state.SpawnAngle = null;
        }
    }

    /// <summary>Teleport to spawn, bypassing the per-round uses + cooldown limits. For unstuck.</summary>
    public bool UnstuckClient(CCSPlayerController client, out string? denyReason)
    {
        denyReason = null;
        if (!client.IsValid || !client.PawnIsAlive) { denyReason = "You must be alive."; return false; }
        var state = _infection.GetState(client);
        if (state?.SpawnPosition is null || state.SpawnAngle is null)
        {
            denyReason = "No spawn position recorded yet — try after the next round start.";
            return false;
        }
        var pawn = client.PlayerPawn.Value;
        if (pawn is null) { denyReason = "No pawn."; return false; }
        pawn.Teleport(state.SpawnPosition, state.SpawnAngle, new Vector(0, 0, 0));
        return true;
    }
}

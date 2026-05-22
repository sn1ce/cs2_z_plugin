using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

namespace ZombieMod.Services;

public sealed class TeleportService
{
    private readonly ILogger _logger;
    private readonly ConfigService _config;
    private readonly InfectionService _infection;

    /// <summary>Plugin reference required for scheduling the delayed teleport via AddTimer.</summary>
    internal BasePlugin? Host { get; set; }

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

    /// <summary>
    /// Schedules a teleport to the captured spawn position. Returns false (with reason) if any
    /// precondition fails. Returns true after queueing — the actual teleport fires after a
    /// 5-second delay. The cooldown + per-round-uses are consumed at queue time so a player
    /// can't spam !ztele during the wait.
    /// </summary>
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

        // Consume use + start cooldown now, so spamming !ztele during the wait is blocked.
        state.TeleportsUsedThisRound++;
        state.LastTeleportAt = DateTime.UtcNow;

        // Capture the destination (and slot) now so a player moving mid-wait doesn't shift it.
        var spawnPos = state.SpawnPosition;
        var spawnAng = state.SpawnAngle;
        var slot     = client.Slot;

        client.PrintToChat(" \x04[ZombieMod]\x01 Teleporting in \x076\x01 seconds…");

        Host?.AddTimer(5.0f, () =>
        {
            var fresh = Utilities.GetPlayerFromSlot(slot);
            if (fresh is null || !fresh.IsValid || !fresh.PawnIsAlive) return;
            var pawn = fresh.PlayerPawn.Value;
            if (pawn is null) return;
            pawn.Teleport(spawnPos, spawnAng, new Vector(0, 0, 0));
            fresh.PrintToChat(" \x04[ZombieMod]\x01 Teleported to spawn.");
        });

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

using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

namespace ZombieMod.Services;

/// <summary>
/// Per-player "flashlight" implemented as a <c>light_dynamic</c> entity parented to the
/// player's pawn. Not a true projected-texture cone with shadows — more like carrying a
/// lantern — but functional in dark workshop maps. Each player can toggle their own light
/// independently via the <c>!flashlight</c> chat command.
///
/// Lifetime: light entities are destroyed on death, disconnect, and round start, so we
/// never leak entities between rounds.
/// </summary>
public sealed class FlashlightService
{
    private readonly ILogger _logger;
    private readonly Dictionary<int, uint> _activeLights = new();   // slot → entity index

    internal BasePlugin? Host { get; set; }

    public FlashlightService(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>Toggle the calling player's flashlight. Returns true if it's now ON, false if OFF
    /// (or if the toggle was rejected — pawn dead, etc).</summary>
    public bool Toggle(CCSPlayerController client)
    {
        if (!client.IsValid || !client.PawnIsAlive) return false;

        if (_activeLights.TryGetValue(client.Slot, out var existingIdx))
        {
            DestroyById(existingIdx);
            _activeLights.Remove(client.Slot);
            return false;
        }

        var pawn = client.PlayerPawn.Value;
        if (pawn is null || !pawn.IsValid) return false;

        try
        {
            var light = Utilities.CreateEntityByName<CBaseEntity>("light_dynamic");
            if (light is null) return false;

            // light_dynamic supports keyvalues for color/range/brightness/cone angles.
            // Set via the generic entity-keyvalue path (AcceptInput "SetParent" handles parenting).
            // Defaults give a warm, ~350u radius soft light.

            var pos = pawn.AbsOrigin;
            var ang = pawn.EyeAngles;
            if (pos is null) return false;

            // Spawn just above + in front of the player's center so the cone projects forward.
            var spawnPos = new Vector(pos.X, pos.Y, pos.Z + 60);
            light.Teleport(spawnPos, ang ?? new QAngle(), new Vector());
            light.DispatchSpawn();

            // Parent to the pawn so the light follows the player as they move.
            // The light won't track pitch/yaw exactly (it stays at the spawn-time orientation),
            // but for "lantern in your hand" effect that's fine.
            light.AcceptInput("SetParent", pawn, client, "!activator");
            light.AcceptInput("TurnOn");

            _activeLights[client.Slot] = light.Index;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Flashlight] Toggle failed for {Name}", client.PlayerName);
            return false;
        }
    }

    /// <summary>Drop the light for a single slot (death / disconnect).</summary>
    public void Cleanup(int slot)
    {
        if (_activeLights.TryGetValue(slot, out var idx))
        {
            DestroyById(idx);
            _activeLights.Remove(slot);
        }
    }

    /// <summary>Drop every active light (round start / map change).</summary>
    public void CleanupAll()
    {
        foreach (var idx in _activeLights.Values.ToList())
            DestroyById(idx);
        _activeLights.Clear();
    }

    private void DestroyById(uint entityIdx)
    {
        try
        {
            var ent = Utilities.GetEntityFromIndex<CBaseEntity>((int)entityIdx);
            if (ent is { IsValid: true })
                ent.AddEntityIOEvent("Kill", ent, null, "", 0.1f);
        }
        catch { /* entity already gone */ }
    }
}

using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using ZombieMod.Config;

namespace ZombieMod.Services;

/// <summary>
/// Spawnable physics props paid for with in-game cash. Each player's spawned props are
/// tracked by entity index so we can clean up on disconnect / round-end / map change.
/// </summary>
public sealed class PropService
{
    private readonly ILogger _logger;
    private readonly ConfigService _config;

    // slot → list of spawned entity indices (CBaseEntity.Index)
    private readonly Dictionary<int, List<int>> _owned = new();

    public PropService(ILogger logger, ConfigService config)
    {
        _logger = logger;
        _config = config;
    }

    public bool TrySpawn(CCSPlayerController client, string key, out string? denyReason)
    {
        denyReason = null;
        if (!client.IsValid || !client.PawnIsAlive)
        {
            denyReason = "You must be alive to spawn props.";
            return false;
        }
        if (!_config.Props.TryGetValue(key, out var prop))
        {
            denyReason = $"Unknown prop '{key}'.";
            return false;
        }

        var account = client.InGameMoneyServices?.Account ?? 0;
        if (account < prop.Cost)
        {
            denyReason = $"Need ${prop.Cost - account} more for {prop.Name}.";
            return false;
        }

        var pawn = client.PlayerPawn.Value;
        if (pawn is null)
        {
            denyReason = "No pawn.";
            return false;
        }

        var origin = pawn.AbsOrigin;
        var viewOffset = pawn.ViewOffset;
        var eyeAng = pawn.EyeAngles;
        if (origin is null)
        {
            denyReason = "No origin.";
            return false;
        }

        // Forward vector from EyeAngles (CS2 angles in degrees, x=pitch, y=yaw).
        var pitchRad = eyeAng.X * MathF.PI / 180f;
        var yawRad   = eyeAng.Y * MathF.PI / 180f;
        var cp = MathF.Cos(pitchRad);
        var fx = cp * MathF.Cos(yawRad);
        var fy = cp * MathF.Sin(yawRad);
        var fz = -MathF.Sin(pitchRad);

        const float dist = 80f;
        var spawnPos = new Vector(
            origin.X + viewOffset.X + fx * dist,
            origin.Y + viewOffset.Y + fy * dist,
            origin.Z + viewOffset.Z + fz * dist);

        try
        {
            // Spawn order matters: CS2's model resolver requires the entity to be fully spawned
            // before SetModel is called (CS2-Parachute / CS2PropHunt pattern). Reversing the
            // order or using `prop_physics_override` instead of `_multiplayer` renders ERROR.
            var entity = Utilities.CreateEntityByName<CPhysicsPropMultiplayer>("prop_physics_multiplayer");
            if (entity is null)
            {
                denyReason = "Engine refused to create the prop.";
                return false;
            }
            entity.DispatchSpawn();
            entity.Teleport(spawnPos, new QAngle(), new Vector());
            entity.SetModel(prop.Model);

            // Deduct cost
            if (client.InGameMoneyServices is not null)
            {
                client.InGameMoneyServices.Account -= prop.Cost;
                Utilities.SetStateChanged(client, "CCSPlayerController", "m_pInGameMoneyServices");
            }

            if (!_owned.TryGetValue(client.Slot, out var list))
                list = _owned[client.Slot] = new List<int>();
            list.Add((int)entity.Index);

            _logger.LogInformation(
                "[Props] {Name} spawned {Prop} (#{Idx}) for ${Cost}, owned-count={N}",
                client.PlayerName, prop.Name, entity.Index, prop.Cost, list.Count);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Props] Spawn of {Prop} failed for {Name}", prop.Model, client.PlayerName);
            denyReason = "Spawn failed (see server log).";
            return false;
        }
    }

    public void CleanupForSlot(int slot)
    {
        if (!_owned.TryGetValue(slot, out var list)) return;
        foreach (var idx in list)
        {
            var ent = Utilities.GetEntityFromIndex<CBaseEntity>(idx);
            if (ent is { IsValid: true })
            {
                try { ent.AddEntityIOEvent("Kill", ent, null, "", 0.1f); } catch { }
            }
        }
        _owned.Remove(slot);
    }

    public void CleanupAll()
    {
        foreach (var slot in _owned.Keys.ToList())
            CleanupForSlot(slot);
    }
}

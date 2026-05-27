using System.Drawing;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using Vector = CounterStrikeSharp.API.Modules.Utils.Vector;

namespace ZombieMod.Services;

/// <summary>
/// Per-player flashlight using <c>light_omni2</c> (typed as <c>COmniLight</c>).
///
/// Implementation cribbed from creazy231/cs2-css-flashlight (the working community plugin).
/// Critical details that "obvious" attempts miss:
///   - <c>DirectLight = 3</c> on the COmniLight — without this the entity emits nothing
///   - Set all properties (Color, Brightness, Range, OuterAngle, ColorTemperature, Enabled)
///     BEFORE <c>DispatchSpawn</c> so they take effect on init
///   - Use <c>pawn.V_angle</c> not <c>EyeAngles</c> for proper view direction
///   - Do NOT parent the light; re-Teleport it every tick to follow the player. Parenting
///     loses tracking of pitch/yaw and produces weird offset behavior.
/// </summary>
public sealed class FlashlightService
{
    private readonly ILogger _logger;
    private readonly Dictionary<int, COmniLight> _entities = new();   // slot → live entity
    private readonly HashSet<int> _wantOn = new();                    // slots currently wanting on
    private readonly Dictionary<int, bool> _lastUseHeld = new();       // slot → previous-tick Use state
    private readonly Dictionary<int, FlickerState> _flicker = new();   // slot → flicker bookkeeping
    private readonly Random _rng = new();

    /// <summary>Per-player flicker bookkeeping. We start a brief stutter every 6–14s on
    /// a random schedule per player so multiple players' flickers don't sync up.</summary>
    private sealed class FlickerState
    {
        public DateTime NextStartAt;   // when the next flicker stutter is allowed to start
        public DateTime EndAt;         // when the current flicker stutter ends
        public bool Active => DateTime.UtcNow < EndAt;
    }

    internal BasePlugin? Host { get; set; }

    public FlashlightService(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>Toggle the flashlight. Returns true if it's now ON, false if OFF.</summary>
    public bool Toggle(CCSPlayerController client)
    {
        if (!client.IsValid || !client.PawnIsAlive) return false;
        var slot = client.Slot;
        if (_wantOn.Contains(slot))
        {
            _wantOn.Remove(slot);
            Destroy(slot);
            return false;
        }
        _wantOn.Add(slot);
        return true;
    }

    /// <summary>
    /// Called from the plugin's OnTick listener. Walks every player with flashlight ON
    /// and re-positions their light to follow their head + view direction. Creates the
    /// entity lazily on first tick.
    /// </summary>
    public void Tick()
    {
        // +use edge detection — toggle once per E-press, no strobe while held.
        // Runs every tick across all live human players, even ones without an active light.
        // We intentionally do NOT rebind the E key: the engine still does its normal +use
        // (pick up weapon, etc.); we just read the button state. The conflict is acceptable —
        // matches the reference plugin's behavior and the user explicitly asked for it.
        foreach (var p in Utilities.GetPlayers())
        {
            if (p is null || !p.IsValid || p.IsBot || p.IsHLTV || !p.PawnIsAlive) continue;
            var currentlyHeld = (p.Buttons & PlayerButtons.Use) != 0;
            var wasHeld = _lastUseHeld.GetValueOrDefault(p.Slot, false);
            _lastUseHeld[p.Slot] = currentlyHeld;
            if (currentlyHeld && !wasHeld) // rising edge
                Toggle(p);
        }

        if (_wantOn.Count == 0) return;

        foreach (var slot in _wantOn.ToList())
        {
            var client = Utilities.GetPlayerFromSlot(slot);
            if (client is null || !client.IsValid || !client.PawnIsAlive)
            {
                Destroy(slot);
                _wantOn.Remove(slot);
                continue;
            }
            var pawn = client.PlayerPawn.Value;
            if (pawn?.AbsOrigin is null || pawn.V_angle is null) continue;

            COmniLight? entity;
            if (_entities.TryGetValue(slot, out var existing) && existing.IsValid)
            {
                entity = existing;
            }
            else
            {
                entity = Utilities.CreateEntityByName<COmniLight>("light_omni2");
                if (entity is null || !entity.IsValid)
                {
                    _logger.LogWarning("[Flashlight] light_omni2 create failed for {Name}", client.PlayerName);
                    _wantOn.Remove(slot);
                    continue;
                }
            }

            // DirectLight=3 is the magic — without it the entity exists but emits nothing.
            entity.DirectLight = 3;

            // Position the light FORWARD of the view-model so the player's own weapon doesn't
            // get washed out by the cone. Compute forward unit vector from V_angle, push the
            // origin ~60u along it. Head height = AbsOrigin.Z + 64.03 (matches reference plugin).
            const float forwardOffset = 60f;
            var pitchRad = pawn.V_angle.X * MathF.PI / 180f;
            var yawRad   = pawn.V_angle.Y * MathF.PI / 180f;
            var cp = MathF.Cos(pitchRad);
            var fx = cp * MathF.Cos(yawRad);
            var fy = cp * MathF.Sin(yawRad);
            var fz = -MathF.Sin(pitchRad);

            entity.Teleport(
                new Vector(
                    pawn.AbsOrigin.X + fx * forwardOffset,
                    pawn.AbsOrigin.Y + fy * forwardOffset,
                    pawn.AbsOrigin.Z + 64.03f + fz * forwardOffset),
                pawn.V_angle,
                pawn.AbsVelocity);

            entity.OuterAngle = 45f;
            entity.Color = Color.White;
            entity.ColorTemperature = 6500;
            entity.Brightness = 1f;
            entity.Range = 5000f;

            // Horror-flick: every 6–14s start a ~400ms stutter; during it, drive Enabled via
            // a sin wave at ~20 Hz so it reads as a "blip-blip-blip" flicker rather than a
            // single off-period. Stable between stutters.
            var st = _flicker.GetValueOrDefault(slot) ?? new FlickerState
            {
                NextStartAt = DateTime.UtcNow.AddSeconds(2 + _rng.NextDouble() * 8),
                EndAt = DateTime.MinValue,
            };
            _flicker[slot] = st;

            if (st.Active)
            {
                var phase = (DateTime.UtcNow - st.EndAt.AddMilliseconds(-400)).TotalSeconds;
                entity.Enabled = Math.Sin(phase * 60.0) > -0.3;
            }
            else
            {
                entity.Enabled = true;
                if (DateTime.UtcNow >= st.NextStartAt)
                {
                    st.EndAt = DateTime.UtcNow.AddMilliseconds(300 + _rng.Next(200));   // 300-500ms stutter
                    st.NextStartAt = st.EndAt.AddSeconds(6 + _rng.NextDouble() * 8);    // 6-14s gap
                }
            }

            // DispatchSpawn every tick — that's what the working reference does. On an already-
            // spawned entity it's effectively a no-op + property re-apply.
            entity.DispatchSpawn();

            _entities[slot] = entity;
        }
    }

    public void Cleanup(int slot)
    {
        _wantOn.Remove(slot);
        _lastUseHeld.Remove(slot);
        _flicker.Remove(slot);
        Destroy(slot);
    }

    public void CleanupAll()
    {
        foreach (var slot in _wantOn.ToList())
            Destroy(slot);
        _wantOn.Clear();
        _lastUseHeld.Clear();
        _flicker.Clear();
    }

    private void Destroy(int slot)
    {
        if (_entities.TryGetValue(slot, out var ent))
        {
            try { if (ent.IsValid) ent.Remove(); } catch { }
            _entities.Remove(slot);
        }
    }
}

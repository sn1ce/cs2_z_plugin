using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;
using Microsoft.Extensions.Logging;
using ZombieMod.Config;
using CsTimer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace ZombieMod.Services;

public sealed class ClassService
{
    private readonly ILogger _logger;
    private readonly ConfigService _config;
    private readonly InfectionService _infection;

    private readonly Dictionary<int, CsTimer> _regenTimers = new();

    internal BasePlugin? Host { get; set; }

    public ClassService(ILogger logger, ConfigService config, InfectionService infection)
    {
        _logger = logger;
        _config = config;
        _infection = infection;
    }

    public ClassConfig? GetClass(string id)
        => _config.Classes.TryGetValue(id, out var cls) ? cls : null;

    public ClassConfig? GetActiveClass(CCSPlayerController client)
        => _infection.GetState(client)?.ActiveClass;

    public void ApplyClass(CCSPlayerController client, ClassConfig cls)
    {
        if (!client.IsValid)
        {
            _logger.LogError("[Class] ApplyClass called with invalid client.");
            return;
        }
        var pawn = client.PlayerPawn.Value;
        if (pawn is null)
        {
            _logger.LogWarning("[Class] {Name} has no pawn yet; deferring apply.", client.PlayerName);
            return;
        }

        var slot = client.Slot;
        StopRegenFor(slot);

        // Model + armor: defer one frame so entity props are settled.
        Server.NextFrame(() =>
        {
            if (!pawn.IsValid) return;

            if (!string.IsNullOrEmpty(cls.Model) && cls.Model != "default")
            {
                pawn.SetModel(cls.Model);
            }
            else
            {
                pawn.SetModel(cls.Team == 0
                    ? "characters/models/tm_phoenix/tm_phoenix.vmdl"
                    : "characters/models/ctm_sas/ctm_sas.vmdl");
            }

            if (cls.Team == 0)
            {
                pawn.ArmorValue = 0;
                client.PawnHasHelmet = false;
            }
        });

        // Health: spawn code clobbers health at t=0, so apply after a beat.
        Host?.AddTimer(0.3f, () =>
        {
            var fresh = Utilities.GetPlayerFromSlot(slot);
            if (fresh is null || !fresh.IsValid || !fresh.PawnIsAlive) return;
            var freshPawn = fresh.PlayerPawn.Value;
            if (freshPawn is null) return;

            freshPawn.Health = cls.Health;
            Utilities.SetStateChanged(freshPawn, "CBaseEntity", "m_iHealth");
        });

        // Speed: gated — CSSharp limitation per ZombieSharp.
        if (_config.GameSettings.EnableClassSpeed)
            pawn.VelocityModifier = cls.Speed / 250f;

        // Regen + napalm — driven from class config.
        if (cls.Regen_Interval > 0 && cls.Regen_Amount > 0)
            StartRegenFor(slot, cls);

        var state = _infection.GetState(client);
        if (state is not null)
        {
            state.ActiveClass = cls;
            state.NapalmExpiresAt = cls.NapalmTime > 0
                ? DateTime.UtcNow.AddSeconds(cls.NapalmTime)
                : null;
        }
    }

    public void OnPlayerDeath(CCSPlayerController client) => StopRegenFor(client.Slot);
    public void OnPlayerDisconnect(int slot) => StopRegenFor(slot);

    /// <summary>
    /// Some maps / mp_freezetime_end events reset velocity. Re-apply velocity modifier 0.5s after
    /// hurt so movement stays in line with the class spec.
    /// </summary>
    public void OnPlayerHurt(CCSPlayerController client)
    {
        if (Host is null || !_config.GameSettings.EnableClassSpeed) return;
        var slot = client.Slot;
        Host.AddTimer(0.5f, () =>
        {
            var fresh = Utilities.GetPlayerFromSlot(slot);
            if (fresh is null || !fresh.IsValid || !fresh.PawnIsAlive) return;
            var pawn = fresh.PlayerPawn.Value;
            var cls = _infection.GetState(fresh)?.ActiveClass;
            if (pawn is null || cls is null) return;
            if (Math.Abs(cls.Speed - 250f) < 0.01f) return;
            pawn.VelocityModifier = cls.Speed / 250f;
        });
    }

    private void StartRegenFor(int slot, ClassConfig cls)
    {
        if (Host is null) return;
        var interval = MathF.Max(0.1f, cls.Regen_Interval);
        var amount = cls.Regen_Amount;
        var maxHealth = cls.Health;

        var timer = Host.AddTimer(interval, () =>
        {
            var p = Utilities.GetPlayerFromSlot(slot);
            if (p is null || !p.IsValid || !p.PawnIsAlive)
            {
                StopRegenFor(slot);
                return;
            }
            var pawn = p.PlayerPawn.Value;
            if (pawn is null) return;
            if (pawn.Health >= maxHealth) return;

            pawn.Health = Math.Min(pawn.Health + amount, maxHealth);
            Utilities.SetStateChanged(pawn, "CBaseEntity", "m_iHealth");
        }, TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE);

        _regenTimers[slot] = timer;
    }

    private void StopRegenFor(int slot)
    {
        if (_regenTimers.TryGetValue(slot, out var t))
        {
            t.Kill();
            _regenTimers.Remove(slot);
        }
    }
}

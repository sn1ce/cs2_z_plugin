using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using ZombieMod.Util;

namespace ZombieMod.Services;

public sealed class KnockbackService
{
    private readonly ILogger _logger;
    private readonly ConfigService _config;
    private readonly KnockbackProviderDetector _provider;
    private readonly InfectionService _infection;

    public KnockbackService(
        ILogger logger,
        ConfigService config,
        KnockbackProviderDetector provider,
        InfectionService infection)
    {
        _logger = logger;
        _config = config;
        _provider = provider;
        _infection = infection;
    }

    /// <summary>
    /// Apply knockback to a zombie from a hurt event. Fails closed (no-op) without a provider.
    /// </summary>
    public void ApplyHurtKnockback(
        CCSPlayerController victim,
        CCSPlayerController attacker,
        string weaponEntityName,
        float damageHealth,
        int hitGroup)
    {
        if (!_provider.Available) return;
        if (weaponEntityName.Contains("hegrenade", StringComparison.OrdinalIgnoreCase)) return;
        if (victim is null || attacker is null) return;
        if (!victim.IsValid || !attacker.IsValid) return;
        if (attacker.DesignerName != "cs_player_controller") return;

        // Only push infected being attacked by survivors.
        if (!_infection.IsClientSurvivor(attacker) || !_infection.IsClientInfected(victim)) return;

        var victimPawn = victim.PlayerPawn.Value;
        var attackerPawn = attacker.PlayerPawn.Value;
        if (victimPawn is null || attackerPawn is null) return;

        var victimPos = victimPawn.AbsOrigin;
        var attackerPos = attackerPawn.AbsOrigin;
        if (victimPos is null || attackerPos is null) return;

        var classKb = _infection.GetState(victim)?.ActiveClass?.Knockback ?? 1.0f;
        var weaponKb = ResolveWeaponKnockback(attacker, weaponEntityName);
        var hitgroupKb = _config.HitgroupsByIndex.TryGetValue(hitGroup, out var hg) ? hg.Knockback : 1.0f;

        var (dx, dy, dz) = (victimPos.X - attackerPos.X, victimPos.Y - attackerPos.Y, victimPos.Z - attackerPos.Z);
        var magnitude = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
        if (magnitude < 1e-3f) return;

        var scale = damageHealth * classKb * weaponKb * hitgroupKb / magnitude;
        var push = new Vector(dx * scale, dy * scale, dz * scale);
        victimPawn.AbsVelocity.Add(push);
    }

    public void ApplyExplosionKnockback(CCSPlayerController victim, CBaseEntity grenade, float damageHealth)
    {
        if (!_provider.Available) return;
        if (victim is null || grenade is null || !victim.IsValid) return;
        if (!_infection.IsClientInfected(victim)) return;

        var victimPawn = victim.PlayerPawn.Value;
        if (victimPawn is null) return;
        var victimPos = victimPawn.AbsOrigin;
        var grenadePos = grenade.AbsOrigin;
        if (victimPos is null || grenadePos is null) return;

        var classKb = _infection.GetState(victim)?.ActiveClass?.Knockback ?? 1.0f;
        var weaponKb = _config.WeaponsByEntity.TryGetValue("weapon_hegrenade", out var w) ? w.Knockback : 1.0f;

        var (dx, dy, dz) = (victimPos.X - grenadePos.X, victimPos.Y - grenadePos.Y, victimPos.Z - grenadePos.Z);
        var magnitude = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
        if (magnitude < 1e-3f) return;

        var scale = damageHealth * classKb * weaponKb / magnitude;
        var push = new Vector(dx * scale, dy * scale, dz * scale);
        victimPawn.AbsVelocity.Add(push);
    }

    private float ResolveWeaponKnockback(CCSPlayerController attacker, string weaponEntityName)
    {
        // Knife events report "knife" or "knife_t" rather than a specific entity slot, so prefer
        // the event-supplied name there. Otherwise look up the currently-equipped weapon.
        if (weaponEntityName.Contains("knife", StringComparison.OrdinalIgnoreCase))
        {
            if (_config.WeaponsByEntity.TryGetValue("weapon_knife", out var knifeCfg))
                return knifeCfg.Knockback;
            return 1.0f;
        }

        var activeWeapon = attacker.PlayerPawn.Value?.WeaponServices?.ActiveWeapon?.Value;
        var designerName = activeWeapon?.DesignerName;
        if (!string.IsNullOrEmpty(designerName) &&
            _config.WeaponsByEntity.TryGetValue(designerName, out var cfg))
            return cfg.Knockback;

        // Fall back to the event-supplied weapon string.
        var prefixed = weaponEntityName.StartsWith("weapon_")
            ? weaponEntityName
            : "weapon_" + weaponEntityName;
        if (_config.WeaponsByEntity.TryGetValue(prefixed, out var byName))
            return byName.Knockback;

        return 1.0f;
    }
}

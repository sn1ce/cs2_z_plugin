using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using ZombieMod.Config;

namespace ZombieMod.Services;

public sealed class WeaponService
{
    private readonly ILogger _logger;
    private readonly ConfigService _config;
    private readonly InfectionService _infection;

    internal BasePlugin? Host { get; set; }

    public WeaponService(ILogger logger, ConfigService config, InfectionService infection)
    {
        _logger = logger;
        _config = config;
        _infection = infection;
    }

    public WeaponConfig? FindByEntity(string entityName)
        => _config.WeaponsByEntity.TryGetValue(entityName, out var w) ? w : null;

    /// <summary>
    /// Keeps every alive player's guns topped to their pinned reserve (2 magazines by default,
    /// or <c>cfg.Reserve</c> if set). Reserve ammo only ever decreases on reload, so re-topping
    /// here means spare ammo effectively never drains to 0 while still showing a finite "2 spare
    /// mags" — no sv_infinite_ammo and no sv_cheats required. Called on a throttle from OnTick;
    /// the "only write when below target" guard makes it a cheap no-op except right after a reload.
    /// </summary>
    public void TickRefillReserves()
    {
        foreach (var player in Utilities.GetPlayers())
        {
            if (player is null || !player.IsValid || !player.PawnIsAlive) continue;
            if (player.Team is not (CsTeam.CounterTerrorist or CsTeam.Terrorist)) continue;
            var ws = player.PlayerPawn.Value?.WeaponServices;
            if (ws is null) continue;

            foreach (var handle in ws.MyWeapons)
            {
                if (handle.Value is not { IsValid: true }) continue;
                var w = new CCSWeaponBase(handle.Value.Handle);
                if (!w.IsValid || w.VData is null) continue;
                if (w.DesignerName.Contains("knife", StringComparison.OrdinalIgnoreCase)) continue;
                if (!_config.WeaponsByEntity.TryGetValue(w.DesignerName, out var cfg)) continue;

                var clipTarget = cfg.Clip > 0 ? cfg.Clip : w.VData.MaxClip1;
                var target = cfg.Reserve > 0 ? cfg.Reserve : clipTarget * 2;
                if (target <= 0) continue;

                if (w.VData.PrimaryReserveAmmoMax < target) w.VData.PrimaryReserveAmmoMax = target;
                if (w.ReserveAmmo[0] < target)
                {
                    w.ReserveAmmo[0] = target;
                    Utilities.SetStateChanged(w, "CBasePlayerWeapon", "m_pReserveAmmo");
                }
            }
        }
    }

    public bool IsRestricted(string entityName)
        => _config.WeaponsByEntity.TryGetValue(entityName, out var w) && w.Restrict;

    /// <summary>Iterates <c>weapons.json</c> and wires each <c>PurchaseCommand</c> alias as a CSSharp console command.</summary>
    public void RegisterPurchaseCommands()
    {
        if (Host is null)
        {
            _logger.LogError("[Weapons] Host plugin not set; cannot register purchase commands.");
            return;
        }

        var count = 0;
        foreach (var (shortName, weapon) in _config.Weapons)
        {
            if (weapon.PurchaseCommand is null) continue;
            foreach (var cmd in weapon.PurchaseCommand)
            {
                if (string.IsNullOrWhiteSpace(cmd)) continue;
                var captured = shortName;
                Host.AddCommand(cmd, $"Purchase {weapon.WeaponName}", (c, _) => HandlePurchase(c, captured));
                count++;
            }
        }
        _logger.LogInformation("[Weapons] Registered {Count} purchase commands across {Weapons} weapons.",
            count, _config.Weapons.Count);
    }

    public bool TryPurchase(CCSPlayerController client, string shortName)
        => HandlePurchase(client, shortName);

    private bool HandlePurchase(CCSPlayerController? client, string shortName)
    {
        if (client is null || !client.IsValid) return false;

        if (!_config.GameSettings.WeaponPurchaseEnable)
        {
            client.PrintToChat(" [ZombieMod] Weapon purchasing is disabled.");
            return false;
        }
        if (!client.PawnIsAlive)
        {
            client.PrintToChat(" [ZombieMod] You must be alive to purchase.");
            return false;
        }
        if (_infection.IsClientInfected(client))
        {
            client.PrintToChat(" [ZombieMod] Zombies cannot purchase weapons.");
            return false;
        }
        if (!_config.Weapons.TryGetValue(shortName, out var weapon))
        {
            _logger.LogWarning("[Weapons] Purchase command for unknown short name '{Name}'", shortName);
            return false;
        }
        if (weapon.Restrict)
        {
            client.PrintToChat($" [ZombieMod] {weapon.WeaponName} is restricted on this server.");
            return false;
        }
        if (_config.GameSettings.WeaponBuyZoneOnly && !(client.PlayerPawn.Value?.InBuyZone ?? false))
        {
            client.PrintToChat(" [ZombieMod] You must be in a buyzone to purchase.");
            return false;
        }

        var state = _infection.GetState(client);
        if (state is null) return false;

        state.PurchaseCounts.TryGetValue(shortName, out var used);
        if (weapon.MaxPurchase > 0 && used >= weapon.MaxPurchase)
        {
            client.PrintToChat($" [ZombieMod] Already purchased the max ({weapon.MaxPurchase}) of {weapon.WeaponName} this life.");
            return false;
        }

        client.GiveNamedItem(weapon.WeaponEntity);
        state.PurchaseCounts[shortName] = used + 1;
        client.PrintToChat($" [ZombieMod] Purchased {weapon.WeaponName}.");
        return true;
    }
}

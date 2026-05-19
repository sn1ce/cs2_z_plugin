using CounterStrikeSharp.API.Core;
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

using CounterStrikeSharp.API.Core;

namespace ZombieMod.Api;

/// <summary>
/// Public capability exposed by ZombieMod under the key <c>zombiemod:core</c>.
/// Downstream plugins reference <c>ZombieMod.Api.dll</c> only — never the plugin dll.
/// </summary>
public interface IZombieModAPI
{
    /// <summary>Fired before a client is infected. Return <see cref="HookResult.Stop"/> to cancel.</summary>
    event Func<CCSPlayerController, CCSPlayerController?, bool, bool, HookResult?>? OnClientInfect;

    /// <summary>Fired before a client is humanized. Return <see cref="HookResult.Stop"/> to cancel.</summary>
    event Func<CCSPlayerController, bool, HookResult?>? OnClientHumanize;

    /// <summary>Fired when the round's mother zombie set has been chosen but before infection runs.</summary>
    event Func<IReadOnlyList<CCSPlayerController>, HookResult?>? OnMotherZombieSelected;

    /// <summary>Fired at the start of a zombie round after freezetime ends and timers are armed.</summary>
    event Func<HookResult?>? OnZombieRoundStart;

    HookResult InfectClient(CCSPlayerController client, CCSPlayerController? attacker, bool motherZombie, bool force);
    void HumanizeClient(CCSPlayerController client, bool respawn);

    bool IsClientInfected(CCSPlayerController client);
    ZombieClass? GetClientClass(CCSPlayerController client);
}

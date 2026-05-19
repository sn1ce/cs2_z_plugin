using CounterStrikeSharp.API.Core;
using ZombieMod.Api;
using ZombieMod.Services;

namespace ZombieMod.ApiImpl;

/// <summary>
/// Concrete implementation of <see cref="IZombieModAPI"/>. Registered as a
/// <c>PluginCapability&lt;IZombieModAPI&gt;("zombiemod:core")</c> at plugin load.
///
/// Internal namespace is <c>ZombieMod.ApiImpl</c> so it does not collide with the
/// <c>ZombieMod.Api</c> assembly namespace consumed by downstream plugins.
/// </summary>
public sealed class ZombieModApi : IZombieModAPI
{
    private readonly InfectionService _infection;
    private readonly ClassService _classes;

    public ZombieModApi(InfectionService infection, ClassService classes)
    {
        _infection = infection;
        _classes = classes;
    }

    public event Func<CCSPlayerController, CCSPlayerController?, bool, bool, HookResult?>? OnClientInfect;
    public event Func<CCSPlayerController, bool, HookResult?>? OnClientHumanize;
    public event Func<IReadOnlyList<CCSPlayerController>, HookResult?>? OnMotherZombieSelected;
    public event Func<HookResult?>? OnZombieRoundStart;

    // External callers route through these; internally the services own the event-firing so we
    // never raise twice. Plugin wires service "Fire*Hook" delegates to these Raise* methods.
    internal HookResult? RaiseClientInfect(CCSPlayerController c, CCSPlayerController? a, bool m, bool f)
        => OnClientInfect?.Invoke(c, a, m, f);
    internal HookResult? RaiseClientHumanize(CCSPlayerController c, bool r)
        => OnClientHumanize?.Invoke(c, r);
    internal HookResult? RaiseMotherZombieSelected(IReadOnlyList<CCSPlayerController> chosen)
        => OnMotherZombieSelected?.Invoke(chosen);
    internal HookResult? RaiseZombieRoundStart()
        => OnZombieRoundStart?.Invoke();

    public HookResult InfectClient(CCSPlayerController client, CCSPlayerController? attacker, bool motherZombie, bool force)
        => _infection.InfectClient(client, attacker, motherZombie, force);

    public void HumanizeClient(CCSPlayerController client, bool respawn)
        => _infection.HumanizeClient(client, respawn);

    public bool IsClientInfected(CCSPlayerController client)
        => _infection.IsClientInfected(client);

    public ZombieClass? GetClientClass(CCSPlayerController client)
    {
        var cls = _classes.GetActiveClass(client);
        if (cls is null) return null;
        return new ZombieClass(
            Id: cls.Name,
            Name: cls.Name,
            Team: cls.Team,
            Model: cls.Model,
            MotherZombie: cls.MotherZombie,
            NapalmTime: cls.NapalmTime,
            Health: cls.Health,
            RegenInterval: cls.Regen_Interval,
            RegenAmount: cls.Regen_Amount,
            Knockback: cls.Knockback,
            Speed: cls.Speed);
    }
}

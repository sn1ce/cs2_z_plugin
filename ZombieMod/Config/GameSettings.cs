namespace ZombieMod.Config;

public sealed record GameSettings
{
    public float FirstInfectionTimer { get; init; } = 15.0f;
    public float MotherZombieRatio { get; init; } = 7.0f;
    public bool MotherZombieTeleport { get; init; } = false;
    public bool CashOnDamage { get; init; } = false;

    /// <summary>0 = zombies win on time, 1 = humans win on time.</summary>
    public int TimeoutWinner { get; init; } = 1;

    public string DefaultHumanBuffer { get; init; } = "human_default";
    public string DefaultZombieBuffer { get; init; } = "zombie_default";
    public string MotherZombieBuffer { get; init; } = "motherzombie";

    public bool RandomClassesOnConnect { get; init; } = false;
    public bool RandomClassesOnSpawn { get; init; } = true;

    public bool WeaponPurchaseEnable { get; init; } = true;
    public bool WeaponRestrictEnable { get; init; } = true;
    public bool WeaponBuyZoneOnly { get; init; } = false;

    public bool TeleportAllow { get; init; } = true;
    public int TeleportUsesPerRound { get; init; } = 1;
    public float TeleportCooldownSeconds { get; init; } = 30.0f;

    public bool RespawnEnable { get; init; } = true;
    public float RespawnDelay { get; init; } = 5.0f;
    public bool AllowRespawnJoinLate { get; init; } = false;

    /// <summary>0 = zombie, 1 = human, 2 = pre-death team.</summary>
    public int RespawnTeam { get; init; } = 0;

    /// <summary>Per-class speed override (Speed in classes.json applied as VelocityModifier).</summary>
    public bool EnableClassSpeed { get; init; } = true;

    /// <summary>Cash floor at the start of each round. Players with less get bumped up to this.
    /// Players with more keep what they earned.</summary>
    public int StartMoney { get; init; } = 4000;

    /// <summary>Cash awarded to a zombie attacker on a successful knife-infect.</summary>
    public int InfectKillReward { get; init; } = 500;

    /// <summary>Rounds to play on a single map before rotating to the next entry in MapRotation.</summary>
    public int MaxRoundsPerMap { get; init; } = 15;

    /// <summary>
    /// Maps to rotate through. Numeric strings are treated as Steam Workshop IDs (loaded via
    /// <c>host_workshop_map</c>); anything else is treated as a vanilla map name (<c>changelevel</c>).
    /// Empty means "stay on the current map forever".
    /// </summary>
    public IReadOnlyList<string> MapRotation { get; init; } = Array.Empty<string>();
}

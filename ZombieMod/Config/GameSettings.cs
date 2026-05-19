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

    /// <summary>Per-class speed override; CSSharp limitation — keep false until verified working.</summary>
    public bool EnableClassSpeed { get; init; } = false;
}

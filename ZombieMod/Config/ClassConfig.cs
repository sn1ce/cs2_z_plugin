namespace ZombieMod.Config;

public sealed record ClassConfig
{
    public string Name { get; init; } = "";
    public bool Enable { get; init; } = true;

    /// <summary>0 = zombie (T), 1 = human (CT).</summary>
    public int Team { get; init; } = 0;

    public string Model { get; init; } = "default";
    public bool MotherZombie { get; init; } = false;
    public float NapalmTime { get; init; } = 0.0f;
    public int Health { get; init; } = 100;
    public int Regen_Interval { get; init; } = 0;
    public int Regen_Amount { get; init; } = 0;
    public float Knockback { get; init; } = 1.0f;
    public float Speed { get; init; } = 250.0f;
}

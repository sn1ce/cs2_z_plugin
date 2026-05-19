namespace ZombieMod.Config;

public sealed record HitgroupConfig
{
    public int Index { get; init; }
    public float Knockback { get; init; } = 1.0f;
}

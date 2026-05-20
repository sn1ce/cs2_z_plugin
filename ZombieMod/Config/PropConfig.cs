namespace ZombieMod.Config;

public sealed record PropConfig
{
    public string Name { get; init; } = "";
    public string Model { get; init; } = "";
    public int Cost { get; init; }
}

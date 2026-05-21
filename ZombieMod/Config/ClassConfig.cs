namespace ZombieMod.Config;

public sealed record ClassConfig
{
    public string Name { get; init; } = "";
    public bool Enable { get; init; } = true;

    /// <summary>0 = infected (T), 1 = survivor (CT).</summary>
    public int Team { get; init; } = 0;

    public string Model { get; init; } = "default";
    public bool PatientZero { get; init; } = false;
    public float NapalmTime { get; init; } = 0.0f;
    public int Health { get; init; } = 100;
    public int Regen_Interval { get; init; } = 0;
    public int Regen_Amount { get; init; } = 0;
    public float Knockback { get; init; } = 1.0f;

    /// <summary>Movement speed in CS2 default units. 250 = normal. Applied as VelocityModifier
    /// (Speed/250). Gated by GameSettings.EnableClassSpeed.</summary>
    public float Speed { get; init; } = 250.0f;

    /// <summary>Body scale multiplier. 1.0 = normal, 1.2 = bigger, 0.8 = smaller.</summary>
    public float Scale { get; init; } = 1.0f;

    /// <summary>RGB tint applied to the player model. 255/255/255 = no tint (white).</summary>
    public int RenderR { get; init; } = 255;
    public int RenderG { get; init; } = 255;
    public int RenderB { get; init; } = 255;
}

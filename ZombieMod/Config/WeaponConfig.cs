namespace ZombieMod.Config;

public sealed record WeaponConfig
{
    public string WeaponName { get; init; } = "";
    public string WeaponEntity { get; init; } = "";
    public float Knockback { get; init; } = 1.0f;
    public int WeaponSlot { get; init; } = 0;
    public int Price { get; init; } = 0;
    public int MaxPurchase { get; init; } = 0;
    public bool Restrict { get; init; } = false;
    public IReadOnlyList<string> PurchaseCommand { get; init; } = Array.Empty<string>();

    /// <summary>Magazine size override. 0 = auto-double the vanilla CS2 MaxClip1.</summary>
    public int Clip { get; init; }

    /// <summary>Reserve (spare) ammo override. 0 = default to exactly 2 spare magazines
    /// (2 × the effective mag size). Requires sv_infinite_ammo 0, else reserve is infinite.</summary>
    public int Reserve { get; init; }
}

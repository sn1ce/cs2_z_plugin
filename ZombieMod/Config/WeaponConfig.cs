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

    /// <summary>Reserve ammo override. 0 = leave vanilla CS2 default.</summary>
    public int Reserve { get; init; }
}

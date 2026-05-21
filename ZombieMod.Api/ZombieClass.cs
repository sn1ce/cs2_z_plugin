namespace ZombieMod.Api;

/// <summary>
/// Read-only snapshot of a class assigned to a client. Mirrors <c>ClassConfig</c> but is
/// intentionally a separate type so the public API is decoupled from internal config records.
/// </summary>
public sealed record ZombieClass(
    string Id,
    string Name,
    int Team,
    string Model,
    bool PatientZero,
    float NapalmTime,
    int Health,
    int RegenInterval,
    int RegenAmount,
    float Knockback,
    float Speed);

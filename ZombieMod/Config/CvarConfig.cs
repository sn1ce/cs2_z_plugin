namespace ZombieMod.Config;

/// <summary>
/// Wraps <c>cvars.json</c>. <see cref="RequiredCvars"/> is an ordered list of complete
/// console commands the plugin issues on Load + every OnMapStart. <see cref="ReapplyDelaysSeconds"/>
/// holds the delayed re-apply schedule (defaults to {5, 15} — empty disables re-apply).
/// </summary>
public sealed record CvarConfig
{
    public IReadOnlyList<string> RequiredCvars { get; init; } = Array.Empty<string>();
    public IReadOnlyList<float> ReapplyDelaysSeconds { get; init; } = new[] { 5.0f, 15.0f };
}

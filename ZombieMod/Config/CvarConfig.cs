namespace ZombieMod.Config;

/// <summary>
/// Wraps <c>cvars.json</c>. <see cref="RequiredCvars"/> is an ordered list of complete
/// console commands the plugin issues on Load + every OnMapStart, plus every 3s via a
/// REPEAT timer to clobber gamemode_casual.cfg overrides.
/// </summary>
public sealed record CvarConfig
{
    public IReadOnlyList<string> RequiredCvars { get; init; } = Array.Empty<string>();
}

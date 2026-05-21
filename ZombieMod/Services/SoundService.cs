using System.Globalization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using Microsoft.Extensions.Logging;

namespace ZombieMod.Services;

/// <summary>
/// Plays sound files from the MAM-mounted HanZombieSoundPackage workshop addon (3644652779).
/// Each event key in sounds.json maps to (Volume, Files[]); we pick a file at random per call
/// and dispatch via "playvol &lt;path&gt; &lt;vol&gt;" — 2D non-positional, simple and reliable.
/// </summary>
public sealed class SoundService
{
    private readonly ILogger _logger;
    private readonly SoundConfig _sounds;
    private readonly Random _rng = new();

    public SoundService(ILogger logger, SoundConfig sounds)
    {
        _logger = logger;
        _sounds = sounds;
    }

    /// <summary>Play one of the sounds bucketed under <paramref name="eventKey"/> to every connected human.</summary>
    public void Broadcast(string eventKey)
    {
        if (!TryBuildCommand(eventKey, out var cmd)) return;
        foreach (var p in Utilities.GetPlayers())
        {
            if (p is null || !p.IsValid || p.IsBot || p.IsHLTV) continue;
            try { p.ExecuteClientCommand(cmd); } catch { /* client gone mid-broadcast */ }
        }
    }

    /// <summary>Cut every currently-playing sound on every client. Useful on round end so
    /// the background music doesn't bleed into post-round / freezetime.</summary>
    public void StopAllForEveryone()
    {
        foreach (var p in Utilities.GetPlayers())
        {
            if (p is null || !p.IsValid || p.IsBot || p.IsHLTV) continue;
            try { p.ExecuteClientCommand("stopsound"); } catch { }
        }
    }

    /// <summary>Play to a single client — typically used for first-person feedback.</summary>
    public void PlayToClient(CCSPlayerController? client, string eventKey)
    {
        if (client is null || !client.IsValid || client.IsBot || client.IsHLTV) return;
        if (!TryBuildCommand(eventKey, out var cmd)) return;
        try { client.ExecuteClientCommand(cmd); } catch { }
    }

    private bool TryBuildCommand(string eventKey, out string cmd)
    {
        cmd = string.Empty;
        if (!_sounds.Events.TryGetValue(eventKey, out var entry) || entry.Files.Count == 0)
            return false;
        var path = entry.Files[_rng.Next(entry.Files.Count)];
        // playvol exists in CS2's command list but appears to be a no-op stub at runtime.
        // Stick with "play" — Volume in sounds.json is currently advisory until we wire up
        // a working attenuation path (soundevent-based EmitSound, or a client cvar).
        cmd = $"play {path}";
        return true;
    }
}

/// <summary>Sounds config root. <see cref="Events"/> maps event key → (Volume, Files[]).</summary>
public sealed class SoundConfig
{
    public IReadOnlyDictionary<string, SoundEntry> Events { get; init; }
        = new Dictionary<string, SoundEntry>();
}

public sealed class SoundEntry
{
    public float Volume { get; init; } = 1.0f;
    public IReadOnlyList<string> Files { get; init; } = Array.Empty<string>();
}

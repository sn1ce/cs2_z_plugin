using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using Microsoft.Extensions.Logging;

namespace ZombieMod.Services;

/// <summary>
/// Plays sounds from MAM-mounted workshop addons. Two modes per event in sounds.json:
///   1. <c>SoundEvent</c> set → engine <c>EmitSound</c> (event name from the addon's
///      .vsndevts; volume + spatialization baked into the soundevent definition)
///   2. <c>SoundEvent</c> empty + <c>Files</c> non-empty → fall back to client
///      <c>play &lt;path&gt;</c> (full volume, 2D non-positional)
/// </summary>
public sealed class SoundService
{
    private readonly ILogger _logger;
    private readonly SoundConfig _sounds;
    private readonly Random _rng = new();
    private CWorld? _worldEntCache;

    public SoundService(ILogger logger, SoundConfig sounds)
    {
        _logger = logger;
        _sounds = sounds;
    }

    /// <summary>Play one of the sounds bucketed under <paramref name="eventKey"/> to every connected client.</summary>
    public void Broadcast(string eventKey)
    {
        if (!_sounds.Events.TryGetValue(eventKey, out var entry)) return;

        if (!string.IsNullOrEmpty(entry.SoundEvent))
        {
            if (TryEmitFromWorld(entry.SoundEvent, eventKey))
                return;
            // World entity unavailable — fall through to the play <path> path if Files is set.
        }

        if (entry.Files.Count == 0) return;
        var path = entry.Files[_rng.Next(entry.Files.Count)];
        var cmd = $"play {path}";
        foreach (var p in Utilities.GetPlayers())
        {
            if (p is null || !p.IsValid || p.IsBot || p.IsHLTV) continue;
            try { p.ExecuteClientCommand(cmd); } catch { /* client gone mid-broadcast */ }
        }
    }

    /// <summary>Cut every currently-playing sound on every client. Used on round end so
    /// background music doesn't bleed into post-round / freezetime.</summary>
    public void StopAllForEveryone()
    {
        foreach (var p in Utilities.GetPlayers())
        {
            if (p is null || !p.IsValid || p.IsBot || p.IsHLTV) continue;
            try { p.ExecuteClientCommand("stopsound"); } catch { }
        }
    }

    private bool TryEmitFromWorld(string soundEvent, string eventKey)
    {
        try
        {
            if (_worldEntCache is null || !_worldEntCache.IsValid)
                _worldEntCache = Utilities.FindAllEntitiesByDesignerName<CWorld>("worldent").FirstOrDefault();
            if (_worldEntCache is null || !_worldEntCache.IsValid)
            {
                _logger.LogWarning("[Sound] World entity not found; EmitSound({Event}) skipped for {Key}", soundEvent, eventKey);
                return false;
            }
            _worldEntCache.EmitSound(soundEvent);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Sound] EmitSound({Event}) failed for {Key}", soundEvent, eventKey);
            return false;
        }
    }
}

/// <summary>Sounds config root. <see cref="Events"/> maps event key → (Volume, Files[], SoundEvent).</summary>
public sealed class SoundConfig
{
    public IReadOnlyDictionary<string, SoundEntry> Events { get; init; }
        = new Dictionary<string, SoundEntry>();
}

public sealed class SoundEntry
{
    /// <summary>Advisory only when using the <c>play</c> path; baked into the soundevent when using EmitSound.</summary>
    public float Volume { get; init; } = 1.0f;

    /// <summary>Workshop-defined soundevent name (e.g. <c>"ZombieMod.Ambient"</c>). When set,
    /// playback uses engine <c>EmitSound</c> from the world entity, which respects user volume
    /// settings and the volume baked into the .vsndevts entry. Takes precedence over <see cref="Files"/>.</summary>
    public string SoundEvent { get; init; } = "";

    /// <summary>Raw <c>.vsnd</c> paths; used only when <see cref="SoundEvent"/> is empty.</summary>
    public IReadOnlyList<string> Files { get; init; } = Array.Empty<string>();
}

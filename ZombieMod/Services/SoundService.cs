using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using Microsoft.Extensions.Logging;

namespace ZombieMod.Services;

/// <summary>
/// Plays sounds from MAM-mounted workshop addons. Two modes per event in sounds.json:
///   1. <c>SoundEvent</c> set → engine <c>EmitSound</c> with <c>Volume</c> passed through
///      at runtime (requires CS2-EmitSoundVolumeFix plugin for the volume to actually
///      take effect — vanilla CS2 ignores the volume parameter on EmitSound).
///   2. <c>SoundEvent</c> empty + <c>Files</c> non-empty → fall back to client
///      <c>play &lt;path&gt;</c> (full volume, 2D non-positional).
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

    /// <summary>
    /// Play one of the sounds bucketed under <paramref name="eventKey"/>. If
    /// <paramref name="sourceEntity"/> is non-null and uses a positional soundevent type
    /// (e.g. <c>cs_player_footstep</c>), the sound spatializes from that entity's position.
    /// Pass null for 2D broadcast (emits from the world entity).
    /// </summary>
    public void Broadcast(string eventKey, CBaseEntity? sourceEntity = null)
    {
        if (!_sounds.Events.TryGetValue(eventKey, out var entry)) return;

        if (!string.IsNullOrEmpty(entry.SoundEvent))
        {
            var emitter = (sourceEntity is { IsValid: true }) ? sourceEntity : ResolveWorldEntity();
            if (emitter is not null)
            {
                try
                {
                    emitter.EmitSound(entry.SoundEvent, volume: entry.Volume);
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[Sound] EmitSound({Event}) failed for {Key}", entry.SoundEvent, eventKey);
                    // fall through to play <path>
                }
            }
            else
            {
                _logger.LogWarning("[Sound] No emitter for {Key} ({Event}) — falling back to play <path>", eventKey, entry.SoundEvent);
            }
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

    private CBaseEntity? ResolveWorldEntity()
    {
        if (_worldEntCache is { IsValid: true })
            return _worldEntCache;
        _worldEntCache = Utilities.FindAllEntitiesByDesignerName<CWorld>("worldent").FirstOrDefault();
        return _worldEntCache;
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
    /// <summary>Volume 0.0–1.0. Passed to EmitSound at runtime (requires CS2-EmitSoundVolumeFix
    /// for the engine to actually honor it). When falling back to <c>play &lt;path&gt;</c> this
    /// field is advisory only — the <c>play</c> command has no volume parameter.</summary>
    public float Volume { get; init; } = 1.0f;

    /// <summary>Workshop-defined soundevent name (e.g. <c>"zm.ambTrack"</c>). When set,
    /// playback uses engine <c>EmitSound</c> with <see cref="Volume"/> passed through.
    /// Takes precedence over <see cref="Files"/>.</summary>
    public string SoundEvent { get; init; } = "";

    /// <summary>Raw <c>.vsnd</c> paths; used only when <see cref="SoundEvent"/> is empty.</summary>
    public IReadOnlyList<string> Files { get; init; } = Array.Empty<string>();
}

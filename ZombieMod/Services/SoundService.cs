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
    private readonly ConfigService _config;
    private readonly Random _rng = new();
    private CWorld? _worldEntCache;

    /// <summary>Take ConfigService (not a SoundConfig snapshot) so css_zreload picks up.
    /// The previous design captured the original SoundConfig instance; the new one
    /// returned by ConfigService.Reload() was unreachable from here.</summary>
    public SoundService(ILogger logger, ConfigService config)
    {
        _logger = logger;
        _config = config;
    }

    private SoundConfig Sounds => _config.Sounds;

    /// <summary>
    /// Play one of the sounds bucketed under <paramref name="eventKey"/>. If
    /// <paramref name="sourceEntity"/> is non-null and uses a positional soundevent type
    /// (e.g. <c>cs_player_footstep</c>), the sound spatializes from that entity's position.
    /// Pass null for 2D broadcast (emits from the world entity).
    /// </summary>
    public void Broadcast(string eventKey, CBaseEntity? sourceEntity = null)
    {
        if (!Sounds.Events.TryGetValue(eventKey, out var entry))
        {
            _logger.LogInformation("[Sound] Broadcast({Key}) — no entry in sounds.json, skip", eventKey);
            return;
        }

        if (!string.IsNullOrEmpty(entry.SoundEvent))
        {
            // Two emission paths:
            //   - sourceEntity provided (e.g. patient zero pawn): emit from that entity so a
            //     positional soundevent (cs_player_footstep type) spatializes correctly.
            //   - sourceEntity null: emit from EACH connected human player's pawn. Worldent-emit
            //     turned out to be silent for csgo_default events (engine returns a handle but
            //     plays nothing audible), so we use per-player emits as a 2D-ish broadcast.
            try
            {
                if (sourceEntity is { IsValid: true })
                {
                    var handle = sourceEntity.EmitSound(entry.SoundEvent);
                    _logger.LogInformation(
                        "[Sound] EmitSound({Event}) → handle={Handle} (event={Key}, baked-vol, emitter=#{Idx} {Designer})",
                        entry.SoundEvent, handle, eventKey, sourceEntity.Index, sourceEntity.DesignerName);
                    return;
                }

                var emittedFromCount = 0;
                foreach (var p in Utilities.GetPlayers())
                {
                    if (p is null || !p.IsValid || p.IsBot || p.IsHLTV) continue;
                    var pawn = p.PlayerPawn.Value;
                    if (pawn is null || !pawn.IsValid) continue;
                    try { pawn.EmitSound(entry.SoundEvent); emittedFromCount++; } catch { }
                }
                if (emittedFromCount > 0)
                {
                    _logger.LogInformation("[Sound] EmitSound({Event}) broadcast to {N} pawns (event={Key}, baked-vol)",
                        entry.SoundEvent, emittedFromCount, eventKey);
                    return;
                }
                _logger.LogWarning("[Sound] No live human pawns for {Key} ({Event}) — falling back to play <path>",
                    eventKey, entry.SoundEvent);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Sound] EmitSound({Event}) THREW for {Key}", entry.SoundEvent, eventKey);
                // fall through to play <path>
            }
        }

        if (entry.Files.Count == 0)
        {
            _logger.LogInformation("[Sound] No Files for {Key}, silent", eventKey);
            return;
        }
        var path = entry.Files[_rng.Next(entry.Files.Count)];
        var cmd = $"play {path}";
        _logger.LogInformation("[Sound] play <path> fallback for {Key}: {Cmd}", eventKey, cmd);
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

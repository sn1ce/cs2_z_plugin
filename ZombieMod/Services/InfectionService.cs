using System.Drawing;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using ZombieMod.Config;
using ZombieMod.Models;
using ZombieMod.Util;
using CsTimer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace ZombieMod.Services;

public sealed class InfectionService
{
    private readonly ILogger _logger;
    private readonly ConfigService _config;

    // Owned by InfectionService; other services reach state via the lookup helpers below.
    private readonly Dictionary<int, PlayerState> _players = new();


    // Reference back to plugin for timer creation. Set by plugin after construction.
    internal BasePlugin? Host { get; set; }

    // Hook for the public API to fire OnClientInfect / OnClientHumanize / OnPatientZeroSelected.
    internal Func<CCSPlayerController, CCSPlayerController?, bool, bool, HookResult?>? FireInfectHook;
    internal Func<CCSPlayerController, bool, HookResult?>? FireHumanizeHook;
    internal Func<IReadOnlyList<CCSPlayerController>, HookResult?>? FirePatientZeroSelectedHook;
    internal Func<HookResult?>? FireRoundStartHook;

    // Hook to apply class attributes when a player is infected/humanized; implemented in Phase 4.
    internal Action<CCSPlayerController, ClassConfig>? ApplyClassHook;

    private CsTimer? _firstInfectionTimer;
    private CsTimer? _roundTimeoutTimer;
    private CsTimer? _countdownTimer;
    private int _countdownRemaining;
    private int _roundsPlayed;
    private int _mapRotationIdx;
    private bool _infectionStarted;

    public bool InfectionStarted => _infectionStarted;
    public IReadOnlyDictionary<int, PlayerState> Players => _players;

    public InfectionService(ILogger logger, ConfigService config)
    {
        _logger = logger;
        _config = config;
    }

    // ─── player lifecycle ─────────────────────────────────────────────────────

    public void OnClientPutInServer(CCSPlayerController client)
    {
        if (!client.IsValid) return;
        _players[client.Slot] = new PlayerState { Controller = client, Slot = client.Slot };
    }

    public void OnClientDisconnect(int slot) => _players.Remove(slot);

    public PlayerState? GetState(CCSPlayerController client)
        => _players.TryGetValue(client.Slot, out var s) ? s : null;

    public PlayerState? GetState(int slot)
        => _players.TryGetValue(slot, out var s) ? s : null;

    // ─── round lifecycle ──────────────────────────────────────────────────────

    /// <summary>True between OnRoundStart and OnRoundEnd. Used to gate background music
    /// so it stops the moment the round ends instead of bleeding into post-round / next freezetime.</summary>
    public bool RoundActive { get; private set; }

    public void OnRoundStart()
    {
        KillRoundTimers();
        _infectionStarted = false;
        RoundActive = true;

        foreach (var p in _players.Values)
        {
            p.IsInfected = false;
            p.IsPatientZero = false;
            p.ActiveClass = null;
            p.ResetForRound();
        }

        var startMoney = _config.GameSettings.StartMoney;
        // Auto-shuffle all alive/team-assigned players to CT so the infection starts even.
        foreach (var player in Utilities.GetPlayers())
        {
            if (player is null || !player.IsValid) continue;
            if (player.Team is CsTeam.Spectator or CsTeam.None) continue;
            if (player.Team != CsTeam.CounterTerrorist)
                player.SwitchTeam(CsTeam.CounterTerrorist);
        }

        // Reset cash to StartMoney — but defer it so we run AFTER casual mode's gamemode cfg
        // sets its own mp_startmoney-derived account value. Without the delay, our set is
        // immediately overridden back to ~$1000. We fire at 0.5s and again at 2s to be safe.
        if (startMoney > 0)
        {
            Host?.AddTimer(0.5f, () => ApplyStartMoney(startMoney));
            Host?.AddTimer(2.0f, () => ApplyStartMoney(startMoney));
        }
    }

    private static void ApplyStartMoney(int floor)
    {
        foreach (var p in Utilities.GetPlayers())
        {
            if (p is null || !p.IsValid) continue;
            if (p.Team is CsTeam.Spectator or CsTeam.None) continue;
            if (p.InGameMoneyServices is null) continue;
            // Only top up — don't clobber money the player earned during the previous round.
            if (p.InGameMoneyServices.Account < floor)
            {
                p.InGameMoneyServices.Account = floor;
                Utilities.SetStateChanged(p, "CCSPlayerController", "m_pInGameMoneyServices");
            }
        }
    }

    public void OnRoundFreezeEnd()
    {
        if (Host is null)
        {
            _logger.LogError("[Infection] Host plugin not set; cannot schedule Patient Zero timer.");
            return;
        }

        // EventRoundFreezeEnd also fires for warmup rounds — skip so we don't run the infection
        // countdown during the pre-match warmup.
        if (GameRules.IsWarmup())
        {
            _logger.LogInformation("[Infection] FreezeEnd during warmup — skipping infection cycle.");
            return;
        }

        KillRoundTimers();

        var delay = MathF.Max(1.0f, _config.GameSettings.FirstInfectionTimer);
        _firstInfectionTimer = Host.AddTimer(
            delay,
            InfectPatientZeros,
            TimerFlags.STOP_ON_MAPCHANGE);

        // Welcome banner + countdown to first infection.
        Server.PrintToChatAll(" \x04[ZombieMod]\x01 Outbreak — a CS2 mod by snice");
        Server.PrintToChatAll($" \x04[ZombieMod]\x01 Outbreak in \x07{(int)delay}\x01 seconds…");

        // Decrement BEFORE printing so the displayed value reflects actual seconds remaining:
        //   t=1s → "14s"  (14 seconds until infection)
        //   ...
        //   t=14s → "1s"  (1 second until infection)
        //   t=15s → infection fires (countdown stops at "1s" — no flash of "0s" or "1s at fire").
        _countdownRemaining = (int)Math.Ceiling(delay);
        _countdownTimer = Host.AddTimer(1.0f, () =>
        {
            _countdownRemaining--;
            if (_countdownRemaining <= 0)
            {
                _countdownTimer?.Kill();
                _countdownTimer = null;
                return;
            }
            foreach (var p in Utilities.GetPlayers())
            {
                if (p is null || !p.IsValid) continue;
                if (p.Team is CsTeam.Spectator or CsTeam.None) continue;
                p.PrintToCenter($"Outbreak in {_countdownRemaining}s");
            }
        }, TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE);

        // Round-timeout timer — if mp_roundtime expires without elimination, end the round in
        // favour of the team configured by GameSettings.TimeoutWinner (0=infected, 1=survivors).
        var mpRoundtime = ConVar.Find("mp_roundtime");
        var roundtimeMinutes = mpRoundtime?.GetPrimitiveValue<float>() ?? 3.0f;
        var roundtimeSec = MathF.Max(5f, roundtimeMinutes * 60f);
        _roundTimeoutTimer = Host.AddTimer(roundtimeSec, OnRoundTimeout, TimerFlags.STOP_ON_MAPCHANGE);

        FireRoundStartHook?.Invoke();
        _logger.LogInformation("[Infection] First infection in {Delay}s, round timeout in {RT}s",
            delay, roundtimeSec);
    }

    private void OnRoundTimeout()
    {
        if (!_infectionStarted) return; // pre-infection: nothing to time out
        var winner = _config.GameSettings.TimeoutWinner switch
        {
            0 => CsTeam.Terrorist,           // infected win
            1 => CsTeam.CounterTerrorist,    // survivors win
            _ => CsTeam.None,
        };
        _logger.LogInformation("[Infection] Round time expired — TimeoutWinner={W}", winner);
        TerminateRound(winner);
    }

    public void OnRoundEnd()
    {
        KillRoundTimers();
        _infectionStarted = false;
        RoundActive = false;
        _roundsPlayed++;

        var maxRounds = _config.GameSettings.MaxRoundsPerMap;
        _logger.LogInformation("[Map] Round {N} of {Max} ended.", _roundsPlayed, maxRounds);

        if (maxRounds > 0 && _roundsPlayed >= maxRounds)
            ScheduleMapRotation();
    }

    private void ScheduleMapRotation()
    {
        var rotation = _config.GameSettings.MapRotation;
        if (rotation is null || rotation.Count == 0)
        {
            _logger.LogInformation("[Map] Rotation list empty — staying on current map.");
            _roundsPlayed = 0;
            return;
        }

        var next = rotation[_mapRotationIdx % rotation.Count];
        _mapRotationIdx++;
        _roundsPlayed = 0;

        // Give the round-end screen ~8s to play before yanking everyone to the next map.
        Host?.AddTimer(8.0f, () =>
        {
            if (long.TryParse(next, out _))
            {
                _logger.LogInformation("[Map] Rotating to workshop map {Id}", next);
                Server.ExecuteCommand($"host_workshop_map {next}");
            }
            else
            {
                _logger.LogInformation("[Map] Rotating to vanilla map {Name}", next);
                Server.ExecuteCommand($"changelevel {next}");
            }
        });
    }

    private void KillRoundTimers()
    {
        _firstInfectionTimer?.Kill();
        _firstInfectionTimer = null;
        _roundTimeoutTimer?.Kill();
        _roundTimeoutTimer = null;
        _countdownTimer?.Kill();
        _countdownTimer = null;
    }

    public void InfectPatientZeros()
    {
        if (_infectionStarted) return;

        var alive = Utilities.GetPlayers()
            .Where(p => p is { IsValid: true, PawnIsAlive: true })
            .Where(p => p.Team is CsTeam.CounterTerrorist or CsTeam.Terrorist)
            .ToList();

        if (alive.Count == 0)
        {
            _logger.LogWarning("[Infection] InfectPatientZeros fired with zero alive players.");
            return;
        }

        var ratio = _config.GameSettings.PatientZeroRatio;
        var needed = Math.Max(1, (int)Math.Ceiling(alive.Count / ratio));

        var rng = new Random();
        // Prefer real players over bots — fall back to bots only if not enough live players alive.
        var realPlayers = alive.Where(p => !p.IsBot).OrderBy(_ => rng.Next()).ToList();
        var bots        = alive.Where(p =>  p.IsBot).OrderBy(_ => rng.Next()).ToList();
        var chosen = realPlayers.Take(needed).ToList();
        if (chosen.Count < needed)
            chosen.AddRange(bots.Take(needed - chosen.Count));

        var vetoResult = FirePatientZeroSelectedHook?.Invoke(chosen);
        if (vetoResult is HookResult.Stop)
        {
            _logger.LogInformation("[Infection] Patient Zero selection cancelled by API consumer.");
            return;
        }

        _infectionStarted = true;
        foreach (var player in chosen)
        {
            InfectClient(player, attacker: null, patientZero: true, force: true);
        }

        _logger.LogInformation(
            "[Infection] Outbreak started: {N} Patient Zero(s) of {Total} alive.",
            chosen.Count, alive.Count);
    }

    // ─── infect / humanize ────────────────────────────────────────────────────

    public HookResult InfectClient(
        CCSPlayerController client,
        CCSPlayerController? attacker,
        bool patientZero,
        bool force)
    {
        if (!client.IsValid)
        {
            _logger.LogError("[Infection] InfectClient called with invalid client.");
            return HookResult.Stop;
        }

        var hook = FireInfectHook?.Invoke(client, attacker, patientZero, force);
        if (hook is HookResult.Stop or HookResult.Handled)
        {
            _logger.LogInformation("[Infection] {Name} infect cancelled by API.", client.PlayerName);
            return HookResult.Stop;
        }

        var state = GetOrCreateState(client);
        state.IsInfected = true;
        state.IsPatientZero = patientZero;

        if (client.Team != CsTeam.Terrorist)
            client.SwitchTeam(CsTeam.Terrorist);

        // Infected are melee-only — strip all weapons except the knife.
        StripWeapons(client, keepKnife: true);

        var classId = patientZero
            ? _config.GameSettings.PatientZeroBuffer
            : _config.GameSettings.DefaultInfectedBuffer;

        if (_config.Classes.TryGetValue(classId, out var cls))
        {
            state.ActiveClass = cls;
            ApplyClassHook?.Invoke(client, cls);
        }
        else
        {
            _logger.LogError("[Infection] Class '{Id}' missing from classes.json; cannot apply.", classId);
        }

        // Infected vision: clear the red survivor glow, then paint a dark-green infected glow so
        // they're visibly team-coded in spectate / kill-cam / through walls.
        ApplyTeamGlow(client, ZombieMod.Models.TeamGlow.Infected);

        // Transition VFX/shake — fires for every infection (knife + Patient Zero + admin) so
        // the newly-turned player always gets a clear "you changed" visual + camera kick.
        FireInfectionEffect(client);
        FireScreenShake(client);

        if (attacker is not null && attacker.IsValid)
        {
            FakeInfectKillfeed(client, attacker);
            // Award the infected cash for a successful infect — CS2's native kill bonus never
            // fires for us since the victim doesn't actually die (we just team-switch them).
            var reward = _config.GameSettings.InfectKillReward;
            if (reward > 0 && attacker.InGameMoneyServices is not null)
            {
                attacker.InGameMoneyServices.Account += reward;
                Utilities.SetStateChanged(attacker, "CCSPlayerController", "m_pInGameMoneyServices");
            }
        }

        // After a team-switch infect, CS2's native round-end check doesn't refire (no death
        // event). Re-evaluate ourselves so the round ends when the last CT is infected.
        Host?.AddTimer(0.2f, CheckRoundEndConditions);

        return HookResult.Continue;
    }

    /// <summary>
    /// Apply a wall-piercing glow keyed off team — red for survivors, dark green for infected,
    /// clear for neither. CS2's glow is broadcast to all clients so this is a team-coding
    /// hint visible everywhere (kill-cam, spectate, through walls), not a per-viewer mask.
    /// </summary>
    private void ApplyTeamGlow(CCSPlayerController client, ZombieMod.Models.TeamGlow tint)
    {
        var slot = client.Slot;
        Server.NextFrame(() =>
        {
            var fresh = Utilities.GetPlayerFromSlot(slot);
            var pawn = fresh?.PlayerPawn.Value;
            if (pawn is null || !pawn.IsValid) return;

            try
            {
                switch (tint)
                {
                    case ZombieMod.Models.TeamGlow.Survivor:
                        pawn.Glow.GlowColorOverride = Color.FromArgb(255, 255, 50, 50);
                        pawn.Glow.GlowType = 3;
                        pawn.Glow.GlowRange = 0;
                        pawn.Glow.GlowRangeMin = 0;
                        pawn.Glow.GlowTime = 0;
                        break;
                    case ZombieMod.Models.TeamGlow.Infected:
                        pawn.Glow.GlowColorOverride = Color.FromArgb(255, 30, 200, 30);
                        pawn.Glow.GlowType = 3;
                        pawn.Glow.GlowRange = 0;
                        pawn.Glow.GlowRangeMin = 0;
                        pawn.Glow.GlowTime = 0;
                        break;
                    case ZombieMod.Models.TeamGlow.None:
                    default:
                        pawn.Glow.GlowColorOverride = Color.FromArgb(0, 0, 0, 0);
                        pawn.Glow.GlowType = 0;
                        pawn.Glow.GlowRange = 0;
                        break;
                }
                Utilities.SetStateChanged(pawn, "CBaseModelEntity", "m_Glow");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Glow] Set failed for {Name}", fresh?.PlayerName ?? "unknown");
            }
        });
    }

    /// <summary>
    /// Spawn a vanilla HE-grenade explosion particle at the victim's feet. Visual only — no
    /// damage, no knockback. Fires for every infection (knife / Patient Zero / admin) so the player
    /// always gets a clear "you turned" signal.
    /// </summary>
    private void FireInfectionEffect(CCSPlayerController victim)
    {
        var pawn = victim.PlayerPawn.Value;
        var pos = pawn?.AbsOrigin;
        if (pos is null || Host is null) return;

        try
        {
            var particle = Utilities.CreateEntityByName<CParticleSystem>("info_particle_system");
            if (particle is null) return;
            particle.EffectName = "particles/explosions_fx/explosion_hegrenade.vpcf";
            particle.Teleport(new Vector(pos.X, pos.Y, pos.Z + 16), new QAngle(), new Vector());
            particle.DispatchSpawn();
            particle.AcceptInput("Start");

            // (Visual only — the HE explode sound used to fire here, but we replaced it with the
            // proper zombie_infect scream broadcast from SoundService via FireInfectHook.)

            Host.AddTimer(2.0f, () =>
            {
                if (particle.IsValid) particle.Remove();
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Infection] Failed to spawn FX particle.");
        }
    }

    /// <summary>
    /// Spawn an <c>env_shake</c> at the victim's feet for a brief camera jolt. Radius is tight
    /// so other players nearby get a faint kick but the victim feels the brunt of it.
    /// </summary>
    private void FireScreenShake(CCSPlayerController victim)
    {
        var pawn = victim.PlayerPawn.Value;
        var pos = pawn?.AbsOrigin;
        if (pos is null || Host is null) return;

        try
        {
            var shake = Utilities.CreateEntityByName<CEnvShake>("env_shake");
            if (shake is null) return;

            shake.Amplitude = 12.0f;
            shake.Frequency = 80.0f;
            shake.Duration  = 0.8f;
            shake.Radius    = 350.0f;

            shake.DispatchSpawn();
            shake.Teleport(new Vector(pos.X, pos.Y, pos.Z), new QAngle(), new Vector());
            shake.AcceptInput("StartShake");

            // Schedule safe destruction the same way we kill weapons — entity-IO "Kill".
            shake.AddEntityIOEvent("Kill", shake, null, "", 2.0f);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Infection] Failed to spawn screen-shake.");
        }
    }

    public void HumanizeClient(CCSPlayerController client, bool respawn)
    {
        if (!client.IsValid) return;

        var hook = FireHumanizeHook?.Invoke(client, respawn);
        if (hook is HookResult.Stop or HookResult.Handled)
        {
            _logger.LogInformation("[Infection] {Name} humanize cancelled by API.", client.PlayerName);
            return;
        }

        var state = GetOrCreateState(client);
        state.IsInfected = false;
        state.IsPatientZero = false;

        if (client.Team != CsTeam.CounterTerrorist)
            client.SwitchTeam(CsTeam.CounterTerrorist);

        if (_config.Classes.TryGetValue(_config.GameSettings.DefaultSurvivorBuffer, out var cls))
        {
            state.ActiveClass = cls;
            ApplyClassHook?.Invoke(client, cls);
        }

        if (respawn && !client.PawnIsAlive)
            client.Respawn();

        // Survivor glow: tag them so infected (and themselves) see them through walls.
        ApplyTeamGlow(client, ZombieMod.Models.TeamGlow.Survivor);
    }

    // ─── queries ──────────────────────────────────────────────────────────────

    public bool IsClientInfected(CCSPlayerController client)
        => GetState(client)?.IsInfected ?? false;

    public bool IsClientSurvivor(CCSPlayerController client)
        => GetState(client) is { IsInfected: false };

    // ─── hurt + death plumbing ────────────────────────────────────────────────

    public void OnPlayerHurt(CCSPlayerController? victim, CCSPlayerController? attacker, string weapon)
    {
        if (victim is null || attacker is null) return;
        if (!victim.IsValid || !attacker.IsValid) return;
        if (victim.Slot == attacker.Slot) return;

        if (IsClientSurvivor(victim) && IsClientInfected(attacker) && IsKnifeWeapon(weapon))
        {
            InfectClient(victim, attacker, patientZero: false, force: false);
        }
    }

    public void OnPlayerDeath(CCSPlayerController? client)
    {
        if (client is null || !client.IsValid) return;
        CheckRoundEndConditions();
    }

    private static bool IsKnifeWeapon(string weapon)
        => weapon.Contains("knife", StringComparison.OrdinalIgnoreCase)
        || weapon.Contains("bayonet", StringComparison.OrdinalIgnoreCase);

    // ─── round-end detection ──────────────────────────────────────────────────

    private void CheckRoundEndConditions()
    {
        if (!_infectionStarted) return;

        var aliveInfected  = 0;
        var aliveSurvivors = 0;

        foreach (var p in Utilities.GetPlayers())
        {
            if (p is null || !p.IsValid || !p.PawnIsAlive) continue;
            if (p.Team is CsTeam.Spectator or CsTeam.None) continue;

            if (IsClientInfected(p)) aliveInfected++;
            else aliveSurvivors++;
        }

        // Solo-session escape hatch: with only one player there's no meaningful "win"
        // condition to fire. Skip the auto-terminate so the player can roam — the
        // round-timeout timer (mp_roundtime * 60s) will reset naturally.
        if (aliveInfected + aliveSurvivors <= 1)
            return;

        if (aliveInfected == 0 && aliveSurvivors > 0)
        {
            _logger.LogInformation("[Infection] Survivors win — no infected remain.");
            TerminateRound(CsTeam.CounterTerrorist);
        }
        else if (aliveSurvivors == 0 && aliveInfected > 0)
        {
            _logger.LogInformation("[Infection] Outbreak claims survivors — no survivors remain.");
            TerminateRound(CsTeam.Terrorist);
        }
    }

    public void ForceEndRound(CsTeam winner) => TerminateRound(winner);
    public void ForceMapRotation() => ScheduleMapRotation();

    private void TerminateRound(CsTeam winner)
    {
        // CheckRoundEndConditions guards against the alive<=1 segfault case before we ever
        // get here, so the native gameRules.TerminateRound path is safe now. Using it instead
        // of mp_restartgame because mp_restartgame resets all scores (breaks the scoreboard)
        // AND doesn't truly respawn pawns (our SetModel zombie override persists, so all
        // post-restart players visually stay zombies). Native termination does both correctly.
        var gameRules = ZombieMod.Util.GameRules.Get();
        if (gameRules is null)
        {
            _logger.LogError("[Infection] Cannot terminate round: game rules entity not found.");
            return;
        }

        var reason = winner switch
        {
            CsTeam.Terrorist => RoundEndReason.TerroristsWin,
            CsTeam.CounterTerrorist => RoundEndReason.CTsWin,
            _ => RoundEndReason.RoundDraw,
        };

        _logger.LogInformation("[Infection] Round won by {Winner}.", winner);
        Server.PrintToChatAll($" \x04[ZombieMod]\x01 Round over — {winner} wins.");
        gameRules.TerminateRound(5f, reason);
        UpdateTeamScore(winner);
        AwardWinCash(winner, amount: 3250);
    }

    /// <summary>
    /// CS2 normally awards round-win cash via the native win-condition pipeline; because we
    /// force the round to terminate ourselves the payout doesn't fire, so we credit the winners
    /// manually. Default $3250 matches the standard elimination bonus.
    /// </summary>
    private static void AwardWinCash(CsTeam winner, int amount)
    {
        if (winner is CsTeam.None or CsTeam.Spectator) return;
        foreach (var p in Utilities.GetPlayers())
        {
            if (p is null || !p.IsValid) continue;
            if (p.Team != winner) continue;
            if (p.InGameMoneyServices is null) continue;
            p.InGameMoneyServices.Account += amount;
            Utilities.SetStateChanged(p, "CCSPlayerController", "m_pInGameMoneyServices");
        }
    }

    private static void UpdateTeamScore(CsTeam team, int delta = 1)
    {
        if (team == CsTeam.None || team == CsTeam.Spectator) return;
        var teamManagers = Utilities.FindAllEntitiesByDesignerName<CCSTeam>("cs_team_manager");
        foreach (var tm in teamManagers)
        {
            if ((int)team == tm.TeamNum)
            {
                tm.Score += delta;
                Utilities.SetStateChanged(tm, "CTeam", "m_iScore");
            }
        }
    }

    /// <summary>Public entry point for the plugin's <c>EventItemPickup</c> handler.</summary>
    public void StripWeaponsKeepKnife(CCSPlayerController client) => StripWeapons(client, keepKnife: true);

    private static void StripWeapons(CCSPlayerController client, bool keepKnife)
    {
        // Direct w.Remove() crashes CS2 with "WriteEnterPVS: GetEntServerClass failed" on the
        // next net tick — the entity is freed but the player's inventory handle still references
        // it. Working pattern: set as active, DropActiveWeapon (removes from inventory
        // cleanly), then schedule a deferred "Kill" entity-IO event for safe destruction
        // through the engine's normal teardown path.
        var slot = client.Slot;
        Server.NextFrame(() =>
        {
            var fresh = Utilities.GetPlayerFromSlot(slot);
            if (fresh is null || !fresh.IsValid) return;
            var pawn = fresh.PlayerPawn.Value;
            if (pawn?.WeaponServices is null) return;

            var weapons = pawn.WeaponServices.MyWeapons.ToList();
            foreach (var handle in weapons)
            {
                var w = handle.Value;
                if (w is null || !w.IsValid) continue;
                if (keepKnife && w.DesignerName.Contains("knife", StringComparison.OrdinalIgnoreCase)) continue;

                try
                {
                    pawn.WeaponServices.ActiveWeapon.Raw = handle.Raw;
                    fresh.DropActiveWeapon();
                    w.AddEntityIOEvent("Kill", w, null, "", 0.5f);
                }
                catch
                {
                    // Entity may be mid-destruction by the engine — tolerate.
                }
            }
        });
    }

    private void FakeInfectKillfeed(CCSPlayerController victim, CCSPlayerController attacker)
    {
        try
        {
            // Fire a synthetic EventPlayerDeath so the killfeed shows "attacker [knife] victim".
            // Our RespawnService guards on PawnIsAlive, so the re-entrant respawn schedule that
            // CSSharp may dispatch back through our handler is a no-op (victim stays alive).
            var death = new EventPlayerDeath(false);
            death.Userid = victim;
            death.Attacker = attacker;
            death.Weapon = "knife";
            death.FireEvent(false);

            // Bump scoreboard kill/death counters so the infection counts as a real kill.
            if (attacker.ActionTrackingServices is not null)
                attacker.ActionTrackingServices.MatchStats.Kills += 1;
            if (victim.ActionTrackingServices is not null)
                victim.ActionTrackingServices.MatchStats.Deaths += 1;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Infection] FakeInfectKillfeed failed.");
        }
    }

    private PlayerState GetOrCreateState(CCSPlayerController client)
    {
        if (!_players.TryGetValue(client.Slot, out var s))
        {
            s = new PlayerState { Controller = client, Slot = client.Slot };
            _players[client.Slot] = s;
        }
        return s;
    }
}

using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using ZombieMod.Config;
using ZombieMod.Models;
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

    // Hook for the public API to fire OnClientInfect / OnClientHumanize / OnMotherZombieSelected.
    internal Func<CCSPlayerController, CCSPlayerController?, bool, bool, HookResult?>? FireInfectHook;
    internal Func<CCSPlayerController, bool, HookResult?>? FireHumanizeHook;
    internal Func<IReadOnlyList<CCSPlayerController>, HookResult?>? FireMotherSelectedHook;
    internal Func<HookResult?>? FireRoundStartHook;

    // Hook to apply class attributes when a player is infected/humanized; implemented in Phase 4.
    internal Action<CCSPlayerController, ClassConfig>? ApplyClassHook;

    private CsTimer? _firstInfectionTimer;
    private CsTimer? _roundTimeoutTimer;
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

    public void OnRoundStart()
    {
        KillRoundTimers();
        _infectionStarted = false;

        foreach (var p in _players.Values)
        {
            p.IsZombie = false;
            p.IsMotherZombie = false;
            p.ActiveClass = null;
            p.ResetForRound();
        }

        // Auto-shuffle all alive/team-assigned players to CT so the infection starts even.
        foreach (var player in Utilities.GetPlayers())
        {
            if (player is null || !player.IsValid) continue;
            if (player.Team is CsTeam.Spectator or CsTeam.None) continue;
            if (player.Team != CsTeam.CounterTerrorist)
                player.SwitchTeam(CsTeam.CounterTerrorist);
        }
    }

    public void OnRoundFreezeEnd()
    {
        if (Host is null)
        {
            _logger.LogError("[Infection] Host plugin not set; cannot schedule mother zombie timer.");
            return;
        }

        KillRoundTimers();

        var delay = MathF.Max(1.0f, _config.GameSettings.FirstInfectionTimer);
        _firstInfectionTimer = Host.AddTimer(
            delay,
            InfectMotherZombies,
            TimerFlags.STOP_ON_MAPCHANGE);

        FireRoundStartHook?.Invoke();
        _logger.LogInformation("[Infection] First infection in {Delay}s", delay);
    }

    public void OnRoundEnd()
    {
        KillRoundTimers();
        _infectionStarted = false;
    }

    private void KillRoundTimers()
    {
        _firstInfectionTimer?.Kill();
        _firstInfectionTimer = null;
        _roundTimeoutTimer?.Kill();
        _roundTimeoutTimer = null;
    }

    public void InfectMotherZombies()
    {
        if (_infectionStarted) return;

        var alive = Utilities.GetPlayers()
            .Where(p => p is { IsValid: true, PawnIsAlive: true })
            .Where(p => p.Team is CsTeam.CounterTerrorist or CsTeam.Terrorist)
            .ToList();

        if (alive.Count == 0)
        {
            _logger.LogWarning("[Infection] InfectMotherZombies fired with zero alive players.");
            return;
        }

        var ratio = _config.GameSettings.MotherZombieRatio;
        var needed = Math.Max(1, (int)Math.Ceiling(alive.Count / ratio));

        var rng = new Random();
        var chosen = alive.OrderBy(_ => rng.Next()).Take(needed).ToList();

        var vetoResult = FireMotherSelectedHook?.Invoke(chosen);
        if (vetoResult is HookResult.Stop)
        {
            _logger.LogInformation("[Infection] Mother zombie selection cancelled by API consumer.");
            return;
        }

        _infectionStarted = true;
        foreach (var player in chosen)
        {
            InfectClient(player, attacker: null, motherZombie: true, force: true);
        }

        _logger.LogInformation(
            "[Infection] Started: {N} mother zombies of {Total} alive.",
            chosen.Count, alive.Count);
    }

    // ─── infect / humanize ────────────────────────────────────────────────────

    public HookResult InfectClient(
        CCSPlayerController client,
        CCSPlayerController? attacker,
        bool motherZombie,
        bool force)
    {
        if (!client.IsValid)
        {
            _logger.LogError("[Infection] InfectClient called with invalid client.");
            return HookResult.Stop;
        }

        var hook = FireInfectHook?.Invoke(client, attacker, motherZombie, force);
        if (hook is HookResult.Stop or HookResult.Handled)
        {
            _logger.LogInformation("[Infection] {Name} infect cancelled by API.", client.PlayerName);
            return HookResult.Stop;
        }

        var state = GetOrCreateState(client);
        state.IsZombie = true;
        state.IsMotherZombie = motherZombie;

        if (client.Team != CsTeam.Terrorist)
            client.SwitchTeam(CsTeam.Terrorist);

        var classId = motherZombie
            ? _config.GameSettings.MotherZombieBuffer
            : _config.GameSettings.DefaultZombieBuffer;

        if (_config.Classes.TryGetValue(classId, out var cls))
        {
            state.ActiveClass = cls;
            ApplyClassHook?.Invoke(client, cls);
        }
        else
        {
            _logger.LogError("[Infection] Class '{Id}' missing from classes.json; cannot apply.", classId);
        }

        if (attacker is not null && attacker.IsValid)
            FakeInfectKillfeed(client, attacker);

        return HookResult.Continue;
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
        state.IsZombie = false;
        state.IsMotherZombie = false;

        if (client.Team != CsTeam.CounterTerrorist)
            client.SwitchTeam(CsTeam.CounterTerrorist);

        if (_config.Classes.TryGetValue(_config.GameSettings.DefaultHumanBuffer, out var cls))
        {
            state.ActiveClass = cls;
            ApplyClassHook?.Invoke(client, cls);
        }

        if (respawn && !client.PawnIsAlive)
            client.Respawn();
    }

    // ─── queries ──────────────────────────────────────────────────────────────

    public bool IsClientInfected(CCSPlayerController client)
        => GetState(client)?.IsZombie ?? false;

    public bool IsClientHuman(CCSPlayerController client)
        => GetState(client) is { IsZombie: false };

    // ─── hurt + death plumbing ────────────────────────────────────────────────

    public void OnPlayerHurt(CCSPlayerController? victim, CCSPlayerController? attacker, string weapon)
    {
        if (victim is null || attacker is null) return;
        if (!victim.IsValid || !attacker.IsValid) return;
        if (victim.Slot == attacker.Slot) return;

        if (IsClientHuman(victim) && IsClientInfected(attacker) && IsKnifeWeapon(weapon))
        {
            InfectClient(victim, attacker, motherZombie: false, force: false);
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

        var aliveZombies = 0;
        var aliveHumans  = 0;

        foreach (var p in Utilities.GetPlayers())
        {
            if (p is null || !p.IsValid || !p.PawnIsAlive) continue;
            if (p.Team is CsTeam.Spectator or CsTeam.None) continue;

            if (IsClientInfected(p)) aliveZombies++;
            else aliveHumans++;
        }

        if (aliveZombies == 0)
        {
            _logger.LogInformation("[Infection] Humans win — no zombies remain.");
            TerminateRound(humansWin: true);
        }
        else if (aliveHumans == 0)
        {
            _logger.LogInformation("[Infection] Zombies win — no humans remain.");
            TerminateRound(humansWin: false);
        }
    }

    private void TerminateRound(bool humansWin)
    {
        // Note: TerminateRound on CCSGameRules is the clean path but the signature shifts between
        // CSSharp versions. For now we slay the losing team — that triggers EventRoundEnd via the
        // standard CS2 win-condition path. Phase 10 polish: switch to direct termination once we
        // pin a known-good CSSharp signature.
        var losingTeam = humansWin ? CsTeam.Terrorist : CsTeam.CounterTerrorist;
        foreach (var p in Utilities.GetPlayers())
        {
            if (p is null || !p.IsValid || !p.PawnIsAlive) continue;
            if (p.Team != losingTeam) continue;
            p.PlayerPawn.Value?.CommitSuicide(false, true);
        }
    }

    private void FakeInfectKillfeed(CCSPlayerController victim, CCSPlayerController attacker)
    {
        // ZombieSharp fires a synthetic EventPlayerDeath here so the killfeed shows the infect.
        // Skipping for the first cut — implement once Knockback/Class are in and we've confirmed
        // the EventPlayerDeath constructor + FireEvent signature against current CSSharp.
        _ = victim; _ = attacker;
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

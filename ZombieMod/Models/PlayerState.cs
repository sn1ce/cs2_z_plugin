using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using ZombieMod.Config;

namespace ZombieMod.Models;

/// <summary>
/// Per-slot runtime state. Keyed by <c>CCSPlayerController.Slot</c> in
/// <c>Dictionary&lt;int, PlayerState&gt;</c>. Created on connect-full, cleared on disconnect.
/// </summary>
public sealed class PlayerState
{
    public required CCSPlayerController Controller { get; init; }
    public required int Slot { get; init; }

    public bool IsInfected { get; set; }
    public bool IsPatientZero { get; set; }

    public ClassConfig? ActiveClass { get; set; }

    public int TeleportsUsedThisRound { get; set; }
    public DateTime LastTeleportAt { get; set; } = DateTime.MinValue;

    /// <summary>Captured on the player's first spawn after a round start; used by !ztele.</summary>
    public Vector? SpawnPosition { get; set; }
    public QAngle? SpawnAngle { get; set; }

    /// <summary>Per-life purchase counts keyed by weapon short name.</summary>
    public Dictionary<string, int> PurchaseCounts { get; } = new();

    public DateTime? NapalmExpiresAt { get; set; }

    public void ResetForRound()
    {
        TeleportsUsedThisRound = 0;
        PurchaseCounts.Clear();
        NapalmExpiresAt = null;
    }
}

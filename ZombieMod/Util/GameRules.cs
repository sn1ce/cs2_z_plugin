using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;

namespace ZombieMod.Util;

internal static class GameRules
{
    public static CCSGameRules? Get()
        => Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules")
            .FirstOrDefault()?
            .GameRules;

    public static bool IsWarmup() => Get()?.WarmupPeriod ?? false;
}

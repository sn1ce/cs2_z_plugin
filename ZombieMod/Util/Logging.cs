using Microsoft.Extensions.Logging;

namespace ZombieMod.Util;

internal static class Logging
{
    public static void ConfigError(this ILogger logger, string file, string key, string reason)
        => logger.LogError("[Config] {File}: key '{Key}' — {Reason}", file, key, reason);

    public static void MissingDependency(this ILogger logger, string what, string why)
        => logger.LogWarning("[Dependency] {What} not found — {Why}", what, why);
}

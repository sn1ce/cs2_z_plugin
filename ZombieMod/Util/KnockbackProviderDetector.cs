using Microsoft.Extensions.Logging;

namespace ZombieMod.Util;

/// <summary>
/// Detects whether a third-party patch that re-enables direct velocity writes is loaded.
/// Vanilla CSSharp cannot reliably push a player — the movement code overwrites velocity each
/// tick. Without one of the providers below, every <c>KnockbackService</c> path no-ops.
/// </summary>
public sealed class KnockbackProviderDetector
{
    public enum Provider
    {
        None,
        CSSharpFixes,
        MovementUnlocker,
        CS2SigPatcher,
    }

    public Provider Detected { get; private set; } = Provider.None;
    public bool Available => Detected != Provider.None;

    private readonly ILogger _logger;

    public KnockbackProviderDetector(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Probes the server filesystem for a loaded provider. Call once on plugin Load.
    /// <paramref name="addonsDir"/> is the <c>game/csgo/addons</c> directory.
    /// </summary>
    public void Detect(string addonsDir)
    {
        var sigPatcher = Path.Combine(addonsDir, "counterstrikesharp", "plugins", "CS2-SigPatcher");
        if (Directory.Exists(sigPatcher) || HasPluginDll(sigPatcher, "CS2-SigPatcher.dll"))
        {
            Detected = Provider.CS2SigPatcher;
            _logger.LogInformation("[Knockback] Provider detected: CS2-SigPatcher (CSSharp plugin)");
            return;
        }

        var metamodDir = Path.Combine(addonsDir, "metamod");
        if (Directory.Exists(metamodDir))
        {
            if (MetamodVdfPresent(metamodDir, "CSSharpFixes") ||
                MetamodBinaryPresent(addonsDir, "cssharpfixes"))
            {
                Detected = Provider.CSSharpFixes;
                _logger.LogInformation("[Knockback] Provider detected: CSSharpFixes (Metamod plugin)");
                return;
            }
            if (MetamodVdfPresent(metamodDir, "MovementUnlocker") ||
                MetamodBinaryPresent(addonsDir, "movementunlocker"))
            {
                Detected = Provider.MovementUnlocker;
                _logger.LogInformation("[Knockback] Provider detected: MovementUnlocker (Metamod plugin)");
                return;
            }
        }

        Detected = Provider.None;
        _logger.LogWarning(
            "[Knockback] No provider detected (looked under '{Addons}'). " +
            "Vanilla CSSharp cannot push players reliably — install one of: " +
            "CSSharpFixes / MovementUnlocker / CS2-SigPatcher. " +
            "Knockback features are DISABLED until one is present.",
            addonsDir);
    }

    private static bool MetamodVdfPresent(string metamodDir, string nameContains)
    {
        try
        {
            return Directory.EnumerateFiles(metamodDir, "*.vdf", SearchOption.TopDirectoryOnly)
                .Any(f => Path.GetFileNameWithoutExtension(f)
                    .Contains(nameContains, StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    private static bool MetamodBinaryPresent(string addonsDir, string subdirNameContains)
    {
        try
        {
            return Directory.EnumerateDirectories(addonsDir)
                .Any(d => Path.GetFileName(d)
                    .Contains(subdirNameContains, StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    private static bool HasPluginDll(string dir, string dllName)
    {
        try { return Directory.Exists(dir) && File.Exists(Path.Combine(dir, dllName)); }
        catch { return false; }
    }
}

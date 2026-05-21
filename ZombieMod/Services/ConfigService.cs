using System.Text.Json;
using Microsoft.Extensions.Logging;
using ZombieMod.Config;

namespace ZombieMod.Services;

public sealed class ConfigService
{
    private readonly ILogger _logger;
    private readonly string _configDir;

    public GameSettings GameSettings { get; private set; } = new();
    public IReadOnlyDictionary<string, WeaponConfig> Weapons { get; private set; }
        = new Dictionary<string, WeaponConfig>();
    public IReadOnlyDictionary<string, ClassConfig> Classes { get; private set; }
        = new Dictionary<string, ClassConfig>();
    public IReadOnlyDictionary<string, HitgroupConfig> Hitgroups { get; private set; }
        = new Dictionary<string, HitgroupConfig>();
    public IReadOnlyDictionary<string, PropConfig> Props { get; private set; }
        = new Dictionary<string, PropConfig>();
    public SoundConfig Sounds { get; private set; } = new();

    /// <summary>Weapon lookup keyed by entity name (e.g. <c>weapon_ak47</c>) — built at load time.</summary>
    public IReadOnlyDictionary<string, WeaponConfig> WeaponsByEntity { get; private set; }
        = new Dictionary<string, WeaponConfig>();

    /// <summary>Hitgroup lookup keyed by <c>Index</c> — built at load time.</summary>
    public IReadOnlyDictionary<int, HitgroupConfig> HitgroupsByIndex { get; private set; }
        = new Dictionary<int, HitgroupConfig>();

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public ConfigService(ILogger logger, string configDir)
    {
        _logger = logger;
        _configDir = configDir;
    }

    public bool LoadAll()
    {
        _logger.LogInformation("[Config] Loading from {Dir}", _configDir);

        var settings = LoadFile<GameSettings>("gamesettings.json");
        var weapons  = LoadDict<WeaponConfig>("weapons.json");
        var classes  = LoadDict<ClassConfig>("classes.json");
        var hitgrp   = LoadDict<HitgroupConfig>("hitgroups.json");
        var props    = LoadDict<PropConfig>("props.json");
        var sounds   = LoadSounds("sounds.json");

        GameSettings = settings ?? new GameSettings();
        Weapons      = weapons;
        Classes      = classes;
        Hitgroups    = hitgrp;
        Props        = props;
        Sounds       = sounds;

        WeaponsByEntity = BuildWeaponEntityIndex(Weapons);
        HitgroupsByIndex = Hitgroups.Values
            .GroupBy(h => h.Index)
            .ToDictionary(g => g.Key, g => g.First());

        return Validate();
    }

    public bool Reload()
    {
        _logger.LogInformation("[Config] Reload requested");
        return LoadAll();
    }

    private T? LoadFile<T>(string filename) where T : class
    {
        var path = Path.Combine(_configDir, filename);
        if (!File.Exists(path))
        {
            _logger.LogError("[Config] Missing required file: {Path}", path);
            return null;
        }
        try
        {
            var json = File.ReadAllText(path);
            var obj = JsonSerializer.Deserialize<T>(json, JsonOptions);
            if (obj is null)
            {
                _logger.LogError("[Config] {File} parsed to null", filename);
                return null;
            }
            return obj;
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex,
                "[Config] {File}: parse error at '{Path}' line {Line} col {Col}",
                filename, ex.Path, ex.LineNumber, ex.BytePositionInLine);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Config] {File}: unexpected load error", filename);
            return null;
        }
    }

    private IReadOnlyDictionary<string, T> LoadDict<T>(string filename) where T : class
    {
        var path = Path.Combine(_configDir, filename);
        if (!File.Exists(path))
        {
            _logger.LogError("[Config] Missing required file: {Path}", path);
            return new Dictionary<string, T>();
        }
        try
        {
            var json = File.ReadAllText(path);
            var dict = JsonSerializer.Deserialize<Dictionary<string, T>>(json, JsonOptions);
            return (IReadOnlyDictionary<string, T>?)dict ?? new Dictionary<string, T>();
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex,
                "[Config] {File}: parse error at '{Path}' line {Line} col {Col}",
                filename, ex.Path, ex.LineNumber, ex.BytePositionInLine);
            return new Dictionary<string, T>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Config] {File}: unexpected load error", filename);
            return new Dictionary<string, T>();
        }
    }

    private SoundConfig LoadSounds(string filename)
    {
        var dict = LoadDict<SoundEntry>(filename);
        return new SoundConfig
        {
            Events = dict.ToDictionary(kv => kv.Key, kv => kv.Value),
        };
    }

    private static IReadOnlyDictionary<string, WeaponConfig> BuildWeaponEntityIndex(
        IReadOnlyDictionary<string, WeaponConfig> weapons)
    {
        var dict = new Dictionary<string, WeaponConfig>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, weapon) in weapons)
        {
            if (string.IsNullOrEmpty(weapon.WeaponEntity)) continue;
            dict[weapon.WeaponEntity] = weapon;
        }
        return dict;
    }

    /// <summary>Returns true if every required cross-reference resolves.</summary>
    private bool Validate()
    {
        var ok = true;

        if (Classes.Count == 0)
        {
            _logger.LogError("[Config] classes.json contains zero entries");
            ok = false;
        }
        if (Hitgroups.Count == 0)
        {
            _logger.LogError("[Config] hitgroups.json contains zero entries");
            ok = false;
        }
        if (!Classes.ContainsKey(GameSettings.DefaultSurvivorBuffer))
        {
            _logger.LogError("[Config] gamesettings.DefaultSurvivorBuffer '{Id}' not in classes.json",
                GameSettings.DefaultSurvivorBuffer);
            ok = false;
        }
        if (!Classes.ContainsKey(GameSettings.DefaultInfectedBuffer))
        {
            _logger.LogError("[Config] gamesettings.DefaultInfectedBuffer '{Id}' not in classes.json",
                GameSettings.DefaultInfectedBuffer);
            ok = false;
        }
        if (!Classes.ContainsKey(GameSettings.PatientZeroBuffer))
        {
            _logger.LogError("[Config] gamesettings.PatientZeroBuffer '{Id}' not in classes.json",
                GameSettings.PatientZeroBuffer);
            ok = false;
        }

        foreach (var (key, cls) in Classes)
        {
            if (cls.Team != 0 && cls.Team != 1)
                _logger.LogWarning("[Config] classes.{Key}.Team={Team} (expected 0=zombie or 1=human)", key, cls.Team);
            if (cls.Health <= 0)
                _logger.LogWarning("[Config] classes.{Key}.Health={Health} (must be > 0)", key, cls.Health);
        }

        foreach (var (key, w) in Weapons)
        {
            if (string.IsNullOrEmpty(w.WeaponEntity))
                _logger.LogWarning("[Config] weapons.{Key} has no WeaponEntity", key);
        }

        if (GameSettings.PatientZeroRatio <= 0)
        {
            _logger.LogError("[Config] gamesettings.PatientZeroRatio must be > 0 (got {V})",
                GameSettings.PatientZeroRatio);
            ok = false;
        }
        if (GameSettings.TimeoutWinner is not (0 or 1))
        {
            _logger.LogError("[Config] gamesettings.TimeoutWinner must be 0 or 1 (got {V})",
                GameSettings.TimeoutWinner);
            ok = false;
        }

        _logger.LogInformation(
            "[Config] Loaded: {Classes} classes, {Weapons} weapons, {HG} hitgroups. Valid={Ok}",
            Classes.Count, Weapons.Count, Hitgroups.Count, ok);
        return ok;
    }
}

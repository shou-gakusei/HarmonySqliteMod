using HarmonySqliteMod.Config;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Utils;

namespace HarmonySqliteMod.Services;

/// <summary>
/// Service for loading and managing mod configuration.
/// 用于加载和管理模组配置的服务。
/// </summary>
[Injectable(InjectionType.Singleton)]
public class ConfigService
{
    private readonly ISptLogger<ConfigService> _logger;
    private readonly JsonUtil _jsonUtil;
    private ModConfig? _config;

    public ConfigService(ISptLogger<ConfigService> logger, JsonUtil jsonUtil)
    {
        _logger = logger;
        _jsonUtil = jsonUtil;
    }

    /// <summary>
    /// Loads the configuration from config.json.
    /// 从 config.json 加载配置。
    /// </summary>
    public ModConfig LoadConfig()
    {
        if (_config != null)
        {
            return _config;
        }

        var modDir = Path.GetDirectoryName(typeof(ModConfig).Assembly.Location);
        if (modDir == null)
        {
            _logger.Warning("Cannot determine mod directory. Using default config.");
            // 无法确定模组目录，使用默认配置
            _config = new ModConfig();
            return _config;
        }

        var configPath = Path.Combine(modDir, "config.json");
        if (!File.Exists(configPath))
        {
            _logger.Info("Config file not found. Creating default config.");
            // 配置文件不存在，创建默认配置
            _config = new ModConfig();
            SaveConfig(_config);
            return _config;
        }

        try
        {
            var jsonText = File.ReadAllText(configPath);
            _config = _jsonUtil.Deserialize<ModConfig>(jsonText) ?? new ModConfig();
            _logger.Success("Config loaded successfully.");
            return _config;
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to load config: {ex.Message}", ex);
            // 加载失败，使用默认配置
            _config = new ModConfig();
            return _config;
        }
    }

    /// <summary>
    /// Saves the configuration to config.json.
    /// 将配置保存到 config.json。
    /// </summary>
    public void SaveConfig(ModConfig config)
    {
        var modDir = Path.GetDirectoryName(typeof(ModConfig).Assembly.Location);
        if (modDir == null)
        {
            _logger.Warning("Cannot determine mod directory. Cannot save config.");
            // 无法确定模组目录，无法保存配置
            return;
        }

        var configPath = Path.Combine(modDir, "config.json");
        try
        {
            var jsonText = _jsonUtil.Serialize(config, true); // true = pretty print
            File.WriteAllText(configPath, jsonText);
            _logger.Success("Config saved successfully.");
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to save config: {ex.Message}", ex);
        }
    }
}
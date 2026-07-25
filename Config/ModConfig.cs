using System.Text.Json.Serialization;

namespace HarmonySqliteMod.Config;

/// <summary>
/// Mod configuration model.
/// 模组配置模型。
/// </summary>
public class ModConfig
{
    /// <summary>
    /// Enable JSON backup alongside SQLite storage.
    /// 启用 SQLite 存储的同时进行 JSON 备份。
    /// </summary>
    [JsonPropertyName("enableJsonBackup")]
    public bool EnableJsonBackup { get; set; } = false;

    /// <summary>
    /// JSON backup interval in milliseconds.
    /// JSON 备份间隔（毫秒）。
    /// </summary>
    [JsonPropertyName("jsonBackupIntervalMs")]
    public int JsonBackupIntervalMs { get; set; } = 1000;

    /// <summary>
    /// SQLite flush interval in milliseconds.
    /// SQLite 刷新间隔（毫秒）。
    /// </summary>
    [JsonPropertyName("sqliteFlushIntervalMs")]
    public int SqliteFlushIntervalMs { get; set; } = 500;
}
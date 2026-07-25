using System.Collections.Concurrent;
using System.Diagnostics;
using HarmonySqliteMod.Config;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Utils;

namespace HarmonySqliteMod.Services;

/// <summary>
/// Service for backing up profiles to JSON files when enabled in config.
/// 当配置中启用时，用于将档案备份到 JSON 文件的服务。
/// </summary>
[Injectable]
public class JsonBackupService
{
    private readonly ISptLogger<JsonBackupService> _logger;
    private readonly JsonUtil _jsonUtil;
    private readonly ConfigService _configService;
    private readonly ConcurrentDictionary<string, string> _backupCache = new(); // 备份缓存
    // Backup cache
    private int _backupDirtyCount; // 备份脏数据计数
    // Backup dirty count
    private readonly object _backupLock = new();

    public JsonBackupService(
        ISptLogger<JsonBackupService> logger,
        JsonUtil jsonUtil,
        ConfigService configService)
    {
        _logger = logger;
        _jsonUtil = jsonUtil;
        _configService = configService;
    }

    /// <summary>
    /// Caches profile data for JSON backup.
    /// 缓存档案数据用于 JSON 备份。
    /// </summary>
    public void CacheForBackup(string profileId, string profileJson)
    {
        _backupCache[profileId] = profileJson;
        Interlocked.Increment(ref _backupDirtyCount);
    }

    /// <summary>
    /// Gets the number of profiles pending backup.
    /// 获取等待备份的档案数量。
    /// </summary>
    public int BackupDirtyCount => _backupDirtyCount;

    /// <summary>
    /// Flushes all dirty profiles to JSON files.
    /// 将所有脏数据档案刷新到 JSON 文件。
    /// </summary>
    public async Task<int> FlushBackupAsync()
    {
        List<KeyValuePair<string, string>> batch;
        lock (_backupLock)
        {
            if (_backupDirtyCount == 0) return 0;

            batch = _backupCache.ToList();
            _backupCache.Clear();
            _backupDirtyCount = 0;
        }

        var sw = Stopwatch.StartNew();
        var profilesDir = Path.Combine("user", "profiles");
        var backedUpCount = 0;

        foreach (var (profileId, profileJson) in batch)
        {
            try
            {
                var jsonPath = Path.Combine(profilesDir, $"{profileId}.json");
                await File.WriteAllTextAsync(jsonPath, profileJson);
                backedUpCount++;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to backup profile {profileId} to JSON: {ex.Message}", ex);
            }
        }

        sw.Stop();
        _logger.Debug($"Backed up {backedUpCount} profiles to JSON in {sw.ElapsedMilliseconds}ms.");
        return backedUpCount;
    }

    /// <summary>
    /// Runs the JSON backup loop.
    /// 运行 JSON 备份循环。
    /// </summary>
    public async Task RunBackupLoopAsync(CancellationToken ct)
    {
        var config = _configService.LoadConfig();
        var interval = config.JsonBackupIntervalMs;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, ct);
                if (_backupDirtyCount > 0)
                {
                    await FlushBackupAsync();
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        // Final backup on shutdown
        // 关闭时最终备份
        if (_backupDirtyCount > 0)
        {
            _logger.Info("Shutdown: backing up remaining dirty profiles to JSON...");
            await FlushBackupAsync();
        }
    }
}
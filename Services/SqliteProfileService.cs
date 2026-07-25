using System.Collections.Concurrent;
using System.Diagnostics;
using Dapper;
using Microsoft.Data.Sqlite;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Profile;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Utils;

namespace HarmonySqliteMod.Services;

/// <summary>
/// Service for loading, saving, and migrating player profiles to/from SQLite.
/// 用于从 SQLite 加载、保存和迁移玩家档案的服务。
/// </summary>
[Injectable]
public class SqliteProfileService
{
    private readonly SqliteDatabaseService _db;
    private readonly JsonUtil _jsonUtil;
    private readonly ISptLogger<SqliteProfileService> _logger;
    private readonly ConcurrentDictionary<string, string> _profileCache = new(); // 内存缓存，存储待刷新的档案数据
    // Memory cache storing profiles pending flush
    private int _dirtyCount; // 脏数据计数，标记需要写入SQLite的档案数量
    // Dirty count, tracks number of profiles needing SQLite write
    private readonly object _flushLock = new();

    public SqliteProfileService(
        SqliteDatabaseService db,
        JsonUtil jsonUtil,
        ISptLogger<SqliteProfileService> logger)
    {
        _db = db;
        _jsonUtil = jsonUtil;
        _logger = logger;
    }

    /// <summary>
    /// Loads a profile from SQLite. Returns the deserialized profile if found.
    /// 从 SQLite 加载档案。如果找到则返回反序列化后的档案。
    /// </summary>
    public async Task<SptProfile?> LoadProfileAsync(MongoId sessionId)
    {
        var connection = _db.GetConnection();

        const string query = """
            SELECT * FROM profile_data WHERE profile_id = @ProfileId
            """;

        var row = await connection.QueryFirstOrDefaultAsync(query, new { ProfileId = sessionId.ToString() });

        if (row is null)
        {
            _logger.Warning($"Profile {sessionId} not found in SQLite.");
            return null;
        }

        // Deserialize from the stored JSON
        var profileJson = (string?)row.info_json ?? (string?)row.pmc_json;
        if (profileJson is null)
        {
            _logger.Warning($"Profile {sessionId} has no data in SQLite.");
            return null;
        }

        var profile = _jsonUtil.Deserialize<SptProfile>(profileJson);
        if (profile is null)
        {
            _logger.Error($"Failed to deserialize profile {sessionId} from SQLite.");
            return null;
        }

        _logger.Success($"Profile {sessionId} loaded from SQLite.");
        return profile;
    }

    /// <summary>
    /// Loads the raw JSON string of a profile from SQLite without deserializing to SptProfile.
    /// 从 SQLite 加载档案的原始 JSON 字符串，不反序列化为 SptProfile。
    /// This preserves all JSON data faithfully, avoiding data loss from SptProfile round-trip serialization.
    /// 忠实保留完整 JSON 数据，避免 SptProfile 折返序列化导致的数据丢失。
    /// Used by SaveServerLoadPatch to mirror the original LoadProfileAsync behavior (JsonObject path).
    /// 由 SaveServerLoadPatch 使用，镜像原始 LoadProfileAsync 的 JsonObject 路径。
    /// </summary>
    public async Task<string?> LoadProfileRawJsonAsync(MongoId sessionId)
    {
        var connection = _db.GetConnection();

        const string query = """
            SELECT info_json FROM profile_data WHERE profile_id = @ProfileId
            """;

        var row = await connection.QueryFirstOrDefaultAsync(query, new { ProfileId = sessionId.ToString() });

        if (row is null)
        {
            _logger.Warning($"Profile {sessionId} not found in SQLite.");
            return null;
        }

        var profileJson = (string?)row.info_json;
        if (profileJson is null)
        {
            _logger.Warning($"Profile {sessionId} has no data in SQLite.");
            return null;
        }

        _logger.Success($"Profile {sessionId} raw JSON loaded from SQLite.");
        return profileJson;
    }

    /// <summary>
    /// Saves a profile to the in-memory cache for delayed SQLite flush.
    /// 将档案保存到内存缓存，延迟写入 SQLite。
    /// The actual SQLite write is deferred to the configured flush interval.
    /// 实际的 SQLite 写入延迟到配置的刷新间隔。
    /// Returns the serialization time in milliseconds.
    /// 返回序列化耗时（毫秒）。
    /// If JSON backup is enabled, also caches data for JSON backup.
    /// 如果启用了 JSON 备份，同时缓存数据用于 JSON 备份。
    /// </summary>
    public async Task<long> SaveProfileAsync(MongoId sessionId, SptProfile profile)
    {
        var sw = Stopwatch.StartNew();

        var profileJson = _jsonUtil.Serialize(profile);
        if (profileJson is null)
        {
            _logger.Error($"Failed to serialize profile {sessionId}.");
            return sw.ElapsedMilliseconds;
        }

        _profileCache[sessionId.ToString()] = profileJson;
        Interlocked.Increment(ref _dirtyCount);

        // If JSON backup is enabled, also cache for backup
        // 如果启用了 JSON 备份，同时缓存用于备份
        try
        {
            var jsonBackupService = SPTarkov.Server.Core.DI.ServiceLocator.ServiceProvider.GetService<JsonBackupService>();
            if (jsonBackupService != null)
            {
                jsonBackupService.CacheForBackup(sessionId.ToString(), profileJson);
            }
        }
        catch (Exception ex)
        {
            _logger.Warning($"Failed to cache profile {sessionId} for JSON backup: {ex.Message}");
        }

        sw.Stop();
        return sw.ElapsedMilliseconds;
    }

    /// <summary>
    /// Migrates a profile from a JSON file into SQLite.
    /// 将档案从 JSON 文件迁移到 SQLite。
    /// Returns true on success.
    /// 成功返回 true。
    /// </summary>
    public async Task<bool> MigrateFromJsonAsync(string jsonFilePath)
    {
        if (!File.Exists(jsonFilePath))
        {
            _logger.Error($"JSON file not found: {jsonFilePath}");
            return false;
        }

        try
        {
            var jsonText = await File.ReadAllTextAsync(jsonFilePath);
            var profileId = Path.GetFileNameWithoutExtension(jsonFilePath);

            // Validate that it's a valid SptProfile JSON
            var profile = _jsonUtil.Deserialize<SptProfile>(jsonText);
            if (profile is null)
            {
                _logger.Warning($"File {jsonFilePath} is not a valid SptProfile. Storing as raw JSON.");
            }

            var connection = _db.GetConnection();

            const string upsertProfile = """
                INSERT INTO profile_data (profile_id, info_json, updated_at)
                VALUES (@ProfileId, @InfoJson, datetime('now', 'subsec'))
                ON CONFLICT(profile_id) DO UPDATE SET
                    info_json = @InfoJson,
                    updated_at = datetime('now', 'subsec')
                """;

            await connection.ExecuteAsync(
                upsertProfile,
                new { ProfileId = profileId, InfoJson = jsonText }
            );

            _logger.Success($"Migrated profile {profileId} from JSON to SQLite.");
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to migrate JSON file {jsonFilePath}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Checks whether a profile with the given session ID exists in SQLite.
    /// 检查指定会话 ID 的档案是否存在于 SQLite 中。
    /// </summary>
    public bool ProfileExists(string profileId)
    {
        var connection = _db.GetConnection();

        const string query = """
            SELECT COUNT(1) FROM profile_data WHERE profile_id = @ProfileId
            """;

        var count = connection.ExecuteScalar<int>(query, new { ProfileId = profileId });
        return count > 0;
    }

    /// <summary>
    /// Gets all profile IDs stored in SQLite.
    /// 获取 SQLite 中存储的所有档案 ID。
    /// Used on server startup to reload profiles that have no corresponding JSON files.
    /// 用于服务器启动时重新加载没有对应 JSON 文件的档案。
    /// </summary>
    public async Task<List<string>> GetAllProfileIdsAsync()
    {
        var connection = _db.GetConnection();

        const string query = """
            SELECT profile_id FROM profile_data ORDER BY profile_id
            """;

        var ids = (await connection.QueryAsync<string>(query)).AsList();
        return ids;
    }

    /// <summary>
    /// Gets the number of dirty (unsaved) profiles awaiting flush.
    /// 获取等待刷新的脏数据（未保存）档案数量。
    /// </summary>
    public int DirtyCount => _dirtyCount;

    /// <summary>
    /// Flushes all dirty profiles to SQLite. Returns the number of profiles flushed.
    /// 将所有脏数据档案刷新到 SQLite。返回刷新的档案数量。
    /// JSON backup is handled separately by JsonBackupService based on its own interval.
    /// JSON 备份由 JsonBackupService 根据其自己的间隔独立处理。
    /// </summary>
    public async Task<int> FlushAsync()
    {
        List<KeyValuePair<string, string>> batch;
        lock (_flushLock)
        {
            if (_dirtyCount == 0) return 0;

            batch = _profileCache.ToList();
            _profileCache.Clear();
            _dirtyCount = 0;
        }

        var sw = Stopwatch.StartNew();
        var connection = _db.GetConnection();
        var flushedCount = 0;

        foreach (var (profileId, profileJson) in batch)
        {
            const string upsert = """
                INSERT INTO profile_data (profile_id, info_json, updated_at)
                VALUES (@ProfileId, @ProfileJson, datetime('now', 'subsec'))
                ON CONFLICT(profile_id) DO UPDATE SET
                    info_json = @ProfileJson,
                    updated_at = datetime('now', 'subsec')
                """;

            await connection.ExecuteAsync(upsert, new { ProfileId = profileId, ProfileJson = profileJson });
            flushedCount++;
        }

        sw.Stop();
        _logger.Debug($"Flushed {flushedCount} dirty profiles to SQLite in {sw.ElapsedMilliseconds}ms.");
        return flushedCount;
    }
}

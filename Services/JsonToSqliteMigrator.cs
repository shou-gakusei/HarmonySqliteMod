using Dapper;
using Microsoft.Data.Sqlite;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Utils;

namespace HarmonySqliteMod.Services;

/// <summary>
/// Handles one-time migration of existing JSON profile files into SQLite.
/// 处理现有 JSON 档案文件到 SQLite 的一次性迁移。
/// Designed to be idempotent — already migrated profiles are skipped.
/// 设计为幂等操作 — 已迁移的档案会被跳过。
/// </summary>
[Injectable]
public class JsonToSqliteMigrator
{
    private const string MigrationMarkerTable = "migration_markers"; // 迁移标记表名
    // Migration marker table name
    private const string MigrationKeyJsonToSqlite = "json_to_sqlite"; // JSON到SQLite迁移的标记键
    // Migration key for JSON-to-SQLite migration

    private readonly SqliteDatabaseService _db;
    private readonly SqliteProfileService _profileService;
    private readonly ISptLogger<JsonToSqliteMigrator> _logger;

    public JsonToSqliteMigrator(
        SqliteDatabaseService db,
        SqliteProfileService profileService,
        ISptLogger<JsonToSqliteMigrator> logger)
    {
        _db = db;
        _profileService = profileService;
        _logger = logger;
    }

    /// <summary>
    /// Checks whether a migration is needed: JSON files exist in the profiles directory
    /// 检查是否需要迁移：档案目录中存在 JSON 文件
    /// and the SQLite database is currently empty.
    /// 且 SQLite 数据库当前为空。
    /// </summary>
    public async Task<bool> NeedMigrationAsync()
    {
        var jsonFiles = GetJsonProfileFiles();
        if (jsonFiles.Length == 0)
        {
            return false;
        }

        if (await IsMigrationDoneAsync())
        {
            return false;
        }

        var connection = _db.GetConnection();
        const string countQuery = "SELECT COUNT(1) FROM profile_data";
        var profileCount = await connection.ExecuteScalarAsync<int>(countQuery);

        return profileCount == 0;
    }

    /// <summary>
    /// Migrates all JSON profile files from the standard profiles directory into SQLite.
    /// 将标准档案目录中的所有 JSON 档案文件迁移到 SQLite。
    /// Skips profiles that have already been migrated.
    /// 跳过已迁移的档案。
    /// </summary>
    public async Task MigrateAllAsync()
    {
        await EnsureMigrationMarkerTableAsync();

        if (await IsMigrationDoneAsync())
        {
            _logger.Info("JSON-to-SQLite migration already completed. Skipping.");
            return;
        }

        var jsonFiles = GetJsonProfileFiles();
        if (jsonFiles.Length == 0)
        {
            _logger.Info("No JSON profile files found for migration.");
            return;
        }

        _logger.Info($"Found {jsonFiles.Length} JSON profile file(s). Starting migration...");

        var successCount = 0;
        foreach (var file in jsonFiles)
        {
            var profileId = Path.GetFileNameWithoutExtension(file);

            // Skip if already present
            if (_profileService.ProfileExists(profileId))
            {
                _logger.Info($"Profile {profileId} already exists in SQLite. Skipping.");
                successCount++;
                continue;
            }

            var result = await _profileService.MigrateFromJsonAsync(file);
            if (result)
            {
                successCount++;
            }
        }

        // Mark migration as done
        await MarkMigrationDoneAsync();

        _logger.Success($"Migration complete. {successCount}/{jsonFiles.Length} profiles migrated.");
    }

    /// <summary>
    /// Returns true if the JSON-to-SQLite migration has been completed.
    /// 如果 JSON 到 SQLite 的迁移已完成，返回 true。
    /// </summary>
    public async Task<bool> IsMigrationDoneAsync()
    {
        try
        {
            var connection = _db.GetConnection();
            const string query = """
                SELECT COUNT(1) FROM migration_markers WHERE marker_key = @MarkerKey
                """;
            var count = await connection.ExecuteScalarAsync<int>(query, new { MarkerKey = MigrationKeyJsonToSqlite });
            return count > 0;
        }
        catch (SqliteException)
        {
            // Marker table does not exist yet — migration not done
            return false;
        }
    }

    private async Task EnsureMigrationMarkerTableAsync()
    {
        var connection = _db.GetConnection();
        const string createTable = """
            CREATE TABLE IF NOT EXISTS migration_markers (
                marker_key TEXT PRIMARY KEY,
                completed_at TEXT NOT NULL DEFAULT (datetime('now', 'subsec'))
            )
            """;
        await connection.ExecuteAsync(createTable);
    }

    private async Task MarkMigrationDoneAsync()
    {
        var connection = _db.GetConnection();
        const string insert = """
            INSERT INTO migration_markers (marker_key, completed_at)
            VALUES (@MarkerKey, datetime('now', 'subsec'))
            ON CONFLICT(marker_key) DO UPDATE SET
                completed_at = datetime('now', 'subsec')
            """;
        await connection.ExecuteAsync(insert, new { MarkerKey = MigrationKeyJsonToSqlite });
    }

    private static string[] GetJsonProfileFiles()
    {
        var profilesDir = Path.Combine("user", "profiles");
        if (!Directory.Exists(profilesDir))
        {
            return [];
        }

        return Directory.GetFiles(profilesDir, "*.json");
    }
}

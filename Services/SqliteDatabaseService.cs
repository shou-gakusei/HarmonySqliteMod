using Dapper;
using Microsoft.Data.Sqlite;
using SPTarkov.DI.Annotations;

namespace HarmonySqliteMod.Services;

/// <summary>
/// Singleton service that manages the SQLite database connection and schema initialization.
/// 单例服务，管理 SQLite 数据库连接和模式初始化。
/// </summary>
[Injectable(InjectionType.Singleton)]
public class SqliteDatabaseService : IDisposable
{
    private readonly string _dbPath;
    private SqliteConnection? _connection;
    private bool _disposed;

    public SqliteDatabaseService(string dbPath = "user/profiles/spt_profiles.db")
    {
        _dbPath = dbPath;
    }

    /// <summary>
    /// Returns the underlying <see cref="SqliteConnection"/>, creating it if necessary.
    /// 返回底层的 <see cref="SqliteConnection"/>，必要时创建新连接。
    /// </summary>
    public SqliteConnection GetConnection()
    {
        if (_connection is null)
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = _dbPath,
                Pooling = false
            }.ToString();

            _connection = new SqliteConnection(connectionString);
        }

        return _connection;
    }

    /// <summary>
    /// Ensures the database directory exists, opens the connection, applies PRAGMAs,
    /// 确保数据库目录存在，打开连接，应用 PRAGMA 设置，
    /// and creates the required tables if they do not already exist.
    /// 如果表不存在则创建所需的表。
    /// </summary>
    public async Task InitializeAsync()
    {
        // Ensure directory exists
        // 确保目录存在
        var directory = Path.GetDirectoryName(Path.GetFullPath(_dbPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var connection = GetConnection();
        await connection.OpenAsync();

        // Configure PRAGMAs
        // 配置 PRAGMA 设置
        await using var walCommand = connection.CreateCommand();
        walCommand.CommandText = "PRAGMA journal_mode=WAL";
        await walCommand.ExecuteNonQueryAsync();

        await using var syncCommand = connection.CreateCommand();
        syncCommand.CommandText = "PRAGMA synchronous=NORMAL";
        await syncCommand.ExecuteNonQueryAsync();

        await using var busyTimeoutCommand = connection.CreateCommand();
        busyTimeoutCommand.CommandText = "PRAGMA busy_timeout=5000";
        await busyTimeoutCommand.ExecuteNonQueryAsync();

        // Create tables
        // 创建表
        const string createProfileDataTable = """
            CREATE TABLE IF NOT EXISTS profile_data (
                profile_id TEXT PRIMARY KEY,
                info_json TEXT,
                pmc_json TEXT,
                scav_json TEXT,
                skills_json TEXT,
                quests_json TEXT,
                traders_json TEXT,
                hideout_json TEXT,
                dialogues_json TEXT,
                insurance_json TEXT,
                builds_json TEXT,
                spt_meta_json TEXT,
                updated_at TEXT NOT NULL DEFAULT (datetime('now', 'subsec'))
            )
            """;

        const string createInventoryItemsTable = """
            CREATE TABLE IF NOT EXISTS inventory_items (
                item_id TEXT PRIMARY KEY,
                profile_id TEXT NOT NULL,
                _tpl TEXT,
                parent_id TEXT,
                slot_id TEXT,
                location_x INTEGER,
                location_y INTEGER,
                location_r INTEGER,
                upd_json TEXT,
                is_pmc INTEGER NOT NULL DEFAULT 1,
                updated_at TEXT NOT NULL DEFAULT (datetime('now', 'subsec'))
            )
            """;

        const string createInventoryProfileIndex = """
            CREATE INDEX IF NOT EXISTS idx_inventory_profile ON inventory_items(profile_id)
            """;

        const string createInventoryParentIndex = """
            CREATE INDEX IF NOT EXISTS idx_inventory_parent ON inventory_items(parent_id)
            """;

        await connection.ExecuteAsync(createProfileDataTable);
        await connection.ExecuteAsync(createInventoryItemsTable);
        await connection.ExecuteAsync(createInventoryProfileIndex);
        await connection.ExecuteAsync(createInventoryParentIndex);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _connection?.Dispose();
        _connection = null;
    }
}

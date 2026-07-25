using SPTarkov.DI.Annotations;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Profile;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Services;
using SPTarkov.Server.Core.Utils;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using HarmonyLib;
using HarmonySqliteMod.Services;

namespace HarmonySqliteMod;

/// <summary>
/// Mod metadata - replaces package.json.
/// 模组元数据 - 替代 package.json。
/// Registers assembly resolve handler to load dependencies from ./dependencies/ folder.
/// 注册程序集解析处理器，从 ./dependencies/ 文件夹加载依赖。
/// </summary>
public record ModMetadata : AbstractModMetadata
{
    static ModMetadata()
    {
        var modDir = Path.GetDirectoryName(typeof(ModMetadata).Assembly.Location);
        if (modDir is null) return;

        var depsDir = Path.Combine(modDir, "dependencies");
        if (!Directory.Exists(depsDir)) return;

        // Managed assembly resolver: loads NuGet DLLs from ./dependencies/
            // 托管程序集解析器：从 ./dependencies/ 加载 NuGet DLL
        AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
        {
            var assemblyName = new AssemblyName(args.Name).Name + ".dll";
            var assemblyPath = Path.Combine(depsDir, assemblyName);
            return File.Exists(assemblyPath) ? Assembly.LoadFrom(assemblyPath) : null;
        };

        // Native assembly resolver: loads e_sqlite3 from ./runtimes/win-x64/native/
            // 原生程序集解析器：从 ./runtimes/win-x64/native/ 加载 e_sqlite3
        AppDomain.CurrentDomain.AssemblyLoad += (sender, args) =>
        {
            if (args.LoadedAssembly.GetName().Name == "SQLitePCLRaw.provider.e_sqlite3")
            {
                NativeLibrary.SetDllImportResolver(args.LoadedAssembly, (name, assembly, path) =>
                {
                    if (name == "e_sqlite3")
                    {
                        var nativePath = Path.Combine(modDir, "runtimes", "win-x64", "native", "e_sqlite3.dll");
                        if (File.Exists(nativePath))
                            return NativeLibrary.Load(nativePath);
                    }
                    return IntPtr.Zero;
                });
            }
        };
    }

    public override string ModGuid { get; init; } = "com.whfwtf.harmony-sqlite-mod";
    public override string Name { get; init; } = "HarmonySqliteMod";
    public override string Author { get; init; } = "WHFWTF";
    public override List<string>? Contributors { get; init; }
    public override SemanticVersioning.Version Version { get; init; } = new("0.0.1-fix3");
    public override SemanticVersioning.Range SptVersion { get; init; } = new("~4.0.0");

    public override List<string>? Incompatibilities { get; init; }
    public override Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; }
    public override string? Url { get; init; }
    public override bool? IsBundleMod { get; init; }
    public override string License { get; init; } = "MIT";
}

[Injectable(TypePriority = OnLoadOrder.PreSptModLoader)]
public class HarmonySqliteModPlugin(
    ISptLogger<HarmonySqliteModPlugin> logger,
    SqliteDatabaseService sqliteDb,
    JsonToSqliteMigrator jsonMigrator,
    SqliteProfileService sqliteProfileService,
    ConfigService configService,
    JsonBackupService jsonBackupService
) : IOnLoad
{
    private CancellationTokenSource? _flushCts;
    private CancellationTokenSource? _jsonBackupCts;

    public async Task OnLoad()
    {
        // Initialize SQLite database (create file, tables, set PRAGMAs)
        // 初始化SQLite数据库（创建文件、表、设置PRAGMA）
        await sqliteDb.InitializeAsync();
        logger.Success("SQLite database initialized successfully.");

        // Migrate existing JSON profiles to SQLite (idempotent)
        // 迁移现有的JSON档案到SQLite（幂等操作）
        if (await jsonMigrator.NeedMigrationAsync())
        {
            logger.Info("JSON profile files detected. Starting migration to SQLite...");
            await jsonMigrator.MigrateAllAsync();
        }

        // Enable Harmony patches to intercept SaveServer
        // 启用Harmony补丁以拦截SaveServer
        new SaveServerLoadPatch().Enable();
        new SaveServerSavePatch().Enable();
        logger.Success("Harmony patches enabled.");

        // Load profiles from SQLite that have no corresponding JSON files
        // (profiles created after mod installation are SQLite-only)
        // 从 SQLite 加载没有对应 JSON 文件的 profile（安装模组后创建的纯 SQLite profile）
        await LoadSqliteOnlyProfilesAsync();

        // Load config
        // 加载配置
        var config = configService.LoadConfig();

        // Start async flush loop (dirty profiles flushed every configured interval)
        // 启动异步刷新循环（按配置的间隔刷新脏数据）
        _flushCts = new CancellationTokenSource();
        _ = RunFlushLoopAsync(_flushCts.Token, config.SqliteFlushIntervalMs);

        // Start JSON backup loop if enabled
        // 如果启用了JSON备份，启动独立的JSON备份任务
        if (config.EnableJsonBackup)
        {
            _jsonBackupCts = new CancellationTokenSource();
            _ = jsonBackupService.RunBackupLoopAsync(_jsonBackupCts.Token);
            logger.Success($"JSON backup enabled with interval {config.JsonBackupIntervalMs}ms.");
        }

        logger.Success("HarmonySqliteMod has successfully loaded!");
    }

    /// <summary>
    /// Loads profiles from SQLite that are NOT already in SaveServer memory.
    /// These are profiles created after mod installation (no JSON files).
    /// 从 SQLite 加载不在 SaveServer 内存中的 profile。
    /// 这些是安装模组后创建的 profile（没有对应的 JSON 文件）。
    /// </summary>
    private async Task LoadSqliteOnlyProfilesAsync()
    {
        try
        {
            var saveServer = SPTarkov.Server.Core.DI.ServiceLocator.ServiceProvider.GetService<SaveServer>();
            if (saveServer is null)
            {
                logger.Warning("SaveServer not available. Skipping SQLite profile loading.");
                return;
            }

            var sqliteIds = await sqliteProfileService.GetAllProfileIdsAsync();
            if (sqliteIds.Count == 0)
            {
                logger.Debug("No profiles found in SQLite to load.");
                return;
            }

            var loadedCount = 0;
            foreach (var id in sqliteIds)
            {
                MongoId profileId = id; // implicit conversion from string
                if (!saveServer.ProfileExists(profileId))
                {
                    // This triggers our LoadPatch which loads from SQLite
                    // 这会触发我们的 LoadPatch，从 SQLite 加载
                    await saveServer.LoadProfileAsync(profileId);
                    loadedCount++;
                }
            }

            if (loadedCount > 0)
            {
                logger.Success($"Loaded {loadedCount} profile(s) from SQLite into memory.");
            }
        }
        catch (Exception ex)
        {
            logger.Error($"Failed to load profiles from SQLite: {ex.Message}", ex);
        }
    }

    private async Task RunFlushLoopAsync(CancellationToken ct, int intervalMs)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(intervalMs, ct);
                if (sqliteProfileService.DirtyCount > 0)
                {
                    await sqliteProfileService.FlushAsync();
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        // Final flush on shutdown
        // 关闭时最终刷新
        if (sqliteProfileService.DirtyCount > 0)
        {
            logger.Info("Shutdown: flushing remaining dirty profiles to SQLite...");
            await sqliteProfileService.FlushAsync();
        }
    }
}

/// <summary>
/// Patches SaveServer.LoadProfileAsync to load profile data from SQLite instead of JSON files.
/// 补丁 SaveServer.LoadProfileAsync，从 SQLite 读取档案数据而非 JSON 文件。
/// Uses Prefix to intercept and replace the original loading logic.
/// 使用 Prefix 拦截并替换原始加载逻辑。
/// </summary>
public class SaveServerLoadPatch : AbstractPatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(SaveServer).GetMethod(
            nameof(SaveServer.LoadProfileAsync),
            new[] { typeof(MongoId) }
        );
    }

    [PatchPrefix]
    public static bool Prefix(MongoId sessionID, SaveServer __instance, ref Task __result)
    {
        var logger = ServiceLocator.ServiceProvider.GetService<ISptLogger<SaveServerLoadPatch>>();

        try
        {
            var profilesRef = AccessTools.FieldRefAccess<SaveServer, ConcurrentDictionary<MongoId, SptProfile>>("profiles");

            // If profile is already in memory (e.g. just created by profile/create handler),
            // skip SQLite load to avoid overwriting the complete in-memory profile with a stale snapshot,
            // but STILL run saveLoadRouters to match the original LoadProfileAsync behavior.
            // 如果 profile 已在内存中（如刚由 profile/create 处理程序创建），
            // 跳过 SQLite 加载，避免用过期快照覆盖完整的内存 profile，
            // 但仍需运行 saveLoadRouters 以匹配原始 LoadProfileAsync 行为。
            if (profilesRef(__instance).ContainsKey(sessionID))
            {
                logger?.Debug($"Profile {sessionID} already in memory. Skipping SQLite load.");

                // Run saveLoadRouters on the in-memory profile (original behavior preserves this)
                // 对内存中的 profile 运行 saveLoadRouters（原始行为保留此步骤）
                try
                {
                    var routers = ServiceLocator.ServiceProvider.GetServices<SaveLoadRouter>();
                    foreach (var router in routers)
                    {
                        profilesRef(__instance)[sessionID] = router.HandleLoad(profilesRef(__instance)[sessionID]);
                    }
                }
                catch (Exception routerEx)
                {
                    logger?.Error($"Error running saveLoadRouters for in-memory profile {sessionID}: {routerEx.Message}", routerEx);
                }

                __result = Task.CompletedTask;
                return false;
            }

            var sqliteProfileService = ServiceLocator.ServiceProvider.GetService<SqliteProfileService>();
            if (sqliteProfileService is null)
            {
                logger?.Error("SqliteProfileService not found in ServiceLocator. Falling back to original method.");
                return true;
            }

            // Load raw JSON from SQLite (preserves all data, avoids SptProfile round-trip data loss)
            // 从 SQLite 加载原始 JSON（保留完整数据，避免 SptProfile 折返丢失数据）
            var profileJson = sqliteProfileService.LoadProfileRawJsonAsync(sessionID).GetAwaiter().GetResult();
            if (profileJson is null)
            {
                logger?.Warning($"Profile {sessionID} not found in SQLite. Falling back to original method.");
                return true;
            }

            var jsonUtil = ServiceLocator.ServiceProvider.GetService<JsonUtil>();
            if (jsonUtil is null)
            {
                logger?.Error("JsonUtil not found in ServiceLocator. Falling back to original method.");
                return true;
            }

            // Deserialize raw JSON directly to JsonObject (same path as original SaveServer.LoadProfileAsync)
            // 直接反序列化原始 JSON 为 JsonObject（与原始 SaveServer.LoadProfileAsync 相同路径）
            var jsonObject = jsonUtil.Deserialize<JsonObject>(profileJson);
            if (jsonObject is null)
            {
                logger?.Error($"Failed to deserialize profile {sessionID} raw JSON. Falling back to original method.");
                return true;
            }

            var profileValidatorService = ServiceLocator.ServiceProvider.GetService<ProfileValidatorService>();
            if (profileValidatorService is null)
            {
                logger?.Error("ProfileValidatorService not found in ServiceLocator. Falling back to original method.");
                return true;
            }

            var validatedProfile = profileValidatorService.MigrateAndValidateProfile(jsonObject);
            if (validatedProfile is null)
            {
                logger?.Error($"Profile {sessionID} migration/validation failed. Falling back to original method.");
                return true;
            }

            var saveLoadRouters = ServiceLocator.ServiceProvider.GetServices<SaveLoadRouter>();
            foreach (var router in saveLoadRouters)
            {
                validatedProfile = router.HandleLoad(validatedProfile);
            }

            profilesRef(__instance)[sessionID] = validatedProfile;

            logger?.Success($"Profile {sessionID} loaded from SQLite successfully.");

            // Original method returns Task, not Task<SptProfile?>, so Task.CompletedTask is correct
            // 原始方法返回 Task，不是 Task<SptProfile?>，所以用 Task.CompletedTask
            __result = Task.CompletedTask;
            return false;
        }
        catch (Exception ex)
        {
            logger?.Error($"Error loading profile {sessionID} from SQLite: {ex.Message}", ex);
            return true;
        }
    }
}

/// <summary>
/// Patches SaveServer.SaveProfileAsync to save profile data to SQLite instead of JSON files.
/// 补丁 SaveServer.SaveProfileAsync，将档案数据保存到 SQLite 而非 JSON 文件。
/// Uses Prefix to intercept and replace the original saving logic with cache-only updates.
/// 使用 Prefix 拦截并替换原始保存逻辑为仅缓存更新。
/// The actual SQLite write is deferred to the configured flush interval loop.
/// 实际的 SQLite 写入延迟到配置的刷新间隔循环。
/// JSON backup is handled separately by JsonBackupService if enabled in config.
/// 如果配置中启用了 JSON 备份，由 JsonBackupService 独立处理。
/// </summary>
public class SaveServerSavePatch : AbstractPatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(SaveServer).GetMethod(
            nameof(SaveServer.SaveProfileAsync),
            new[] { typeof(MongoId) }
        );
    }

    [PatchPrefix]
    public static bool Prefix(MongoId sessionID, SaveServer __instance, ref Task<long> __result)
    {
        var logger = ServiceLocator.ServiceProvider.GetService<ISptLogger<SaveServerSavePatch>>();

        try
        {
            if (__instance.IsProfileInvalidOrUnloadable(sessionID))
            {
                __result = Task.FromResult(0L);
                return false;
            }

            var sqliteService = ServiceLocator.ServiceProvider.GetService<SqliteProfileService>();
            if (sqliteService is null)
            {
                logger?.Error("SqliteProfileService not found in ServiceLocator. Falling back to original method.");
                return true;
            }

            SptProfile profile;
            try
            {
                profile = __instance.GetProfile(sessionID);
            }
            catch (Exception ex)
            {
                logger?.Error($"Profile {sessionID} not found in memory: {ex.Message}");
                __result = Task.FromResult(0L);
                return false;
            }

            var onBeforeSaveCallbacksRef = AccessTools.FieldRefAccess<SaveServer, Dictionary<string, Func<SptProfile, SptProfile>>>("onBeforeSaveCallbacks");
            var onBeforeSaveCallbacks = onBeforeSaveCallbacksRef(__instance);
            if (onBeforeSaveCallbacks != null)
            {
                foreach (var callback in onBeforeSaveCallbacks)
                {
                    var previous = profile;
                    try
                    {
                        profile = callback.Value(profile);
                    }
                    catch (Exception e)
                    {
                        logger?.Error($"Profile save callback error for {sessionID}: {e.Message}", e);
                        profile = previous;
                    }
                }
            }

            var profilesRef = AccessTools.FieldRefAccess<SaveServer, ConcurrentDictionary<MongoId, SptProfile>>("profiles");
            profilesRef(__instance)[sessionID] = profile;

            sqliteService.SaveProfileAsync(sessionID, profile).GetAwaiter().GetResult();

            logger?.Success($"Profile {sessionID} cached for delayed SQLite flush.");

            __result = Task.FromResult(0L);
            return false;
        }
        catch (Exception ex)
        {
            logger?.Error($"Error saving profile {sessionID} to cache: {ex.Message}", ex);
            return true;
        }
    }
}

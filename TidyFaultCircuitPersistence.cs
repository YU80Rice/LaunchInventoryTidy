using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using BepInEx;
using Newtonsoft.Json;
using Steamworks;

namespace LaunchInventoryTidy
{
    /// <summary>
    /// v2.0.6 修订：持久熔断磁盘持久化（fail-closed + 安全初始化事务 + 格式版本迁移）。
    ///
    /// Codex v2.0.5 第六次静态审计 §2 要求：
    ///   - P0-1 Critical：marker 必须先于 main 落盘，避免崩溃窗口导致安全状态丢失被误判为首次安装
    ///   - P1-1 Medium：FORMAT_VERSION 必须升级到 2，v1 文件含 restoreVerified 字段需迁移
    ///   - P1-2 Medium：recover 必须以磁盘快照原子替换内存状态，不是增量注入
    ///
    /// 三状态机（v2.0.6 修订）：
    ///   UNINITIALIZED -> 首次启动：先 marker 后 main 原子创建 -> HEALTHY
    ///   HEALTHY -> 主文件有效加载；marker 缺失则补建（失败 -> DEGRADED）
    ///   HEALTHY -> 主文件无效但备份有效 -> 从备份加载并原子修复主文件
    ///   HEALTHY -> marker 存在 + 主/备份均无效 -> DEGRADED（不自动空初始化）
    ///   DEGRADED -> 所有整理 fail-closed；仅授权 /tidy_fault_recover 可触发完整重载
    ///   DEGRADED -> ReplacePersistentFromSnapshot 成功 -> HEALTHY；失败 -> 保持 DEGRADED
    ///
    /// 文件目录：BepInEx/config/LaunchInventoryTidy/fault_scopes/
    /// 文件路径：persistent_faults_{mode}_{safeMap}_{mapHash}_slot{slot}.json
    /// 备份路径：persistent_faults_{mode}_{safeMap}_{mapHash}_slot{slot}.json.bak
    /// 临时路径：persistent_faults_{mode}_{safeMap}_{mapHash}_slot{slot}.json.tmp
    /// 初始化标记：persistent_faults_{mode}_{safeMap}_{mapHash}_slot{slot}.json.initialized
    /// 标记临时：persistent_faults_{mode}_{safeMap}_{mapHash}_slot{slot}.json.initialized.tmp
    /// mapHash = SHA256(mapName) 前 8 字节十六进制，防 sanitization 碰撞导致跨世界持久化污染。
    /// SteamID 只保留在 JSON 记录内容中，不得写入文件名。
    /// 旧全局 persistent_faults.json 已废弃（P0-LIT-02），新逻辑不得读取它。
    /// </summary>
    public static class TidyFaultCircuitPersistence
    {
        private static readonly object _ioLock = new object();
        private static string _filePath;
        private static string _tmpPath;
        private static string _bakPath;
        private static string _initializedMarkerPath;
        private static string _initializedMarkerTmpPath;
        private static string _directoryPath;

        /// <summary>
        /// v2.0.6 P1-1：文件格式版本升级到 2。
        /// v1（v2.0.4 及之前）：含 restoreVerified 字段，schema 破坏性变更后无法兼容。
        /// v2（v2.0.5 起）：删除 restoreVerified 字段，所有持久熔断强制 RestoreVerified=false。
        /// 读取 v1 文件时，必须验证 restoreVerified=false 再迁移到 v2 格式。
        /// </summary>
        public const int FORMAT_VERSION = 2;

        /// <summary>
        /// v2.0.4 P0-3：全局持久化降级标志。
        /// true 时所有整理请求必须在读取/修改库存前被拒绝，直到管理员修复并显式确认。
        /// </summary>
        public static bool GlobalFaultPersistenceDegraded { get; private set; }

        /// <summary>
        /// P0-LIT-02 R2：无副作用 scope 参数校验。
        /// 在任何状态变更前独立校验 mode/mapName/saveSlot 合法性，校验失败抛异常，调用方负责 fail-closed。
        /// 不修改 _filePath 等静态字段，不清空运行时熔断。
        /// </summary>
        internal static void ValidateScopeArguments(string mode, string mapName, int saveSlot)
        {
            if (!string.Equals(mode, "singleplayer", StringComparison.Ordinal) &&
                !string.Equals(mode, "p2p", StringComparison.Ordinal))
                throw new ArgumentOutOfRangeException(nameof(mode), "Unsupported fault scope mode.");

            if (string.IsNullOrWhiteSpace(mapName))
                throw new ArgumentException("Map name is required.", nameof(mapName));

            if (saveSlot < 0 || saveSlot > 4)
                throw new ArgumentOutOfRangeException(nameof(saveSlot));

            string ignored = BuildScopeFileStem(mode, mapName, saveSlot);
        }

        /// <summary>
        /// P0-LIT-02 R2：会话作用域隔离 - 唯一初始化入口。
        /// 按 mode + safeMap + mapHash + saveSlot 隔离持久熔断文件，SteamID 不入文件名。
        /// 内部调用 ValidateScopeArguments 校验参数；校验通过后才修改静态字段。
        /// 旧全局 persistent_faults.json 已废弃，新逻辑不得读取它。
        /// 调用时机：必须在世界、模式、slot 已稳定的会话边界执行。
        /// 切换 scope 前必须先清空运行时持久熔断状态：
        ///   TidyFaultCircuit.ReplacePersistentFromSnapshot(new List&lt;PersistentRecord&gt;());
        /// 然后才调用 Load()。
        /// </summary>
        internal static void InitializeForScope(string mode, string mapName, int saveSlot)
        {
            ValidateScopeArguments(mode, mapName, saveSlot);
            string fileStem = BuildScopeFileStem(mode, mapName, saveSlot);

            lock (_ioLock)
            {
                _directoryPath = Path.Combine(Paths.ConfigPath, "LaunchInventoryTidy", "fault_scopes");
                Directory.CreateDirectory(_directoryPath);
                _filePath = Path.Combine(_directoryPath, "persistent_faults_" + fileStem + ".json");
                _tmpPath = _filePath + ".tmp";
                _bakPath = _filePath + ".bak";
                _initializedMarkerPath = _filePath + ".initialized";
                _initializedMarkerTmpPath = _initializedMarkerPath + ".tmp";
                GlobalFaultPersistenceDegraded = false;
            }
        }

        /// <summary>
        /// P0-LIT-02 R2：构建 scope 文件名 stem。
        /// 格式：{mode}_{safeMap}_{mapHash}_slot{slot}
        /// - safeMap：mapName 中字母/数字/-/_ 保留，其余替换为 _，首尾 _ 去除，最长 48 字符
        /// - mapHash：SHA256(mapName) 前 8 字节十六进制，防 sanitization 碰撞（如 "A/B" 与 "A?B" 都规范化为 "A_B"）
        /// </summary>
        private static string BuildScopeFileStem(string mode, string mapName, int saveSlot)
        {
            var safe = new StringBuilder(mapName.Length);
            foreach (char c in mapName)
            {
                safe.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
            }

            string safeMap = safe.ToString().Trim('_');
            if (safeMap.Length == 0)
                throw new ArgumentException("Map name has no usable filename characters.", nameof(mapName));
            if (safeMap.Length > 48)
                safeMap = safeMap.Substring(0, 48);

            string mapHash;
            using (SHA256 sha256 = SHA256.Create())
            {
                byte[] hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(mapName));
                var hex = new StringBuilder(16);
                for (int i = 0; i < 8; ++i)
                    hex.Append(hash[i].ToString("X2"));
                mapHash = hex.ToString();
            }

            return mode + "_" + safeMap + "_" + mapHash + "_slot" + saveSlot;
        }

        /// <summary>
        /// v2.0.4 P0-3：显式设置降级状态（供管理员修复后清除）。
        /// </summary>
        public static void SetDegraded(string reason)
        {
            if (!GlobalFaultPersistenceDegraded)
            {
                LaunchInventoryTidyPlugin.Log?.LogError(
                    $"[FaultPersistence] 进入全局持久化降级状态：{reason}");
            }
            GlobalFaultPersistenceDegraded = true;
        }

        /// <summary>
        /// v2.0.5 P0-4/P0-5：管理员显式确认修复后清除降级状态。
        /// 必须先成功执行一次 LoadInternal() 才允许清除。
        /// 由 /tidy_fault_recover 授权命令调用。
        /// v2.0.6 P1-2 修订：使用 ReplacePersistentFromSnapshot 原子替换内存状态。
        /// </summary>
        public struct RecoveryResult
        {
            public bool Success;
            public int LoadedCount;
            public bool FromBackup;
            public string FailureReason;
        }

        public static RecoveryResult TryClearDegraded()
        {
            if (_filePath == null) return new RecoveryResult { FailureReason = "未初始化" };
            lock (_ioLock)
            {
                // 先尝试创建首次启动文件（若 .initialized 标记不存在且主/备都不存在）
                EnsureFirstBootInitialized();

                var result = LoadInternal();
                if (result.Success)
                {
                    GlobalFaultPersistenceDegraded = false;
                    LaunchInventoryTidyPlugin.Log?.LogInfo(
                        $"[FaultPersistence] 管理员已确认修复，清除降级状态（loaded={result.LoadedCount}, fromBackup={result.FromBackup}）");
                    return new RecoveryResult
                    {
                        Success = true,
                        LoadedCount = result.LoadedCount,
                        FromBackup = result.FromBackup,
                    };
                }
                return new RecoveryResult { FailureReason = result.FailureReason };
            }
        }

        /// <summary>启动时加载持久熔断记录，重新注入 TidyFaultCircuit。</summary>
        public static int Load()
        {
            lock (_ioLock)
            {
                // v2.0.5 P0-4：首次启动先原子创建合法空文件 + .initialized 标记
                EnsureFirstBootInitialized();

                var result = LoadInternal();
                return result.LoadedCount;
            }
        }

        private struct LoadResult
        {
            public bool Success;
            public int LoadedCount;
            public bool FromBackup;
            public string FailureReason;
        }

        /// <summary>
        /// v2.0.6 P0-1 修订：首次启动检测 + 安全初始化提交顺序。
        /// Codex v2.0.5 第六次审计 §2 Critical 指出：原实现先写 main 后写 marker，
        /// 若进程在两步之间终止，下次启动因 main 有效而进入 HEALTHY，但 marker 永久缺失；
        /// 以后 main 被删除时会被误判为"从未初始化"，自动创建空文件，安全锁可能 fail-open。
        ///
        /// 修订提交顺序：marker 先于 main 落盘。
        ///   - marker 存在 + main 缺失/无效 = 曾初始化但 main 丢失 -> DEGRADED（不自动空初始化）
        ///   - marker 缺失 + main 缺失 + backup 缺失 = 真正首次启动 -> 创建 marker + main
        ///   - marker 缺失 + main/backup 有效 = 曾初始化但 marker 丢失 -> 加载后补建 marker（失败 -> DEGRADED）
        /// </summary>
        private static void EnsureFirstBootInitialized()
        {
            if (_filePath == null) return;

            bool mainExists = File.Exists(_filePath);
            bool bakExists = File.Exists(_bakPath);
            bool markerExists = File.Exists(_initializedMarkerPath);

            // 真正首次启动：marker、main、backup 全部不存在
            if (!mainExists && !bakExists && !markerExists)
            {
                try
                {
                    // v2.0.6 P0-1：marker 先于 main 落盘
                    // Step 1: marker.tmp + Flush(true) + Move
                    string markerContent = DateTime.UtcNow.ToString("o");
                    WriteFileWithSync(_initializedMarkerTmpPath, markerContent);
                    File.Move(_initializedMarkerTmpPath, _initializedMarkerPath);

                    // Step 2: main file (tmp + Flush(true) + Move)
                    var dto = new PersistentFaultFile
                    {
                        version = FORMAT_VERSION,
                        persistentFaults = new List<PersistentFaultRecord>(),
                    };
                    string json = JsonConvert.SerializeObject(dto, Formatting.Indented);
                    WriteFileWithSync(_tmpPath, json);
                    File.Move(_tmpPath, _filePath);

                    LaunchInventoryTidyPlugin.Log?.LogInfo(
                        "[FaultPersistence] 首次启动：已原子创建 .initialized 标记 + 合法空文件（marker 先于 main 落盘）");
                }
                catch (Exception e)
                {
                    LaunchInventoryTidyPlugin.Log?.LogError(
                        $"[FaultPersistence] 首次启动原子初始化失败: {e}");
                    SetDegraded("首次启动原子初始化失败: " + e.Message);
                }
                return;
            }

            // v2.0.6 P0-1：marker 缺失但 main/backup 存在
            // 这是"曾初始化但 marker 丢失"的恢复路径，不在此自动空初始化。
            // LoadInternal 成功后会补建 marker；失败则 DEGRADED。
            if (!markerExists && (mainExists || bakExists))
            {
                LaunchInventoryTidyPlugin.Log?.LogWarning(
                    "[FaultPersistence] 检测到 main/bak 存在但 marker 缺失，将在加载验证后补建 marker");
            }
        }

        /// <summary>
        /// v2.0.5 P1-3：使用 FileStream + Flush(true) 真正落盘，禁止用 File.WriteAllText 替代。
        /// </summary>
        private static void WriteFileWithSync(string path, string content)
        {
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var sw = new StreamWriter(fs))
            {
                sw.Write(content);
                sw.Flush();
                fs.Flush(true);  // 强制刷盘到磁盘
            }
        }

        /// <summary>
        /// v2.0.6 P0-1 修订：加载逻辑 - 主文件完整验证 -> 备份完整验证 -> 降级
        /// 任何无法证明完整加载的情况都必须全局拒绝整理。
        /// 加载成功后验证/补建 marker（失败 -> DEGRADED）。
        /// </summary>
        private static LoadResult LoadInternal()
        {
            if (_filePath == null)
            {
                SetDegraded("文件路径未初始化");
                return new LoadResult { Success = false, LoadedCount = 0, FailureReason = "未初始化" };
            }

            // 主文件
            var primary = TryLoadFile(_filePath);
            if (primary.Success)
            {
                // v2.0.6 P1-2：使用 ReplacePersistentFromSnapshot 原子替换内存状态
                int loaded = TidyFaultCircuit.ReplacePersistentFromSnapshot(primary.Records);

                // v2.0.6 P0-1：加载成功后验证/补建 marker
                if (!File.Exists(_initializedMarkerPath))
                {
                    try
                    {
                        string markerContent = DateTime.UtcNow.ToString("o");
                        WriteFileWithSync(_initializedMarkerTmpPath, markerContent);
                        File.Move(_initializedMarkerTmpPath, _initializedMarkerPath);
                        LaunchInventoryTidyPlugin.Log?.LogInfo(
                            "[FaultPersistence] 主文件加载成功，已补建 .initialized 标记");
                    }
                    catch (Exception e)
                    {
                        LaunchInventoryTidyPlugin.Log?.LogError(
                            $"[FaultPersistence] 主文件加载成功但补建 marker 失败: {e}");
                        SetDegraded("补建 marker 失败: " + e.Message);
                        return new LoadResult
                        {
                            Success = false,
                            LoadedCount = 0,
                            FailureReason = "补建 marker 失败: " + e.Message,
                        };
                    }
                }

                // v2.0.6 P1-1：若文件是 v1 格式，迁移到 v2（原子写入）
                if (primary.DetectedVersion == 1)
                {
                    LaunchInventoryTidyPlugin.Log?.LogInfo(
                        "[FaultPersistence] 检测到 v1 格式文件，启动迁移到 v2");
                    var migrateResult = Save();
                    if (!migrateResult.Success)
                    {
                        LaunchInventoryTidyPlugin.Log?.LogWarning(
                            $"[FaultPersistence] v1->v2 迁移写盘失败，但内存已加载：{migrateResult.FailureReason}");
                        // 内存已加载，迁移失败不阻塞当前加载，但记录警告
                    }
                }

                LaunchInventoryTidyPlugin.Log?.LogInfo(
                    $"[FaultPersistence] 已从主文件加载 {loaded} 条持久熔断记录（formatVersion={primary.DetectedVersion}）");
                return new LoadResult { Success = true, LoadedCount = loaded, FromBackup = false };
            }

            LaunchInventoryTidyPlugin.Log?.LogWarning(
                $"[FaultPersistence] 主文件加载失败：{primary.FailureReason}，尝试备份文件");

            // 备份文件
            if (File.Exists(_bakPath))
            {
                var backup = TryLoadFile(_bakPath);
                if (backup.Success)
                {
                    // v2.0.6 P1-2：使用 ReplacePersistentFromSnapshot
                    int loaded = TidyFaultCircuit.ReplacePersistentFromSnapshot(backup.Records);

                    // v2.0.6 P0-1：加载成功后验证/补建 marker
                    if (!File.Exists(_initializedMarkerPath))
                    {
                        try
                        {
                            string markerContent = DateTime.UtcNow.ToString("o");
                            WriteFileWithSync(_initializedMarkerTmpPath, markerContent);
                            File.Move(_initializedMarkerTmpPath, _initializedMarkerPath);
                            LaunchInventoryTidyPlugin.Log?.LogInfo(
                                "[FaultPersistence] 备份文件加载成功，已补建 .initialized 标记");
                        }
                        catch (Exception e)
                        {
                            LaunchInventoryTidyPlugin.Log?.LogError(
                                $"[FaultPersistence] 备份文件加载成功但补建 marker 失败: {e}");
                            SetDegraded("补建 marker 失败: " + e.Message);
                            return new LoadResult
                            {
                                Success = false,
                                LoadedCount = 0,
                                FailureReason = "补建 marker 失败: " + e.Message,
                            };
                        }
                    }

                    // 尝试用备份恢复主文件
                    TryRestoreFromBackup();
                    LaunchInventoryTidyPlugin.Log?.LogWarning(
                        $"[FaultPersistence] 已从备份文件加载 {loaded} 条持久熔断记录（主文件损坏）");
                    return new LoadResult { Success = true, LoadedCount = loaded, FromBackup = true };
                }
                LaunchInventoryTidyPlugin.Log?.LogError(
                    $"[FaultPersistence] 备份文件也加载失败：{backup.FailureReason}");
            }

            // v2.0.6 P0-1：marker 存在 + main/backup 均无效 -> DEGRADED（不自动空初始化）
            // 这是关键的安全行为：marker 证明曾初始化过，main 丢失意味着安全状态丢失
            bool markerExists = File.Exists(_initializedMarkerPath);
            if (markerExists)
            {
                SetDegraded($"marker 存在但主文件与备份文件均无法完整加载（主：{primary.FailureReason}）- 安全状态丢失，不自动空初始化");
            }
            else
            {
                SetDegraded($"主文件与备份文件均无法完整加载且无 marker（主：{primary.FailureReason}）");
            }
            return new LoadResult { Success = false, LoadedCount = 0, FailureReason = primary.FailureReason };
        }

        private struct FileLoadResult
        {
            public bool Success;
            public int DetectedVersion;
            public List<PersistentRecord> Records;
            public string FailureReason;
        }

        /// <summary>
        /// v2.0.6 P1-1：尝试加载并完整验证单个文件，支持 v1/v2 格式检测与迁移。
        /// </summary>
        private static FileLoadResult TryLoadFile(string path)
        {
            try
            {
                if (!File.Exists(path)) return new FileLoadResult { Success = false, FailureReason = "文件不存在" };

                string json = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(json))
                    return new FileLoadResult { Success = false, FailureReason = "文件为空" };

                // v2.0.6 P1-1：先宽松解析 version 字段，确定格式版本
                int detectedVersion;
                try
                {
                    var peekSettings = new JsonSerializerSettings
                    {
                        MissingMemberHandling = MissingMemberHandling.Ignore,
                        NullValueHandling = NullValueHandling.Include,
                    };
                    var peek = JsonConvert.DeserializeObject<PersistentFaultFile>(json, peekSettings);
                    if (peek == null)
                        return new FileLoadResult { Success = false, FailureReason = "version peek 反序列化结果为 null" };
                    detectedVersion = peek.version;
                }
                catch (Exception ex)
                {
                    return new FileLoadResult { Success = false, FailureReason = "version peek 失败: " + ex.Message };
                }

                if (detectedVersion == 1)
                {
                    return TryLoadV1File(json);
                }
                else if (detectedVersion == FORMAT_VERSION)
                {
                    return TryLoadV2File(json);
                }
                else
                {
                    return new FileLoadResult
                    {
                        Success = false,
                        FailureReason = $"不支持的版本（期望 1 或 {FORMAT_VERSION}，实际 {detectedVersion}）",
                    };
                }
            }
            catch (Exception e)
            {
                return new FileLoadResult { Success = false, FailureReason = e.Message };
            }
        }

        /// <summary>
        /// v2.0.6 P1-1：v1 文件解析（含 restoreVerified 字段）。
        /// 迁移规则：所有记录的 restoreVerified 必须为 false，否则拒绝（DEGRADED）。
        /// </summary>
        private static FileLoadResult TryLoadV1File(string json)
        {
            PersistentFaultFileV1 v1Dto;
            try
            {
                var settings = new JsonSerializerSettings
                {
                    MissingMemberHandling = MissingMemberHandling.Error,
                    NullValueHandling = NullValueHandling.Include,
                };
                v1Dto = JsonConvert.DeserializeObject<PersistentFaultFileV1>(json, settings);
            }
            catch (Exception ex)
            {
                return new FileLoadResult { Success = false, FailureReason = "v1 JSON 反序列化失败: " + ex.Message };
            }

            if (v1Dto == null)
                return new FileLoadResult { Success = false, FailureReason = "v1 反序列化结果为 null" };

            if (v1Dto.persistentFaults == null)
                return new FileLoadResult { Success = false, FailureReason = "v1 persistentFaults 字段缺失" };

            var seen = new HashSet<ulong>();
            var records = new List<PersistentRecord>(v1Dto.persistentFaults.Count);
            for (int i = 0; i < v1Dto.persistentFaults.Count; i++)
            {
                var r = v1Dto.persistentFaults[i];
                if (r.steamId == 0)
                    return new FileLoadResult { Success = false, FailureReason = $"v1 记录 {i}: steamId 为 0" };
                if (!seen.Add(r.steamId))
                    return new FileLoadResult { Success = false, FailureReason = $"v1 记录 {i}: steamId {r.steamId} 重复" };
                if (r.openedAt == default(DateTime))
                    return new FileLoadResult { Success = false, FailureReason = $"v1 记录 {i}: openedAt 缺失或无效" };
                if (string.IsNullOrEmpty(r.reason))
                    return new FileLoadResult { Success = false, FailureReason = $"v1 记录 {i}: reason 缺失" };

                // v2.0.6 P1-1：v1 迁移门 - restoreVerified 必须为 false
                if (r.restoreVerified)
                    return new FileLoadResult
                    {
                        Success = false,
                        FailureReason = $"v1 记录 {i}: restoreVerified=true，无法迁移到 v2（持久熔断文件不允许 restoreVerified=true）",
                    };

                records.Add(new PersistentRecord
                {
                    SteamId = r.steamId,
                    Reason = r.reason,
                    OpenedAt = r.openedAt,
                    RestoreVerified = false,
                });
            }

            return new FileLoadResult { Success = true, DetectedVersion = 1, Records = records };
        }

        /// <summary>v2.0.6 P1-1：v2 文件解析（无 restoreVerified 字段）。</summary>
        private static FileLoadResult TryLoadV2File(string json)
        {
            PersistentFaultFile dto;
            try
            {
                var settings = new JsonSerializerSettings
                {
                    MissingMemberHandling = MissingMemberHandling.Error,
                    NullValueHandling = NullValueHandling.Include,
                };
                dto = JsonConvert.DeserializeObject<PersistentFaultFile>(json, settings);
            }
            catch (Exception ex)
            {
                return new FileLoadResult { Success = false, FailureReason = "v2 JSON 反序列化失败: " + ex.Message };
            }

            if (dto == null)
                return new FileLoadResult { Success = false, FailureReason = "v2 反序列化结果为 null" };

            if (dto.version != FORMAT_VERSION)
                return new FileLoadResult { Success = false, FailureReason = $"v2 版本不匹配（期望 {FORMAT_VERSION}，实际 {dto.version}）" };

            if (dto.persistentFaults == null)
                return new FileLoadResult { Success = false, FailureReason = "v2 persistentFaults 字段缺失" };

            var seen = new HashSet<ulong>();
            var records = new List<PersistentRecord>(dto.persistentFaults.Count);
            for (int i = 0; i < dto.persistentFaults.Count; i++)
            {
                var r = dto.persistentFaults[i];
                if (r.steamId == 0)
                    return new FileLoadResult { Success = false, FailureReason = $"v2 记录 {i}: steamId 为 0" };
                if (!seen.Add(r.steamId))
                    return new FileLoadResult { Success = false, FailureReason = $"v2 记录 {i}: steamId {r.steamId} 重复" };
                if (r.openedAt == default(DateTime))
                    return new FileLoadResult { Success = false, FailureReason = $"v2 记录 {i}: openedAt 缺失或无效" };
                if (string.IsNullOrEmpty(r.reason))
                    return new FileLoadResult { Success = false, FailureReason = $"v2 记录 {i}: reason 缺失" };

                records.Add(new PersistentRecord
                {
                    SteamId = r.steamId,
                    Reason = r.reason,
                    OpenedAt = r.openedAt,
                    RestoreVerified = false,
                });
            }

            return new FileLoadResult { Success = true, DetectedVersion = FORMAT_VERSION, Records = records };
        }

        /// <summary>尝试用备份文件恢复主文件。</summary>
        private static void TryRestoreFromBackup()
        {
            try
            {
                if (File.Exists(_bakPath))
                {
                    File.Copy(_bakPath, _filePath, overwrite: true);
                    LaunchInventoryTidyPlugin.Log?.LogInfo(
                        "[FaultPersistence] 已用备份文件恢复主文件");
                }
            }
            catch (Exception e)
            {
                LaunchInventoryTidyPlugin.Log?.LogWarning(
                    "[FaultPersistence] 用备份恢复主文件失败: " + e.Message);
            }
        }

        /// <summary>
        /// v2.0.4 P0-3：将当前所有持久熔断（RestoreVerified=false）原子写入磁盘。
        /// 返回结构化结果，失败时设置全局降级。
        /// v2.0.5 P1-3：使用 FileStream + Flush(true) 真正落盘。
        /// v2.0.6 P1-1：写入 FORMAT_VERSION=2 格式。
        /// </summary>
        public struct SaveResult
        {
            public bool Success;
            public int SavedCount;
            public string FailureReason;
        }

        public static SaveResult Save()
        {
            if (_filePath == null) return new SaveResult { Success = false, FailureReason = "未初始化" };
            lock (_ioLock)
            {
                try
                {
                    var persistent = TidyFaultCircuit.GetPersistentSnapshot();
                    var dto = new PersistentFaultFile
                    {
                        version = FORMAT_VERSION,
                        persistentFaults = new List<PersistentFaultRecord>(persistent.Count),
                    };
                    foreach (var r in persistent)
                    {
                        dto.persistentFaults.Add(new PersistentFaultRecord
                        {
                            steamId = r.SteamId,
                            reason = r.Reason ?? "",
                            openedAt = r.OpenedAt,
                        });
                    }

                    string json = JsonConvert.SerializeObject(dto, Formatting.Indented);

                    // v2.0.5 P1-3：使用 FileStream + Flush(true) 真正落盘
                    WriteFileWithSync(_tmpPath, json);

                    // File.Replace 原子替换目标文件，并自动生成 .bak
                    if (File.Exists(_filePath))
                    {
                        File.Replace(_tmpPath, _filePath, _bakPath);
                    }
                    else
                    {
                        // 首次写入，没有目标文件，直接移动
                        File.Move(_tmpPath, _filePath);
                    }

                    // v2.0.6 P0-1：若 .initialized 标记不存在，写入它（marker 先于后续 Save）
                    if (!File.Exists(_initializedMarkerPath))
                    {
                        string markerContent = DateTime.UtcNow.ToString("o");
                        WriteFileWithSync(_initializedMarkerTmpPath, markerContent);
                        File.Move(_initializedMarkerTmpPath, _initializedMarkerPath);
                    }

                    // v2.0.5 P1-3：写后回读验证，确保磁盘内容可被完整反序列化
                    var verify = TryLoadFile(_filePath);
                    if (!verify.Success)
                    {
                        throw new IOException("写后回读验证失败: " + verify.FailureReason);
                    }

                    return new SaveResult { Success = true, SavedCount = persistent.Count };
                }
                catch (Exception e)
                {
                    string reason = e.Message;
                    LaunchInventoryTidyPlugin.Log?.LogError(
                        $"[FaultPersistence] Save 失败: {e}");
                    SetDegraded("Save 失败: " + reason);
                    return new SaveResult { Success = false, FailureReason = reason };
                }
            }
        }

        // ─────────────────────────────────────────────────────────────
        // JSON DTO（Newtonsoft.Json 反序列化目标）
        // ─────────────────────────────────────────────────────────────

        /// <summary>v2 格式顶层结构（v2.0.5 起，不含 restoreVerified 字段）。</summary>
        public class PersistentFaultFile
        {
            public int version;
            public List<PersistentFaultRecord> persistentFaults;
        }

        /// <summary>
        /// v2 单条持久熔断记录。
        /// v2.0.5 P1-2：删除冗余 restoreVerified 字段。
        ///   - 本文件只能包含持久熔断（RestoreVerified=false），所有记录注入时强制为 false
        ///   - 任何未知字段（包括手写 restoreVerified=true）会被 MissingMemberHandling.Error 拒绝
        /// </summary>
        public class PersistentFaultRecord
        {
            public ulong steamId;
            public string reason;
            public DateTime openedAt;
        }

        /// <summary>
        /// v2.0.6 P1-1：v1 顶层结构（含 restoreVerified 字段），仅用于读取和迁移 v1 文件。
        /// </summary>
        public class PersistentFaultFileV1
        {
            public int version;
            public List<PersistentFaultRecordV1> persistentFaults;
        }

        /// <summary>v2.0.6 P1-1：v1 单条记录（含 restoreVerified 字段）。</summary>
        public class PersistentFaultRecordV1
        {
            public ulong steamId;
            public string reason;
            public DateTime openedAt;
            public bool restoreVerified;
        }

        /// <summary>向后兼容的公开结构体（供外部读取）。</summary>
        public struct PersistentRecord
        {
            public ulong SteamId;
            public string Reason;
            public DateTime OpenedAt;
            public bool RestoreVerified;
        }
    }
}

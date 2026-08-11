using System;
using System.Collections.Generic;
using Steamworks;

namespace LaunchInventoryTidy
{
    /// <summary>
    /// 玩家级故障熔断器（v2.0.2 修订）。
    ///
    /// v2.0.1 缺陷：
    ///   - 玩家断开时无条件清除熔断，重新登录即可恢复整理权限
    ///   - 即使回滚失败、库存仍异常，熔断也被清除
    ///
    /// v2.0.2 修复：
    ///   - ClearPlayer 仅清除 RestoreVerified=true 的临时熔断
    ///   - RestoreVerified=false 的持久熔断不得因断线自动删除
    ///   - 持久熔断必须由管理员显式 TryClose 恢复
    /// </summary>
    public static class TidyFaultCircuit
    {
        public enum State : byte
        {
            /// <summary>正常，允许整理。</summary>
            Closed = 0,

            /// <summary>已熔断，拒绝所有整理请求。</summary>
            Open = 1,
        }

        /// <summary>
        /// v2.0.3 P2-L13 新增：ClearPlayer 的处理结果。
        /// 让调用方知道实际发生了什么（已删除 / 保留持久熔断 / 未找到）。
        /// </summary>
        public enum ClearPlayerResult : byte
        {
            NotFound = 0,
            Removed = 1,
            Preserved = 2,
        }

        public struct FaultRecord
        {
            public State State;
            public DateTime OpenedAt;
            public string Reason;
            public bool RestoreVerified;
        }

        private static readonly Dictionary<CSteamID, FaultRecord> _states =
            new Dictionary<CSteamID, FaultRecord>();
        private static readonly object _lock = new object();

        /// <summary>查询玩家当前熔断状态（不修改）。</summary>
        public static State GetState(CSteamID steamId)
        {
            lock (_lock)
            {
                if (!_states.TryGetValue(steamId, out var rec)) return State.Closed;
                return rec.State;
            }
        }

        /// <summary>是否允许整理（Closed=true, Open=false）。</summary>
        /// <remarks>
        /// v2.0.4 P0-3：全局持久化降级状态下拒绝所有整理请求，
        /// 直到管理员修复并显式确认（TryClearDegraded）。
        /// </remarks>
        public static bool IsAllowed(CSteamID steamId)
        {
            if (TidyFaultCircuitPersistence.GlobalFaultPersistenceDegraded) return false;
            return GetState(steamId) == State.Closed;
        }

        /// <summary>熔断玩家。reason 必须是人类可读的失败原因。</summary>
        /// <param name="restoreVerified">
        /// true = 回滚已验证恢复，临时熔断（断线可清除）
        /// false = 回滚未验证或失败，持久熔断（断线不得清除，必须管理员显式恢复，写盘）
        /// </param>
        public static void Open(CSteamID steamId, string reason, bool restoreVerified)
        {
            lock (_lock)
            {
                _states[steamId] = new FaultRecord
                {
                    State = State.Open,
                    OpenedAt = DateTime.UtcNow,
                    Reason = reason ?? "unknown",
                    RestoreVerified = restoreVerified,
                };
            }
            LaunchInventoryTidyPlugin.Log?.LogError(
                $"[FaultCircuit] 玩家 {(ulong)steamId} 已熔断：{reason} (restoreVerified={restoreVerified}, persistent={!restoreVerified})");

            // v2.0.3 P0-C4：持久熔断立即写盘
            // v2.0.4 P0-3：检查 Save() 结果，失败则进入全局降级（Save 内部已处理）
            if (!restoreVerified)
            {
                var saveResult = TidyFaultCircuitPersistence.Save();
                if (!saveResult.Success)
                {
                    LaunchInventoryTidyPlugin.Log?.LogError(
                        $"[FaultCircuit] 持久熔断写盘失败，已进入全局降级状态：{saveResult.FailureReason}");
                }
            }
        }

        /// <summary>显式恢复玩家整理能力。仅管理员命令 / 显式核验调用。</summary>
        public static bool TryClose(CSteamID steamId, out FaultRecord wasOpen)
        {
            lock (_lock)
            {
                if (_states.TryGetValue(steamId, out var rec) && rec.State == State.Open)
                {
                    _states.Remove(steamId);
                    wasOpen = rec;
                    LaunchInventoryTidyPlugin.Log?.LogInfo(
                        $"[FaultCircuit] 玩家 {(ulong)steamId} 已恢复整理能力（原熔断原因：{rec.Reason}, restoreVerified={rec.RestoreVerified}）");

                    // v2.0.3 P0-C4：持久熔断被解除时写盘
                    // v2.0.4 P0-3：检查 Save() 结果，失败则进入全局降级
                    if (!rec.RestoreVerified)
                    {
                        var saveResult = TidyFaultCircuitPersistence.Save();
                        if (!saveResult.Success)
                        {
                            LaunchInventoryTidyPlugin.Log?.LogError(
                                $"[FaultCircuit] 持久熔断解除后写盘失败，已进入全局降级状态：{saveResult.FailureReason}");
                        }
                    }
                    return true;
                }
                wasOpen = default;
                return false;
            }
        }

        /// <summary>
        /// v2.0.3 P0-C4 新增：启动时从磁盘加载持久熔断记录，重新注入内存。
        /// 仅由 TidyFaultCircuitPersistence.Load 调用。
        /// v2.0.6 P1-2：保留用于增量注入，但 LoadInternal 已改用 ReplacePersistentFromSnapshot。
        /// </summary>
        internal static void RestoreFromPersistence(CSteamID steamId, string reason, DateTime openedAt)
        {
            lock (_lock)
            {
                _states[steamId] = new FaultRecord
                {
                    State = State.Open,
                    OpenedAt = openedAt,
                    Reason = reason ?? "loaded from disk",
                    RestoreVerified = false,  // 持久熔断永远是 RestoreVerified=false
                };
            }
        }

        /// <summary>
        /// v2.0.6 P1-2 新增：以磁盘快照原子替换内存中的所有持久熔断。
        /// Codex v2.0.5 第六次审计 §2 Medium 指出：原 Load 使用 InjectRecords 增量注入，
        /// 管理员修复文件并删除某条记录后执行 recover，旧内存记录仍保留；
        /// 返回的 LoadedCount 也不代表最终内存集合。
        ///
        /// 修订语义：
        ///   - 在单一锁内
        ///   - 保留临时熔断（RestoreVerified=true）
        ///   - 原子替换全部持久熔断（RestoreVerified=false）
        ///   - 返回最终内存集合数量
        /// </summary>
        public static int ReplacePersistentFromSnapshot(List<TidyFaultCircuitPersistence.PersistentRecord> snapshot)
        {
            if (snapshot == null) snapshot = new List<TidyFaultCircuitPersistence.PersistentRecord>(0);

            lock (_lock)
            {
                // 保留临时熔断
                var temporary = new List<KeyValuePair<CSteamID, FaultRecord>>();
                foreach (var kv in _states)
                {
                    if (kv.Value.RestoreVerified)
                    {
                        temporary.Add(kv);
                    }
                }

                // 清空所有状态
                _states.Clear();

                // 恢复临时熔断
                for (int i = 0; i < temporary.Count; i++)
                {
                    _states[temporary[i].Key] = temporary[i].Value;
                }

                // 注入持久熔断快照
                for (int i = 0; i < snapshot.Count; i++)
                {
                    var r = snapshot[i];
                    _states[new CSteamID(r.SteamId)] = new FaultRecord
                    {
                        State = State.Open,
                        OpenedAt = r.OpenedAt,
                        Reason = r.Reason ?? "loaded from disk",
                        RestoreVerified = false,
                    };
                }

                return _states.Count;
            }
        }

        /// <summary>
        /// v2.0.3 P0-C4 新增：返回当前所有持久熔断（RestoreVerified=false）的快照，
        /// 供持久化层写盘和管理员命令查询使用。
        /// </summary>
        public static List<TidyFaultCircuitPersistence.PersistentRecord> GetPersistentSnapshot()
        {
            var list = new List<TidyFaultCircuitPersistence.PersistentRecord>();
            lock (_lock)
            {
                foreach (var kv in _states)
                {
                    if (!kv.Value.RestoreVerified)
                    {
                        list.Add(new TidyFaultCircuitPersistence.PersistentRecord
                        {
                            SteamId = (ulong)kv.Key,
                            Reason = kv.Value.Reason,
                            OpenedAt = kv.Value.OpenedAt,
                            RestoreVerified = false,
                        });
                    }
                }
            }
            return list;
        }

        /// <summary>
        /// v2.0.2 修订：玩家断开连接时清理状态。
        /// 仅清除 RestoreVerified=true 的临时熔断；持久熔断（RestoreVerified=false）不得因断线自动删除。
        /// v2.0.3 P2-L13 修订：返回 ClearPlayerResult，让调用方知道真实处理结果。
        /// </summary>
        public static ClearPlayerResult ClearPlayer(CSteamID steamId)
        {
            lock (_lock)
            {
                if (!_states.TryGetValue(steamId, out var rec))
                    return ClearPlayerResult.NotFound;

                if (!rec.RestoreVerified)
                {
                    LaunchInventoryTidyPlugin.Log?.LogWarning(
                        $"[FaultCircuit] 玩家 {(ulong)steamId} 断线但熔断为持久熔断（restoreVerified=false, reason={rec.Reason}），不得自动清除，需管理员显式恢复");
                    return ClearPlayerResult.Preserved;
                }

                _states.Remove(steamId);
                return ClearPlayerResult.Removed;
            }
        }

        // 保留 ClearAllForTests 仅供单元测试；生产 Shutdown 使用 ClearAllNonPersistent
        internal static void ClearAllForTests()
        {
            lock (_lock) _states.Clear();
        }

        /// <summary>v2.0.2 新增：生产 Shutdown 仅清除临时熔断，保留持久熔断（待管理员处理）。</summary>
        internal static void ClearAllNonPersistent()
        {
            lock (_lock)
            {
                var persistent = new List<KeyValuePair<CSteamID, FaultRecord>>();
                foreach (var kv in _states)
                    if (!kv.Value.RestoreVerified)
                        persistent.Add(kv);

                _states.Clear();

                foreach (var kv in persistent)
                    _states[kv.Key] = kv.Value;

                if (persistent.Count > 0)
                {
                    LaunchInventoryTidyPlugin.Log?.LogWarning(
                        $"[FaultCircuit] Shutdown 保留 {persistent.Count} 个持久熔断记录，需管理员显式恢复");
                }
            }
        }
    }

    /// <summary>
    /// 玩家级请求限流：滑动窗口 + 最小间隔，防止恶意客户端制造清空重添风暴。
    /// 限流在任何求解或库存读取前生效。
    /// v2.0.2：拒绝日志节流，防止攻击者制造日志洪水。
    /// </summary>
    public static class TidyRateLimiter
    {
        private struct Window
        {
            public DateTime LastRequestAt;
            public int RequestsInWindow;
            public DateTime WindowStartAt;
            // v2.0.2：日志节流状态
            public DateTime LastIntervalLogAt;
            public DateTime LastWindowLogAt;
            public int SuppressedIntervalLogs;
            public int SuppressedWindowLogs;
        }

        private static readonly Dictionary<CSteamID, Window> _windows =
            new Dictionary<CSteamID, Window>();
        private static readonly object _lock = new object();

        /// <summary>
        /// 最小请求间隔（秒）。低于此间隔的请求被拒绝。
        /// v2.0.6.3：1.0s -> 1.5s，提高玩家级防刷闸门强度（防高频恶意发包重入）。
        /// </summary>
        public const double MIN_INTERVAL_SECONDS = 1.5;

        /// <summary>滑动窗口长度（秒）。</summary>
        public const double WINDOW_SECONDS = 10.0;

        /// <summary>窗口内最大请求数。</summary>
        public const int MAX_REQUESTS_PER_WINDOW = 5;

        /// <summary>v2.0.2：拒绝日志节流间隔（秒）。每类拒绝原因在此周期内最多输出一次。</summary>
        public const double LOG_SUPPRESSION_SECONDS = 5.0;

        /// <summary>是否允许请求。允许时更新窗口状态。</summary>
        public static bool Allow(CSteamID steamId)
        {
            lock (_lock)
            {
                DateTime now = DateTime.UtcNow;
                if (!_windows.TryGetValue(steamId, out var w))
                {
                    _windows[steamId] = new Window
                    {
                        LastRequestAt = now,
                        RequestsInWindow = 1,
                        WindowStartAt = now,
                    };
                    return true;
                }

                double sinceLast = (now - w.LastRequestAt).TotalSeconds;
                if (sinceLast < MIN_INTERVAL_SECONDS)
                {
                    // v2.0.2：日志节流
                    LogSuppressed(steamId, ref w, now, isIntervalReject: true,
                        $"请求过快（{sinceLast:F2}s < {MIN_INTERVAL_SECONDS}s），拒绝");
                    _windows[steamId] = w;
                    return false;
                }

                // 滑动窗口：若窗口过期则重置
                double windowAge = (now - w.WindowStartAt).TotalSeconds;
                if (windowAge > WINDOW_SECONDS)
                {
                    w.RequestsInWindow = 0;
                    w.WindowStartAt = now;
                }

                if (w.RequestsInWindow >= MAX_REQUESTS_PER_WINDOW)
                {
                    // v2.0.2：日志节流
                    LogSuppressed(steamId, ref w, now, isIntervalReject: false,
                        $"窗口内请求数超限（{w.RequestsInWindow} >= {MAX_REQUESTS_PER_WINDOW}），拒绝");
                    _windows[steamId] = w;
                    return false;
                }

                w.RequestsInWindow++;
                w.LastRequestAt = now;
                _windows[steamId] = w;
                return true;
            }
        }

        private static void LogSuppressed(CSteamID steamId, ref Window w, DateTime now,
            bool isIntervalReject, string message)
        {
            DateTime lastLog = isIntervalReject ? w.LastIntervalLogAt : w.LastWindowLogAt;
            double sinceLog = (now - lastLog).TotalSeconds;

            if (sinceLog >= LOG_SUPPRESSION_SECONDS)
            {
                int suppressed = isIntervalReject ? w.SuppressedIntervalLogs : w.SuppressedWindowLogs;
                LaunchInventoryTidyPlugin.Log?.LogWarning(
                    $"[RateLimit] 玩家 {(ulong)steamId} {message}" +
                    (suppressed > 0 ? $"（已抑制 {suppressed} 条同类日志）" : ""));

                if (isIntervalReject)
                {
                    w.LastIntervalLogAt = now;
                    w.SuppressedIntervalLogs = 0;
                }
                else
                {
                    w.LastWindowLogAt = now;
                    w.SuppressedWindowLogs = 0;
                }
            }
            else
            {
                if (isIntervalReject)
                    w.SuppressedIntervalLogs++;
                else
                    w.SuppressedWindowLogs++;
            }
        }

        /// <summary>玩家断开连接时清理状态。</summary>
        public static void ClearPlayer(CSteamID steamId)
        {
            lock (_lock) _windows.Remove(steamId);
        }

        internal static void ClearAllForTests()
        {
            lock (_lock) _windows.Clear();
        }
    }

    /// <summary>
    /// 请求账本：记录每名玩家近期已处理的 requestId + 完整响应缓存，防止重放攻击。
    /// v2.0.2 修订：
    ///   - 容量从 16 扩到 64（覆盖 60s TTL 内理论请求数）
    ///   - 缓存完整 mappings（不仅仅是 result 枚举）
    ///   - Received 状态明确区分于 Committed
    /// </summary>
    public static class RequestLedger
    {
        public enum RequestState : byte
        {
            Received = 0,
            Committed = 1,
            Failed = 2,
            Expired = 3,
        }

        public struct LedgerEntry
        {
            public uint RequestId;
            /// <summary>v2.0.6.5 新增：客户端会话 nonce，用于 (SteamID, nonce, requestId) 复合键。</summary>
            public ulong SessionNonce;
            public DateTime RecordedAt;
            public RequestState State;
            public TidyCommitResult Result;
            /// <summary>v2.0.2 新增：完整 mappings 缓存，用于重复请求时重发相同响应。</summary>
            public List<NewPositionMapping> Mappings;
        }

        private static readonly Dictionary<CSteamID, List<LedgerEntry>> _entries =
            new Dictionary<CSteamID, List<LedgerEntry>>();
        private static readonly object _lock = new object();

        /// <summary>
        /// v2.0.2：每玩家保留的近期 requestId 数量上限。
        /// 原 16 条不足以覆盖 60s TTL 内的理论请求数（限流允许 10s 内 5 个请求 = 60s 内最多 30 个）。
        /// 扩到 64 条留有余量。
        ///
        /// v2.0.3 P2-L12 修订：容量满时的行为已修正为"驱逐最老条目"（而非拒绝新请求）。
        /// 拒绝新请求会让合法客户端的正常整理请求失败，反而造成更大问题。
        /// 在限流（10s 内 5 个请求 = 60s 内最多 30 个）下，64 条容量有 2x 余量，
        /// 实际几乎不会触发驱逐；即使触发，驱逐的也是最老条目（重放保护窗口最弱），
        /// 安全性损失可接受。驱逐会记录 Warning 日志供运维监控。
        /// </summary>
        public const int MAX_ENTRIES_PER_PLAYER = 64;

        /// <summary>条目 TTL（秒）。超期后可被清理。</summary>
        public const double ENTRY_TTL_SECONDS = 60.0;

        /// <summary>
        /// v2.0.6.5 修订（Codex v2.0.6.4 审计 §五阻断项 4）：
        /// 尝试开始处理 requestId。账本键升级为 (SteamID, sessionNonce, requestId) 复合键。
        /// 返回 true 表示首次收到可继续处理；
        /// false 表示重复请求（同 nonce + requestId），调用方应返回缓存结果。
        /// nonce 不匹配的旧 requestId 被视为新请求（跨会话重放保护）。
        ///
        /// v2.0.6.8 修订（Codex v2.0.6.7 审计 §三 Critical 2 模板 B 修复）：
        ///   - 本方法保留作为向后兼容入口；新代码必须使用 RequestAdmissionStore.TryAdmit
        ///   - 容量满时不再驱逐 Received 条目（只驱逐 Committed/Failed/Expired）
        ///   - 防止攻击者发送 65+ 个不同 requestId 驱逐正在执行的 in-flight 条目
        /// </summary>
        public static bool TryBegin(CSteamID steamId, ulong sessionNonce, uint requestId, out LedgerEntry existing)
        {
            lock (_lock)
            {
                CleanupExpired();

                if (_entries.TryGetValue(steamId, out var list))
                {
                    for (int i = 0; i < list.Count; i++)
                    {
                        // v2.0.6.5：复合键匹配（nonce + requestId）
                        if (list[i].SessionNonce == sessionNonce && list[i].RequestId == requestId)
                        {
                            existing = list[i];
                            return false;
                        }
                    }
                }

                // v2.0.6.8：容量检查 - 若已达上限且无法驱逐非 Received 条目，拒绝新请求
                // 不再驱逐 Received 条目（防止 in-flight 请求被攻击者驱逐）
                if (list != null && list.Count >= MAX_ENTRIES_PER_PLAYER)
                {
                    int removed = list.RemoveAll(e => e.State != RequestState.Received);
                    if (removed == 0 || list.Count >= MAX_ENTRIES_PER_PLAYER)
                    {
                        LaunchInventoryTidyPlugin.Log?.LogWarning(
                            $"[Ledger] 玩家 {(ulong)steamId} 账本容量已达上限 {MAX_ENTRIES_PER_PLAYER}，" +
                            $"且全部为 Received 状态（in-flight），拒绝新请求 reqId={requestId} nonce={sessionNonce:X16}（v2.0.8：不驱逐 Received）");
                        existing = default;
                        return false;
                    }
                }

                // 首次收到：登记 Received 状态
                if (!_entries.TryGetValue(steamId, out list))
                {
                    list = new List<LedgerEntry>(MAX_ENTRIES_PER_PLAYER);
                    _entries[steamId] = list;
                }

                list.Add(new LedgerEntry
                {
                    RequestId = requestId,
                    SessionNonce = sessionNonce,  // v2.0.6.5：绑定 nonce
                    RecordedAt = DateTime.UtcNow,
                    State = RequestState.Received,
                    Result = TidyCommitResult.Rejected,  // 默认 Rejected，未完成前不得冒充 Committed
                    Mappings = null,
                });

                existing = default;
                return true;
            }
        }

        /// <summary>
        /// v2.0.6.8 新增（Codex v2.0.6.7 审计 §三 Critical 2 模板 B 修复）：
        /// 只读查询 (nonce, requestId) 是否已在账本中，不创建新条目。
        /// 供 RequestAdmissionStore.TryAdmit 在原子临界区内调用。
        /// </summary>
        public static bool TryLookup(CSteamID steamId, ulong sessionNonce, uint requestId, out LedgerEntry existing)
        {
            lock (_lock)
            {
                existing = default;
                if (!_entries.TryGetValue(steamId, out var list)) return false;
                for (int i = 0; i < list.Count; i++)
                {
                    if (list[i].SessionNonce == sessionNonce && list[i].RequestId == requestId)
                    {
                        existing = list[i];
                        return true;
                    }
                }
                return false;
            }
        }

        /// <summary>
        /// v2.0.6.8 新增（Codex v2.0.6.7 审计 §三 Critical 2 模板 B 修复）：
        /// 检查是否可以为该玩家创建新 Received 条目。
        /// 返回 false 表示容量已满且无法驱逐非 Received 条目（in-flight 保护）。
        /// 供 RequestAdmissionStore.TryAdmit 在原子临界区内调用。
        /// </summary>
        public static bool HasCapacityForNew(CSteamID steamId)
        {
            lock (_lock)
            {
                CleanupExpired();
                if (!_entries.TryGetValue(steamId, out var list)) return true;
                if (list.Count < MAX_ENTRIES_PER_PLAYER) return true;
                // 容量满：检查是否可以驱逐非 Received 条目
                for (int i = 0; i < list.Count; i++)
                {
                    if (list[i].State != RequestState.Received) return true;
                }
                return false;
            }
        }

        /// <summary>
        /// v2.0.6.8 新增（Codex v2.0.6.7 审计 §三 Critical 2 模板 B 修复）：
        /// 创建 Received 条目。调用前必须先通过 TryLookup + HasCapacityForNew 检查。
        /// 供 RequestAdmissionStore.TryAdmit 在原子临界区内调用。
        /// 注意：调用方必须持有外层 _gate 锁以保证原子性。
        ///
        /// v2.0.6.9 弃用（Codex v2.0.6.8 审计 §三 Medium 2 模板 2 修复）：
        ///   - 容量检查（HasCapacityForNew）和插入（CreateReceivedEntry）分离，
        ///     CreateReceivedEntry 不驱逐任何非 Received 条目，直接 append
        ///   - MAX_ENTRIES_PER_PLAYER=64 会被突破，容量限制为假
        ///   - 新代码必须使用 TryCreateReceivedNonEvicting（容量检查 + 驱逐 + 插入合并）
        /// </summary>
        [Obsolete("v2.0.6.9：使用 TryCreateReceivedNonEvicting 替代")]
        public static void CreateReceivedEntry(CSteamID steamId, ulong sessionNonce, uint requestId)
        {
            lock (_lock)
            {
                if (!_entries.TryGetValue(steamId, out var list))
                {
                    list = new List<LedgerEntry>(MAX_ENTRIES_PER_PLAYER);
                    _entries[steamId] = list;
                }
                list.Add(new LedgerEntry
                {
                    RequestId = requestId,
                    SessionNonce = sessionNonce,
                    RecordedAt = DateTime.UtcNow,
                    State = RequestState.Received,
                    Result = TidyCommitResult.Rejected,
                    Mappings = null,
                });
            }
        }

        /// <summary>
        /// v2.0.6.9 新增（Codex v2.0.6.8 审计 §三 Medium 2 模板 2 修复）：
        /// 单一 API 合并容量检查 + 驱逐 + 插入，强制不变量。
        ///
        /// 行为（在同一 _lock 锁内执行）：
        ///   1. CleanupExpired
        ///   2. 若 list.Count < MAX，直接 add（容量未满）
        ///   3. 若 list.Count >= MAX，查找一个非 Received 条目（FindIndex）：
        ///      - 找到 -> RemoveAt(victim)，再 add（驱逐一个最旧终态）
        ///      - 未找到 -> return false（全部为 Received，拒绝以保护 in-flight）
        ///   4. 成功插入后返回 true
        ///
        /// 不变量：
        ///   - Received 条目永不被驱逐
        ///   - list.Count 永不超过 MAX_ENTRIES_PER_PLAYER
        ///   - 调用方必须先通过 TryLookup 确认 (nonce, requestId) 不存在
        /// </summary>
        public static bool TryCreateReceivedNonEvicting(CSteamID steamId, ulong sessionNonce, uint requestId)
        {
            lock (_lock)
            {
                CleanupExpired();

                if (!_entries.TryGetValue(steamId, out var list))
                {
                    list = new List<LedgerEntry>(MAX_ENTRIES_PER_PLAYER);
                    _entries[steamId] = list;
                }

                if (list.Count >= MAX_ENTRIES_PER_PLAYER)
                {
                    // 容量满：查找一个非 Received 条目驱逐
                    int victim = -1;
                    for (int i = 0; i < list.Count; i++)
                    {
                        if (list[i].State != RequestState.Received)
                        {
                            victim = i;
                            break;
                        }
                    }
                    if (victim < 0)
                    {
                        // 全部为 Received，拒绝以保护 in-flight
                        LaunchInventoryTidyPlugin.Log?.LogWarning(
                            $"[Ledger] 玩家 {(ulong)steamId} 账本容量已达上限 {MAX_ENTRIES_PER_PLAYER}，" +
                            $"且全部为 Received 状态（in-flight），拒绝新请求 reqId={requestId} nonce={sessionNonce:X16}（v2.0.6.9：保护 in-flight）");
                        return false;
                    }
                    // 驱逐一个最旧终态条目（FindIndex 返回第一个非 Received，即最旧的终态）
                    var evicted = list[victim];
                    list.RemoveAt(victim);
                    LaunchInventoryTidyPlugin.Log?.LogInfo(
                        $"[Ledger] 玩家 {(ulong)steamId} 容量满，驱逐最旧终态条目 reqId={evicted.RequestId} state={evicted.State}（Received 保留）");
                }

                list.Add(new LedgerEntry
                {
                    RequestId = requestId,
                    SessionNonce = sessionNonce,
                    RecordedAt = DateTime.UtcNow,
                    State = RequestState.Received,
                    Result = TidyCommitResult.Rejected,
                    Mappings = null,
                });
                return true;
            }
        }

        /// <summary>
        /// v2.0.6.8 新增（Codex v2.0.6.7 审计 §三 Critical 2 模板 B 修复）：
        /// 标记条目为 Failed（取消 New 状态）。供 RequestAdmissionStore.CancelNew 调用。
        /// </summary>
        public static void MarkFailed(CSteamID steamId, ulong sessionNonce, uint requestId)
        {
            MarkResult(steamId, sessionNonce, requestId, RequestState.Failed, TidyCommitResult.Rejected, null);
        }

        /// <summary>
        /// v2.0.6.5 修订：更新条目状态为 Committed 或 Failed，并缓存完整 mappings。
        /// 必须提供 sessionNonce 以匹配复合键。
        /// </summary>
        public static void MarkResult(CSteamID steamId, ulong sessionNonce, uint requestId, RequestState state,
            TidyCommitResult result, List<NewPositionMapping> mappings = null)
        {
            lock (_lock)
            {
                if (!_entries.TryGetValue(steamId, out var list)) return;
                for (int i = 0; i < list.Count; i++)
                {
                    // v2.0.6.5：复合键匹配（nonce + requestId）
                    if (list[i].SessionNonce == sessionNonce && list[i].RequestId == requestId)
                    {
                        var e = list[i];
                        e.State = state;
                        e.Result = result;
                        // v2.0.2：深拷贝 mappings 防止外部修改
                        e.Mappings = mappings == null ? null : new List<NewPositionMapping>(mappings);
                        list[i] = e;
                        return;
                    }
                }
            }
        }

        /// <summary>玩家断开连接时清理状态。</summary>
        public static void ClearPlayer(CSteamID steamId)
        {
            lock (_lock) _entries.Remove(steamId);
        }

        internal static void ClearAllForTests()
        {
            lock (_lock) _entries.Clear();
        }

        private static void CleanupExpired()
        {
            DateTime now = DateTime.UtcNow;
            foreach (var kv in _entries)
            {
                kv.Value.RemoveAll(e => (now - e.RecordedAt).TotalSeconds > ENTRY_TTL_SECONDS);
            }
        }
    }

    /// <summary>
    /// v2.0.3 P1-M14 新增，v2.0.4 P1 扩展：协议拒绝日志与异常日志统一节流器。
    ///
    /// 设计目标：
    ///   - 攻击者发送畸形包（非法 mode/page/hotkeyCount、短包、尾随数据等）时，
    ///     所有拒绝日志通过此节流器，按 sender+category 聚合 suppressed 数量
    ///   - 节流窗口内最多输出一次日志，其余计入 suppressed 计数
    ///   - 防止攻击者绕过 RateLimiter（协议解析失败前）制造日志洪水
    ///
    /// v2.0.4 P1 扩展（覆盖所有外层协议拒绝路径 + 异常日志低频采样）：
    ///   - LogRejection(sender, category, message)：服务端按 sender+category 节流（不变）
    ///   - LogClientRejection(category, message)：客机端无 sender，按全局 category 节流
    ///   - LogException(category, context, exception)：异常日志低频采样完整堆栈
    ///     · 抑制窗口内仅输出异常类型 + Message（无堆栈），抑制窗口外输出一次完整堆栈
    ///     · 防止攻击者制造大量畸形包触发异常堆栈日志洪水
    ///
    /// 调用方：所有协议拒绝路径（HandleServerMessage/HandleClientMessage/HandleRequestTidyV2 等）
    /// </summary>
    public static class SecurityLogLimiter
    {
        private struct CategoryWindow
        {
            public DateTime LastLogAt;
            public int SuppressedCount;
        }

        private static readonly Dictionary<(CSteamID, string), CategoryWindow> _windows =
            new Dictionary<(CSteamID, string), CategoryWindow>();
        private static readonly Dictionary<string, CategoryWindow> _clientWindows =
            new Dictionary<string, CategoryWindow>();
        private static readonly Dictionary<string, CategoryWindow> _exceptionWindows =
            new Dictionary<string, CategoryWindow>();
        private static readonly object _lock = new object();

        /// <summary>服务端节流窗口（秒）。同一 sender+category 在此周期内最多输出一次日志。</summary>
        public const double SUPPRESSION_SECONDS = 5.0;

        /// <summary>客户端节流窗口（秒）。同一 category 在此周期内最多输出一次日志。</summary>
        public const double CLIENT_SUPPRESSION_SECONDS = 5.0;

        /// <summary>异常日志节流窗口（秒）。同一 category 完整堆栈最多输出一次。</summary>
        public const double EXCEPTION_SUPPRESSION_SECONDS = 30.0;

        /// <summary>窗口内最大不同 category 数量（防内存膨胀）。</summary>
        public const int MAX_CATEGORIES_PER_SENDER = 16;
        private const int MAX_CLIENT_CATEGORIES = 32;
        private const int MAX_EXCEPTION_CATEGORIES = 32;

        /// <summary>
        /// 输出一条拒绝日志（带节流）。
        /// 若距上次同 (sender, category) 日志不足 SUPPRESSION_SECONDS，仅增加 suppressed 计数；
        /// 否则输出日志并附带 suppressed 数量，然后重置计数。
        /// v2.0.6 P1-6：返回 true 表示本次未节流（调用方可发送聊天响应）；
        /// 返回 false 表示本次已节流（调用方应跳过聊天响应，避免聊天洪水）。
        /// </summary>
        public static bool LogRejection(CSteamID sender, string category, string message)
        {
            DateTime now = DateTime.UtcNow;
            var key = (sender, category ?? "");

            lock (_lock)
            {
                if (_windows.TryGetValue(key, out var w))
                {
                    double sinceLog = (now - w.LastLogAt).TotalSeconds;
                    if (sinceLog < SUPPRESSION_SECONDS)
                    {
                        w.SuppressedCount++;
                        _windows[key] = w;
                        return false;  // v2.0.6 P1-6：节流窗口内，跳过聊天响应
                    }

                    // 输出并重置
                    int suppressed = w.SuppressedCount;
                    LaunchInventoryTidyPlugin.Log?.LogWarning(
                        $"[TidyNet] {message}" +
                        (suppressed > 0 ? $"（已抑制 {suppressed} 条同类日志）" : ""));
                    _windows[key] = new CategoryWindow
                    {
                        LastLogAt = now,
                        SuppressedCount = 0,
                    };
                    return true;  // v2.0.6 P1-6：窗口外，允许聊天响应
                }
                else
                {
                    // 容量限制
                    if (_windows.Count >= MAX_CATEGORIES_PER_SENDER * 64)  // 全局上限
                    {
                        // 清理最老的条目
                        DateTime oldest = DateTime.MaxValue;
                        (CSteamID, string) oldestKey = default;
                        foreach (var kv in _windows)
                        {
                            if (kv.Value.LastLogAt < oldest)
                            {
                                oldest = kv.Value.LastLogAt;
                                oldestKey = kv.Key;
                            }
                        }
                        if (!oldestKey.Equals(default))
                            _windows.Remove(oldestKey);
                    }

                    LaunchInventoryTidyPlugin.Log?.LogWarning($"[TidyNet] {message}");
                    _windows[key] = new CategoryWindow
                    {
                        LastLogAt = now,
                        SuppressedCount = 0,
                    };
                    return true;  // v2.0.6 P1-6：首次，允许聊天响应
                }
            }
        }

        /// <summary>玩家断开连接时清理状态。</summary>
        public static void ClearPlayer(CSteamID steamId)
        {
            lock (_lock)
            {
                var toRemove = new List<(CSteamID, string)>();
                foreach (var kv in _windows)
                {
                    if (kv.Key.Item1 == steamId) toRemove.Add(kv.Key);
                }
                for (int i = 0; i < toRemove.Count; i++)
                    _windows.Remove(toRemove[i]);
                // 客户端窗口和异常窗口不绑定 sender，不在此清理
            }
        }

        /// <summary>
        /// v2.0.4 P1 新增：客机端拒绝日志（无 sender，按全局 category 节流）。
        /// 用于 HandleClientMessage / HandleTidyCommittedFromServer 等客机端协议拒绝路径。
        /// </summary>
        public static void LogClientRejection(string category, string message)
        {
            DateTime now = DateTime.UtcNow;
            string key = category ?? "";

            lock (_lock)
            {
                if (_clientWindows.TryGetValue(key, out var w))
                {
                    double sinceLog = (now - w.LastLogAt).TotalSeconds;
                    if (sinceLog < CLIENT_SUPPRESSION_SECONDS)
                    {
                        w.SuppressedCount++;
                        _clientWindows[key] = w;
                        return;
                    }

                    int suppressed = w.SuppressedCount;
                    LaunchInventoryTidyPlugin.Log?.LogWarning(
                        $"[TidyNet] {message}" +
                        (suppressed > 0 ? $"（已抑制 {suppressed} 条同类日志）" : ""));
                    _clientWindows[key] = new CategoryWindow
                    {
                        LastLogAt = now,
                        SuppressedCount = 0,
                    };
                }
                else
                {
                    if (_clientWindows.Count >= MAX_CLIENT_CATEGORIES)
                    {
                        DateTime oldest = DateTime.MaxValue;
                        string oldestKey = null;
                        foreach (var kv in _clientWindows)
                        {
                            if (kv.Value.LastLogAt < oldest)
                            {
                                oldest = kv.Value.LastLogAt;
                                oldestKey = kv.Key;
                            }
                        }
                        if (oldestKey != null) _clientWindows.Remove(oldestKey);
                    }

                    LaunchInventoryTidyPlugin.Log?.LogWarning($"[TidyNet] {message}");
                    _clientWindows[key] = new CategoryWindow
                    {
                        LastLogAt = now,
                        SuppressedCount = 0,
                    };
                }
            }
        }

        /// <summary>
        /// v2.0.4 P1 新增：异常日志低频采样。
        /// 抑制窗口内仅累计 suppressed 计数（不写任何日志），抑制窗口外输出一次摘要 + 完整堆栈。
        /// v2.0.5 P1-1 修订：Codex v2.0.4 第五次审计 §2 P1 指出原实现每次异常都写一行，
        /// 10,000 次异常仍产生约 10,000 行日志。修订为窗口内完全不写日志。
        /// 用于所有 HandleXxx 入口的外层 catch 捕获的异常。
        /// </summary>
        public static void LogException(string category, string context, Exception e)
        {
            if (e == null) return;
            DateTime now = DateTime.UtcNow;
            string key = category ?? "";

            lock (_lock)
            {
                if (_exceptionWindows.TryGetValue(key, out var w))
                {
                    double sinceLog = (now - w.LastLogAt).TotalSeconds;
                    if (sinceLog < EXCEPTION_SUPPRESSION_SECONDS)
                    {
                        // v2.0.5 P1-1：抑制窗口内完全不写日志，仅累计 suppressed 计数
                        w.SuppressedCount++;
                        _exceptionWindows[key] = w;
                        return;
                    }

                    // 抑制窗口已过：输出摘要 + 上次抑制数量 + 完整堆栈，重置计数
                    int suppressed = w.SuppressedCount;
                    _exceptionWindows[key] = new CategoryWindow
                    {
                        LastLogAt = now,
                        SuppressedCount = 0,
                    };
                    LaunchInventoryTidyPlugin.Log?.LogError(
                        $"[TidyNet] {context}: {e.GetType().Name}: {e.Message}" +
                        (suppressed > 0 ? $"（已抑制 {suppressed} 条同类异常）" : ""));
                    LaunchInventoryTidyPlugin.Log?.LogError($"[TidyNet] 完整堆栈: {e}");
                    return;
                }

                // 首次：输出完整堆栈
                if (_exceptionWindows.Count >= MAX_EXCEPTION_CATEGORIES)
                {
                    DateTime oldest = DateTime.MaxValue;
                    string oldestKey = null;
                    foreach (var kv in _exceptionWindows)
                    {
                        if (kv.Value.LastLogAt < oldest)
                        {
                            oldest = kv.Value.LastLogAt;
                            oldestKey = kv.Key;
                        }
                    }
                    if (oldestKey != null) _exceptionWindows.Remove(oldestKey);
                }

                _exceptionWindows[key] = new CategoryWindow
                {
                    LastLogAt = now,
                    SuppressedCount = 0,
                };
                LaunchInventoryTidyPlugin.Log?.LogError($"[TidyNet] {context}: {e}");
            }
        }

        internal static void ClearAllForTests()
        {
            lock (_lock)
            {
                _windows.Clear();
                _clientWindows.Clear();
                _exceptionWindows.Clear();
            }
        }
    }
}

using System;
using System.Collections.Generic;
using Steamworks;

namespace LaunchInventoryTidy
{
    /// <summary>
    /// v2.0.6.4 新增，v2.0.6.5 修订（Codex v2.0.6.4 审计 §五阻断项 3）：
    /// 玩家级操作闸门（per-player lease），持有 owner/requestId 生命周期。
    ///
    /// v2.0.6.5 修订：
    ///   - lease 从单纯的 bool 升级为 Lease 结构，持有 requestId 和获取时间
    ///   - Release 增加 requestId 参数，只有持有对应 requestId 的 lease 才能释放
    ///   - 防止 A 事务的 finally 误释放 B 事务的 lease（lease 错配防御）
    ///
    /// v2.0.6.6 修订（Codex v2.0.6.5 审计 §三 Critical 2 修复）：
    ///   - lease 增加 Nonce 字段，绑定 (owner, nonce, requestId) 复合键
    ///   - TryAcquire 升级为 TryAcquireWithResult，返回 AcquireResult 枚举：
    ///     * Acquired = 获取成功
    ///     * BusyDuplicate = 玩家已有进行中事务且 (nonce, requestId) 匹配（重复包，静默丢弃）
    ///     * BusyDifferent = 玩家已有进行中事务但 (nonce, requestId) 不同（不同请求，可拒绝）
    ///   - 修复 v2.0.6.5 的阻断项：同一 (nonce, requestId) 重复包在 lease 已持有时被回送 Rejected，
    ///     客户端会清掉 pending，导致原请求 Committed 被忽略，ACK 链路断裂
    ///
    /// Codex v2.0.6.4 审计 §三 Medium 阻断项指出：
    ///   - PlayerOperationGate 只锁 LIT 的请求入口，不拥有或拦截 Items
    ///   - 注释把协议状态与锁生命周期混为一谈（"ACK 后释放" 错误）
    ///   - lease 实际在发送 TidyCommitted 后立即 finally Release，ACK 尚未发生
    ///
    /// v2.0.6.5 lease 生命周期（修正后）：
    ///   - TryAcquire：网络回调入口或主线程调度入口
    ///   - 持有：Prepare -> Commit -> Verify -> 发送 TidyCommitted 响应
    ///   - Release：主线程 finally（响应发送后立即释放，不等 ACK）
    ///   - ACK 由独立的 TidyTransactionManager 跟踪（Commit 后存入，ACK 后清除）
    ///
    /// 注意：本 lease 只保证 LIT 事务在 Prepare->Commit->Verify 期间串行化，
    /// 不拦截原版库存回调或其他模组的直接写入。
    /// 对并发修改的防御由 post-commit 写前比较提供（Codex §五阻断项 1）。
    /// </summary>
    public static class PlayerOperationGate
    {
        /// <summary>
        /// v2.0.6.6 新增：TryAcquire 的返回结果。
        /// 区分"重复包"和"不同请求"两种 busy 情况。
        /// </summary>
        public enum AcquireResult : byte
        {
            /// <summary>lease 获取成功。</summary>
            Acquired = 0,
            /// <summary>玩家已有进行中事务且 (nonce, requestId) 匹配（重复包，应静默丢弃，不回送响应）。</summary>
            BusyDuplicate = 1,
            /// <summary>玩家已有进行中事务但 (nonce, requestId) 不同（不同请求，可回送 Rejected）。</summary>
            BusyDifferent = 2,
        }

        /// <summary>
        /// v2.0.6.5 新增：lease 持有 owner + requestId + 获取时间。
        /// v2.0.6.6 修订：增加 Nonce 字段，绑定 (owner, nonce, requestId) 复合键。
        /// 用于验证释放 lease 的调用方是否为持有 lease 的同一事务。
        /// </summary>
        public struct Lease
        {
            public CSteamID Owner;
            public ulong Nonce;  // v2.0.6.6：复合键的一部分
            public uint RequestId;
            public DateTime AcquiredAt;

            public Lease(CSteamID owner, ulong nonce, uint requestId)
            {
                Owner = owner;
                Nonce = nonce;
                RequestId = requestId;
                AcquiredAt = DateTime.UtcNow;
            }
        }

        private static readonly Dictionary<CSteamID, Lease> _leases =
            new Dictionary<CSteamID, Lease>();
        private static readonly object _lock = new object();

        /// <summary>
        /// v2.0.6.6 新增（Codex v2.0.6.5 审计 §三 Critical 2 修复）：
        /// 尝试获取玩家操作 lease，区分"重复包"和"不同请求"两种 busy 情况。
        ///
        /// 返回值：
        ///   - Acquired = 获取成功
        ///   - BusyDuplicate = 玩家已有进行中事务且 (nonce, requestId) 完全匹配（重复包，应静默丢弃）
        ///   - BusyDifferent = 玩家已有进行中事务但 (nonce, requestId) 不同（不同请求，可拒绝）
        ///
        /// 调用方处理：
        ///   - Acquired: 入队到主线程执行
        ///   - BusyDuplicate: 静默丢弃（不回送任何响应，原请求仍在处理中，会自己发 Committed）
        ///   - BusyDifferent: 回送 Rejected（不同请求）
        /// </summary>
        public static AcquireResult TryAcquireWithResult(CSteamID steamId, ulong nonce, uint requestId)
        {
            if (steamId == CSteamID.Nil) return AcquireResult.BusyDifferent;
            if (requestId == 0) return AcquireResult.BusyDifferent;
            lock (_lock)
            {
                if (_leases.TryGetValue(steamId, out var existing))
                {
                    // v2.0.6.6：检查是否为同一 (nonce, requestId) 的重复包
                    if (existing.Nonce == nonce && existing.RequestId == requestId)
                        return AcquireResult.BusyDuplicate;
                    return AcquireResult.BusyDifferent;
                }
                _leases[steamId] = new Lease(steamId, nonce, requestId);
                return AcquireResult.Acquired;
            }
        }

        /// <summary>
        /// v2.0.6.5：尝试获取玩家操作 lease，绑定 (owner, requestId)。
        /// 若玩家已有进行中事务（任意 requestId），返回 false。
        /// requestId == 0 时拒绝获取（防误调用）。
        /// v2.0.6.6：保留兼容方法，内部调用 TryAcquireWithResult。
        /// </summary>
        public static bool TryAcquire(CSteamID steamId, uint requestId)
        {
            // v2.0.6.6：兼容方法，使用 nonce=0 表示不区分重复包
            // 新代码应使用 TryAcquireWithResult
            return TryAcquireWithResult(steamId, 0, requestId) == AcquireResult.Acquired;
        }

        /// <summary>
        /// v2.0.6.5：释放玩家操作 lease，必须匹配 requestId。
        /// 若当前 lease 的 requestId != 传入 requestId，拒绝释放（lease 错配）。
        /// 这防止 A 事务的 finally 误释放 B 事务的 lease。
        /// </summary>
        public static void Release(CSteamID steamId, uint requestId)
        {
            if (steamId == CSteamID.Nil) return;
            lock (_lock)
            {
                if (_leases.TryGetValue(steamId, out var existing))
                {
                    if (existing.RequestId == requestId)
                    {
                        _leases.Remove(steamId);
                    }
                    else
                    {
                        // v2.0.6.5：lease 错配，记录但不释放
                        LaunchInventoryTidyPlugin.Log?.LogWarning(
                            $"[Tidy] PlayerOperationGate.Release lease 错配：current requestId={existing.RequestId}, release requestId={requestId}，拒绝释放");
                    }
                }
            }
        }

        /// <summary>玩家断开连接时清理 lease（防止残留）。</summary>
        public static void ClearPlayer(CSteamID steamId)
        {
            if (steamId == CSteamID.Nil) return;
            lock (_lock)
            {
                _leases.Remove(steamId);
            }
        }

        /// <summary>插件卸载时清理所有 lease。</summary>
        internal static void ClearAll()
        {
            lock (_lock)
            {
                _leases.Clear();
            }
        }

        /// <summary>测试用：查询当前是否持有 lease。仅诊断用途。</summary>
        internal static bool IsHeld(CSteamID steamId)
        {
            lock (_lock)
            {
                return _leases.ContainsKey(steamId);
            }
        }

        /// <summary>测试用：查询当前 lease 的 requestId。仅诊断用途。</summary>
        internal static uint CurrentRequestId(CSteamID steamId)
        {
            lock (_lock)
            {
                if (_leases.TryGetValue(steamId, out var lease))
                    return lease.RequestId;
                return 0;
            }
        }
    }
}

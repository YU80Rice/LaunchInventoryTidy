using System;
using System.Collections.Generic;
using Steamworks;

namespace LaunchInventoryTidy
{
    /// <summary>
    /// 单条快捷键恢复条目：整理完成后，原 (oldX, oldY) 的物品已迁移到 (newX, newY)，
    /// 服务器在客户端 ACK 库存已应用后，按新坐标调用 ServerBindItemHotkey。
    ///
    /// v2.0.6.13 Round 9（Codex v2.0.6.13 Round 8 §3.1）：
    ///   - 由 struct 升级为 sealed class，ExpectedItemId 字段替换为完整 ItemFingerprint。
    ///   - 服务端在整理前从已解析的真实 ItemJar 取得 trusted fingerprint；
    ///     绝不信任客户端上传的 quality/state。
    ///   - ACK 阶段按完整指纹校验目标 ItemJar，避免同 ID 不同实例错位绑定。
    /// </summary>
    public sealed class HotkeyRestoreEntry
    {
        public byte HotkeyIndex { get; private set; }
        public byte NewPage { get; private set; }
        public byte NewX { get; private set; }
        public byte NewY { get; private set; }
        public ItemFingerprint ExpectedFingerprint { get; private set; }

        public HotkeyRestoreEntry(byte hotkeyIndex, byte newPage, byte newX, byte newY,
            ItemFingerprint expectedFingerprint)
        {
            HotkeyIndex = hotkeyIndex;
            NewPage = newPage;
            NewX = newX;
            NewY = newY;
            // ItemFingerprint 的 State 是数组，重新按值构造，禁止外部可变引用进入 pending 事务。
            ExpectedFingerprint = new ItemFingerprint(
                expectedFingerprint.Id,
                expectedFingerprint.Amount,
                expectedFingerprint.Quality,
                expectedFingerprint.State);
        }
    }

    /// <summary>
    /// v2.0.6.5 修订（Codex v2.0.6.4 审计 §五阻断项 4）：
    /// 待恢复快捷键事务新增 SessionNonce 字段，账本键升级为 (SteamID, nonce, requestId) 复合键。
    /// </summary>
    public class PendingHotkeyRestore
    {
        public uint RequestId;
        /// <summary>v2.0.6.5 新增：客户端会话 nonce，用于复合键匹配。</summary>
        public ulong SessionNonce;
        public DateTime ExpiresAt;
        public List<HotkeyRestoreEntry> Entries;

        public bool IsExpired => DateTime.UtcNow > ExpiresAt;

        public PendingHotkeyRestore(uint requestId, ulong sessionNonce, List<HotkeyRestoreEntry> entries, TimeSpan ttl)
        {
            RequestId = requestId;
            SessionNonce = sessionNonce;
            Entries = entries ?? new List<HotkeyRestoreEntry>(0);
            ExpiresAt = DateTime.UtcNow + ttl;
        }
    }

    /// <summary>
    /// 事务管理器：按 CSteamID 维护每名玩家的待恢复快捷键事务。
    /// 提供 HasActiveTransaction / Store / Get / Remove / CleanupExpired 接口。
    /// v2.0.6.4：_nextRequestId 改为加密随机初始化，防止客户端重启后复用旧 requestId
    ///           在 RequestLedger 60s TTL 内重放旧响应（Codex v2.0.6.3 审计 §五阻断项 3）。
    /// </summary>
    public static class TidyTransactionManager
    {
        private static readonly Dictionary<CSteamID, PendingHotkeyRestore> _pending =
            new Dictionary<CSteamID, PendingHotkeyRestore>();

        private static readonly object _lock = new object();

        /// <summary>
        /// v2.0.6.4：使用加密随机数生成器初始化 _nextRequestId。
        /// 旧实现：_nextRequestId = 1（固定值），客户端重启后在 RequestLedger 60s TTL 内
        ///         可复用旧 requestId 触发服务器重发旧 mappings。
        /// 新实现：进程启动时生成 31-bit 随机起始值（避免 0 + 避免符号问题），
        ///         客户端重启后 requestId 几乎不可能与上一会话碰撞（2^31 ≈ 21 亿可能性）。
        /// </summary>
        private static uint _nextRequestId = InitializeRandomStart();

        /// <summary>
        /// v2.0.6.4 新增：使用 RandomNumberGenerator 生成加密安全的 31-bit 随机起始值。
        /// Random.Next 是伪随机且可预测，不适合防重放场景；RandomNumberGenerator 基于操作系统熵池。
        /// </summary>
        private static uint InitializeRandomStart()
        {
            try
            {
                byte[] bytes = new byte[4];
                using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create())
                {
                    rng.GetBytes(bytes);
                }
                uint value = System.BitConverter.ToUInt32(bytes, 0);
                // 掩码为 31-bit 避免算术溢出时的符号问题
                value &= 0x7FFFFFFF;
                // 确保非零
                if (value == 0) value = 1;
                LaunchInventoryTidyPlugin.Log?.LogInfo(
                    $"[Txn] requestId 起始值已用加密随机数初始化（start={value}，防重放）");
                return value;
            }
            catch (Exception e)
            {
                // 降级：使用时间戳作为种子（不如加密随机安全，但保证非零且进程间不同）
                uint fallback = (uint)(DateTime.UtcNow.Ticks & 0x7FFFFFFF);
                if (fallback == 0) fallback = 1;
                LaunchInventoryTidyPlugin.Log?.LogWarning(
                    $"[Txn] requestId 加密随机初始化失败，降级为时间戳种子 start={fallback}: {e.Message}");
                return fallback;
            }
        }

        /// <summary>生成下一个 requestId。客户端使用，单调递增。</summary>
        public static uint NextRequestId()
        {
            uint id;
            lock (_lock)
            {
                id = _nextRequestId++;
                if (_nextRequestId == 0) _nextRequestId = 1; // 防 overflow
            }
            return id;
        }

        /// <summary>玩家是否有进行中的事务。</summary>
        public static bool HasActiveTransaction(CSteamID steamId)
        {
            lock (_lock)
            {
                if (!_pending.TryGetValue(steamId, out var p)) return false;
                if (p.IsExpired) { _pending.Remove(steamId); return false; }
                return true;
            }
        }

        /// <summary>存入待恢复事务。若玩家已有事务，覆盖前先记录日志（不抛异常）。</summary>
        public static void Store(CSteamID steamId, PendingHotkeyRestore pending)
        {
            lock (_lock)
            {
                if (_pending.ContainsKey(steamId))
                {
                    LaunchInventoryTidyPlugin.Log?.LogWarning(
                        $"[Txn] 玩家 {(ulong)steamId} 已有进行中事务，将被覆盖 (oldRequestId={_pending[steamId].RequestId}, newRequestId={pending.RequestId})");
                }
                _pending[steamId] = pending;
            }
        }

        /// <summary>
        /// v2.0.6.5 修订：获取指定 (nonce, requestId) 的事务。复合键必须完全匹配。
        /// 若事务不存在或已过期或 nonce 不匹配，返回 null。
        /// </summary>
        public static PendingHotkeyRestore Get(CSteamID steamId, ulong sessionNonce, uint requestId)
        {
            lock (_lock)
            {
                if (!_pending.TryGetValue(steamId, out var p)) return null;
                if (p.SessionNonce != sessionNonce) return null;  // v2.0.6.5：nonce 不匹配
                if (p.RequestId != requestId) return null;
                if (p.IsExpired) { _pending.Remove(steamId); return null; }
                return p;
            }
        }

        /// <summary>v2.0.6.5 修订：移除指定 (nonce, requestId) 的事务。复合键必须完全匹配。</summary>
        public static void Remove(CSteamID steamId, ulong sessionNonce, uint requestId)
        {
            lock (_lock)
            {
                if (_pending.TryGetValue(steamId, out var p)
                    && p.SessionNonce == sessionNonce
                    && p.RequestId == requestId)
                    _pending.Remove(steamId);
            }
        }

        /// <summary>清理所有过期事务。建议由插件定期调用（如每 30 秒一次）。</summary>
        public static void CleanupExpired()
        {
            lock (_lock)
            {
                if (_pending.Count == 0) return;
                var expiredKeys = new List<CSteamID>();
                foreach (var kv in _pending)
                    if (kv.Value.IsExpired) expiredKeys.Add(kv.Key);
                for (int i = 0; i < expiredKeys.Count; i++)
                    _pending.Remove(expiredKeys[i]);
                if (expiredKeys.Count > 0)
                    LaunchInventoryTidyPlugin.Log?.LogInfo($"[Txn] 清理 {expiredKeys.Count} 个过期事务");
            }
        }

        /// <summary>玩家断开连接时清理该玩家的所有待恢复事务（由 Provider.onEnemyDisconnected 调用）。</summary>
        public static void ClearPlayer(CSteamID steamId)
        {
            lock (_lock) _pending.Remove(steamId);
        }

        /// <summary>测试用：清空所有事务。仅单元测试调用。</summary>
        internal static void ClearAllForTests()
        {
            lock (_lock) _pending.Clear();
        }
    }
}

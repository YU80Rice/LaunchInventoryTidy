using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using Steamworks;

namespace LaunchInventoryTidy
{
    /// <summary>
    /// v2.0.6.9 重写（Codex v2.0.6.8 审计 §三 Critical 1 模板 1 + Medium 4 修复）：
    /// 服务端会话 token 注册表 - 服务端生成 + 连接绑定 + 单锁准入状态机组件。
    ///
    /// v2.0.6.9 关键修复（Codex v2.0.6.8 §三 Critical 1 模板 1）：
    ///   - 拆分 ValidateRequest 为 ValidateTokenOnly + TryReserveNextRequestId
    ///   - ValidateTokenOnly：只验证 token 存在 + 匹配（不检查 requestId 单调性）
    ///   - TryReserveNextRequestId：仅对 ledger 不存在的 New 请求才调用
    ///   - 修复 v2.0.6.8 的阻断项：ValidateRequest 在 TryLookup 之前调用导致
    ///     首次 New 已立即更新 Highest；同 token + 同 requestId 的重复包被判定为 Replay
    ///
    /// v2.0.6.9 关键修复（Codex v2.0.6.8 §三 Medium 4）：
    ///   - RNG 失败时不再降级为时间戳，抛 CryptographicException 终止会话建立
    ///   - 整理功能禁用，fail-closed
    ///   - 64-bit token 作为明确的临时测试限制，禁止安全声明
    ///
    /// 协议保留：V3 wire 格式（64-bit token）不变；升级到 V4 128-bit 应单独审计门处理
    /// </summary>
    public static class ServerSessionRegistry
    {
        public enum ValidateResult : byte
        {
            Accepted = 0,
            ReplayDetected = 1,
            InvalidNonce = 2,
        }

        private struct SessionInfo
        {
            public ulong Token;
            public uint HighestRequestId;
            public int Generation;
            public DateTime RegisteredAt;
        }

        private static readonly Dictionary<CSteamID, SessionInfo> _sessions =
            new Dictionary<CSteamID, SessionInfo>();
        private static readonly object _lock = new object();

        private static int _globalGeneration;

        /// <summary>
        /// v2.0.6.8 保留：玩家连接时开始新会话。
        /// v2.0.6.9 修订（Codex v2.0.6.8 §三 Medium 4）：RNG 失败时抛 CryptographicException，
        /// 不再降级为时间戳。调用方应捕获异常并禁用整理功能。
        /// </summary>
        public static ulong BeginSession(CSteamID steamId)
        {
            if (steamId == CSteamID.Nil) return 0;

            // v2.0.6.9：RNG 失败时抛异常，不降级
            ulong token = GenerateTokenOrFail();
            int generation;

            lock (_lock)
            {
                generation = ++_globalGeneration;
                _sessions[steamId] = new SessionInfo
                {
                    Token = token,
                    HighestRequestId = 0,
                    Generation = generation,
                    RegisteredAt = DateTime.UtcNow,
                };
            }

            LaunchInventoryTidyPlugin.Log?.LogInfo(
                $"[Session] 玩家 {(ulong)steamId} 新会话已建立（generation={generation}, token={token:X16}），" +
                $"通过 MSG_SESSION_CHALLENGE 发送给客户端");

            try
            {
                ManualTidyNetwork.SendSessionChallenge(steamId, token);
            }
            catch (Exception e)
            {
                LaunchInventoryTidyPlugin.Log?.LogWarning(
                    $"[Session] SendSessionChallenge 异常 steamId={(ulong)steamId}: {e.Message}");
            }

            return token;
        }

        /// <summary>
        /// v2.0.6.9 新增（Codex v2.0.6.8 §三 Critical 1 模板 1）：
        /// 只验证 token 存在 + 匹配（不检查 requestId 单调性）。
        ///
        /// 用于 TryAdmit 的第一步：先 token-only 验证，再查 ledger（幂等优先）。
        /// 修复 v2.0.6.8 的阻断项：原 ValidateRequest 在 TryLookup 之前检查 requestId 单调性，
        /// 首次 New 已立即更新 Highest，同 token + 同 requestId 的重复包被判定为 Replay。
        /// </summary>
        public static ValidateResult ValidateTokenOnly(CSteamID steamId, ulong token)
        {
            if (steamId == CSteamID.Nil) return ValidateResult.InvalidNonce;
            if (token == 0) return ValidateResult.InvalidNonce;

            lock (_lock)
            {
                if (!_sessions.TryGetValue(steamId, out var info))
                {
                    // v2.0.6.8：未注册的 SteamID - 拒绝（不再自动注册）
                    LaunchInventoryTidyPlugin.Log?.LogWarning(
                        $"[Session] 玩家 {(ulong)steamId} 未注册会话（token={token:X16}），" +
                        $"拒绝（v2.0.6.9：需先收到 MSG_SESSION_CHALLENGE 建立会话）");
                    return ValidateResult.InvalidNonce;
                }

                if (info.Token != token)
                {
                    // token 不匹配 - 拒绝
                    LaunchInventoryTidyPlugin.Log?.LogWarning(
                        $"[Session] 玩家 {(ulong)steamId} token 不匹配（expected={info.Token:X16}, received={token:X16}），" +
                        $"拒绝（可能是旧 token 重放，需断线重连获得新 token）");
                    return ValidateResult.InvalidNonce;
                }

                return ValidateResult.Accepted;
            }
        }

        /// <summary>
        /// v2.0.6.9 新增（Codex v2.0.6.8 §三 Critical 1 模板 1）：
        /// 尝试为 New 请求预留 requestId。
        ///
        /// 仅在 ledger 不存在该 (token, requestId) 时调用（TryLookup 已确认是新请求）。
        /// 检查 requestId > HighestRequestId，若满足则更新 Highest。
        ///
        /// 返回 false 表示 requestId <= Highest（重放或乱序），应拒绝。
        /// </summary>
        public static bool TryReserveNextRequestId(CSteamID steamId, ulong token, uint requestId)
        {
            if (steamId == CSteamID.Nil) return false;
            if (token == 0) return false;
            if (requestId == 0) return false;

            lock (_lock)
            {
                if (!_sessions.TryGetValue(steamId, out var info))
                {
                    // 不应发生（ValidateTokenOnly 已确认），防御性拒绝
                    return false;
                }

                if (info.Token != token)
                {
                    // token 在 ValidateTokenOnly 和 TryReserveNextRequestId 之间被替换（断线重连）
                    // 拒绝旧 token 的请求
                    return false;
                }

                if (requestId <= info.HighestRequestId)
                {
                    LaunchInventoryTidyPlugin.Log?.LogWarning(
                        $"[Session] 玩家 {(ulong)steamId} 检测到重放或乱序：reqId={requestId} <= Highest={info.HighestRequestId}，拒绝");
                    return false;
                }

                // 更新 HighestRequestId
                info.HighestRequestId = requestId;
                _sessions[steamId] = info;
                return true;
            }
        }

        /// <summary>
        /// v2.0.6.9 新增（Codex v2.0.6.8 §三 Critical 1 模板 1）：
        /// 仅在新请求 TryCreateReceivedNonEvicting 成功后调用，更新 HighestRequestId。
        ///
        /// 这是为了保持与 v2.0.6.8 行为兼容的辅助方法。
        /// 实际上 TryReserveNextRequestId 已更新 Highest，本方法为向后兼容保留。
        /// </summary>
        public static void UpdateHighestRequestId(CSteamID steamId, uint requestId)
        {
            // v2.0.6.9：TryReserveNextRequestId 已更新 Highest，本方法为向后兼容保留
            // 新代码应使用 TryReserveNextRequestId
            lock (_lock)
            {
                if (!_sessions.TryGetValue(steamId, out var info)) return;
                if (requestId > info.HighestRequestId)
                {
                    info.HighestRequestId = requestId;
                    _sessions[steamId] = info;
                }
            }
        }

        /// <summary>
        /// v2.0.6.9 新增：token-only 验证通过后的 HighestRequestId 查询（不修改状态）。
        /// 供 TryAdmit 在 TryReserveNextRequestId 失败时诊断用。
        /// </summary>
        internal static uint GetHighestRequestId(CSteamID steamId)
        {
            lock (_lock)
            {
                if (!_sessions.TryGetValue(steamId, out var info))
                    return 0;
                return info.HighestRequestId;
            }
        }

        /// <summary>
        /// v2.0.6.9 修订（Codex v2.0.6.8 §三 Medium 4）：
        /// 生成 64-bit token。RNG 失败时抛 CryptographicException，不降级为时间戳。
        /// 调用方（BeginSession）应捕获异常并禁用整理功能。
        ///
        /// 64-bit vs 128-bit 声明（v2.0.6.9 明确临时测试限制）：
        ///   - 当前保持 V3 wire 格式（64-bit）
        ///   - 64-bit 是临时测试限制，不是安全声明
        ///   - 升级到 V4 128-bit 应作为协议大版本升级单独审计门处理
        ///   - 已禁止 RNG 降级，token 必须是加密随机
        /// </summary>
        private static ulong GenerateTokenOrFail()
        {
            byte[] bytes = new byte[8];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(bytes);
            }
            ulong token = BitConverter.ToUInt64(bytes, 0);
            if (token == 0) token = 1;  // 非零保证
            return token;
            // 注意：不再有 catch 降级为时间戳的路径
            // RandomNumberGenerator.Create() 失败会抛异常，由调用方处理
        }

        /// <summary>
        /// v2.0.6.8 保留（向后兼容）：验证请求的 token 和 requestId。
        /// v2.0.6.9 修订：内部调用 ValidateTokenOnly，不再检查 requestId 单调性。
        /// 新代码应使用 RequestAdmissionStore.TryAdmit + ValidateTokenOnly + TryReserveNextRequestId。
        /// </summary>
        public static ValidateResult ValidateRequest(CSteamID steamId, ulong token, uint requestId)
        {
            // v2.0.6.9：仅做 token-only 验证，requestId 单调性由 TryReserveNextRequestId 处理
            return ValidateTokenOnly(steamId, token);
        }

        /// <summary>向后兼容包装。</summary>
        public static ValidateResult ValidateOrUpdate(CSteamID steamId, ulong token, uint requestId)
        {
            var result = ValidateTokenOnly(steamId, token);
            if (result == ValidateResult.Accepted)
            {
                TryReserveNextRequestId(steamId, token, requestId);
            }
            return result;
        }

        internal static ulong GetCurrentNonce(CSteamID steamId)
        {
            lock (_lock)
            {
                if (_sessions.TryGetValue(steamId, out var info))
                    return info.Token;
                return 0;
            }
        }

        public static void ClearPlayer(CSteamID steamId)
        {
            if (steamId == CSteamID.Nil) return;
            lock (_lock)
            {
                _sessions.Remove(steamId);
            }
        }

        internal static void ClearAll()
        {
            lock (_lock)
            {
                _sessions.Clear();
                _globalGeneration = 0;
            }
        }
    }
}

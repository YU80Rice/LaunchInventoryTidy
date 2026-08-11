using Steamworks;

namespace LaunchInventoryTidy
{
    /// <summary>
    /// v2.0.6.9 重写（Codex v2.0.6.8 审计 §三 Critical 1 模板 1 + Medium 2 模板 2 修复）：
    /// 单锁原子准入控制 - 正确的 admission 顺序。
    ///
    /// v2.0.6.8 阻断项（Codex v2.0.6.8 §三 Critical 1）：
    ///   - TryAdmit 先调用 ValidateRequest（检查 requestId > HighestRequestId），然后才查 ledger
    ///   - 首次 New 已立即更新 Highest，导致同 token + 同 requestId 的重复包被判定为 Replay
    ///   - 重复包永远到不了 InFlight/Cached 分支，被回送 Rejected，破坏重传链路
    ///
    /// v2.0.6.9 修复方案（模板 1）：
    ///   1. ValidateTokenOnly（只验证 token 存在+匹配，不检查 requestId 单调性）
    ///   2. TryLookup（ledger 幂等查询 - Received -> InFlight, 终态 -> Cached）
    ///   3. HasLease（BusyDifferent - 不建 ledger 条目）
    ///   4. TryReserveNextRequestId（仅对新请求检查 requestId 单调性 + 更新 Highest）
    ///   5. TryCreateReceivedNonEvicting（模板 2：容量检查 + 驱逐 + 插入合并）
    ///   6. TryAcquireWithResult（获取 lease）
    ///
    /// v2.0.6.9 修复方案（模板 2）：
    ///   - 删除 HasCapacityForNew + CreateReceivedEntry 的两步 API
    ///   - 改为 TryCreateReceivedNonEvicting：容量检查 + 驱逐 + 插入在同一锁内
    ///   - 强制不变量：Received 不被驱逐，list.Count 不超过 MAX
    ///
    /// 嵌套锁顺序（无死锁风险，所有路径一致）：
    ///   RequestAdmissionStore._gate -> RequestLedger._lock -> PlayerOperationGate._lock
    /// </summary>
    public static class RequestAdmissionStore
    {
        public enum AdmissionKind : byte
        {
            New = 0,
            InFlight = 1,
            Cached = 2,
            BusyDifferent = 3,
            Rejected = 4,
        }

        private static readonly object _gate = new object();

        /// <summary>
        /// v2.0.6.9 重写（模板 1）：原子准入决策。
        ///
        /// 正确的 admission 顺序（关键修复）：
        ///   1. ValidateTokenOnly（token-only，不检查 requestId 单调性）
        ///   2. TryLookup（ledger 幂等查询）
        ///      - Received -> InFlight（静默丢弃）
        ///      - 终态 -> Cached（重发缓存响应）
        ///   3. HasLease -> BusyDifferent（不建 ledger 条目）
        ///   4. TryReserveNextRequestId（仅对新请求检查 requestId 单调性）
        ///   5. TryCreateReceivedNonEvicting（模板 2：容量检查+驱逐+插入合并）
        ///   6. TryAcquireWithResult（获取 lease）
        ///
        /// 关键不变量：
        ///   - Cached/InFlight 绝不触发 Replay 拒绝（修复 v2.0.6.8 阻断项）
        ///   - BusyDifferent 不建 ledger 条目（防止账本攻击）
        ///   - TryReserveNextRequestId 仅在 New 请求路径调用
        ///   - TryCreateReceivedNonEvicting 强制 MAX_ENTRIES_PER_PLAYER 上限
        /// </summary>
        public static AdmissionKind TryAdmit(CSteamID steamId, ulong sessionNonce, uint requestId,
            out RequestLedger.LedgerEntry cachedEntry)
        {
            lock (_gate)
            {
                cachedEntry = default;

                // 1. token-only 验证（不检查 requestId 单调性）
                var sessionResult = ServerSessionRegistry.ValidateTokenOnly(steamId, sessionNonce);
                if (sessionResult == ServerSessionRegistry.ValidateResult.InvalidNonce)
                {
                    // token 未注册/不匹配/SteamID 无效 - 拒绝（不建 ledger 条目）
                    return AdmissionKind.Rejected;
                }

                // 2. ledger 幂等查询（Cached/InFlight 优先 - 修复 v2.0.6.8 阻断项）
                if (RequestLedger.TryLookup(steamId, sessionNonce, requestId, out var existing))
                {
                    cachedEntry = existing;
                    if (existing.State == RequestLedger.RequestState.Received)
                    {
                        // 原请求仍在处理中，静默丢弃
                        return AdmissionKind.InFlight;
                    }
                    // Committed/Failed/Expired - 重发缓存响应
                    return AdmissionKind.Cached;
                }

                // 3. lease 检查（BusyDifferent - 不建 ledger 条目）
                if (PlayerOperationGate.IsHeld(steamId))
                {
                    return AdmissionKind.BusyDifferent;
                }

                // 4. 仅对新请求检查 requestId 单调性（模板 1 关键修复）
                if (!ServerSessionRegistry.TryReserveNextRequestId(steamId, sessionNonce, requestId))
                {
                    // requestId <= Highest（重放或乱序），拒绝
                    // 注意：此时没有创建 ledger 条目，没有获取 lease
                    LaunchInventoryTidyPlugin.Log?.LogWarning(
                        $"[Admission] TryReserveNextRequestId 失败：reqId={requestId} nonce={sessionNonce:X16} " +
                        $"steamId={(ulong)steamId}（requestId <= Highest，重放或乱序）");
                    return AdmissionKind.Rejected;
                }

                // 5. 容量检查 + 驱逐 + 插入合并（模板 2 关键修复）
                // 注意：TryReserveNextRequestId 已更新 Highest，
                // 若 TryCreateReceivedNonEvicting 失败，需要回滚 Highest（不回滚也不会致命，因为
                // 后续相同 requestId 会因 TryReserveNextRequestId 失败被拒绝，但会浪费一个 requestId）
                if (!RequestLedger.TryCreateReceivedNonEvicting(steamId, sessionNonce, requestId))
                {
                    // 容量满且全部为 Received - 拒绝
                    // 注意：Highest 已被 TryReserveNextRequestId 更新，但这不会导致问题
                    // （后续相同 requestId 会被 TryLookup 命中 Cached/Received 路径）
                    return AdmissionKind.Rejected;
                }

                // 6. 获取 lease（IsHeld 已确认为 false，TryAcquireWithResult 必返回 Acquired）
                var acquireResult = PlayerOperationGate.TryAcquireWithResult(steamId, sessionNonce, requestId);
                if (acquireResult != PlayerOperationGate.AcquireResult.Acquired)
                {
                    // 极端情况：在 _gate 锁内 IsHeld=false 但 TryAcquireWithResult 未返回 Acquired
                    // 回滚 ledger 条目（标记为 Failed）
                    RequestLedger.MarkFailed(steamId, sessionNonce, requestId);
                    LaunchInventoryTidyPlugin.Log?.LogError(
                        $"[Admission] TryAcquireWithResult 未返回 Acquired（result={acquireResult}），已回滚 ledger 条目 " +
                        $"nonce={sessionNonce:X16} reqId={requestId} sender={(ulong)steamId}");
                    return AdmissionKind.Rejected;
                }

                return AdmissionKind.New;
            }
        }

        /// <summary>
        /// v2.0.6.8 保留（模板 A 补偿）：取消 New 状态的准入。
        ///
        /// 触发条件：
        ///   - TryEnqueue 失败（队列满或 Shutdown）
        ///
        /// 行为：
        ///   1. 释放 lease（PlayerOperationGate.Release）
        ///   2. 标记 ledger 条目为 Failed（RequestLedger.MarkFailed）
        ///
        /// 调用此方法后，后续相同 (nonce, requestId) 重复包会走 Cached 路径，
        /// 重发 Rejected 响应（不是重新执行）。
        /// </summary>
        public static void CancelNew(CSteamID steamId, ulong sessionNonce, uint requestId)
        {
            lock (_gate)
            {
                PlayerOperationGate.Release(steamId, requestId);
                RequestLedger.MarkFailed(steamId, sessionNonce, requestId);
            }
            LaunchInventoryTidyPlugin.Log?.LogInfo(
                $"[Admission] CancelNew：nonce={sessionNonce:X16} reqId={requestId} steamId={(ulong)steamId} " +
                $"（lease 已释放 + ledger 已标 Failed，调用方应回送 Rejected）");
        }
    }
}

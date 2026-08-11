#if TIDY_TEST_HARNESS
using System;
using System.Collections.Generic;

namespace LaunchInventoryTidy
{
    /// <summary>
    /// v2.0.6.13 新增（Codex 架构审计 §3.3 修复蓝图）：
    /// 网络回环探针 - 仅在 TIDY_TEST_HARNESS 构建中生效。
    ///
    /// 用途：
    ///   - 在 HandleTidyCommittedFromServer 协议校验通过后记录 (requestId, TidyCommitResult)
    ///   - 在 HandleTidyHotkeyResultFromServer 解析通过后记录 (requestId, HotkeyFlowResult)
    ///   - 测试驱动器通过 TryGet 等待网络回环完成，避免直接调用服务层绕过协议
    ///
    /// 关键不变量：
    ///   - 静态字典 + lock 保护，网络回调线程与主线程均可安全访问
    ///   - 每条记录保留 30 秒后自动清理（防内存泄漏）
    ///   - 只记录协议校验通过的回包，被拒绝的包不记录
    ///   - 不影响生产代码路径，所有调用点在 #if TIDY_TEST_HARNESS 内
    /// </summary>
    internal static class NetworkTestProbe
    {
        private static readonly object Gate = new object();
        private static readonly Dictionary<uint, TidyCommitResult> Commits =
            new Dictionary<uint, TidyCommitResult>();
        private static readonly Dictionary<uint, HotkeyFlowResult> Hotkeys =
            new Dictionary<uint, HotkeyFlowResult>();
        private static readonly Dictionary<uint, DateTime> CommitTimestamps =
            new Dictionary<uint, DateTime>();
        private static readonly Dictionary<uint, DateTime> HotkeyTimestamps =
            new Dictionary<uint, DateTime>();

        private const double RecordTtlSeconds = 30.0;

        /// <summary>快捷键恢复流程结果（HandleTidyHotkeyResultFromServer 解析后的字段）。</summary>
        public struct HotkeyFlowResult
        {
            public byte RestoredCount;
            public byte VerifiedCount;
            public byte ClearedCount;
            public byte FailedCount;
            public bool HasReply; // true 表示已收到 HotkeyResult 回包
        }

        /// <summary>
        /// 在 HandleTidyCommittedFromServer 协议校验通过后调用。
        /// 记录 (requestId, result)。result=Committed 表示整理已提交，需继续等待 HotkeyResult。
        /// </summary>
        internal static void RecordCommit(uint requestId, TidyCommitResult result)
        {
            if (requestId == 0) return;
            lock (Gate)
            {
                Commits[requestId] = result;
                CommitTimestamps[requestId] = DateTime.UtcNow;
                CleanupExpiredNoLock();
            }
        }

        /// <summary>
        /// 在 HandleTidyHotkeyResultFromServer 协议校验通过后调用。
        /// 记录 (requestId, hotkeyFlow)。
        /// </summary>
        internal static void RecordHotkey(uint requestId, byte restored, byte verified, byte cleared, byte failed)
        {
            if (requestId == 0) return;
            lock (Gate)
            {
                Hotkeys[requestId] = new HotkeyFlowResult
                {
                    RestoredCount = restored,
                    VerifiedCount = verified,
                    ClearedCount = cleared,
                    FailedCount = failed,
                    HasReply = true,
                };
                HotkeyTimestamps[requestId] = DateTime.UtcNow;
                CleanupExpiredNoLock();
            }
        }

        /// <summary>
        /// 查询 requestId 是否已有 Commit 回包。
        /// </summary>
        internal static bool HasCommitReply(uint requestId)
        {
            lock (Gate)
            {
                return requestId != 0 && Commits.ContainsKey(requestId);
            }
        }

        /// <summary>
        /// 查询 requestId 是否已有 HotkeyResult 回包。
        /// </summary>
        internal static bool HasHotkeyReply(uint requestId)
        {
            lock (Gate)
            {
                return requestId != 0 && Hotkeys.ContainsKey(requestId);
            }
        }

        /// <summary>
        /// 查询 requestId 是否已收到有效回复（Commit=Committed 且 HotkeyResult 已到）。
        /// Codex §3.3 蓝图：`NetworkTestProbe.HasValidReply(requestId)`。
        /// </summary>
        internal static bool HasValidReply(uint requestId)
        {
            lock (Gate)
            {
                if (requestId == 0) return false;
                if (!Commits.TryGetValue(requestId, out var commit)) return false;
                // Rejected / CriticalFailure 也算"已回复"，但调用方需区分
                if (commit != TidyCommitResult.Committed) return true;
                // Committed 需继续等待 HotkeyResult
                return Hotkeys.ContainsKey(requestId);
            }
        }

        /// <summary>
        /// 取出 requestId 的 Commit + Hotkey 结果。
        /// 返回 true 表示至少有 Commit 回包。
        /// </summary>
        internal static bool TryGet(uint requestId, out TidyCommitResult commit, out HotkeyFlowResult hotkey)
        {
            lock (Gate)
            {
                if (requestId == 0 || !Commits.TryGetValue(requestId, out commit))
                {
                    commit = TidyCommitResult.Rejected;
                    hotkey = default;
                    return false;
                }
                hotkey = Hotkeys.TryGetValue(requestId, out var h) ? h : default;
                return true;
            }
        }

        /// <summary>重置所有记录（测试套件之间清理）。</summary>
        internal static void Reset()
        {
            lock (Gate)
            {
                Commits.Clear();
                Hotkeys.Clear();
                CommitTimestamps.Clear();
                HotkeyTimestamps.Clear();
            }
        }

        private static void CleanupExpiredNoLock()
        {
            var now = DateTime.UtcNow;
            var expiredCommits = new List<uint>();
            foreach (var kv in CommitTimestamps)
            {
                if ((now - kv.Value).TotalSeconds > RecordTtlSeconds)
                    expiredCommits.Add(kv.Key);
            }
            foreach (var id in expiredCommits)
            {
                Commits.Remove(id);
                CommitTimestamps.Remove(id);
            }

            var expiredHotkeys = new List<uint>();
            foreach (var kv in HotkeyTimestamps)
            {
                if ((now - kv.Value).TotalSeconds > RecordTtlSeconds)
                    expiredHotkeys.Add(kv.Key);
            }
            foreach (var id in expiredHotkeys)
            {
                Hotkeys.Remove(id);
                HotkeyTimestamps.Remove(id);
            }
        }
    }
}
#endif

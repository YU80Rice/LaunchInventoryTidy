#if TIDY_TEST_HARNESS
using System;

namespace LaunchInventoryTidy
{
    /// <summary>
    /// v2.0.6.13 新增（Codex 架构审计 §3.4 修复蓝图）：
    /// 关闭流程探针 - 仅在 TIDY_TEST_HARNESS 构建中生效。
    ///
    /// 用途：
    ///   - SP-SD：在 MainThreadDispatcher.TryEnqueue 入队后立即 Provider.quit
    ///   - 验证真实 drain（Shutdown() 内 Cancel 回调执行次数）+ lease 释放 + client pending 清除
    ///   - 不得把"无任务关闭"计作通过
    ///
    /// 关键不变量：
    ///   - 只读快照 MainThreadDispatcher.PendingCount / CancelledDuringShutdownCount
    ///   - 不修改生产状态
    ///   - 入队后立即触发关闭，记录入队前/关闭前/关闭后三阶段状态
    /// </summary>
    internal static class ShutdownTestProbe
    {
        /// <summary>SP-SD 单次测试的状态快照。</summary>
        public struct ShutdownStateSnapshot
        {
            public int PendingBeforeEnqueue;       // 入队前队列大小
            public int PendingAfterEnqueue;        // 入队后队列大小
            public int CancelledBeforeShutdown;    // 入队后已取消数
            public int PendingBeforeShutdown;      // 关闭前队列大小
            public int CancelledAfterShutdown;     // 关闭后已取消数
            public int PendingAfterShutdown;       // 关闭后队列大小（应=0）
            public bool TaskEntered;               // 任务是否真的进入执行
            public bool TaskCancelled;             // 任务是否被 Cancel 回调标记
            public DateTime CapturedAtUtc;
        }

        /// <summary>
        /// 捕获 SP-SD 三阶段状态快照。
        /// 调用时序：
        ///   1. BeforeEnqueue：TryEnqueue 之前
        ///   2. AfterEnqueue：TryEnqueue 之后、BeginQuiesce 之前
        ///   3. AfterShutdown：CompleteShutdown 之后
        /// </summary>
        internal static ShutdownStateSnapshot Capture(
            int pendingBeforeEnqueue,
            int pendingAfterEnqueue,
            int cancelledBeforeShutdown,
            int pendingBeforeShutdown,
            int cancelledAfterShutdown,
            int pendingAfterShutdown,
            bool taskEntered,
            bool taskCancelled)
        {
            return new ShutdownStateSnapshot
            {
                PendingBeforeEnqueue = pendingBeforeEnqueue,
                PendingAfterEnqueue = pendingAfterEnqueue,
                CancelledBeforeShutdown = cancelledBeforeShutdown,
                PendingBeforeShutdown = pendingBeforeShutdown,
                CancelledAfterShutdown = cancelledAfterShutdown,
                PendingAfterShutdown = pendingAfterShutdown,
                TaskEntered = taskEntered,
                TaskCancelled = taskCancelled,
                CapturedAtUtc = DateTime.UtcNow,
            };
        }

        /// <summary>
        /// SP-SD 通过条件（Codex §4 SP-SD 清单）：
        ///   1. 入队后队列大小 > 入队前（任务真的入队）
        ///   2. 关闭前队列大小 > 0（确实有在途请求）
        ///   3. 关闭后队列大小 == 0（真实 drain 完成）
        ///   4. 关闭后已取消数 > 关闭前已取消数（CancelNew 或等效补偿已执行）
        ///   5. 任务未真的执行（taskEntered=false）或已通过 Cancel 路径（taskCancelled=true）
        /// </summary>
        internal static bool IsPassing(ShutdownStateSnapshot s, out string failure)
        {
            if (s.PendingAfterEnqueue <= s.PendingBeforeEnqueue)
            {
                failure = $"入队未生效：PendingBeforeEnqueue={s.PendingBeforeEnqueue}, PendingAfterEnqueue={s.PendingAfterEnqueue}";
                return false;
            }
            if (s.PendingBeforeShutdown <= 0)
            {
                failure = $"关闭前无在途请求：PendingBeforeShutdown={s.PendingBeforeShutdown}（不得把无任务关闭计作通过）";
                return false;
            }
            if (s.PendingAfterShutdown != 0)
            {
                failure = $"关闭后队列未排空：PendingAfterShutdown={s.PendingAfterShutdown}";
                return false;
            }
            if (s.CancelledAfterShutdown <= s.CancelledBeforeShutdown)
            {
                failure = $"CancelNew 未执行：CancelledBefore={s.CancelledBeforeShutdown}, CancelledAfter={s.CancelledAfterShutdown}";
                return false;
            }
            // 任务若已执行（taskEntered=true），说明队列已 drain 到该任务，不算"在途被取消"
            if (s.TaskEntered && !s.TaskCancelled)
            {
                failure = $"任务已执行（TaskEntered=true）但未被 Cancel 路径标记，无法证明在途取消";
                return false;
            }
            failure = null;
            return true;
        }
    }
}
#endif

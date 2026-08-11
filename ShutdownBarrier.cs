#if TIDY_TEST_HARNESS
using System;
using System.Collections.Generic;
using System.Threading;

namespace LaunchInventoryTidy
{
    /// <summary>
    /// v2.0.6.13 Codex 第二轮复审 §3.1 + 第三轮复审 §3.1 实现：
    /// SP-SD 真实事务屏障 - 仅在 TIDY_TEST_HARNESS 构建中生效。
    ///
    /// 第三轮修订（Codex 第三轮 §3.1 + §3.4）：
    ///   - 新增 HoldNextQueuedRequest：Armed 后真正冻结 MainThreadDispatcher.ProcessAll 取队列，
    ///     使被测请求保留在队列中直到测试主动调用 MainThreadDispatcher.Shutdown() 触发真实 CancelNew
    ///   - 新增 ReleaseHold：测试已观测到入队后释放冻结，让 Shutdown drain 能取到该请求
    ///   - 不再仅"记录"：以 _holdQueued 标志真正阻止 ProcessAll dequeue
    ///
    /// 关键不变量：
    ///   - 不修改 Release 构建行为（整文件在 #if TIDY_TEST_HARNESS 内）
    ///   - Armed + HoldQueued 时 ProcessAll 直接 return；Release 后允许正常 dequeue
    ///   - 线程安全：lock 保护所有读写
    /// </summary>
    internal static class ShutdownBarrier
    {
        private static readonly object _gate = new object();
        private static bool _armed;
        private static bool _holdQueued;
        private static uint _lastQueuedRequestId;
        private static bool _hasQueuedRequest;
        private static int _cancelInvocationCount;
        private static DateTime _armedAtUtc;

        /// <summary>武装屏障，准备捕获下一个入队的真实请求，并立即冻结 ProcessAll 取队列。</summary>
        internal static void Arm()
        {
            lock (_gate)
            {
                _armed = true;
                _holdQueued = true;
                _lastQueuedRequestId = 0;
                _hasQueuedRequest = false;
                _cancelInvocationCount = 0;
                _armedAtUtc = DateTime.UtcNow;
            }
        }

        /// <summary>解除武装，清理状态并释放冻结。</summary>
        internal static void Disarm()
        {
            lock (_gate)
            {
                _armed = false;
                _holdQueued = false;
                _lastQueuedRequestId = 0;
                _hasQueuedRequest = false;
            }
        }

        /// <summary>
        /// MainThreadDispatcher.ProcessAll 开头调用：若屏障武装且冻结激活，则真正阻止取队列。
        /// Release 构建中整文件被 #if 排除，此方法不存在。
        /// </summary>
        internal static bool ShouldHoldNextQueuedRequest()
        {
            lock (_gate) { return _armed && _holdQueued; }
        }

        /// <summary>
        /// 测试观测到真实请求已入队后调用，释放冻结，让后续 Shutdown drain 能取到该请求并触发真实 Cancel 回调。
        /// 不解除 Armed 状态：Cancel 回调仍需 RecordCancelInvocation 计数。
        /// </summary>
        internal static void ReleaseHold()
        {
            lock (_gate) { _holdQueued = false; }
        }

        /// <summary>生产代码钩子：TryEnqueue 成功后调用，记录真实入队的 requestId。</summary>
        internal static void RecordQueuedRequest(uint requestId)
        {
            lock (_gate)
            {
                if (!_armed) return;
                _lastQueuedRequestId = requestId;
                _hasQueuedRequest = true;
            }
        }

        /// <summary>生产代码钩子：Cancel 回调调用，记录取消调用次数。</summary>
        internal static void RecordCancelInvocation(uint requestId)
        {
            lock (_gate)
            {
                if (!_armed) return;
                _cancelInvocationCount++;
            }
        }

        /// <summary>查询是否有真实请求入队，并返回 requestId。</summary>
        internal static bool TryGetQueuedRequestId(out uint requestId)
        {
            lock (_gate)
            {
                requestId = _lastQueuedRequestId;
                return _hasQueuedRequest;
            }
        }

        /// <summary>查询 Cancel 回调调用次数。</summary>
        internal static int CancelInvocationCount
        {
            get
            {
                lock (_gate) { return _cancelInvocationCount; }
            }
        }

        /// <summary>查询屏障是否武装。</summary>
        internal static bool IsArmed
        {
            get
            {
                lock (_gate) { return _armed; }
            }
        }
    }
}
#endif

using System;
using System.Security.Cryptography;
using System.Threading;

namespace LaunchInventoryTidy
{
    /// <summary>
    /// v2.0.6.10 重写（Codex v2.0.6.9 审计 §三 Medium / §五阻断项 3 修复）：
    /// 客户端会话 token - 服务端签发 + 客户端入口闸门 + 锁保护原子读 API。
    ///
    /// v2.0.6.10 关键修复（Codex v2.0.6.9 §三 Medium）：
    ///   - 新增 _stateLock 私有锁，保护所有 nonce 状态（_value/_isServerIssued/_initializationFailed/_initialized）
    ///   - 新增 TryGetServerIssuedToken(out ulong token) 原子读 API
    ///   - 发送路径 ManualTidyNetwork.TrySendTidyRequest 禁止分别读取 IsReady + Value
    ///     必须使用 TryGetServerIssuedToken 在同一临界区复制 token
    ///   - challenge 更新（ReplaceWithServerChallenge）和 Initialize 也走同一锁
    ///   - 32-bit 运行时不假定 ulong 读写原子
    ///   - 网络回调未证明必在 Unity 主线程，裸读写允许读到"已签发=true + 旧临时 token"或不一致状态
    ///
    /// v2.0.6.9 保留（历史）：
    ///   - IsReady 属性 + NextRequestId() 单调递增
    ///   - RNG 失败 fail-closed（CryptographicException）
    ///
    /// 64-bit vs 128-bit 声明（v2.0.6.9 明确临时测试限制）：
    ///   - 当前保持 V3 wire 格式（64-bit）
    ///   - 64-bit 是临时测试限制，不是安全声明
    ///   - 升级到 V4 128-bit 应作为协议大版本升级单独审计门处理
    /// </summary>
    public static class ClientSessionNonce
    {
        // v2.0.6.10：私有锁，保护所有 nonce 状态字段
        private static readonly object _stateLock = new object();

        private static ulong _value;
        private static bool _initialized;
        private static bool _isServerIssued;
        private static bool _initializationFailed;  // v2.0.6.9：RNG 失败标记

        // v2.0.6.9：客户端 requestId 计数器（单调递增）
        private static int _nextRequestIdCounter;

        /// <summary>
        /// v2.0.6.10 新增（Codex v2.0.6.9 §三 Medium / §五阻断项 3）：
        /// 原子读取服务端签发的 token。
        ///
        /// 在同一锁临界区内：
        ///   1. 检查 _initializationFailed == false
        ///   2. 检查 _isServerIssued == true
        ///   3. 检查 _value != 0
        ///   4. 复制 _value 到 token out 参数
        ///
        /// 返回 true 时 token 为有效服务端签发 token；返回 false 时整理功能不可用。
        ///
        /// 调用方：ManualTidyNetwork.TrySendTidyRequest
        /// 行为：返回 false 时拒绝发送，不建 pending，不发包
        /// 禁止：分别读取 IsReady + Value（存在 TOCTOU 窗口）
        /// </summary>
        public static bool TryGetServerIssuedToken(out ulong token)
        {
            lock (_stateLock)
            {
                if (_initializationFailed)
                {
                    token = 0;
                    return false;
                }
                if (!_isServerIssued)
                {
                    token = 0;
                    return false;
                }
                if (_value == 0)
                {
                    token = 0;
                    return false;
                }
                token = _value;
                return true;
            }
        }

        /// <summary>
        /// v2.0.6.10 锁保护版本：当前会话 token。
        /// 0 表示未初始化或初始化失败。
        /// 注意：发送路径应使用 TryGetServerIssuedToken 原子读，不应分别读 IsReady + Value。
        /// 此属性保留仅为诊断和向后兼容用途。
        /// </summary>
        public static ulong Value
        {
            get
            {
                lock (_stateLock)
                {
                    if (!_initialized && !_initializationFailed)
                    {
                        // v2.0.6.10：Initialize 必须在锁内调用，避免重入
                        InitializeLocked();
                    }
                    return _value;
                }
            }
        }

        public static bool IsInitialized
        {
            get
            {
                lock (_stateLock) { return _initialized; }
            }
        }

        /// <summary>v2.0.6.8：是否为服务端签发的 token。</summary>
        public static bool IsServerIssued
        {
            get
            {
                lock (_stateLock) { return _isServerIssued; }
            }
        }

        /// <summary>
        /// v2.0.6.9 新增（Codex v2.0.6.8 §三 Medium 3）：
        /// 客户端是否已就绪发送整理请求。
        ///
        /// v2.0.6.10：加锁保护读取，但发送路径必须使用 TryGetServerIssuedToken
        /// 原子读 API，不应分别读 IsReady + Value（存在 TOCTOU 窗口）。
        ///
        /// 此属性保留仅为 UI 诊断和向后兼容用途。
        /// </summary>
        public static bool IsReady
        {
            get
            {
                lock (_stateLock)
                {
                    if (_initializationFailed) return false;
                    if (!_isServerIssued) return false;
                    if (_value == 0) return false;
                    return true;
                }
            }
        }

        /// <summary>
        /// v2.0.6.9 新增：生成下一个 requestId（单调递增）。
        /// 使用 Interlocked.Increment 保证线程安全。
        /// </summary>
        public static uint NextRequestId()
        {
            int next = Interlocked.Increment(ref _nextRequestIdCounter);
            if (next <= 0)
            {
                // 溢出（理论不会发生，int.MaxValue 次整理），重置为 1
                Interlocked.Exchange(ref _nextRequestIdCounter, 1);
                next = 1;
            }
            return (uint)next;
        }

        /// <summary>
        /// v2.0.6.10 修订（Codex v2.0.6.9 §三 Medium）：
        /// 显式初始化 token - 加锁保护。
        /// RNG 失败时标记 _initializationFailed=true，不降级。
        ///
        /// 由 LaunchInventoryTidyPlugin.Awake 调用。
        /// v2.0.6.8：生成客户端临时 nonce，服务端 challenge 到达后由 ReplaceWithServerChallenge 替换。
        /// v2.0.6.9：RNG 失败时 fail-closed，不再降级为时间戳。
        /// v2.0.6.10：所有状态读写走 _stateLock。
        /// </summary>
        public static void Initialize()
        {
            lock (_stateLock)
            {
                InitializeLocked();
            }
        }

        /// <summary>v2.0.6.10：锁内初始化实现（调用方必须持有 _stateLock）。</summary>
        private static void InitializeLocked()
        {
            if (_initialized) return;
            try
            {
                byte[] bytes = new byte[8];
                using (var rng = RandomNumberGenerator.Create())
                {
                    rng.GetBytes(bytes);
                }
                _value = BitConverter.ToUInt64(bytes, 0);
                if (_value == 0) _value = 1;
                _initialized = true;
                _isServerIssued = false;
                _initializationFailed = false;
                LaunchInventoryTidyPlugin.Log?.LogInfo(
                    $"[TidyNet] 客户端临时 nonce 已初始化（nonce={_value:X16}，等待服务端 challenge）");
            }
            catch (Exception e)
            {
                // v2.0.6.9：RNG 失败时 fail-closed，不降级为时间戳
                _initializationFailed = true;
                _initialized = false;
                _isServerIssued = false;
                _value = 0;
                LaunchInventoryTidyPlugin.Log?.LogError(
                    $"[TidyNet] 客户端临时 nonce 加密随机初始化失败，整理功能禁用（fail-closed）: {e.Message}");
            }
        }

        /// <summary>
        /// v2.0.6.10 锁保护：收到服务端 MSG_SESSION_CHALLENGE 后，替换为服务端签发的 token。
        /// </summary>
        public static void ReplaceWithServerChallenge(ulong serverToken)
        {
            lock (_stateLock)
            {
                if (serverToken == 0)
                {
                    LaunchInventoryTidyPlugin.Log?.LogWarning(
                        "[TidyNet] 拒绝替换为无效服务端 token（=0），保留当前 nonce");
                    return;
                }

                ulong oldNonce = _value;
                bool wasServerIssued = _isServerIssued;

                _value = serverToken;
                _initialized = true;
                _isServerIssued = true;
                _initializationFailed = false;  // v2.0.6.9：收到有效 challenge 清除失败标记

                LaunchInventoryTidyPlugin.Log?.LogInfo(
                    $"[TidyNet] 客户端 nonce 已替换为服务端签发 token（old={oldNonce:X16}, new={serverToken:X16}, " +
                    $"wasServerIssued={wasServerIssued}），后续整理请求将使用此 token");
            }
        }

        /// <summary>测试用：重置 nonce（仅单元测试调用）。</summary>
        internal static void ResetForTests()
        {
            lock (_stateLock)
            {
                _value = 0;
                _initialized = false;
                _isServerIssued = false;
                _initializationFailed = false;
            }
            _nextRequestIdCounter = 0;
        }
    }
}

using UnityEngine;

namespace LaunchInventoryTidy
{
    /// <summary>
    /// v2.0.6 P1-3 新增：客户端 ACK 发送后等待服务器 HotkeyResult 的超时监视器。
    ///
    /// Codex v2.0.5 第六次审计 §2 Medium 指出：原实现 ACK 发送后立即清除 pending，
    /// 没有等待服务器 HotkeyResult 的状态关联。延迟、重复或错误 requestId 的旧结果
    /// 也会触发"快捷键未恢复"警告。
    ///
    /// 修订流程：
    ///   1. 客户端收敛成功 -> 注册 ClientHotkeyResultPending + 发送 ACK
    ///   2. 启动本监视器，超时 3 秒
    ///   3. 服务器返回 HotkeyResult -> HandleTidyHotkeyResultFromServer 消费 pending -> 本监视器自毁
    ///   4. 超时未收到 -> 提示"服务器恢复结果未知" + 清除 pending
    /// v2.0.6.5：复合键升级为 (sessionNonce, requestId)，监视器持有 nonce 用于清理。
    /// </summary>
    public class HotkeyResultWaitBehaviour : MonoBehaviour
    {
        private ulong _sessionNonce;  // v2.0.6.5：V3 协议 nonce
        private uint _requestId;
        private float _timeoutSeconds;
        private float _startTime;
        private bool _done;

        public void StartWait(ulong sessionNonce, uint requestId, float timeoutSeconds)
        {
            _sessionNonce = sessionNonce;
            _requestId = requestId;
            _timeoutSeconds = timeoutSeconds;
            _startTime = Time.realtimeSinceStartup;
            _done = false;
        }

        private void Update()
        {
            if (_done) return;

            // 检查 pending 是否已被消费（HotkeyResult 已到达）
            if (!ManualTidyNetwork.ClientHotkeyResultPending.IsPending(_sessionNonce, _requestId))
            {
                _done = true;
                Destroy(gameObject);
                return;
            }

            // 超时
            if (Time.realtimeSinceStartup - _startTime > _timeoutSeconds)
            {
                _done = true;
                LaunchInventoryTidyPlugin.Log?.LogWarning(
                    $"[TidyNet] 等待 HotkeyResult 超时 nonce={_sessionNonce:X16} reqId={_requestId}，服务器恢复结果未知");
                ManualTidyNetwork.ClientHotkeyResultPending.ClearPending(_sessionNonce, _requestId);
                Destroy(gameObject);
            }
        }
    }
}

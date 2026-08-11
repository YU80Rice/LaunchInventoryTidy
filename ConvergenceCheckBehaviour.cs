using System;
using System.Collections.Generic;
using SDG.Unturned;
using UnityEngine;

namespace LaunchInventoryTidy
{
    /// <summary>
    /// 库存收敛检查协程：客户端收到 TidyCommitted 后，
    /// 在每帧检查本地 inventory 是否已收到服务器发回的重排结果。
    /// 收敛（所有 mappings 对应坐标都有正确 ID 的 ItemJar）后回调 onSuccess；
    /// 超时回调 onFailure。
    ///
    /// v2.0.6.5 修订（Codex v2.0.6.4 审计 §五阻断项 5）：
    /// 本检查仅证明"快捷键目标物品已到达新坐标（id 匹配）"，不证明全量库存已同步。
    /// 收敛成功后发送的 ACK 语义已降级为"HotkeyFlowAck"（快捷键流程 ACK），
    /// 不再命名为"InventoryAppliedAck"（库存已应用）。
    ///
    /// v2.0.6.6 修订（Codex v2.0.6.5 审计 §三 Medium 4 修复）：
    /// 删除"TidyHotkeyResult 提供全量库存同步最终证据"的错误表述。
    /// TidyHotkeyResult 仅含快捷键绑定统计（restoredCount/verifiedCount/clearedCount），
    /// 不证明全量库存同步。全量库存同步证据必须由外部双端测试前后导出的
    /// 全页 (x, y, rot, id, amount, quality, state) 多重集合对照证明。
    /// </summary>
    public class ConvergenceCheckBehaviour : MonoBehaviour
    {
        private Player _player;
        private uint _requestId;
        private List<NewPositionMapping> _targets;
        private int _maxChecks;
        private float _timeoutSeconds;
        private float _startTime;
        private int _checkCount;
        private System.Action _onSuccess;
        private System.Action _onFailure;

        public void StartCheck(Player player, uint requestId,
            List<NewPositionMapping> mappings, int maxChecks, float timeoutSeconds,
            System.Action onSuccess, System.Action onFailure)
        {
            _player = player;
            _requestId = requestId;
            _maxChecks = maxChecks;
            _timeoutSeconds = timeoutSeconds;
            _startTime = Time.realtimeSinceStartup;
            _checkCount = 0;
            _onSuccess = onSuccess;
            _onFailure = onFailure;
            _targets = mappings ?? new List<NewPositionMapping>(0);
        }

        private void Update()
        {
            if (_player == null || _targets == null)
            {
                _onFailure?.Invoke();
                enabled = false;
                return;
            }

            _checkCount++;
            if (_checkCount > _maxChecks || Time.realtimeSinceStartup - _startTime > _timeoutSeconds)
            {
                _onFailure?.Invoke();
                enabled = false;
                return;
            }

            bool allConverged = true;
            for (int i = 0; i < _targets.Count; i++)
            {
                if (!CheckTarget(_targets[i])) { allConverged = false; break; }
            }

            // 没有 mappings 也算收敛（无快捷键需要恢复）
            if (allConverged)
            {
                _onSuccess?.Invoke();
                enabled = false;
            }
        }

        private bool CheckTarget(NewPositionMapping target)
        {
            if (_player.inventory == null) return false;
            if (target.NewPage >= _player.inventory.items.Length) return false;
            Items pageItems = _player.inventory.items[target.NewPage];
            if (pageItems == null) return false;
            if (target.NewX >= pageItems.width || target.NewY >= pageItems.height) return false;
            byte jarIdx = pageItems.getIndex(target.NewX, target.NewY);
            if (jarIdx == byte.MaxValue) return false;
            ItemJar jar = pageItems.getItem(jarIdx);
            return jar?.item != null && jar.item.id == target.ExpectedItemId;
        }
    }
}

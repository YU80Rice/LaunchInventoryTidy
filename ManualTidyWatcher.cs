using LaunchMultiplayerNet;
using SDG.Unturned;
using UnityEngine;

namespace LaunchInventoryTidy
{
    /// <summary>
    /// 监听 Unturned 原生 Plugin 0 按键，触发玩家 5 个多格页（page 2-6）的手动整理。
    /// 玩家需在 Settings -> Controls 中给 Plugin 0 槽位绑定键位后才生效；未绑定时静默不触发。
    ///
    /// v2.0.0 架构：
    /// - 统一走 V2 协议（含快捷键快照上传 + 服务器事务化整理 + ACK 后绑定）
    /// - 默认使用 SameType 模式（同类聚合优先）
    /// - 房主 Unturned 客户端 = 普通客户端，请求统一走 ModTransport.SendToServer -> U3DS
    /// - U3DS 收到请求后在 sender 的 inventory 上执行，vanilla onItemAdded/onItemRemoved 事件链
    ///   自动同步回所有客机端
    /// </summary>
    public class ManualTidyWatcher : MonoBehaviour
    {
        private const int PLUGIN_KEY_INDEX = 0;

        private void Update()
        {
            Player player = Player.LocalPlayer;
            if (player == null || player.inventory == null) return;

            KeyCode key = ControlsSettings.getPluginKeyCode(PLUGIN_KEY_INDEX);
            if (key == KeyCode.None) return;

            if (!InputEx.GetKeyDown(key)) return;

            try
            {
                // v2.0.0：Plugin 0 默认使用 SameType 模式（同类聚合优先），降序排序
                ManualTidyNetwork.SendTidyV2Request(
                    page: ManualTidyNetwork.ALL_PAGES,
                    mode: TidyMode.SameType,
                    sortDescending: true);
            }
            catch (System.Exception e)
            {
                LaunchInventoryTidyPlugin.Log?.LogError("[Tidy] uncaught: " + e);
            }
        }
    }
}

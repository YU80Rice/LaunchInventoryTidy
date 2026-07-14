using LaunchMultiplayerNet;
using SDG.Unturned;
using UnityEngine;

namespace LaunchInventoryTidy
{
    /// <summary>
    /// 监听 Unturned 原生 Plugin 0 按键，触发玩家 5 个多格页（page 2-6）的手动整理。
    /// 玩家需在 Settings -> Controls 中给 Plugin 0 槽位绑定键位后才生效；未绑定时静默不触发。
    ///
    /// 双端自适应（Multiplayer Consistency）：
    /// - Provider.isServer == true（房主）：直接在本地 PlayerInventory 上跑算法
    /// - Provider.isServer == false（客机）：通过 P2P 通道 ModChannels.TidyPage 向服务器
    ///   发送 RequestTidyPage 包，服务器在 sender 的 inventory 上执行，原生 onItemAdded/
    ///   onItemRemoved 事件链自动把结果同步回客机端
    /// </summary>
    public class ManualTidyWatcher : MonoBehaviour
    {
        private const int PLUGIN_KEY_INDEX = 0;

        private void Update()
        {
            // 必须每帧驱动 P2P 轮询（房主收请求，客机无动作但调用安全）
            ModP2PTransport.Poll();

            Player player = Player.LocalPlayer;
            if (player == null || player.inventory == null) return;

            KeyCode key = ControlsSettings.getPluginKeyCode(PLUGIN_KEY_INDEX);
            if (key == KeyCode.None) return;

            if (!InputEx.GetKeyDown(key)) return;

            try
            {
                if (Provider.isServer)
                {
                    // 房主：直接执行（无需网络往返）
                    ManualTidyService.TidyAllPlayerPages(player.inventory);
                }
                else
                {
                    // 客机：发请求包给服务器
                    ManualTidyNetwork.SendTidyAllRequest(sortDescending: true);
                }
            }
            catch (System.Exception e)
            {
                LaunchInventoryTidyPlugin.Log?.LogError("[Tidy] uncaught: " + e);
            }
        }
    }
}

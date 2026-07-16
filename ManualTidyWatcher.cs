using LaunchMultiplayerNet;
using SDG.Unturned;
using UnityEngine;

namespace LaunchInventoryTidy
{
    /// <summary>
    /// 监听 Unturned 原生 Plugin 0 按键，触发玩家 5 个多格页（page 2-6）的手动整理。
    /// 玩家需在 Settings -> Controls 中给 Plugin 0 槽位绑定键位后才生效；未绑定时静默不触发。
    ///
    /// v2.0 架构（2026-07-14 重构）：
    /// - 弃用 listen server + SteamNetworking P2P，改用 U3DS dedicated server + vanilla SteamChannel
    /// - 房主 Unturned 客户端 = 普通客户端，请求统一走 ModTransport.SendToServer -> U3DS
    /// - U3DS 收到请求后在 sender 的 inventory 上执行，vanilla onItemAdded/onItemRemoved 事件链
    ///   自动同步回所有客机端
    /// - 删除 Poll（vanilla SteamChannel 自动路由）
    /// - 删除 Provider.isServer 分支（房主也是客户端，统一发请求给 U3DS）
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
                // 统一走网络路径：客户端 -> U3DS 服务器 -> 自动事件同步回客户端
                // Watcher 是全身整理，无当前页概念，恒用 MaxRects（C 优先）
                ManualTidyNetwork.SendTidyAllRequest(sortDescending: true, mode: TidyMode.MaxRects);
            }
            catch (System.Exception e)
            {
                LaunchInventoryTidyPlugin.Log?.LogError("[Tidy] uncaught: " + e);
            }
        }
    }
}

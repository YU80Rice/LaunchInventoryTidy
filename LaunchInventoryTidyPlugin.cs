using BepInEx.Logging;
using BepInEx;
using HarmonyLib;
using LaunchMultiplayerNet;
using UnityEngine;

namespace LaunchInventoryTidy
{
    [BepInPlugin("com.yu80rice.launchinventorytidy", "LaunchInventoryTidy [v1.4.1 v3.2 网络层适配 + MaxRects + 被动整理已禁用]", "1.4.1")]
    [BepInDependency(LaunchMultiplayerNetPlugin.Guid, BepInDependency.DependencyFlags.HardDependency)]
    public class LaunchInventoryTidyPlugin : BaseUnityPlugin
    {
        public const string HARMONY_ID = "com.yu80rice.launchinventorytidy";

        public static LaunchInventoryTidyPlugin Instance { get; private set; }

        public static ManualLogSource Log { get; private set; }

        public Harmony HarmonyInstance { get; private set; }

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            DontDestroyOnLoad(this.gameObject);
            this.gameObject.hideFlags = HideFlags.HideAndDontSave;

            HarmonyInstance = new Harmony(HARMONY_ID);
            HarmonyInstance.PatchAll();

            // LaunchMultiplayerNetPlugin.Awake 已自动 ModTransport.Initialize()，
            // 此处仅注册本插件的服务器端通道处理器（注册时自动回放暂存请求）。
            ManualTidyNetwork.RegisterHandlers();

            SpawnManualTidyWatcher();

            Logger.LogInfo("===============================================");
            Logger.LogInfo(" LaunchInventoryTidy v1.4.1 已加载（v3.2 网络层适配 + MaxRects + 被动整理已禁用）");
            Logger.LogInfo(" 注意：被动整理 Patch 已禁用，请用 [整理] 按钮或 Plugin 0 按键手动整理");
            Logger.LogInfo("===============================================");
        }

        private void SpawnManualTidyWatcher()
        {
            var go = new GameObject("LaunchInventoryTidy_ManualTidyWatcher");
            Object.DontDestroyOnLoad(go);
            go.AddComponent<ManualTidyWatcher>();
        }

        private void OnDestroy()
        {
            HarmonyInstance?.UnpatchSelf();
            Instance = null;
            Log = null;
        }
    }
}

using BepInEx.Logging;
using BepInEx;
using HarmonyLib;
using LaunchMultiplayerNet;
using UnityEngine;

namespace LaunchInventoryTidy
{
    [BepInPlugin("com.yu80rice.launchinventorytidy", "LaunchInventoryTidy [v1.0 正式版]", "1.0.0")]
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

            // 初始化 P2P 网络层并注册通道处理器
            ModP2PTransport.Initialize();
            ManualTidyNetwork.RegisterHandlers();

            SpawnManualTidyWatcher();

            Logger.LogInfo("===============================================");
            Logger.LogInfo(" LaunchInventoryTidy 已加载（被动+手动整理+联机）");
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

using System;
using System.Reflection;
using BepInEx.Logging;
using LaunchMultiplayerNet;

namespace LaunchInventoryTidy
{
    /// <summary>
    /// v2.0.6.3 新增：前置库最低版本强约束（Dependency Guard）。
    ///
    /// 作用：在插件 Awake 阶段验证当前加载的 LaunchMultiplayerNet 的 AssemblyFileVersion
    /// 不低于 <see cref="MIN_REQUIRED_VERSION"/>。低于此版本时输出 Error 日志并阻止
    /// LaunchInventoryTidy 完成初始化（防止用户在服务器部署旧版前置导致 MissingMethodException
    /// 触发整理请求持续熔断）。
    ///
    /// 版本基线选择 4.0.0.0 的理由（v3.0.0 升级，2026-08-01）：
    /// - LMN v4.0.0.0 是 breaking change 版本（AssemblyVersion 3.2.0.0 -> 4.0.0.0）
    /// - LaunchInventoryTidy v3.0.0+ 重新编译引用 LMN v4.0.0.0，旧 v3 LMN 无法二进制兼容
    /// - 提升最低约束至 4.0.0.0 防止 v3/v4 混合部署（Codex 第二十二轮指导文档 §1 要求）
    /// - 历史基线 3.3.1.0（移除 Dedicator.IsDedicatedServer 直接调用）仍由 4.0.0.0 满足
    /// </summary>
    public static class LmnDependencyGuard
    {
        /// <summary>LMN 最低要求的 AssemblyFileVersion（含）。</summary>
        public static readonly Version MIN_REQUIRED_VERSION = new Version(4, 0, 0, 0);

        /// <summary>当前加载的 LMN AssemblyFileVersion（Guard 通过后非空）。</summary>
        public static Version LoadedVersion { get; private set; }

        /// <summary>Guard 是否已通过。插件其他模块据此判断是否可继续初始化。</summary>
        public static bool IsPassed { get; private set; }

        /// <summary>
        /// 验证当前加载的 LMN 版本是否不低于 <see cref="MIN_REQUIRED_VERSION"/>。
        /// 通过后写入 <see cref="LoadedVersion"/> 与 <see cref="IsPassed"/>=true。
        /// </summary>
        /// <param name="log">插件 Logger（可为 null，null 时仅返回结果）。</param>
        /// <returns>true = 通过；false = 版本不足或无法读取，调用方必须终止初始化。</returns>
        public static bool Verify(ManualLogSource log)
        {
            IsPassed = false;
            LoadedVersion = null;

            try
            {
                Assembly lmnAssembly = typeof(LaunchMultiplayerNetPlugin)?.Assembly;
                if (lmnAssembly == null)
                {
                    log?.LogError("[LmnGuard] 无法解析 LaunchMultiplayerNet 程序集（typeof(LaunchMultiplayerNetPlugin).Assembly == null）");
                    return false;
                }

                var fileVerAttr = lmnAssembly.GetCustomAttribute<AssemblyFileVersionAttribute>();
                if (fileVerAttr == null || string.IsNullOrWhiteSpace(fileVerAttr.Version))
                {
                    log?.LogError($"[LmnGuard] LMN 程序集缺少 AssemblyFileVersion 特性：{lmnAssembly.Location}");
                    return false;
                }

                if (!Version.TryParse(fileVerAttr.Version, out var loaded))
                {
                    log?.LogError($"[LmnGuard] LMN AssemblyFileVersion 非法格式：'{fileVerAttr.Version}'（path={lmnAssembly.Location}）");
                    return false;
                }

                LoadedVersion = loaded;

                if (loaded < MIN_REQUIRED_VERSION)
                {
                    log?.LogError($"[LmnGuard] LaunchMultiplayerNet 版本不足：当前 {loaded} < 最低要求 {MIN_REQUIRED_VERSION}");
                    log?.LogError("[LmnGuard] 请升级 BepInEx/plugins/LaunchMultiplayerNet.dll 至 v4.0.0.0 或更高版本（当前 Release 已冻结为 v4.0.0.0）");
                    log?.LogError("[LmnGuard] LaunchInventoryTidy 已拒绝完成初始化，整理功能将不可用（防止 MissingMethodException 触发持久熔断；禁止 v3/v4 混合部署）");
                    return false;
                }

                log?.LogInfo($"[LmnGuard] LaunchMultiplayerNet 版本检查通过：当前 {loaded} >= 最低要求 {MIN_REQUIRED_VERSION}");
                IsPassed = true;
                return true;
            }
            catch (Exception e)
            {
                log?.LogError($"[LmnGuard] 版本检查异常：{e}");
                return false;
            }
        }
    }
}

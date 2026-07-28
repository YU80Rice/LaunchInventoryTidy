// v1.4.1: 被动整理 Patch 已禁用。
//
// 历史背景：v1.4.0 的被动整理 Patch 在 Items.tryAddItem 上执行"清空整个网格 + 按 TryPack
// 结果重新添加"的大规模重组，导致以下社区反馈 BUG：
//   - BUG-1: 背包满+手上有枪，捡地上的枪被"吞掉"
//   - BUG-2: 装备栏+背包都满，捡地上的枪有收起动作但无替换
//   - BUG-3: 背包空间不够时合成物品被"吞掉"
//
// 根因（经外部审计确认）：
//   1. TryPack 把"尺寸超过当前页面"的新物品排除在 validCount 外，即使新物品 Placed=false
//      仍可能返回 true。Patch 对 Tag=null 的新物品没有原位可恢复，tryFindSpace 再次失败后
//      静默跳过，却仍返回 __result=true。这是 BUG-1/BUG-3 的首要确定性根因。
//   2. Patch 违反 tryAddItem 的成功契约：新物品未实际加入时仍可能设置 __result=true，
//      导致 vanilla 调用方（ItemManager/forceAddItem）错误销毁源物品或跳过 dropItem 兜底。
//   3. 清空整个网格触发 2N 次 Reliable 网络包 + 可能触发 dequip。
//
// 禁用方式：移除 [HarmonyPatch] 特性，类留空。HarmonyInstance.PatchAll() 不会发现此类，
// Patch 不会被注册。
//
// 手动整理路径（[整理] 按钮 + Plugin 0 按键）仍保留，但存在残余风险（ManualTidyService
// 仍执行清空+重添），在物品守恒验证完成前不得宣称生产安全。
//
// 详见：.audit/v1.4.0-bug-analysis-20260716/items-tryadditem-bug-analysis-v2.md（v2 修正版）
// 及外部审计报告。旧版 items-tryadditem-bug-analysis.md 已被 SUPERSEDED 标记。

namespace LaunchInventoryTidy.Patches
{
    /// <summary>
    /// v1.4.1 已禁用。保留类文件用于历史参考，不再被 Harmony 注册。
    /// 不要在此类内添加 Prefix/Postfix 方法或 [HarmonyPatch] 特性。
    /// </summary>
    internal static class ItemsTryAddItemPatch
    {
    }
}

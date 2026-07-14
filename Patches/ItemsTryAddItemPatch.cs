using System.Collections.Generic;
using HarmonyLib;
using SDG.Unturned;

namespace LaunchInventoryTidy.Patches
{
    /// <summary>
    /// 拦截 Items.tryAddItem，在背包已散乱导致 tryFindSpace 失败时，
    /// 启用 2D 装箱求解器对整个网格进行重组，让原本"装不下"的物品能塞进去。
    ///
    /// 行为流程（根据 spec）：
    ///  1) 装备槽单格页 (page < PlayerInventory.SLOTS) → 放行原版逻辑
    ///  2) tryFindSpace 已能找到空位 → 放行原版逻辑
    ///  3) 调用 InventorySolver.TryPack 重排整个网格（含新物品）
    ///     - 装不下 → 放行原版逻辑（让其返回 false）
    ///     - 装得下 → 真实执行 removeItem + addItem 完成重组，
    ///       __result = true 并跳过原方法
    ///
    /// 注意：本补丁在 Prefix 内同步执行 removeItem/addItem，会触发
    /// Items.onItemRemoved/onItemAdded/onStateUpdated 委托链；
    /// 在 PlayerInventory 上下文中这些委托会驱动网络同步包发送。
    /// 大规模重排会产生一批网络包，后续可考虑批量化优化。
    /// </summary>
    [HarmonyPatch(typeof(Items), "tryAddItem", new[] { typeof(Item), typeof(bool) })]
    public static class ItemsTryAddItemPatch
    {
        public static bool Prefix(Items __instance, Item __0, ref bool __result)
        {
            // 1) 装备槽单格页直接放行
            if (__instance.page < PlayerInventory.SLOTS)
            {
                return true;
            }

            // 参数 __0 即原方法第一个参数 (Item item)；
            // 用位置参数避免在 stripped 程序集中参数名匹配失败。
            Item newItem = __0;
            if (newItem == null)
            {
                return true;
            }

            // 拿新物品的尺寸（与 ItemJar 构造时同一路径：item.GetAsset() → asset.size_x/size_y）
            ItemAsset asset = newItem.GetAsset();
            if (asset == null)
            {
                // 资产未注册（极端情况），放行让原版处理
                return true;
            }

            byte newItemSx = asset.size_x;
            byte newItemSy = asset.size_y;

            // 2) 先看是否有现成空位 —— 若原生 tryFindSpace 已能解决，无需重排
            if (__instance.tryFindSpace(newItemSx, newItemSy, out _, out _, out _))
            {
                return true;
            }

            // 3) 构建 InventorySolver 输入列表
            var packList = new List<PackableItem>();
            // 备份现有 ItemJar 引用（removeItem 后 List 会变化，所以预先快照）
            var backupJars = new List<ItemJar>(__instance.getItemCount());
            byte existingCount = __instance.getItemCount();
            for (byte i = 0; i < existingCount; i++)
            {
                ItemJar jar = __instance.getItem(i);
                if (jar == null) continue;
                backupJars.Add(jar);
                packList.Add(new PackableItem
                {
                    Tag = jar,
                    size_x = jar.size_x,
                    size_y = jar.size_y,
                });
            }
            // 把新物品追加到列表末尾，Tag = null 标记"新物品"
            packList.Add(new PackableItem
            {
                Tag = null,
                size_x = newItemSx,
                size_y = newItemSy,
            });

            // 4) 调用算法（被动整理路径恒用降序，与原 FFD 行为一致）
            if (!InventorySolver.TryPack(__instance.width, __instance.height, packList, out var result, sortDescending: true))
            {
                // 装不下，放行让原版 tryAddItem 走"返回 false"流程
                return true;
            }

            // 5) 装箱成功 → 执行实际重组
            //    按 spec：先备份 Item 引用，再循环 removeItem(0)，最后按 result 顺序 addItem。
            //    注意 result 是算法排序后的列表（FFD 降序），与原背包排列不同。

            // 5.1 备份所有现有 ItemJar 的 Item 引用
            //     （removeItem 不会释放 ItemJar 对象，但我们仍需在备份后操作 Item 实例）
            // backupJars 已保存 ItemJar 引用，可直接通过 jar.item 取 Item。

            // 5.2 清空当前网格
            //     spec 要求循环 removeItem(0) 直到 items.Count == 0。
            //     getItemCount() 是实时变化的，故用 while 检查。
            while (__instance.getItemCount() > 0)
            {
                __instance.removeItem(0);
            }

            // 5.3 按 result 重新添加
            foreach (PackableItem p in result)
            {
                Item itemToAdd;
                if (p.Tag is ItemJar oldJar)
                {
                    itemToAdd = oldJar.item;
                }
                else
                {
                    // Tag == null 表示这是新物品
                    itemToAdd = newItem;
                }

                if (itemToAdd == null) continue;

                __instance.addItem(p.ResultX, p.ResultY, p.ResultRot, itemToAdd);
            }

            // 6) 拦截原方法，告知调用方"已成功放入"
            __result = true;
            return false;
        }
    }
}

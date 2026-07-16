using System.Collections.Generic;
using HarmonyLib;
using SDG.Unturned;

namespace LaunchInventoryTidy.Patches
{
    /// <summary>
    /// 拦截 Items.tryAddItem，在背包已散乱导致 tryFindSpace 失败时，
    /// 启用 2D 装箱求解器对整个网格进行重组，让原本"装不下"的物品能塞进去。
    ///
    /// 行为流程：
    ///  1) 装备槽单格页 (page &lt; PlayerInventory.SLOTS) -> 放行原版逻辑
    ///  2) tryFindSpace 已能找到空位 -> 放行原版逻辑
    ///  3) 调用 InventorySolver.TryPack 重排整个网格（含新物品）
    ///     - TryPack 返回 false（部分物品未放置） -> 放行原版逻辑（让其返回 false）
    ///     - TryPack 返回 true（全部合法物品已放置） -> 真实执行 removeItem + addItem
    ///       Placed=true 的物品按 ResultX/Y/Rot 重排
    ///       Placed=false 的物品（异常 size=0 等）尝试恢复原位，原位被占则 tryFindSpace
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

            // 拿新物品的尺寸（与 ItemJar 构造时同一路径：item.GetAsset() -> asset.size_x/size_y）
            ItemAsset asset = newItem.GetAsset();
            if (asset == null)
            {
                // 资产未注册（极端情况），放行让原版处理
                return true;
            }

            byte newItemSx = asset.size_x;
            byte newItemSy = asset.size_y;

            // 2) 先看是否有现成空位 -- 若原生 tryFindSpace 已能解决，无需重排
            if (__instance.tryFindSpace(newItemSx, newItemSy, out _, out _, out _))
            {
                return true;
            }

            // 3) 构建 InventorySolver 输入列表
            var packList = new List<PackableItem>();
            byte existingCount = __instance.getItemCount();
            for (byte i = 0; i < existingCount; i++)
            {
                ItemJar jar = __instance.getItem(i);
                if (jar == null) continue;
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

            // 4) 调用算法（被动整理路径恒用 MaxRects + 降序，C 优先级与用户要求一致）
            //    TryPack 返回 false 表示部分合法物品未放置 -> 放行原版（不破坏现有物品）
            if (!InventorySolver.TryPack(__instance.width, __instance.height, packList,
                                          out var result, sortDescending: true, mode: TidyMode.MaxRects))
            {
                // 装不下，放行让原版 tryAddItem 走"返回 false"流程
                return true;
            }

            // 5) 装箱成功 -> 执行实际重组
            //    Placed=true 的物品按 ResultX/Y/Rot 重排
            //    Placed=false 的物品（异常物品 size=0 等）尝试恢复原位，原位被占则 tryFindSpace

            // 5.1 清空当前网格
            while (__instance.getItemCount() > 0)
            {
                __instance.removeItem(0);
            }

            // 5.2 按 result 重新添加
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

                if (p.Placed)
                {
                    // 已放置：按算法结果重排
                    __instance.addItem(p.ResultX, p.ResultY, p.ResultRot, itemToAdd);
                }
                else
                {
                    // 未放置（异常物品）：尝试恢复原位（仅对 oldJar 有原位置）
                    bool restored = false;
                    if (p.Tag is ItemJar restoreJar)
                    {
                        try
                        {
                            __instance.addItem(restoreJar.x, restoreJar.y, restoreJar.rot, itemToAdd);
                            restored = true;
                        }
                        catch { }
                    }
                    if (!restored)
                    {
                        // 原位恢复失败，用 tryFindSpace 找空位
                        if (__instance.tryFindSpace(p.size_x, p.size_y, out byte fx, out byte fy, out byte fr))
                        {
                            __instance.addItem(fx, fy, fr, itemToAdd);
                        }
                        // tryFindSpace 也失败则丢弃（极端情况）
                    }
                }
            }

            // 6) 拦截原方法，告知调用方"已成功放入"
            __result = true;
            return false;
        }
    }
}

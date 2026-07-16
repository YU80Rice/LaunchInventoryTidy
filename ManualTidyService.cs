using System;
using System.Collections.Generic;
using SDG.Unturned;

namespace LaunchInventoryTidy
{
    /// <summary>
    /// 手动整理服务：对玩家多格页 (page 2..6: ITEMS/BACKPACK/VEST/SHIRT/PANTS，
    /// 以及 page=7 STORAGE 容器页) 执行 2D 装箱重排。
    /// 复用 InventorySolver.TryPack 算法，与被动整理 patch 共用 solver。
    /// 支持 sortDescending 控制排序方向：true=大件优先（默认）；false=小件优先。
    /// </summary>
    public static class ManualTidyService
    {
        /// <summary>
        /// 对玩家 page 2..6 五个多格页依次执行装箱重排。
        /// 单页失败仅 log，不阻塞其它页。
        /// </summary>
        public static void TidyAllPlayerPages(PlayerInventory inv, bool sortDescending = true,
                                              TidyMode mode = TidyMode.MaxRects)
        {
            if (inv == null) return;

            for (byte page = PlayerInventory.SLOTS; page <= PlayerInventory.PANTS; page++)
            {
                try
                {
                    TidyPage(inv.items[page], page, sortDescending, mode);
                }
                catch (Exception e)
                {
                    LaunchInventoryTidyPlugin.Log?.LogError($"[Tidy] page {page} crashed: {e}");
                }
            }
        }

        /// <summary>
        /// 便捷入口：直接使用 Player.LocalPlayer.inventory 整理全部 5 个多格页。
        /// </summary>
        public static void TidyAllPlayerPages(bool sortDescending, TidyMode mode = TidyMode.MaxRects)
        {
            PlayerInventory inv = Player.LocalPlayer?.inventory;
            if (inv == null) return;
            TidyAllPlayerPages(inv, sortDescending, mode);
        }

        /// <summary>
        /// 便捷入口：仅整理单个 page（page 2..6 服装页，或 page=7 STORAGE 容器页）。
        /// page 超出 [SLOTS, STORAGE] 范围时静默忽略。
        /// </summary>
        public static void TidyPage(byte page, bool sortDescending = true,
                                    TidyMode mode = TidyMode.MaxRects)
        {
            PlayerInventory inv = Player.LocalPlayer?.inventory;
            if (inv == null) return;
            if (page < PlayerInventory.SLOTS || page > PlayerInventory.STORAGE) return;
            try
            {
                TidyPage(inv.items[page], page, sortDescending, mode);
            }
            catch (Exception e)
            {
                LaunchInventoryTidyPlugin.Log?.LogError($"[Tidy] page {page} crashed: {e}");
            }
        }

        /// <summary>
        /// 直接对指定 Items 实例执行整理。供网络层（ManualTidyNetwork）在服务器端
        /// 操作任意 sender 的 inventory 时使用，不能走 Player.LocalPlayer 便捷入口。
        ///
        /// 容错策略：
        ///  - 装箱前记录每个物品的原位置（jar.x/y/rot）
        ///  - 调用 InventorySolver.TryPack（贪心 First-Fit，不回溯）
        ///  - Placed=true 的物品按 ResultX/Y/Rot 重排
        ///  - Placed=false 的物品（异常或装不下）用原位置恢复，原位被占则 tryFindSpace
        ///  - 即使部分物品未放置，也执行部分重排（玩家能看到整理效果）
        /// </summary>
        internal static void TidyPage(Items items, byte page, bool sortDescending,
                                      TidyMode mode = TidyMode.MaxRects)
        {
            if (items == null)
            {
                LaunchInventoryTidyPlugin.Log?.LogWarning(
                    $"[Tidy] page {page}: items is null, 跳过（服务器端未拿到 Items 实例）");
                return;
            }
            if (items.width == 0 || items.height == 0)
            {
                LaunchInventoryTidyPlugin.Log?.LogWarning(
                    $"[Tidy] page {page}: items.width={items.width} height={items.height}，跳过" +
                    $"（page=7 STORAGE 时表示玩家未在服务器端打开任何容器，工坊虚拟容器可能不走标准 openStorage 路径）");
                return;
            }

            byte count = items.getItemCount();
            if (count == 0)
            {
                LaunchInventoryTidyPlugin.Log?.LogInfo(
                    $"[Tidy] page {page}: 容器为空（width={items.width}x height={items.height}），无物品可整理");
                return;
            }

            // 1) 构建算法输入 + 记录每个物品的原位置（用于 Placed=false 时恢复）
            var packList = new List<PackableItem>(count);
            var originalPositions = new Dictionary<ItemJar, (byte x, byte y, byte rot)>(count);
            for (byte i = 0; i < count; i++)
            {
                ItemJar jar = items.getItem(i);
                if (jar == null) continue;
                packList.Add(new PackableItem
                {
                    Tag = jar,
                    size_x = jar.size_x,
                    size_y = jar.size_y,
                });
                originalPositions[jar] = (jar.x, jar.y, jar.rot);
            }

            // 2) 装箱（贪心 First-Fit 或 MaxRects，部分物品可能 Placed=false）
            //    即使 TryPack 返回 false（部分未放置），也继续执行部分重排
            InventorySolver.TryPack(items.width, items.height, packList,
                                     out List<PackableItem> result, sortDescending, mode);

            int placedCount = 0;
            int restoredCount = 0;
            int lostCount = 0;

            // 3) 清空 + 按 result 重添
            while (items.getItemCount() > 0)
            {
                items.removeItem(0);
            }

            foreach (PackableItem p in result)
            {
                if (!(p.Tag is ItemJar jar) || jar.item == null) continue;

                if (p.Placed)
                {
                    // 已放置：按算法结果重排
                    items.addItem(p.ResultX, p.ResultY, p.ResultRot, jar.item);
                    placedCount++;
                }
                else
                {
                    // 未放置：尝试恢复原位
                    var orig = originalPositions[jar];
                    bool restored = TryAddAt(items, jar.item, orig.x, orig.y, orig.rot);
                    if (!restored)
                    {
                        // 原位被占，用 tryFindSpace 找空位
                        if (items.tryFindSpace(jar.size_x, jar.size_y, out byte fx, out byte fy, out byte fr))
                        {
                            items.addItem(fx, fy, fr, jar.item);
                            restored = true;
                        }
                    }
                    if (restored) restoredCount++;
                    else
                    {
                        lostCount++;
                        LaunchInventoryTidyPlugin.Log?.LogWarning(
                            $"[Tidy] page {page}: cannot restore jar (size={jar.size_x}x{jar.size_y})");
                    }
                }
            }

            LaunchInventoryTidyPlugin.Log?.LogInfo(
                $"[Tidy] page {page}: total={count}, placed={placedCount}, restored={restoredCount}, lost={lostCount} (desc={sortDescending}, mode={mode})");
        }

        /// <summary>
        /// 尝试在指定位置添加物品，失败返回 false（不抛异常）。
        /// 用于 Placed=false 物品恢复原位。
        /// </summary>
        private static bool TryAddAt(Items items, Item item, byte x, byte y, byte rot)
        {
            try
            {
                items.addItem(x, y, rot, item);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}

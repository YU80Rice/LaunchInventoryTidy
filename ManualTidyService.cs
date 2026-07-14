using System;
using System.Collections.Generic;
using SDG.Unturned;

namespace LaunchInventoryTidy
{
    /// <summary>
    /// 手动整理服务：对玩家 5 个多格页 (page 2..6: ITEMS/BACKPACK/VEST/SHIRT/PANTS) 执行 2D 装箱重排。
    /// 复用 InventorySolver.TryPack 算法，与被动整理 patch 共用 solver。
    /// 支持 sortDescending 控制排序方向：true=大件优先（默认）；false=小件优先。
    /// </summary>
    public static class ManualTidyService
    {
        /// <summary>
        /// 对玩家 page 2..6 五个多格页依次执行装箱重排。
        /// 单页失败仅 log，不阻塞其它页。
        /// </summary>
        public static void TidyAllPlayerPages(PlayerInventory inv, bool sortDescending = true)
        {
            if (inv == null) return;

            for (byte page = PlayerInventory.SLOTS; page <= PlayerInventory.PANTS; page++)
            {
                try
                {
                    TidyPage(inv.items[page], page, sortDescending);
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
        public static void TidyAllPlayerPages(bool sortDescending)
        {
            PlayerInventory inv = Player.LocalPlayer?.inventory;
            if (inv == null) return;
            TidyAllPlayerPages(inv, sortDescending);
        }

        /// <summary>
        /// 便捷入口：仅整理单个 page（page 2..6）。
        /// page 超出 [SLOTS, PANTS] 范围时静默忽略。
        /// </summary>
        public static void TidyPage(byte page, bool sortDescending = true)
        {
            PlayerInventory inv = Player.LocalPlayer?.inventory;
            if (inv == null) return;
            if (page < PlayerInventory.SLOTS || page > PlayerInventory.PANTS) return;
            try
            {
                TidyPage(inv.items[page], page, sortDescending);
            }
            catch (Exception e)
            {
                LaunchInventoryTidyPlugin.Log?.LogError($"[Tidy] page {page} crashed: {e}");
            }
        }

        /// <summary>
        /// 直接对指定 Items 实例执行整理。供网络层（ManualTidyNetwork）在服务器端
        /// 操作任意 sender 的 inventory 时使用——不能走 Player.LocalPlayer 便捷入口。
        /// </summary>
        internal static void TidyPage(Items items, byte page, bool sortDescending)
        {
            if (items == null) return;
            if (items.width == 0 || items.height == 0) return;

            byte count = items.getItemCount();
            if (count == 0) return;

            // 1) 构建算法输入
            var packList = new List<PackableItem>(count);
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
            }

            // 2) 装箱（按调用方指定的方向）
            if (!InventorySolver.TryPack(items.width, items.height, packList,
                                          out List<PackableItem> result, sortDescending))
            {
                LaunchInventoryTidyPlugin.Log?.LogWarning(
                    $"[Tidy] page {page}: solver failed for {packList.Count} jars, skipped");
                return;
            }

            // 3) 清空 + 按 result 顺序重添
            while (items.getItemCount() > 0)
            {
                items.removeItem(0);
            }

            foreach (PackableItem p in result)
            {
                if (!(p.Tag is ItemJar jar) || jar.item == null) continue;
                items.addItem(p.ResultX, p.ResultY, p.ResultRot, jar.item);
            }

            LaunchInventoryTidyPlugin.Log?.LogInfo(
                $"[Tidy] page {page}: {count} jars packed (desc={sortDescending})");
        }
    }
}

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using SDG.Unturned;
using UnityEngine;

namespace LaunchInventoryTidy.Patches
{
    /// <summary>
    /// 拦截 PlayerDashboardInventoryUI 构造函数，为 5 个标题栏（headers[0..4]，对应
    /// Hands/Backpack/Vest/Shirt/Pants）分别注入两个按钮：
    ///   - 方向切换按钮 [↓]/[↑]：宽度 40px，紧贴右侧；切换该页排序方向。
    ///   - 整理按钮 [整理]：宽度 60px，紧贴方向按钮左侧；点击整理当前页，
    ///     Ctrl+点击整理全身。
    ///
    /// 由于编译时不认识 ISleekElement/ISleekButton（在 Glazier.dll 中），
    /// 全部用反射 + Reflection.Emit 动态生成委托。
    /// </summary>
    [HarmonyPatch(typeof(PlayerDashboardInventoryUI), MethodType.Constructor)]
    public static class PlayerDashboardInventoryUIPatch
    {
        private const string TAG = "[TidyUI]";

        // ── headers 私有静态字段（Assembly-CSharp 内）──
        private static readonly FieldInfo s_HeadersField =
            typeof(PlayerDashboardInventoryUI).GetField("headers", BindingFlags.Static | BindingFlags.NonPublic);

        // ── 反射缓存 ──
        private static Type     s_GlazierType;
        private static Type     s_ISleekElementType;
        private static Type     s_ISleekButtonType;
        private static MethodInfo s_GlazierGet;
        private static MethodInfo s_CreateButton;
        private static bool      s_CreateButtonResolved;
        private static MethodInfo s_AddChild;
        // 布局 (ISleekElement)
        private static PropertyInfo s_PosScaleX;
        private static PropertyInfo s_PosOffsetX;
        private static PropertyInfo s_SizeOffsetX;
        private static PropertyInfo s_SizeOffsetY;
        // 按钮 (ISleekButton)
        private static PropertyInfo s_Text;
        private static PropertyInfo s_TooltipText;
        private static EventInfo    s_OnClicked;

        private static readonly object[] s_EmptyArgs = new object[0];
        private static readonly object[] s_OneArg    = new object[1];
        private static bool s_Initialised;

        // ── 业务状态：每页排序方向字典 + 方向按钮实例引用 ──
        // key = page (2..6)，value = true 表示降序（↓），false 表示升序（↑）。默认降序。
        private static readonly Dictionary<byte, bool> s_PageSortDescending =
            new Dictionary<byte, bool>();
        // key = page，value = 该页方向按钮的反射实例（用于切换文本）。
        private static readonly Dictionary<byte, object> s_DirectionButtons =
            new Dictionary<byte, object>();

        // ── 按钮布局常量 ──
        // 右侧预留 70px 安全空间，避让原版 "100%" 耐久度文字与绿色品质角标。
        //
        // 排版几何（PositionScale_X = 1，相对父容器右边缘）：
        //   - [整理] 按钮 B：宽 60，PositionOffset_X = -130  -> 右边缘 -70，恰好填满安全区左边界
        //   - [↓]/[↑] 按钮 A：宽 40，PositionOffset_X = -175 -> 右边缘 -135，与 B 左边缘(-130) 间隔 5px
        //
        // 视觉顺序（从左到右）：[↓/↑]  5px  [整理]  70px  [原版 100% 耐久度+绿色角标]
        private const float DIR_POS_OFFSET_X  = -175f;
        private const float DIR_SIZE_X        = 40f;
        private const float BTN_SIZE_Y        = 60f;
        private const float TIDY_POS_OFFSET_X = -130f;
        private const float TIDY_SIZE_X        = 60f;

        // ── 容器页（page=STORAGE=7，headers[5]）专用布局 ──
        // 容器页右侧需避让原版 rot_xButton/rot_yButton/rot_zButton（展示柜场景下可见，
        // 各 60×60 共 180px，PlayerDashboardInventoryUI.cs:1916 处 header SizeOffset_X=-180）。
        // 为兼容展示柜，所有容器（含普通储物箱/后备箱）统一让出右侧 180px 安全区。
        //
        // 排版几何：
        //   - [整理] 按钮 B：宽 60，PositionOffset_X = -240 -> 右边缘 -180，恰好填满安全区左边界
        //   - [↓]/[↑] 按钮 A：宽 40，PositionOffset_X = -285 -> 右边缘 -245，与 B 左边缘(-240) 间隔 5px
        //
        // 视觉顺序：[↓/↑]  5px  [整理]  180px 安全区  [rot_x/y/z 或空]
        private const float STORAGE_DIR_POS_OFFSET_X  = -285f;
        private const float STORAGE_TIDY_POS_OFFSET_X = -240f;

        // headers 循环上限：i=0..5 -> page 2..7（含 STORAGE 容器页）
        private const int HEADER_INJECT_COUNT = 6;

        private const string TOOLTIP_TIDY =
            "左键点击：整理当前空间的物品。\nCtrl + 左键点击：一键自动整理全身背包。";
        private const string TOOLTIP_DIR = "切换排序方向（↓ 从大到小 / ↑ 从小到大）";

        private static void LogError(string msg) => Debug.LogError($"{TAG} {msg}");
        private static void LogInfo(string msg)  => Debug.Log($"{TAG} {msg}");

        // ─────────────────────────────────────────────────────────────────
        // 反射预热
        // ─────────────────────────────────────────────────────────────────
        private static void WarmupReflection()
        {
            if (s_Initialised) return;
            s_Initialised = true;

            if (s_HeadersField == null) { LogError("无法定位 PlayerDashboardInventoryUI.headers 字段！"); return; }
            LogInfo("headers 字段 OK");

            s_GlazierType = AccessTools.TypeByName("SDG.Unturned.Glazier");
            if (s_GlazierType == null) { LogError("无法定位 SDG.Unturned.Glazier 类型！"); return; }
            LogInfo("Glazier 类型 OK");

            s_ISleekElementType = AccessTools.TypeByName("SDG.Unturned.ISleekElement");
            if (s_ISleekElementType == null) { LogError("无法定位 ISleekElement 类型！"); return; }
            LogInfo("ISleekElement 类型 OK");

            s_ISleekButtonType = AccessTools.TypeByName("SDG.Unturned.ISleekButton");
            if (s_ISleekButtonType == null) { LogError("无法定位 ISleekButton 类型！"); return; }
            LogInfo("ISleekButton 类型 OK");

            s_GlazierGet = s_GlazierType.GetMethod("Get", BindingFlags.Static | BindingFlags.Public);
            if (s_GlazierGet == null) { LogError("无法定位 Glazier.Get() 方法！"); return; }

            s_AddChild = GetInterfaceMethod(s_ISleekElementType, "AddChild");
            if (s_AddChild == null) { LogError("无法定位 ISleekElement.AddChild() 方法！"); return; }

            LogInfo("Glazier.Get / AddChild OK (CreateButton 推迟)");

            s_PosScaleX   = GetInterfaceProperty(s_ISleekElementType, "PositionScale_X");
            s_PosOffsetX  = GetInterfaceProperty(s_ISleekElementType, "PositionOffset_X");
            s_SizeOffsetX = GetInterfaceProperty(s_ISleekElementType, "SizeOffset_X");
            s_SizeOffsetY = GetInterfaceProperty(s_ISleekElementType, "SizeOffset_Y");
            if (s_PosScaleX == null || s_PosOffsetX == null || s_SizeOffsetX == null || s_SizeOffsetY == null)
            {
                LogError("ISleekElement 布局属性定位失败！");
                DumpAvailableMembers(s_ISleekElementType, "ISleekElement");
                return;
            }
            LogInfo("ISleekElement 布局属性 OK");

            s_Text        = GetInterfaceProperty(s_ISleekButtonType, "Text");
            s_TooltipText = GetInterfaceProperty(s_ISleekButtonType, "TooltipText");
            if (s_Text == null || s_TooltipText == null)
            {
                LogError("ISleekButton.Text / TooltipText 属性定位失败！");
                DumpAvailableMembers(s_ISleekButtonType, "ISleekButton");
                return;
            }
            LogInfo("ISleekButton Text / TooltipText OK");

            s_OnClicked = ResolveClickedEvent(s_ISleekButtonType);
            if (s_OnClicked == null)
            {
                LogError("ISleekButton.OnClicked 事件定位失败！");
                DumpAvailableMembers(s_ISleekButtonType, "ISleekButton");
                return;
            }
            LogInfo("OnClicked 事件 OK");

            LogInfo("全部 Reflection 缓存预热成功");
        }

        // ─────────────────────────────────────────────────────────────────
        // 递归接口成员查找（穿透接口继承链）
        // ─────────────────────────────────────────────────────────────────
        private static PropertyInfo GetInterfaceProperty(Type type, string name)
        {
            if (type == null) return null;
            PropertyInfo prop = type.GetProperty(name,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (prop != null) return prop;
            foreach (Type parent in type.GetInterfaces())
            {
                prop = GetInterfaceProperty(parent, name);
                if (prop != null) return prop;
            }
            return null;
        }

        private static EventInfo GetInterfaceEvent(Type type, string name)
        {
            if (type == null) return null;
            EventInfo ev = type.GetEvent(name,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (ev != null) return ev;
            foreach (Type parent in type.GetInterfaces())
            {
                ev = GetInterfaceEvent(parent, name);
                if (ev != null) return ev;
            }
            return null;
        }

        private static MethodInfo GetInterfaceMethod(Type type, string name)
        {
            if (type == null) return null;
            MethodInfo m = type.GetMethod(name,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (m != null) return m;
            foreach (Type parent in type.GetInterfaces())
            {
                m = GetInterfaceMethod(parent, name);
                if (m != null) return m;
            }
            return null;
        }

        private static void DumpAvailableMembers(Type type, string label)
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"{TAG} -- {label} ({type.FullName}) 可用成员 --");
                sb.AppendLine("  [Properties]");
                foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                    sb.AppendLine($"    {p.PropertyType.Name} {p.Name}");
                foreach (var parent in type.GetInterfaces())
                    foreach (var p in parent.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                        sb.AppendLine($"    [{parent.Name}] {p.PropertyType.Name} {p.Name}");
                sb.AppendLine("  [Events]");
                foreach (var e in type.GetEvents(BindingFlags.Public | BindingFlags.Instance))
                    sb.AppendLine($"    {e.Name}");
                foreach (var parent in type.GetInterfaces())
                    foreach (var e in parent.GetEvents(BindingFlags.Public | BindingFlags.Instance))
                        sb.AppendLine($"    [{parent.Name}] {e.Name}");
                sb.AppendLine("  [Methods]");
                foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                    sb.AppendLine($"    {m.Name}");
                foreach (var parent in type.GetInterfaces())
                    foreach (var m in parent.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                        sb.AppendLine($"    [{parent.Name}] {m.Name}");
                LogInfo(sb.ToString());
            }
            catch { }
        }

        private static EventInfo ResolveClickedEvent(Type type)
        {
            var ev = GetInterfaceEvent(type, "OnClicked");
            if (ev != null) return ev;
            foreach (var e in type.GetEvents(BindingFlags.Public | BindingFlags.Instance))
            {
                if (e.Name.IndexOf("clicked", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    LogInfo("OnClicked 事件模糊匹配 -> " + e.Name);
                    return e;
                }
            }
            foreach (var parent in type.GetInterfaces())
                foreach (var e in parent.GetEvents(BindingFlags.Public | BindingFlags.Instance))
                    if (e.Name.IndexOf("clicked", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        LogInfo("OnClicked 事件模糊匹配(父接口) -> " + parent.Name + "." + e.Name);
                        return e;
                    }
            return null;
        }

        // ─────────────────────────────────────────────────────────────────
        // Postfix：构造完成后注入 5 组双按钮
        // ─────────────────────────────────────────────────────────────────
        public static void Postfix()
        {
            WarmupReflection();
            if (s_GlazierType == null || s_ISleekElementType == null || s_ISleekButtonType == null) return;
            if (s_HeadersField == null || s_OnClicked == null) return;

            Array headers = s_HeadersField.GetValue(null) as Array;
            if (headers == null || headers.Length < HEADER_INJECT_COUNT)
            {
                LogError($"headers 数组为 null 或长度 < {HEADER_INJECT_COUNT}！");
                return;
            }
            LogInfo("headers 数组 OK (Length=" + headers.Length + ")");

            object glazier;
            try { glazier = s_GlazierGet.Invoke(null, s_EmptyArgs); }
            catch (Exception e) { LogError("Glazier.Get() 调用失败: " + e); return; }
            if (glazier == null) { LogError("Glazier.Get() 返回 null！"); return; }
            LogInfo("Glazier.Get() 单例 OK");

            if (!s_CreateButtonResolved)
            {
                Type instanceType = glazier.GetType();
                LogInfo("Glazier 实例运行时类型: " + instanceType.FullName);
                s_CreateButton = AccessTools.Method(instanceType, "CreateButton", new Type[0]);
                if (s_CreateButton == null) { LogError("无法在 " + instanceType.FullName + " 上定位 CreateButton()！"); return; }
                s_CreateButtonResolved = true;
                LogInfo("CreateButton OK (来自 " + instanceType.Name + ")");
            }

            // 循环注入 6 个标题栏：headers[0..5] -> page 2..7（含 STORAGE 容器页）
            // i=0..4 服装页用 DIR_POS_OFFSET_X / TIDY_POS_OFFSET_X（让出右侧 70px 避让耐久度角标）
            // i=5    容器页用 STORAGE_*_POS_OFFSET_X（让出右侧 180px 避让 rot_x/y/z 按钮）
            int injected = 0;
            for (int i = 0; i < HEADER_INJECT_COUNT; i++)
            {
                byte currentPage = (byte)(i + 2);
                EnsurePageDefault(currentPage);

                // 容器页（i=5, page=7=STORAGE）使用专用偏移避让 rot 按钮区
                bool isStoragePage = (i == 5);
                float dirPosOffsetX  = isStoragePage ? STORAGE_DIR_POS_OFFSET_X  : DIR_POS_OFFSET_X;
                float tidyPosOffsetX = isStoragePage ? STORAGE_TIDY_POS_OFFSET_X : TIDY_POS_OFFSET_X;

                object headerElement = headers.GetValue(i);
                if (headerElement == null)
                {
                    LogError($"headers[{i}] 为 null，跳过");
                    continue;
                }

                // ── 创建方向按钮 A：[↓]/[↑] ──
                object dirButton;
                try { dirButton = s_CreateButton.Invoke(glazier, s_EmptyArgs); }
                catch (Exception e) { LogError($"headers[{i}] dirButton CreateButton 失败: {e}"); continue; }
                if (dirButton == null) { LogError($"headers[{i}] dirButton 返回 null"); continue; }

                try
                {
                    s_PosScaleX  .SetValue(dirButton, 1f,                null);
                    s_PosOffsetX .SetValue(dirButton, dirPosOffsetX,    null);
                    s_SizeOffsetX.SetValue(dirButton, DIR_SIZE_X,        null);
                    s_SizeOffsetY.SetValue(dirButton, BTN_SIZE_Y,        null);
                    s_Text       .SetValue(dirButton, "↓",                null);
                    s_TooltipText.SetValue(dirButton, TOOLTIP_DIR,        null);
                }
                catch (Exception e) { LogError($"headers[{i}] dirButton 属性设置失败: {e}"); }

                // 绑定方向按钮点击事件 -> HandleDirectionClick(currentPage)
                try
                {
                    Delegate dirHandler = CreatePageDelegate(s_OnClicked.EventHandlerType, currentPage, isDirection: true);
                    s_OnClicked.AddEventHandler(dirButton, dirHandler);
                }
                catch (Exception e) { LogError($"headers[{i}] dirButton 事件绑定失败: {e}"); }

                // ── 创建整理按钮 B：[整理] ──
                object tidyButton;
                try { tidyButton = s_CreateButton.Invoke(glazier, s_EmptyArgs); }
                catch (Exception e) { LogError($"headers[{i}] tidyButton CreateButton 失败: {e}"); continue; }
                if (tidyButton == null) { LogError($"headers[{i}] tidyButton 返回 null"); continue; }

                try
                {
                    s_PosScaleX  .SetValue(tidyButton, 1f,                 null);
                    s_PosOffsetX .SetValue(tidyButton, tidyPosOffsetX,     null);
                    s_SizeOffsetX.SetValue(tidyButton, TIDY_SIZE_X,         null);
                    s_SizeOffsetY.SetValue(tidyButton, BTN_SIZE_Y,          null);
                    s_Text       .SetValue(tidyButton, "整理",              null);
                    s_TooltipText.SetValue(tidyButton, TOOLTIP_TIDY,        null);
                }
                catch (Exception e) { LogError($"headers[{i}] tidyButton 属性设置失败: {e}"); }

                // 绑定整理按钮点击事件 -> HandleTidyClick(currentPage)
                try
                {
                    Delegate tidyHandler = CreatePageDelegate(s_OnClicked.EventHandlerType, currentPage, isDirection: false);
                    s_OnClicked.AddEventHandler(tidyButton, tidyHandler);
                }
                catch (Exception e) { LogError($"headers[{i}] tidyButton 事件绑定失败: {e}"); }

                // ── AddChild 到 header ──
                try
                {
                    s_OneArg[0] = dirButton;
                    s_AddChild.Invoke(headerElement, s_OneArg);
                    s_OneArg[0] = tidyButton;
                    s_AddChild.Invoke(headerElement, s_OneArg);
                }
                catch (Exception e) { LogError($"headers[{i}] AddChild 失败: {e}"); }

                s_DirectionButtons[currentPage] = dirButton;
                injected++;
                LogInfo($"headers[{i}] -> page {currentPage} 双按钮注入 OK");
            }

            LogInfo($"==== 注入完成：共 {injected}/{HEADER_INJECT_COUNT} 组双按钮 ====");
        }

        // ─────────────────────────────────────────────────────────────────
        // 业务状态辅助
        // ─────────────────────────────────────────────────────────────────
        private static void EnsurePageDefault(byte page)
        {
            if (!s_PageSortDescending.ContainsKey(page))
                s_PageSortDescending[page] = true; // 默认降序
        }

        // ─────────────────────────────────────────────────────────────────
        // 委托生成（Emit）：把 page 常量嵌入到 OnClicked 委托的调用链中。
        // ClickedButton 签名为 void(ISleekElement)，所以 DynamicMethod 接收一个参数。
        // ─────────────────────────────────────────────────────────────────
        private static Delegate CreatePageDelegate(Type delegateType, byte page, bool isDirection)
        {
            MethodInfo invokeMethod = delegateType.GetMethod("Invoke");
            ParameterInfo[] parameters = invokeMethod.GetParameters();
            Type[] paramTypes = parameters.Length > 0
                ? new[] { parameters[0].ParameterType }
                : Type.EmptyTypes;

            var dm = new DynamicMethod(
                (isDirection ? "DirClick_" : "TidyClick_") + page + "_" + Guid.NewGuid().ToString("N").Substring(0, 6),
                null,
                paramTypes,
                typeof(PlayerDashboardInventoryUIPatch));

            var il = dm.GetILGenerator();
            // 把 page 常量压栈
            il.Emit(OpCodes.Ldc_I4, (int)page);
            // 调用对应静态方法
            MethodInfo target = isDirection
                ? typeof(PlayerDashboardInventoryUIPatch).GetMethod("HandleDirectionClick",
                    BindingFlags.Static | BindingFlags.NonPublic)
                : typeof(PlayerDashboardInventoryUIPatch).GetMethod("HandleTidyClick",
                    BindingFlags.Static | BindingFlags.NonPublic);
            il.Emit(OpCodes.Call, target);
            il.Emit(OpCodes.Ret);

            return dm.CreateDelegate(delegateType);
        }

        // ─────────────────────────────────────────────────────────────────
        // 事件回调：方向按钮点击 -> 切换该页排序方向 + 更新按钮文本
        // ─────────────────────────────────────────────────────────────────
        private static void HandleDirectionClick(byte page)
        {
            EnsurePageDefault(page);
            bool newDescending = !s_PageSortDescending[page];
            s_PageSortDescending[page] = newDescending;

            string arrow = newDescending ? "↓" : "↑";
            string label = newDescending ? "降序（大件优先）" : "升序（小件优先）";

            if (s_DirectionButtons.TryGetValue(page, out object btn) && btn != null)
            {
                try { s_Text.SetValue(btn, arrow, null); }
                catch (Exception e) { LogError($"page {page} 方向按钮文本切换失败: {e}"); }
            }

            LogInfo($"page {page} 排序方向切换为 {label}");
        }

        // ─────────────────────────────────────────────────────────────────
        // 事件回调：整理按钮点击
        //   - Ctrl 按下 -> 整理全身（用当前页的方向作为统一方向，尊重玩家最近的选择）
        //   - 否则     -> 仅整理当前页
        //
        // 双端自适应：房主直接执行；客机通过 P2P 通道发请求给服务器
        // ─────────────────────────────────────────────────────────────────
        private static void HandleTidyClick(byte page)
        {
            Player player = Player.LocalPlayer;
            if (player?.inventory == null)
            {
                LogError("Player.LocalPlayer.inventory 为 null，忽略点击");
                return;
            }

            bool ctrl = InputEx.GetKey(KeyCode.LeftControl) || InputEx.GetKey(KeyCode.RightControl);
            EnsurePageDefault(page);
            bool desc = s_PageSortDescending[page];

            try
            {
                if (Provider.isServer)
                {
                    // 房主：直接执行
                    if (ctrl)
                    {
                        LogInfo($"Ctrl+点击 -> 一键整理全身 (方向={desc}) [房主本地]");
                        ManualTidyService.TidyAllPlayerPages(player.inventory, desc);
                    }
                    else
                    {
                        LogInfo($"点击 -> 整理 page {page} (方向={desc}) [房主本地]");
                        ManualTidyService.TidyPage(page, desc);
                    }
                }
                else
                {
                    // 客机：通过网络请求服务器
                    if (ctrl)
                    {
                        LogInfo($"Ctrl+点击 -> 一键整理全身 (方向={desc}) [客机 P2P 请求]");
                        ManualTidyNetwork.SendTidyAllRequest(desc);
                    }
                    else
                    {
                        LogInfo($"点击 -> 整理 page {page} (方向={desc}) [客机 P2P 请求]");
                        ManualTidyNetwork.SendTidyPageRequest(page, desc);
                    }
                }
            }
            catch (Exception e)
            {
                LogError($"HandleTidyClick crashed: {e}");
            }
        }
    }
}

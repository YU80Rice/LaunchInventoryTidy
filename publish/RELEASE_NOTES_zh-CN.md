# LaunchInventoryTidy v3.0.1

## 本次更新

- 建立单机与 P2P 的持久熔断作用域隔离：作用域由运行模式、地图标识、地图哈希与存档槽位组成，避免同一 SteamID 在不同模式间污染熔断记录。
- 作用域切换改为 fail-closed：先进行无副作用参数校验，再清空旧运行时状态、初始化新作用域并加载；任一失败都会进入降级保护，禁止继续整理。
- SteamP2PFriends 在房主身份、Stage6A 会话和地图均稳定后，可选地初始化 LIT P2P 作用域。LIT 未安装时不影响 P2P 房主启动；LIT 已安装但作用域失败时，房主启动会安全中止。
- 升级 LIT 版本身份至 3.0.1.0，并统一发布包、README、依赖关系与哈希清单。

## 安装

解压 `LaunchInventoryTidy.zip` 到 Unturned 游戏根目录。压缩包只包含：

```text
BepInEx/plugins/LaunchInventoryTidy.dll
```

必须另行安装 `LaunchMultiplayerNet.dll` 4.0.0 或更高版本到同一插件目录。

## 测试与范围

- 单机自动化测试：已通过。
- U3DS 双端自动化测试：已通过。
- Steam P2P / SteamP2PFriends：仍处于 Alpha；P2P 双机动态 T1-T3 测试尚未完成，不作为正式支持或生产可用声明。

## 校验

- DLL SHA-256：`2CF2FD3C486BF810067E328C330F11A6A2F45973900F78962FFF73498123AE16`
- ZIP SHA-256：`20750CA3E940FF6210F8110B943D15367123FD5EF3EEE12C620BA451D6B12024`

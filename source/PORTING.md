# PORTING — 47242 分支

本分支目标游戏版本：**EFT 1.2.0.47242 / SPT 5.0**

## 当前状态

| 项 | 状态 |
|---|---|
| 源码基线 | ✅ 已导入 47242 版源码树（Fika 3.0.0/3.0.2 派生（更旧），含 `Client` / `Headless` / `Server`） |
| 编译 | ⚠️ **未在本仓验证**（缺 47242 的 `BepInEx/core` 与 `SPT_Runtime` 程序集；`BepInEx/interop` 副本在旧源码树的 `tests/Fika.PatchChecks/bin-ajplant/` 下） |
| 发布包 | ❌ 尚未生成（需先完成编译验证） |
| 实机 | ❌ 未测试 |

**不要**把本分支当作可直接部署的版本；可部署的版本见 `47965` 分支与对应 Release。

## 与 47965 分支的关系（差异比 47888 更大）

两个分支同源（同一上游 3.0.x）。`47965` 分支在本仓已经过多轮修复（W41–W51、PR #1、N5）与 i18n，且**构建已验证 0 error**。

把 `47965` 的修复移植到本分支时，必须逐项判断：

| 类型 | 处理 |
|---|---|
| 纯托管逻辑修复（如 `MarkOperationItemsKnown` 递归、`transitPlayers` 双形状、`Forget()` 吞异常、看门狗、IK 批次调用） | ✅ 直接移植 |
| 依赖 47965 才有的 interop 成员/签名 | ⚠️ 先核 47242 的 `Assembly-CSharp` 是否有该成员，否则跳过并在本文件登记 |
| i18n 注释翻译 | 可选，不阻塞功能 |
| `Directory.Build.props` 的 `GameDir` 默认值 | 保持各分支本地化，不要跨分支覆盖 |

## 移植清单（按 `47965` 的修复编号）

- [ ] W41 物品链：`MarkOperationItemsKnown` 递归 `InternalOperation` + `MarkOperationOwnItemsKnown`
- [ ] W41 拾取看门狗：`NativeInteractionRelease.WatchLostInteractionSchedule`（8 秒）
- [ ] W42 转移：`transitPlayers` 双形状（含 Headless 4 处）
- [ ] W42/W43 撤离：`ExfilCountdown` 不覆盖本机激活、`FikaExfilManager` 保留本机倒计时
- [ ] W46 `RallyZoneEventCountPatch` 改 patch 声明处（`BaseEvent.Invoke`）
- [ ] W47 无头低开销：640x360 / 30Hz
- [ ] W48 N1：`InteractWithTransit` 按 `parameters.active` 放行
- [ ] W48 N4 + PR #1：过场结束恢复 `DamageCoeff` + `DamageMultiplier` + `UnpauseAllEffectsOnPlayer()` + 守卫 + 主机看门狗
- [ ] W48 N2 + W49：`CutsceneStallWatchdog` + 主机座位表收窄
- [ ] W51：`HostOperationWatchdog` + `PickupContinuationRepair` + 描述符失败不取消操作
- [ ] N5：`ObservedPlayer.LateUpdate` 加入 `BeginLocalSceneIkLateUpdate` / `EndLocalSceneIkLateUpdate`
- [ ] W39 复活补丁移除 `[IgnoreAutoPatch]`

## 移植后需要的东西（缺了无法出包）

1. **47242 的 SPT 5.0 安装目录**（至少含 `BepInEx/core`、`BepInEx/interop/Assembly-CSharp.dll`、`SPT_Runtime/*.dll`、`BepInEx/plugins/spt*`）
2. 或：确认可以用 47965 的 `BepInEx/core` + `SPT_Runtime` 搭配 47242 的 `interop` 编译（有 API 不匹配风险，需实测）
3. 实机验收：47242 客户端 + 服务端各跑一局

## 构建（拿到环境后）

```powershell
dotnet build source\Client\Fika.Core\Fika.Core.csproj            -c Release -p:GameDir=<47242 安装目录> --nologo
dotnet build source\Headless\Fika.Headless\Fika.Headless.csproj  -c Release -p:GameDir=<47242 安装目录> --nologo
dotnet build source\Server\FikaServer\FikaServer.csproj          -c Release -p:GameDir=<47242 安装目录> --nologo
```

## 47242 额外注意

- 本树来自 `mods/fika-47242/3.0.0源码`（**Fika 3.0.0 时代**），比 47888 分支更旧：`Client` 721 个 .cs（47888 是 761），`Headless` 152，`Server` 268。
- 旧 `tests/Fika.PatchChecks/bin/Release/net6.0/` 下有 47242 的 `Assembly-CSharp.dll`（54.8 MB），可作为 interop 来源线索。
- 该时代用 **net6.0** 测试工程，主项目若仍是 net10.0 需确认 SDK 与目标框架一致。
- 移植顺序建议：先 N5（IK 批次，改动最小）→ W51（拾取回调）→ W48/W49（过场）→ W42（转移形状）→ W41（物品链）→ W46/W47（补丁与无头）。
## 2026-11-13 编译探测结果（实测）

用「47965 的 `BepInEx/core` + `SPT_Runtime` + 本分支的 47242 `Assembly-CSharp`(54.8 MB)」组装 GameDir 试编 `Fika.Core`：

**失败：CS1705** —— `BepInEx.Unity.IL2CPP` / `Il2CppInterop.Runtime` / `SPTushonka.Reflection` / `MonoMod.Utils` 都是以 **System.Runtime 10.0.0.0** 编译的，而本分支 `Fika.Core.csproj` 是 **`net6.0`**，引用了 System.Runtime 6.0.0.0。

结论：**47242 时代需要 net6.0 的 BepInEx 6 程序集**，不能直接套用 47965 的 net10 版。

两条路线：

1. **保持 `net6.0`（推荐，贴合 47242 真机）**：需要 47242 那个时代的 `BepInEx/core`（net6.0 的 `BepInEx.Unity.IL2CPP`、`Il2CppInterop.Runtime`）与对应 `SPTushonka`/`SPT` 客户端程序集；拿到后 `-p:GameDir=<47242 安装>` 即可验证。
2. **提到 `net10.0`**：改 csproj 后能用 47965 的 BepInEx 编过，但产物要求 net10 运行时，与 47242 的实际安装可能不匹配 —— 除非确认 47242 的 SPT 也跑 net10 版 BepInEx。

对比：**47888 分支已实测能编过**（`net10.0` + 47888 的 `Assembly-CSharp` 62.7 MB + 47965 的 BepInEx/SPT，`Fika.Core` **0 error**），因为 47888 时代与 47965 同属 net10 世代。
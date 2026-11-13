# PORTING — 47888 分支

本分支目标游戏版本：**EFT 1.2.0.47888 / SPT 5.0**

## 当前状态

| 项 | 状态 |
|---|---|
| 源码 | ✅ **已移植**：直接采用 `47965` 分支最新源码（含 W41–W51、PR #1、N5、i18n） |
| 编译 | ✅ **通过**：`Client` / `Headless` / `Server` 三项目 **0 error** |
| 发布包 | ✅ 已生成（`v2026.11.13-47888`） |
| 实机 | ❌ 未验证（本机 47888 游戏客户端已删除） |

## 移植方式与依据

两分支同源（同一上游 3.0.x）。把 `47965` 的整个 `source/` 覆盖到本分支后，用 **47888 自己的 interop** 编译：

```
interop: mods/fika-47888/source/tests/Fika.PatchChecks/bin-ajplant/Debug/net10.0/Assembly-CSharp.dll  (62.7 MB)
GameDir: 47965 的 BepInEx/core + SPT_Runtime，interop 目录替换为上面这份
结果:   Fika.Core 0 error / Fika.Headless 0 error / FikaServer 0 error
```

**编译通过即说明：`47965` 代码用到的所有 interop 成员（`MovementContext.PickupAction`、`BeginLocalSceneIkLateUpdate`、`CutsceneProcess.playersInCurrentView` 等）在 47888 的 `Assembly-CSharp` 里全部存在且签名一致。** 因此不需要逐个 API 判断兼容性。

## 已知限制

1. **未实机验证**：本机 47888 客户端已删除，只有 interop 副本。编译通过只证明 API 兼容，不证明运行时行为。
2. **BepInEx 版本要求**：本次构建用的是 47965 的 `BepInEx/core`（net10 版）。若 47888 实际安装的 BepInEx 是 net6 版，产物会加载失败（参考 47242 分支的 `CS1705` 记录）。**部署前请确认 47888 的 BepInEx 为 net10（be 版）。**
3. **版本常量**：`FikaVersion` 仍是 `3.0.4`（与 47965 相同），未按目标版本改写；这会影响日志里的版本行，不影响功能。

## 与 47965 的差异

**只有 `source/PORTING.md` 本身**（本文件）。代码完全一致。

## 后续若要做真机适配

1. 拿到 47888 客户端安装 → 部署 `v2026.11.13-47888` 的两个包 → 跑 47965 那六条验收
2. 若出现 47965 没有的 47888 专属行为差异，再按 `47965` 的提交逐条对照定位（`git log 47965 --oneline`）

## 构建

```powershell
dotnet build source\Client\Fika.Core\Fika.Core.csproj            -c Release -p:GameDir=<47888 GameDir> --nologo
dotnet build source\Headless\Fika.Headless\Fika.Headless.csproj  -c Release -p:GameDir=<47888 GameDir> --nologo
dotnet build source\Server\FikaServer\FikaServer.csproj          -c Release -p:GameDir=<47888 GameDir> --nologo
```

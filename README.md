# Fika-Refactor5.0BE

Fika 联机 mod 的多版本重构线（BepInEx 6 / SPT 5.0）。同一份代码库按**游戏构建版本**分成独立分支，每个分支只服务它自己的 `EFT` 版本。

> **License**: [CC BY-NC-SA 4.0](LICENSE) —— 非商业用途；修改与再分发须沿用同一协议并署名原作者。
> `SPDX-License-Identifier: CC-BY-NC-SA-4.0`（`source/*/Licenses/` 下的 `LICENSE-*.md` 是随源码分发的第三方许可，各自适用）
> 本仓库是 [Project Fika](https://github.com/project-fika/Fika-Plugin) 的**修改版（派生作品）**，改动见 [MODIFICATIONS.md](MODIFICATIONS.md)，署名见 [NOTICE](NOTICE)。
> 不含任何 Battlestate Games 的代码、资产或游戏文件。

## 版本分支

| 分支 | 目标版本 | 状态 | 可部署包 |
|---|---|---|---|
| `47965` | EFT 1.2.0.**47965** / SPT 5.0 | ✅ 构建验证通过，功能最完整 | ✅ `v2026.11.13-47965` |
| `47888` | EFT 1.2.0.**47888** / SPT 5.0 | ✅ 已移植 `47965` 源码，**编译验证通过**（47888 自己的 interop）；未实机验证 | ✅ `v2026.11.13-47888` |
| `47242` | EFT 1.2.0.**47242** / SPT 5.0 | ⚠️ 源码基线已导入，**未完成移植**；编译需 net6.0 时代的 BepInEx | ⚠️ `v2026.11.13-47242`（源码归档，实验性） |
| `main` | 跟随 `47965` | 主线 | — |

分支内的移植计划与逐项清单写在各自分支的 `source/PORTING.md`。

## Release 与分支的对应关系

GitHub 的 Release 绑定在 **tag** 上，而 tag 指向任意 commit，所以**每个分支都可以有自己的包**，互不覆盖：

| tag | 分支 | 内容 |
|---|---|---|
| `v2026.11.13-47965` | `47965` | `client-server.zip`（`Fika.Core.dll` + `fika-server` 全套）+ `headless.zip` |
| `v2026.11.13-47888` | `47888` | 同上两个包，用 **47888 interop** 编译；**未实机验证**（编译 0 error） |
| `v2026.11.13-47242` | `47242` | 源码归档（该分支快照），**实验性、未经编译验证** |
| `v2026.10.29` / `v2026.10.27-W47b` | 历史 | 旧版包，仅存档 |

## 安装（可部署包）

包内已是**散落目录**，解压到**游戏根目录**（与 `BepInEx/`、`SPT_Runtime/` 同级）即可，不需要安装器：

```
BepInEx/plugins/fika/Fika.Core.dll          ← client-server 包
SPT_Runtime/user/mods/fika-server/          ← client-server 包（含 assets/configs 与语言库）
BepInEx/plugins/fika/Fika.Headless.dll      ← headless 包（专用宿主）
```

包里**不含**这些运行时/按 profile 生成的文件，避免覆盖你已有的环境：
`assets/scripts/<profile>/HeadlessConfig.json`、`StartHeadless.cmd`、`database/friendRequests.json`、`playerRelations.json`、`sendItemTransfers/*`。

## 从源码构建

需要 .NET 10 SDK 与一份已安装对应版本 SPT 的游戏目录（写作 `<GameDir>`）。三个项目都通过 `GameDir` 解析 BepInEx 与 SPT 程序集，仓库不分发这些二进制。

```powershell
dotnet build source\Client\Fika.Core\Fika.Core.csproj            -c Release -p:GameDir=<GameDir> --nologo
dotnet build source\Headless\Fika.Headless\Fika.Headless.csproj  -c Release -p:GameDir=<GameDir> --nologo
dotnet build source\Server\FikaServer\FikaServer.csproj          -c Release -p:GameDir=<GameDir> --nologo
```

- **`-p:GameDir` 请始终显式传**：`source/Directory.Build.props` 的默认值被本地化过（当前是 `D:\EFT`），不传会指向错误的安装
- 客户端构建前需有 `<GameDir>\BepInEx\interop\Assembly-CSharp.dll`
- Server 缺 `GameDir` 会报 `SPT 5.0 runtime missing`
- MSBuild 报临时目录拒绝访问时，先把 `TEMP`/`TMP` 指到可写目录

## 目录

| 路径 | 内容 |
|---|---|
| `source/Client/Fika.Core/` | 客户端插件（Harmony 补丁、过场/转移/物品/撤离同步、LiteNetLib 网络层） |
| `source/Headless/Fika.Headless/` | 无头宿主插件（专用服务器，低开销配置） |
| `source/Server/FikaServer/`、`FikaShared/`、`FikaWebApp/` | SPT 服务端 mod（大厅、匹配、配置下发、物品寄送、Web 面板） |
| `source/*/Licenses/` | 第三方许可（LiteNetLib / Open.NAT / Mirror / K4os LZ4 = MIT；SIT = NCSA） |

## 已知限制

- `47242` 分支的 `Fika.Core.csproj` 仍是 `net6.0`，而 BepInEx/SPT 程序集是 net10 —— 直接套用会得到 `CS1705`；需要 47242 时代的 net6.0 BepInEx 程序集才能编译验证
- 47965 上仍有未解决的老问题：结局界面卡在播放背景处、客机不播落地 CG；诊断日志尚未清理

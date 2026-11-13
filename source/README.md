# Fika SPT 5.0 移植工作区

> **最新工作方式（2026-09-25）**：按用户要求先补齐功能实现，实机集中到后续阶段。最新批补齐可重复撤离点、BTR 乘客缓存、Bot 加载确认、结算晚到保存、Headless 预约回滚及 WebSocket/VOIP 生命周期边界；Client/Server Release 编译和网络/补丁静态检查通过，握手协议为 **v18**。本批未启动游戏或探针。此前第一人称/ESC/TAB 问题已由用户确认解决。当前进度及后续清单见 [先实现后验收进度](docs/implementation-first-progress.md)。下方按轮次记录的哈希、协议和 PASS 为历史证据。
源码阶段最新批次又修复了 StartLocalRaid 战利品生成参数语义，并完成服务端重编译、发布包/安装目录同步和 HTTP/Session 重验；当前源码完成度约 96%，整体交付约 86%～90%，待一次性实机验收的范围见 `docs/implementation-first-progress.md`。本次集中验证还通过了 NativeProbe 与隔离服务端全套 HTTP/Session/并发/Threading/Networking 检查。

本轮又完成服务端认证边界和保险重复登记的并发收口：创建/加入/离开及状态更新按认证 session 约束，创建成功后才广播，重复登记保留首次保险快照；最新构建结果与证据见 [先实现后验收进度](docs/implementation-first-progress.md) 顶部。当前仍属于集中验收前的源码阶段。

这是基于用户提供的 Fika 4.1 源码建立的独立移植工作区，目标是本地 SPT 5.0 IL2CPP。**当前是开发版本，尚不能视为完整可用的联机移植。** 原始资料和 `G:\fika\SPT5.0` 未被本工作区的探针部署覆盖。

本轮针对 SPT 5 画面问题增加了两项修复：主菜单“在线玩家”面板在保留配置相对比例的基础上自动应用 `0.75` 基准缩放；倒计时结束时显式关闭倒计时屏幕、恢复 `FPS Camera`、清理 `DeathFade/FastBlur` 和黑屏覆盖层。隔离图形入口已实际进入 `Game.Status=Started`，诊断确认 `CameraManager.IsActive=true`、`FirstPersonCameraState`、`FPS Camera` 启用且模糊值为 0。该记录是历史画面修复证据；当前客户端 DLL SHA256 为 `3820A8E8379F92F1EB99D5B255EDFCF4638CD6ADB9CAAD81CF03F2F5E6491674`。

## 当前状态（2026-09-23，SPT 5.0 IL2CPP 开发构建）

本轮完成最新客户端/服务端/探针构建同步，并重新验证 managed ProfileDescriptor 编码、NativeProbe、服务端 HTTP/Session/Concurrency/Threading 与 PatchChecks：空投容器未完成 prefab 挂载时现在安全退出并记录错误，避免晚加入/落地边界空引用；重连快照对 IL2CPP 原生集合取出的烟雾弹和破门对象改用 `TryCast`，避免 CLR `is` 判断漏掉原生派生类型；同时修复 LootList、空投逻辑和同步对象池的原生派生类型转换；同时保留上一轮撤离快照、Secret 撤离状态和重复 `Run()` 修复：`ExfiltrationRequest` 的倒计时时间戳按 SPT 5.0 原生 `float` 解码，`ExfilCountdown` 使用主机发送的权威时间，`FikaExfilManager` 以快照遍历并在回调前消费完成项，避免同一帧多个玩家或多个撤离点被跳过及同步回调重入；同时补齐独立 `SecretExfiltrationPoints` 数组的晚加入/重连快照，包含倒计时、PMC/Scav 可用性、物品转移和发现状态。Networking Checks、Client/Server/StartupDriver Release、PatchChecks 184/0 和 NativeProbe 均通过。该修复尚未替代双端真实地图自然撤离验收。

| 范围 | 已取得的证据 | 尚未证明 |
| --- | --- | --- |
| 客户端构建/入口 | Release client：0错误；保留 `FikaClientGameWorld.ShotDelegate` 空对象防御、空 `DamageInfo.SourceId` 保护、原生 `SinglePlayerSearchContentOperation` 搜索和库存权威重同步；撤离倒计时按主机 `float` 时间戳同步，并修复同帧多点/多玩家列表跳过；双端隔离实机 Host/Peer 均进入 `Started`，Bot 更新回归证据仍见 `artifacts/bot-update-client216-evidence.json` | `UtilityOperationEvent` 仍有真实武器 prefab 错误；`Failed prewarm shot delegate` 已确认来自 SPT 5.0 原生 `BallisticCalculatorPrewarmer.SimulateShot → Shot.Init`，不能归因于 Fika；真实地图自然撤离、拾取后撤离保存、自然医疗回枪、转轮、库存ID、教程、任务链与奖励验收仍待完成 |
| 补丁 | 184 个目标静态/原生安装通过；恢复 SPT 5 手雷注销、资源所有权唯一登记，补齐自动接任务与角色结算衔接及医疗任务清理 | 每个补丁的实战行为 |
| 注入与虚表 | 131 个类型；6 个泛型 Proceed 分派；健康 ApplyItem 槽位及接口；121 个基类 wrapper；世界集合跨 GC 保留、合作战局构造、原生进度/结束/灯塔死亡事件通过 | 全部玩家、机器人、物品生命周期及真实灯塔区域 |
| 原生行为 | ref/out 与护甲写回、Nullable、玩家/移动/15 类手持/7 类手雷构造；3 类主机手雷注销及活动/最终包往返；客机手雷碰撞、无刚体、重复/晚到包；伤害包32组合与死亡包4组合及复用探针通过；本轮双端主机发送的 548 条 BodyHealth 与客机应用值逐条一致，7 个死亡包的 ProfileId/攻击者/尸体 owner 正确，尸体实际库存与发送装备逐项一致 | 自然开枪 DamagePacket 发送/接收的完整覆盖、普通手雷跨帧平滑、炮击、音频、搜索/拾取/撤离及完整角色生命周期 |
| UI | 11 个 prefab、6 个 Sprite；加载界面操作；主菜单在线名单、人数/等级、本地化和刷新；任务栏页面切换、主持和客机正常加入房间通过 | 画面验收、其余界面、帧动画、队友血条完整行为 |
| 网络 | CRC32C、LZ4、DTO、握手、三通道UDP回环、有序消息/分片及档案往返通过；双端医疗同步修复；协议 v5 拒绝旧布局和不兼容客户端；208/driver215双端302次取弹/入膛状态一致，含PPSh两次耗尽/原生换弹/再射击共75发；前轮莫辛装填后射击通过；库存操作拒绝后的权威 `InventoryDescriptor` 重同步已通过双端冲突实机 | PPSh自主换弹决策、卡弹、Customization库存根差异、拾取后撤离保存、丢包/重连、完整玩家库存/任务/转场同步 |
| 死亡/结算 | round196客机自然死亡保存、198受控死亡档案落盘；206同进程两局均双端受控死亡、保存回执成功、返回主菜单且医疗绑定归零；208双端无受控死亡指令、死亡后正常结算回菜单；保留182/183自动任务与Scav日常证据 | 第二局死亡附近双方各1条无调用栈Unity空引用；保存竞争、自然撤离、Scav/转场；受控死亡不是自然射击验收 |
| 服务端 | .NET 10 round9：0 错误、372 警告；真实SPT5启动；首次哈希检查并发修复，100轮并发及5 GET/19 POST通过；两独立档案加入、双端死亡保存与房间移除 | 好友、物品发送、转场及headless生命周期 |

剩余问题及证据边界见 [迁移状态与阻断点](docs/client-il2cpp-port-blockers.md)。服务端版本 2.4.1、客户端版本 2.4.3 沿用源码版本号，不代表已发布 SPT 5.0 兼容版本。

client213 延续 `BotSurgeryPending` 的生产实现，并新增只读的 `AnimationEventsEmitter` 生命周期诊断，记录 `SetAnimator`、订阅/解绑和 `Dispose` 时序。新一轮双端各 20 名角色进入 Started，两次原生弹匣/膛室检查请求均接受；AK74N 样本的 state behaviours 从 0 增长到 43–45 后完成 `SetAnimator`，没有新的诊断异常或访问冲突。该证据仍未形成 `UtilityOperationEvent not found` 的生产修复。取消/死亡完整战局样本、库存 ID 差异、VOIP/headless/BTR/transit/reconnect、自然撤离以及完整任务/奖励链仍待验收，完整功能移植仍未完成。

client214 增加 `DamagePacket` 空 `SourceId` 的回归覆盖：SPT 5.0 的 `DamageInfo.SourceId` 为空字符串时表示没有来源，必须保持 packet nullable 为空；旧实现会在发包前执行 `new MongoID("")` 并崩溃。负对照和修复后的原生探针分别归档在 `artifacts/client214-empty-source-negative.txt` 与 `artifacts/native-probe-game/damage-codec-result.txt`。双端实机 `artifacts/combat-client214-final.json` 记录 548/548 条生命值同步无差异；尸体交付 7/7 个死亡包均有描述，实际尸体库存与发送装备一致。`controlled-hit` 只调用原生碰撞入口，本轮一次返回空命中、一次因目标血量不足拒绝，不能当作自然射击通过；本轮没有把它计入成功证据。随后修复了 `ObservedInventoryController.CreateSearchOperation` 返回 null 的缺口，使用原生 `SinglePlayerSearchContentOperation` 并分配观察端操作 ID；之后新增 `InventoryResyncPacket` 并以双端冲突探针完成真实拒绝回滚和后续拾取回归，证据为 `artifacts/inventory-conflict-evidence.jsonl` 与 `artifacts/inventory-resync-round2.json`。Release 构建 0 错误/21 既有警告，PatchChecks 184/0。完整任务、奖励、撤离保存与其他功能仍需逐项验收。

最新 StartupDriver 双端回归使用哈希 `E39E19A3E185E0E87E04205312D7C92263B9DCC96C6F85070A0F87B2002639C2`。`IAnimationEventsConsumer.ReceiveEvent` 诊断 Hook 已移除，因为 SPT 5.0 IL2CPP 接口 wrapper 无法被 HarmonyX 安装；这不属于生产逻辑。Host/Peer 均进入 Started，Host 生成 14 个 FikaBot，Peer 同步 15 个 ObservedPlayer；无哈希拦截、Harmony ReceiveEvent 错误或 StartupDriver `diagnostic.error`。最新入口日志仍出现 `UtilityOperationEvent`，而 `Failed prewarm shot delegate` 的完整原生栈位于 `BallisticCalculatorPrewarmer.SimulateShot → Shot.Init`；干净 SPT 5.0 基线同样复现该警告。完整功能验收仍未完成。

## 构建与检查

在本目录中运行（需要本地游戏生成的 interop 程序集）：

```powershell
& G:\fika\tools\dotnet\dotnet.exe build Client/Fika.Core/Fika.Core.csproj -c Release
& G:\fika\tools\dotnet\dotnet.exe build Server/FikaServer/FikaServer.csproj -c Release
& G:\fika\tools\dotnet\dotnet.exe run --project tests/Fika.PatchChecks -c Release -- artifacts/native-field-migrations.json
& G:\fika\tools\dotnet\dotnet.exe run --project tests/Fika.Networking.Checks -c Release
```

客户端 DLL 位于 `Client/Fika.Core/bin/Release/net6.0/`；服务端 DLL、FikaShared 和 assets 位于 `Server/FikaServer/bin/Release/`。可以用 `-p:GameDir=游戏路径` 覆盖默认引用路径。产物当前只供继续开发和隔离验证。

`scripts/Convert-FikaBundle.py` 使用 UnityPy 1.25.3 将原 bundle 中的 Fika MonoScript 程序集指向 IL2CPP 可解析的 image；源 bundle 保留，转换清单与 SHA256 在同目录的 `masterbundle.il2cpp.json`。

## 隔离验证

客户端：先构建 `tests/Fika.NativeProbe`，首次用 `scripts/Prepare-NativeProbe.ps1` 建立副本，再将最新客户端/探针 DLL 复制到副本并运行 `scripts/Start-NativeProbe.ps1 -Graphics`。脚本会归档旧日志并记录 DLL 哈希。探针等待 EFTHardSettings 正常异步加载后执行。`artifacts/native-probe-game/EscapeFromTarkov_Data` 是只读使用的原游戏数据 junction，禁止通过它修改或递归删除游戏数据。探针不调用 `FikaPlugin.Load`，在检查完成后退出。

服务端：`scripts/Prepare-ServerProbe.ps1` 建立 `artifacts/server-probe`，物理复制数据库，使用空用户目录和本机 `6975` 端口。已有副本时脚本拒绝覆盖。以该目录为工作目录运行 `G:\fika\tools\dotnet\dotnet.exe SPT.Server.dll`，然后执行：

```powershell
& G:\fika\tools\dotnet\dotnet.exe run --project tests/Fika.ServerHttp.Checks -c Release -- https://127.0.0.1:6975
```

HTTP 检查只用于此测试副本，会记录测试模组哈希；它通过真实 SPT HTTP 客户端进行 shuffle/zlib 及 Fika DTO 往返。NAT 打洞、Webhook 和 headless 自动生成在此副本关闭。

完整客户端入口使用 `scripts/Prepare-ClientStartup.ps1` 建立另一副本（SPT 插件保留原始字节/入口），构建并复制 `tests/Fika.StartupDriver`，用 `scripts/Start-ClientStartup.ps1 -SessionId <测试档案ID> -Graphics` 启动。必须同时检查 `BepInEx/LogOutput.log`、`unity-startup.log`、当次 `Logs/* errors_*.log` 和进程是否仍存活；最后一条状态可能来自崩溃前。完整入口和原生探针均使用图形模式，本地 DLSS 在无图形异步加载中可能崩溃。

`tests/Fika.ServerFixture --create-character` 建立 Standard 教程测试档案；加 `--menu-profile` 建立独立的 SPT Easy start 菜单测试档案（服务器模板自身跳过教程）。旧档案不覆盖。输出分别在 `artifacts/startup-test-profile.json`、`artifacts/menu-test-profile.json`。`tests/Fika.ServerSession.Checks` 验证这两个测试档案的通知/在线状态。每次替换客户端或测试驱动 DLL 后用 `scripts/Start-ServerProbe.ps1 -Restart` 清除隔离服务器的内存哈希基线。

StartupDriver 只在 `client-startup-game` 运行，通过正常按钮/开关事件驱动界面；`Invoke-StartupHostRoute.ps1` 包含最终 FikaStartButton。显式 `controlled-death` 诊断命令按 expectedProfileId 校验测试角色后调用原生 Kill，`request-exit` 使用与 F8 相同的退出入口及条件；仅用于独立测试档案，不能当自然交火证据。记录 `startup-state.json`、`startup-history.jsonl`、`startup-actions.log`、`profile-codec-result.txt` 及只读 `movement-diagnostics.log`（伤害、库存线程、手雷、尸体包和 Dispose 调用栈）。状态包含原生角色信息、保存任务与 GameOverSaveStatus 标志；`cleanup-diagnostics.log` 记录原生语音资源登记/释放；不等于画面或联机验收。不替代游戏任务或跳过 Fika 校验。客户端副本的数据目录是原游戏数据 junction，禁止通过它写入或递归删除。

本地结果在 `artifacts/`，不纳入源码提交。不要把探针 PASS 当成完整游戏验收，也不要将探针插件用于正式游戏。


本轮 client215 完成了空 Shot 的防御性检查、客户端/服务端 Release 构建、Networking Checks、PatchChecks 184/0 以及 NativeProbe 全量 PASS。该保护只覆盖 Fika 回调收到空对象的边界，不能修复原生预热器自身的弹药构造失败。

本轮 client218 修复 SPT 5.0 SecretExfiltrationPointFoundShareEvent 的 TeamId 传递：SecretExfilFound 现在按 groupId/teamId/exitName 三字段编码并调用原生事件，保留旧两参数源码兼容 helper。NativeProbe 验证原生事件签名、字段和包往返；Client/NativeProbe Release 均为 0 错误。真实重连、自然 Secret 发现和撤离保存仍未验收。


本轮 client219 继续验证重连相关撤离生命周期：NativeProbe 覆盖普通与 Secret 点混合绑定、重复绑定、同帧倒计时完成，以及 Stop 清理两类点的原生事件；结果 PASS。新增本地玩家 TeamId 为空时回退 GroupId 的防御。真实双端重连、地图 Secret 发现和自然撤离保存仍未验收。

本轮 client223 在此前修复基础上补齐远端武器/投掷物手持工厂、任务物品搜索、主机绊雷创建和重连绊雷生成的 `TryCast` 原生类型判断；客户端/服务端/NativeProbe 均通过。真实双端重连、自然撤离和完整玩法验收仍待完成。

本轮 client220 修复撤离管理器重入缺口：Run() 不再通过旧 Stop() 关闭仍保留的普通/Secret 触发器，重复 Run() 会只解绑后重新绑定；NativeProbe 已实际执行 ApplySnapshot → Run 两次并验证 Countdown、Secret 资格/物品状态、Collider/Root 启用和最终 Stop 清理。协议当前为 v5，拒绝旧布局的客户端。






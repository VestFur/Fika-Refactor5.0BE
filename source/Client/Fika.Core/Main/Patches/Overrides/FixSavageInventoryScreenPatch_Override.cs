using System;
using System.Linq;
using System.Reflection;
using EFT;
using Fika.Core.Main.GameMode;
using Fika.Core.Main.Utils;
using HarmonyLib;
using SPTushonka.Common.Http;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.Overrides;

internal static class RaidEndProfile
{
    public static ProfileDescriptor ProfileDescriptor { get; private set; }

    internal static void Capture(Profile profile)
    {
        // 战局设置失败时可能在没有任何应提供给 scav 结算界面的
        // profile 的情况下走到 CoopGame.Stop。不要让上一场战局的
        // 描述符在这条路径上存活下来。
        if (profile == null)
        {
            ProfileDescriptor = null;
            return;
        }

        ProfileDescriptor = new ProfileDescriptor(profile, FikaGlobals.SearchControllerSerializer);
        // 战局结束描述符会被提交到 /raid/profile/scavsave；此时本地
        // profile 的 group 仍持有 Fika 的 "Fika" 哨兵值，它绝不能
        // 出现在被保存的 profile 中。
        FikaGlobals.StripFikaGroupSentinel(ProfileDescriptor);
    }

    internal static void Clear() => ProfileDescriptor = null;
}
/// <summary>
/// 从另一个补丁（GetProfileAtEndOfRaidPatch）获取 profile
/// 若我们的 profile 是 savage，则新建 Session.AllProfiles 并传入自己的 profile，以便使用 ScavengerInventoryScreen
/// </summary>
public class FixSavageInventoryScreenPatch_Override : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.Method(typeof(SessionResultShowOperation), nameof(SessionResultShowOperation.Init));
    }

    [PatchPrefix]
    public static void PatchPrefix(EFT.SessionResultShowOperation __instance)
    {
        // GameWorld.Dispose 会在原生结果操作开始之前释放网络管理器，
        // 把 ClientType 重置为 None。捕获到的战局结束描述符
        // 就是此 Fika 结算界面的标记。
        var descriptor = RaidEndProfile.ProfileDescriptor;
        if (descriptor == null || __instance?._session == null)
        {
            return;
        }

        // 在反序列化之前先取走跨战局描述符。如果
        // 原生 profile 构造函数抛异常，后续的结算界面回调
        // 不得再重试上一场战局的 profile。
        RaidEndProfile.Clear();

        Profile profile = new(descriptor);

        if (profile.Side != EPlayerSide.Savage)
        {
            return;
        }

        // 结果操作保留的是本场战局开始时提供的 Scav profile。
        // 后端的当前 ProfileOfPet 可能在死亡之后已经
        // 被重新生成，因此在把捕获的库存应用到本界面之前，
        // 要与其所属操作的战局 profile 做比较。
        var raidScavProfile = __instance._savageProfile;
        if (raidScavProfile == null ||
            !string.Equals(profile.Id, raidScavProfile.Id, System.StringComparison.Ordinal))
        {
            return;
        }

        var session = __instance._session.TryCast<ClientBackendSession>();
        if (session == null || session.AllProfiles == null || session.AllProfiles.Length == 0)
        {
            return;
        }

        var primaryProfile = session.AllProfiles.FirstOrDefault(x => x != null && x.Side != EPlayerSide.Savage);
        if (primaryProfile == null)
        {
            return;
        }

        session.AllProfiles = new Profile[]
        {
            primaryProfile,
            profile
        };
        session.ProfileOfPet?.LearnAll();

        // 向服务器发起请求，让它知道我们可能转移的物品
        var payload = Serialize(descriptor);
        if (payload == null)
        {
            FikaGlobals.LogError("Could not serialize the raid-end profile, the scav save was skipped");
            return;
        }

        RequestHandler.PutJson("/raid/profile/scavsave", payload);
    }

    /// <summary>
    /// 单独放在一个不被内联的方法里：针对被遮蔽的 Newtonsoft.Json 的链接失败
    /// 会在该方法被编译时暴露，而同一方法内的 try/catch
    /// 永远无法观察到它。
    /// </summary>
    private static string Serialize(EFT.ProfileDescriptor descriptor)
    {
        try
        {
            return ProfileDescriptorJson.ToServerJson(descriptor);
        }
        catch (Exception exception)
        {
            FikaGlobals.LogError($"Could not serialize the raid-end profile: {exception}");
            return null;
        }
    }
}

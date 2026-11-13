using SPTushonka.Reflection.Patching;
using System.Reflection;

namespace Fika.Headless.Patches.TextureValidateFormat;

/// <summary>
/// 在本 Unity 构建中，Texture.ValidateFormat 的 RenderTextureFormat 重载是一个托管
/// 包装器。改为补丁它原生的支持性查询，这样
/// 原生调用方也能得到同样的、headless 兼容的结果。
/// </summary>
public class ValidateFormatPatch1 : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(SystemInfo).GetMethod(nameof(SystemInfo.SupportsRenderTextureFormat),
            BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(RenderTextureFormat) }, null);
    }

    [PatchPostfix]
    public static void Postfix(ref bool __result)
    {
        __result = true;
    }
}

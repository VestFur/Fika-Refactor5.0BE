using BSG.Unity.Wires;
using EFT.Interactive;
using SPTushonka.Reflection.Patching;
using HarmonyLib;
using Il2CppInterop.Runtime;
using BepInEx.Unity.IL2CPP.Utils.Collections;
using System;
using TMPro;
using System.Collections;
using System.Reflection;
using UnityEngine.SceneManagement;

namespace Fika.Headless.Patches.DestroyGraphics;

public class LoadScenePatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.Method(typeof(SceneManager), nameof(SceneManager.LoadSceneAsync), [typeof(string), typeof(LoadSceneMode)]);
    }

    /// <summary>
    /// 销毁刚加载场景中的每个渲染器和材质。正是这个
    /// 补丁让一个正在游玩的安装的主菜单变得不可读，所以它
    /// 被双重保护：插件必须是为专用主机而启用的，并且
    /// "Destroy Renderers" 配置项必须仍然处于启用状态。
    /// </summary>
    [PatchPostfix]
    static void Postfix(string sceneName, AsyncOperation __result)
    {
        if (!FikaHeadlessPlugin.IsArmed)
        {
            FikaHeadlessPlugin.FikaHeadlessLogger?.LogWarning($"[Headless] Refusing scene modification for '{sceneName}': " +
                "this process is not an armed dedicated host");
            return;
        }

        if (!FikaHeadlessPlugin.DestroyRenderersOnSceneLoad.Value)
        {
            FikaHeadlessPlugin.FikaHeadlessLogger?.LogInfo($"[Headless] Scene '{sceneName}' left untouched: 'Destroy Renderers' is disabled");
            return;
        }

        GameObject tempGameObject = new("SceneModificationHandler");
        SceneModificationHandler handler = tempGameObject.AddComponent<SceneModificationHandler>();

        handler.StartCoroutine(handler.WaitForSceneLoad(sceneName, __result).WrapToIl2Cpp());
    }
}

public class SceneModificationHandler : MonoBehaviour
{
    public SceneModificationHandler(IntPtr pointer) : base(pointer)
    {
    }

    public IEnumerator WaitForSceneLoad(string sceneName, AsyncOperation operation)
    {
        // 等待场景加载完成
        while (!operation.isDone)
        {
            yield return null;
        }

        // 门闸可能已被重新关闭（强制模式 + 普通档案）。
        if (!FikaHeadlessPlugin.IsArmed)
        {
            Destroy(gameObject);
            yield break;
        }

        FikaHeadlessPlugin.FikaHeadlessLogger.LogDebug($"Scene {sceneName} is fully loaded.");

        Scene loadedScene = SceneManager.GetSceneByName(sceneName);
        if (loadedScene.IsValid())
        {
            ModifyLoadedScene(loadedScene);
        }

        Destroy(gameObject);
    }

    private void ModifyLoadedScene(Scene scene)
    {
        foreach (GameObject RootGameObjects in scene.GetRootGameObjects())
        {
            //Logger.LogInfo($"Inspecting root object: {rootObj.name}");
            DestroyRenderers(RootGameObjects);
        }
    }

    private static readonly Type[] _protectedComponents = [
        typeof(WindowBreaker),
        typeof(AmbientLight),
        typeof(AreaLight),
        typeof(HotObject),
        typeof(RoadSplineGenerator),
        typeof(RoadSolidMarkGenerator),
        typeof(RoadMarksGenerator),
        typeof(WireGenerator), // These can't be removed, MeshRenderers keeps these alive at the moment
			typeof(WireSplineGenerator)
    ];

    private void DestroyRenderers(GameObject prefab)
    {
        Renderer[] renderers = prefab.GetComponentsInChildren<Renderer>(true);
        ParticleSystem[] particles = prefab.GetComponentsInChildren<ParticleSystem>(true);

        foreach (ParticleSystem particle in particles)
        {
            if (particle.gameObject.name.ToLower().Contains("door"))
            {
                continue;
            }

            particle.Stop();
            Destroy(particle);
        }

        foreach (Renderer renderer in renderers)
        {
            if (renderer.GetComponent<TMP_Text>() != null)
            {
                continue;
            }

            // 检查我们绝对不能卸载的受保护组件，这些东西无论如何都会把游戏搞坏。
            bool hasProtectedRenderer = false;
            foreach (Type componentType in _protectedComponents)
            {
                if (renderer.gameObject.GetComponent(Il2CppType.From(componentType)) != null || renderer.gameObject.name.ToLower().Contains("door") || renderer.gameObject.name.ToLower().Contains("glass"))
                {
                    hasProtectedRenderer = true;
                    break;
                }
            }

            // 卸载材质和纹理。
            foreach (Material material in renderer.sharedMaterials)
            {
                if (material != null)
                {
                    if (material.name.ToLower().Contains("glass") || (material.shader != null && material.shader.name.StartsWith("TextMeshPro")))
                    {
                        continue;
                    }

                    if (material.HasProperty("_MainTex"))
                    {
                        material.mainTexture = null;
                    }

                    Destroy(material);
                }
            }

            if (hasProtectedRenderer)
            {
                continue;
            }

            // 销毁渲染器本身，注意：这不应移除任何碰撞体之类的东西。
#if DEBUG
            FikaHeadlessPlugin.FikaHeadlessLogger.LogDebug($"Removing Renderer: {renderer.gameObject.name}");
#endif
            Destroy(renderer);
        }
    }
}

// © 2026 Lacyway All Rights Reserved

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Fika.Core.Main.Utils;

namespace Fika.Core.Bundles;

/// <summary>
/// 创建者：Nexus / pandahhcorp <br/>
/// 由 Lacyway 重构为直接从内存加载 bundle
/// </summary>
internal class InternalBundleLoader
{
    public static InternalBundleLoader Instance { get; private set; }

    private AssetBundle _masterBundle;

    public InternalBundleLoader()
    {
        // Plugin.Load 运行在 Unity 主线程上。要在任何补丁或 UI 消费方
        // 能从该 bundle 请求资源之前完成加载。
        LoadBundles();
        Instance = this;
    }

    private void LoadBundles()
    {
        var assembly = Assembly.GetExecutingAssembly();
        const string resource = "Fika.Core.Bundles.Files.masterbundle.bundle";
        using var stream = assembly.GetManifestResourceStream(resource)
            ?? throw new FileNotFoundException("Fika master bundle is missing from the plugin", resource);
        using MemoryStream memoryStream = new();
        stream.CopyTo(memoryStream);
        _masterBundle = AssetBundle.LoadFromMemory(memoryStream.ToArray());
        if (_masterBundle == null)
            throw new InvalidOperationException("Unity could not load the Fika master bundle");
    }

    /// <summary>
    /// 从 Master Bundle 加载一个 Fika 资源
    /// </summary>
    /// <param name="asset">要加载的 <see cref="EFikaAsset"/> 类型</param>
    /// <returns></returns>
    /// <exception cref="NullReferenceException">找不到 Master Bundle</exception>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="EFikaAsset"/> 超出范围</exception>
    internal GameObject GetFikaAsset(EFikaAsset asset)
    {
        if (_masterBundle == null)
        {
            throw new NullReferenceException("GetFikaAsset::MasterBundle did not exist!");
        }

        return asset switch
        {
            EFikaAsset.Ping => LoadPrefab("BasePingPrefab.prefab"),
            EFikaAsset.SendItemMenu => LoadPrefab("SendItemMenu.prefab"),
            EFikaAsset.MainMenuUI => LoadPrefab("MainMenuUI.prefab"),
            EFikaAsset.MatchmakerUI => LoadPrefab("NewMatchMakerUI.prefab"),
            EFikaAsset.PlayerUI => LoadPrefab("PlayerFriendlyUI.prefab"),
            EFikaAsset.FreecamUI => LoadPrefab("FreecamUI.prefab"),
            EFikaAsset.ListPlayer => LoadPrefab("ListPlayer.prefab"),
            EFikaAsset.AdminUI => LoadPrefab("AdminSettingsUI.prefab"),
            EFikaAsset.FikaChatUI => LoadPrefab("FikaChatUI.prefab"),
            EFikaAsset.RaidAdminUI => LoadPrefab("RaidAdminUI.prefab"),
            EFikaAsset.LoadingScreenUI => LoadPrefab("LoadingScreenUI.prefab"),
            EFikaAsset.DebugUI => LoadPrefab("DebugUI.prefab"),
            _ => throw new ArgumentOutOfRangeException(nameof(asset), "Invalid type was given")
        };
    }

    /// <summary>
    /// 加载一个 prefab，并在资源缺失时报告，而不是把它转交给
    /// <c>Cast&lt;T&gt;()</c>。
    /// </summary>
    /// <remarks>
    /// <c>Cast&lt;T&gt;()</c> 会解析被包装指针的 il2cpp 类。当 bundle
    /// 不包含该资源时，返回的包装器携带空的原生指针，而该
    /// 类查找会在 GameAssembly 内部出错（0xC0000005，读取地址 0x0），而不会
    /// 抛出托管异常——它不是 CLR 的 NullReferenceException，调用方
    /// 无法捕获它。返回 null 可让消费方跳过自己的功能。
    /// </remarks>
    private GameObject LoadPrefab(string name)
    {
        var loaded = _masterBundle.LoadAsset(name, Il2CppInterop.Runtime.Il2CppType.Of<GameObject>());
        if (loaded is null || loaded.Pointer == IntPtr.Zero)
        {
            FikaGlobals.LogError($"[BundleLoader] '{name}' is missing from the Fika master bundle; the feature that needs it is skipped");
            return null;
        }

        return loaded.Cast<GameObject>();
    }

    /// <summary>
    /// <see cref="LoadPrefab(string)"/> 的 Sprite 对应版本。
    /// </summary>
    private Sprite LoadSprite(string name)
    {
        var loaded = _masterBundle.LoadAsset(name, Il2CppInterop.Runtime.Il2CppType.Of<Sprite>());
        if (loaded is null || loaded.Pointer == IntPtr.Zero)
        {
            FikaGlobals.LogError($"[BundleLoader] '{name}' is missing from the Fika master bundle; the feature that needs it is skipped");
            return null;
        }

        return loaded.Cast<Sprite>();
    }

    /// <summary>
    /// 将所有 <see cref="EFikaSprite"/> 加载到内存并缓存
    /// </summary>
    /// <returns>以 <see cref="Dictionary{TKey, TValue}"/> 存放的所有 sprite</returns>
    internal Dictionary<EFikaSprite, Sprite> GetFikaSprites()
    {
        Dictionary<EFikaSprite, Sprite> sprites = [];

        sprites.Add(EFikaSprite.PingPoint, LoadSprite("PingPoint.png"));
        sprites.Add(EFikaSprite.PingPlayer, LoadSprite("PingPlayer.png"));
        sprites.Add(EFikaSprite.PingLootableContainer, LoadSprite("PingLootableContainer.png"));
        sprites.Add(EFikaSprite.PingDoor, LoadSprite("PingDoor.png"));
        sprites.Add(EFikaSprite.PingDeadBody, LoadSprite("PingDeadBody.png"));
        sprites.Add(EFikaSprite.PingLootItem, LoadSprite("PingLootItem.png"));

        return sprites;
    }

    public enum EFikaSprite
    {
        PingPoint,
        PingPlayer,
        PingLootableContainer,
        PingDoor,
        PingDeadBody,
        PingLootItem
    }

    public enum EFikaAsset
    {
        Ping,
        SendItemMenu,
        MainMenuUI,
        MatchmakerUI,
        PlayerUI,
        FreecamUI,
        ListPlayer,
        AdminUI,
        FikaChatUI,
        RaidAdminUI,
        LoadingScreenUI,
        DebugUI
    }
}

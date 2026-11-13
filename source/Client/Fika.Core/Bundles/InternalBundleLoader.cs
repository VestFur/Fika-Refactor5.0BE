// © 2026 Lacyway All Rights Reserved

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Fika.Core.Main.Utils;

namespace Fika.Core.Bundles;

/// <summary>
/// Created by Nexus / pandahhcorp <br/>
/// Refactored by Lacyway to load bundles directly from memory
/// </summary>
internal class InternalBundleLoader
{
    public static InternalBundleLoader Instance { get; private set; }

    private AssetBundle _masterBundle;

    public InternalBundleLoader()
    {
        // Plugin.Load runs on Unity's main thread. Finish loading before any
        // patches or UI consumers can request an asset from this bundle.
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
    /// Loads a Fika asset from the Master Bundle
    /// </summary>
    /// <param name="asset">The <see cref="EFikaAsset"/> type to load</param>
    /// <returns></returns>
    /// <exception cref="NullReferenceException">Master Bundle could not be found</exception>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="EFikaAsset"/> was out of range</exception>
    internal GameObject GetFikaAsset(EFikaAsset asset)
    {
        if (_masterBundle == null)
        {
            throw new NullReferenceException("GetFikaAsset::MasterBundle did not exist!");
        }

        return asset switch
        {
            EFikaAsset.Ping => _masterBundle.LoadAsset("BasePingPrefab.prefab", Il2CppInterop.Runtime.Il2CppType.Of<GameObject>()).Cast<GameObject>(),
            EFikaAsset.SendItemMenu => _masterBundle.LoadAsset("SendItemMenu.prefab", Il2CppInterop.Runtime.Il2CppType.Of<GameObject>()).Cast<GameObject>(),
            EFikaAsset.MainMenuUI => _masterBundle.LoadAsset("MainMenuUI.prefab", Il2CppInterop.Runtime.Il2CppType.Of<GameObject>()).Cast<GameObject>(),
            EFikaAsset.MatchmakerUI => _masterBundle.LoadAsset("NewMatchMakerUI.prefab", Il2CppInterop.Runtime.Il2CppType.Of<GameObject>()).Cast<GameObject>(),
            EFikaAsset.PlayerUI => _masterBundle.LoadAsset("PlayerFriendlyUI.prefab", Il2CppInterop.Runtime.Il2CppType.Of<GameObject>()).Cast<GameObject>(),
            EFikaAsset.FreecamUI => _masterBundle.LoadAsset("FreecamUI.prefab", Il2CppInterop.Runtime.Il2CppType.Of<GameObject>()).Cast<GameObject>(),
            EFikaAsset.ListPlayer => _masterBundle.LoadAsset("ListPlayer.prefab", Il2CppInterop.Runtime.Il2CppType.Of<GameObject>()).Cast<GameObject>(),
            EFikaAsset.AdminUI => _masterBundle.LoadAsset("AdminSettingsUI.prefab", Il2CppInterop.Runtime.Il2CppType.Of<GameObject>()).Cast<GameObject>(),
            EFikaAsset.FikaChatUI => _masterBundle.LoadAsset("FikaChatUI.prefab", Il2CppInterop.Runtime.Il2CppType.Of<GameObject>()).Cast<GameObject>(),
            EFikaAsset.RaidAdminUI => _masterBundle.LoadAsset("RaidAdminUI.prefab", Il2CppInterop.Runtime.Il2CppType.Of<GameObject>()).Cast<GameObject>(),
            EFikaAsset.LoadingScreenUI => _masterBundle.LoadAsset("LoadingScreenUI.prefab", Il2CppInterop.Runtime.Il2CppType.Of<GameObject>()).Cast<GameObject>(),
            EFikaAsset.DebugUI => _masterBundle.LoadAsset("DebugUI.prefab", Il2CppInterop.Runtime.Il2CppType.Of<GameObject>()).Cast<GameObject>(),
            _ => throw new ArgumentOutOfRangeException(nameof(asset), "Invalid type was given")
        };
    }

    /// <summary>
    /// Loads all <see cref="EFikaSprite"/> into memory and caches them
    /// </summary>
    /// <returns>All sprites in a <see cref="Dictionary{TKey, TValue}"/></returns>
    internal Dictionary<EFikaSprite, Sprite> GetFikaSprites()
    {
        Dictionary<EFikaSprite, Sprite> sprites = [];

        sprites.Add(EFikaSprite.PingPoint, _masterBundle.LoadAsset("PingPoint.png", Il2CppInterop.Runtime.Il2CppType.Of<Sprite>()).Cast<Sprite>());
        sprites.Add(EFikaSprite.PingPlayer, _masterBundle.LoadAsset("PingPlayer.png", Il2CppInterop.Runtime.Il2CppType.Of<Sprite>()).Cast<Sprite>());
        sprites.Add(EFikaSprite.PingLootableContainer, _masterBundle.LoadAsset("PingLootableContainer.png", Il2CppInterop.Runtime.Il2CppType.Of<Sprite>()).Cast<Sprite>());
        sprites.Add(EFikaSprite.PingDoor, _masterBundle.LoadAsset("PingDoor.png", Il2CppInterop.Runtime.Il2CppType.Of<Sprite>()).Cast<Sprite>());
        sprites.Add(EFikaSprite.PingDeadBody, _masterBundle.LoadAsset("PingDeadBody.png", Il2CppInterop.Runtime.Il2CppType.Of<Sprite>()).Cast<Sprite>());
        sprites.Add(EFikaSprite.PingLootItem, _masterBundle.LoadAsset("PingLootItem.png", Il2CppInterop.Runtime.Il2CppType.Of<Sprite>()).Cast<Sprite>());

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

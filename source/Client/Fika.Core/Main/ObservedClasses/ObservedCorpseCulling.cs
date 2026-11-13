using System;
using System.Collections.Generic;
using EFT.Interactive;
using Fika.Core.Main.Players;

namespace Fika.Core.Main.ObservedClasses;

public sealed class ObservedCorpseCulling : IDisposable
{
    public bool IsVisible;

    private readonly ObservedPlayer _observedPlayer;
    private readonly Corpse _observedCorpse;
    private readonly List<Renderer> _renderers = new(256);
    private ObservedCullingObject _gClass999;
    private bool _ragdollDone;
    private bool _localVisible = true;
    private System.Action<bool> _visibilityChangedHandler;

    public ObservedCorpseCulling(ObservedPlayer observedPlayer, Corpse observedCorpse)
    {
        _observedPlayer = observedPlayer;
        _observedCorpse = observedCorpse;
        _gClass999 = new(observedPlayer.PlayerBones.Spine3.Original,
            EFTHardSettings.Instance.CULLING_PLAYER_SPHERE_DEAD_BODY_RADIUS,
            EFTHardSettings.Instance.CULLING_PLAYER_DEAD_BODY_DISTANCE);
        _visibilityChangedHandler = (System.Action<bool>)ChangeVisibility;
        _gClass999.OnVisibilityChanged += _visibilityChangedHandler;
        _gClass999.Register();
    }

    public void ManualUpdate()
    {
        if (_gClass999 == null || _observedCorpse == null) return;
        if (!_ragdollDone)
        {
            _gClass999.CustomUpdate();
        }
        if (!_ragdollDone && (!_observedCorpse.HasRagdoll || _observedCorpse.Ragdoll == null || _observedCorpse.Ragdoll._isPhysicsDone))
        {
            _ragdollDone = true;
        }
        if (_localVisible != IsVisible)
        {
            IsVisible = _localVisible;
            ChangeRendererState();
        }
    }

    private void ChangeVisibility(bool value)
    {
        _localVisible = value;
    }

    private void ChangeRendererState()
    {
        for (var i = 0; i < _renderers.Count; i++)
        {
            if (_renderers[i] != null)
            {
                _renderers[i].forceRenderingOff = false;
            }
        }
        _renderers.Clear();
        var isVisible = IsVisible;
        // 当尸体比其所有者存活更久时，玩家躯体已经被拆除（游戏会在 OnDestroy
        // 到达本对象之前销毁该躯体）。若没有这层保护，
        // 该异常会中断 ObservedPlayer.OnDestroy 的剩余流程。
        var playerBody = _observedPlayer != null ? _observedPlayer.PlayerBody : null;
        if (playerBody == null)
        {
            return;
        }

        var nativeRenderers = new Il2CppSystem.Collections.Generic.List<Renderer>();
        playerBody.GetRenderersNonAlloc(nativeRenderers);
        foreach (var renderer in nativeRenderers) _renderers.Add(renderer);
        for (var k = 0; k < _renderers.Count; k++)
        {
            if (_renderers[k] != null)
            {
                _renderers[k].forceRenderingOff = !isVisible;
            }
        }
    }

    public void Dispose()
    {
        if (_gClass999 == null) return;
        _gClass999.OnVisibilityChanged -= _visibilityChangedHandler;
        _gClass999?.Dispose();
        _gClass999 = null;
        IsVisible = false;
        ChangeRendererState();
    }
}

using EFT.Weather;
using System.Collections.Generic;
using EFT;

namespace Fika.Core.Main.GameMode;

/// <summary>
/// <see cref="CoopGame"/> 的接口
/// </summary>
public interface IFikaGame
{
    public List<int> ExtractedPlayers { get; }

    ExitStatus ExitStatus { get; set; }

    string ExitLocation { get; set; }

    public void Stop(string profileId, ExitStatus exitStatus, string exitName, float delay = 0f);

    public ESeason Season { get; set; }

    public SeasonsSettings SeasonsSettings { get; set; }

    public BaseGameController GameController { get; set; }

    /// <summary>在走完 IL2CPP 的部署路径之后恢复本机第一人称的手部显示。</summary>
    public void RepairLocalHands(bool force = false);


    /// <summary>解除原生动画始终没有退出的拾取/战利品状态。</summary>
    public void RepairLocalInteractionState();

    /// <summary>
    /// 解除一个明明无人持锁却一直关着的背包闸门，
    /// 使物品移动与武器切换重新可用。
    /// </summary>
    public void RepairLocalInventoryState();
}

using EFT.Communications;
using System.Reflection;
using EFT;
using Fika.Core.Networking.Websocket;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.UI.Patches;

/// <summary>
/// Creates <see cref="FikaNotificationManager"/> once the application is up.
///
/// This is the only place that opens SPT's <c>/fika/notification/</c> websocket, and that
/// socket is what registers a player's presence on the server
/// (<c>FikaServer.WebSockets.NotificationWebSocket.OnConnectionAsync</c> is the only caller of
/// <c>PresenceService.AddPlayerPresence</c>). While this patch was unapplied nobody announced
/// itself, <c>/fika/presence/get</c> answered an empty array and the main menu panel could not
/// show a single row - the missing self entry lived here, not in the panel and not in the
/// presence providers.
///
/// 47888 renamed the hooked method: <c>TarkovApplication.InitNotificationManager</c> (the name
/// this patch used up to 47242) is now <c>TarkovApplication.EnsureNotificationManagerAsync</c>,
/// private and returning <c>Task</c>. The old name stays as a fallback so this file works on
/// both builds.
/// </summary>
public class TarkovApplication_InitNotificationManager_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        // Both members are private in the game build, so the lookup has to include non-public
        // instance methods; without that the 47888 rename makes the patch silently dead (the
        // loader then reports "target method does not exist in this game build").
        return typeof(TarkovApplication)
                   .GetMethod("EnsureNotificationManagerAsync",
                       BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
               ?? typeof(TarkovApplication)
                   .GetMethod("InitNotificationManager",
                       BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    }

    /// <summary>
    /// Runs when the native initializer returns its task. The manager itself does not depend on
    /// the notification UI: <see cref="FikaNotificationManager"/> only opens the socket in its
    /// constructor and every notification it renders goes through
    /// <c>Singleton&lt;PreloaderUI&gt;.Instance?.NotifierView</c> on the main thread, so being
    /// created a few awaits early cannot fault - and connecting earlier is what makes the server
    /// know this player before the main menu asks for the presence list.
    /// </summary>
    [PatchPostfix]
    internal static void Postfix()
    {
        FikaNotificationManager.EnsureCreated("TarkovApplication notification manager");
    }
}

using FikaServer.Models.Enums;
using FikaServer.Models.Fika.WebSocket.Notifications;
using FikaServer.WebSockets;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Utils;

namespace FikaServer.Callbacks;

[Injectable]
public class NotificationCallbacks(NotificationWebSocket notificationWebSocket,
    ISptLogger<NotificationWebSocket> logger, HttpResponseUtil httpResponseUtil)
{
    /// <summary>
    /// 处理 /fika/notification/push
    /// </summary>
    public async ValueTask<string> HandlePushNotification(string url, PushNotification info, MongoId sessionID)
    {
        if (info.Notification == null)
        {
            return httpResponseUtil.NullResponse();
        }

        // 为客户端做一些异常处理：图标 6 似乎会引发异常，超出枚举边界时同样会。
        if (info.NotificationIcon == EEFTNotificationIconType.Achievement || (int)info.NotificationIcon > 14)
        {
            info.NotificationIcon = EEFTNotificationIconType.Default;
        }

        //Todo: 调试日志
        logger.Error("broadcasting");
        await notificationWebSocket.BroadcastAsync(info);

        return httpResponseUtil.NullResponse();
    }
}

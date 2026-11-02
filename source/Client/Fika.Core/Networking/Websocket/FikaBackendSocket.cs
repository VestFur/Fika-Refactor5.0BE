using System;
using System.Net.WebSockets;
using System.Text;

namespace Fika.Core.Networking.Websocket;

internal static class FikaBackendSocket
{
    internal static ClientWebSocket Create(string sessionId)
    {
        var socket = new ClientWebSocket();
        // SPT 会生成它自己的后端证书。为这些仅用于后端的 socket 匹配其
        // HTTP 客户端的信任策略，同时不改变全局的
        // TLS 校验，也不安装机器级的根证书。
        socket.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        socket.Options.SetRequestHeader("Authorization", "Basic " +
            Convert.ToBase64String(Encoding.UTF8.GetBytes(sessionId + ":")));
        return socket;
    }
}

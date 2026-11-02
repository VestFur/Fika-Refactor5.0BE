using BitPacking;
using EFT.Vehicle;
using System;
using System.Net;
using System.Net.Sockets;
using Fika.Core.Networking.LZ4;

namespace Fika.Core.Networking;

public static class NetworkUtils
{
    public static IBitReaderStream EventDataReader { get; private set; } = new BitReaderStream(new byte[16_384]);
    public static IBitWriterStream EventDataWriter { get; private set; } = new BitWriterStream(new byte[10_000]);

    internal static void ResetReaderAndWriter()
    {
        EventDataReader = new BitReaderStream(new byte[16_384]);
        EventDataWriter = new BitWriterStream(new byte[10_000]);
    }

    /// <summary>
    /// 使用 LZ4 压缩给定的字节数组
    /// </summary>
    /// <param name="input">原始未压缩的字节数组</param>
    /// <returns>压缩后的字节数组</returns>
    public static ReadOnlySpan<byte> CompressBytes(byte[] input)
    {
        var buffer = new byte[LZ4Codec.MaximumOutputSize(input.Length)];
        var encoded = LZ4Codec.Encode(input, 0, input.Length, buffer, 0, buffer.Length, LZ4Level.L04_HC);

        return buffer.AsSpan(0, encoded);
    }

    /// <summary>
    /// 将 LZ4 压缩的字节数组解压回其原始形式
    /// </summary>
    /// <param name="compressedData">要解压的已压缩字节数组</param>
    /// <param name="originalLength">原始字节数组的长度</param>
    /// <returns>解压后的字节数组</returns>
    public static byte[] DecompressBytes(byte[] compressedData, int originalLength)
    {
        var result = new byte[originalLength];
        var decoded = LZ4Codec.Decode(compressedData, 0, compressedData.Length, result, 0, originalLength);
        if (decoded != originalLength)
        {
            throw new InvalidOperationException("LZ4 decompression failed: length mismatch.");
        }

        return result;
    }

    public static string FormatMongoId(uint timeStamp, ulong counter)
    {
        return string.Create(24, (timeStamp, counter), (span, state) =>
        {
            state.timeStamp.TryFormat(span[..8], out _, "x8");
            state.counter.TryFormat(span[8..], out _, "x16");
        });
    }

    /// <summary>
    /// 校验给定的 IP 字符串是否表示一个可连接、可路由的 IP 地址。
    /// </summary>
    /// <param name="ip">要校验的 IP 地址字符串（IPv4 或 IPv6）。</param>
    /// <returns>
    /// 若 IP 有效且适合用于通告或绑定则为 <see langword="true"/>；否则为 <see langword="false"/>。
    /// </returns>
    /// <remarks>
    /// 应用的校验规则：
    /// <list type="bullet">
    ///   <item><description>null、空串或空白字符串会被拒绝。</description></item>
    ///   <item><description>校验前会移除 IPv6 scope 标识符（例如 fe80::1%12）。</description></item>
    ///   <item><description>未指定地址（<c>0.0.0.0</c> 或 <c>::</c>）会被拒绝。</description></item>
    ///   <item><description>回环地址（<c>127.0.0.1</c> 或 <c>::1</c>）会被拒绝。</description></item>
    ///   <item><description>IPv4 APIPA 地址 (169.254.x.x) 会被拒绝。</description></item>
    ///   <item><description>IPv4 组播与保留地址（224.0.0.0 及以上）会被拒绝。</description></item>
    ///   <item><description>IPv6 链路本地、站点本地与组播地址会被拒绝。</description></item>
    ///   <item><description>IPv6 地址必须是全局单播 (2000::/3) 才会被接受。</description></item>
    /// </list>
    /// </remarks>
    public static bool ValidateIP(string ip)
    {
        if (string.IsNullOrWhiteSpace(ip))
        {
            return false;
        }

        // 若存在则移除 IPv6 scope ID (fe80::1%12)
        var percentIndex = ip.IndexOf('%');
        if (percentIndex >= 0)
        {
            ip = ip[..percentIndex];
        }

        if (!IPAddress.TryParse(ip, out var address))
        {
            return false;
        }

        // 拒绝未指定地址
        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
        {
            return false;
        }

        // 拒绝回环地址
        if (IPAddress.IsLoopback(address))
        {
            return false;
        }

        switch (address.AddressFamily)
        {
            case AddressFamily.InterNetwork:
                {
                    var b = address.GetAddressBytes();

                    // APIPA 169.254.0.0/16
                    if (b[0] == 169 && b[1] == 254)
                    {
                        return false;
                    }

                    // 组播 / 保留地址 (224+)
                    if (b[0] >= 224)
                    {
                        return false;
                    }

                    return true;
                }

            case AddressFamily.InterNetworkV6:
                {
                    // 链路本地、站点本地、组播
                    if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast)
                    {
                        return false;
                    }

                    // 唯一本地地址 (fc00::/7) 是私有局域网网段在
                    // IPv6 中的对应物。虚拟局域网适配器（Radmin VPN、ZeroTier、
                    // Tailscale、easyN2N）会分配这类地址，该网络上的对端
                    // 通过它们直接互相可达，因此它们会被通告和绑定，
                    // 就像私有 IPv4 地址一样，而不是被丢弃。
                    var uniqueLocal = address.GetAddressBytes();
                    if ((uniqueLocal[0] & 0b1111_1100) == 0b1111_1100)
                    {
                        return true;
                    }

                    // 全局单播必须位于 2000::/3 之内
                    var b = address.GetAddressBytes();
                    return (b[0] & 0b1110_0000) == 0b0010_0000;
                }
        }

        return false;
    }

    /// <summary>
    /// 从字符串 IP 或主机名以及端口解析出一个远程地址。
    /// </summary>
    /// <param name="ip">IP 地址或主机名。</param>
    /// <param name="port">端口号。</param>
    /// <returns>解析得到的 <see cref="IPEndPoint"/>。</returns>
    /// <exception cref="ParseException">地址无法解析时抛出。</exception>
    public static IPEndPoint ResolveRemoteAddress(string ip, int port)
    {
        var resolved = NetUtils.ResolveAddress(ip);
        return new IPEndPoint(resolved, port);
    }

    /// <summary>
    /// 用于判断在 <see cref="IFikaNetworkManager"/> 上收到的是哪种数据包
    /// </summary>
    public enum EPacketType : byte
    {
        /// <summary>
        /// 一个实现了 <see cref="INetSerializable"/> 的数据包
        /// </summary>
        Serializable,
        /// <summary>
        /// 原始的 <see cref="Packets.Player.PlayerStateData"/>
        /// </summary>
        PlayerState,
        /// <summary>
        /// 原始的 <see cref="ShapshotBTRMessage"/>
        /// </summary>
        BTR,
        /// <summary>
        /// 一个包含原始字节数组的 voip 数据包
        /// </summary>
        VOIP
    }
}

using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace SlideColours.Services;

public record DmxNode(string Ip, string ShortName, string LongName)
{
    public string Label
    {
        get
        {
            string name = string.IsNullOrWhiteSpace(LongName) ? ShortName : LongName;
            return string.IsNullOrWhiteSpace(name) ? Ip : $"{Ip}  —  {name}";
        }
    }
}

/// <summary>
/// Finds DMX nodes using standard Art-Net discovery: broadcast an ArtPoll on every
/// network interface and collect the ArtPollReply packets nodes send back.
/// (Most sACN hardware also speaks Art-Net, so this finds those nodes too.)
/// </summary>
public static class ArtNetDiscovery
{
    private const int ArtNetPort = 6454;

    public static async Task<List<DmxNode>> DiscoverAsync(int timeoutMs = 2500)
    {
        var found = new Dictionary<string, DmxNode>();

        using var udp = CreateSocket();
        byte[] poll = BuildArtPoll();
        foreach (var broadcast in GetBroadcastAddresses().Distinct())
        {
            try { await udp.SendAsync(poll, poll.Length, new IPEndPoint(broadcast, ArtNetPort)); }
            catch { /* unreachable interface — keep trying the others */ }
        }

        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            using var cts = new CancellationTokenSource(deadline - DateTime.UtcNow);
            try
            {
                var result = await udp.ReceiveAsync(cts.Token);
                if (ParseReply(result.Buffer, result.RemoteEndPoint) is { } node)
                    found[node.Ip] = node;
            }
            catch (OperationCanceledException) { break; }
            catch { /* malformed packet — ignore */ }
        }

        return found.Values
            .OrderBy(n => Version.TryParse(n.Ip, out var v) ? v : new Version(255, 255, 255, 255))
            .ToList();
    }

    private static UdpClient CreateSocket()
    {
        // Nodes broadcast their reply to port 6454, so listen there — shared, since
        // other Art-Net software (or our own sender) may be around.
        var udp = new UdpClient();
        udp.EnableBroadcast = true;
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        try
        {
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, ArtNetPort));
        }
        catch (SocketException)
        {
            // port held exclusively by another app — an ephemeral port still catches
            // nodes that unicast their reply to the poll's source address
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        }
        return udp;
    }

    private static byte[] BuildArtPoll()
    {
        var p = new byte[14];
        Encoding.ASCII.GetBytes("Art-Net\0").CopyTo(p, 0);
        p[8] = 0x00; p[9] = 0x20;   // OpPoll (little-endian)
        p[10] = 0x00; p[11] = 14;   // protocol version
        p[12] = 0x00;               // talk-to-me: reply once
        p[13] = 0x00;               // priority
        return p;
    }

    private static DmxNode? ParseReply(byte[] d, IPEndPoint from)
    {
        if (d.Length < 26 || Encoding.ASCII.GetString(d, 0, 8) != "Art-Net\0")
            return null;
        if (d[8] != 0x00 || d[9] != 0x21) // OpPollReply
            return null;

        var ip = new IPAddress(new[] { d[10], d[11], d[12], d[13] });
        if (ip.Equals(IPAddress.Any))
            ip = from.Address;

        return new DmxNode(ip.ToString(), ReadString(d, 26, 18), ReadString(d, 44, 64));
    }

    private static string ReadString(byte[] d, int offset, int length)
    {
        if (offset >= d.Length)
            return "";
        length = Math.Min(length, d.Length - offset);
        int end = Array.IndexOf(d, (byte)0, offset, length);
        if (end < 0) end = offset + length;
        return Encoding.ASCII.GetString(d, offset, end - offset).Trim();
    }

    private static IEnumerable<IPAddress> GetBroadcastAddresses()
    {
        yield return IPAddress.Broadcast;

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up
                || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;

            foreach (var addr in nic.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily != AddressFamily.InterNetwork || addr.IPv4Mask == null)
                    continue;
                byte[] ip = addr.Address.GetAddressBytes();
                byte[] mask = addr.IPv4Mask.GetAddressBytes();
                var broadcast = new byte[4];
                for (int i = 0; i < 4; i++)
                    broadcast[i] = (byte)(ip[i] | ~mask[i]);
                yield return new IPAddress(broadcast);
            }
        }
    }
}

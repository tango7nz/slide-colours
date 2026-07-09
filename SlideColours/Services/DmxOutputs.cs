using System.IO.Ports;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace SlideColours.Services;

/// <summary>Transmits a full 512-channel DMX frame to some kind of DMX hardware.</summary>
public interface IDmxOutput : IDisposable
{
    void SendFrame(byte[] dmx512);
}

/// <summary>Art-Net (ArtDMX) over UDP port 6454. Blank IP broadcasts to the local network.</summary>
public sealed class ArtNetOutput : IDmxOutput
{
    private readonly UdpClient _udp = new();
    private readonly IPEndPoint _dest;
    private readonly byte[] _packet = new byte[18 + 512];
    private byte _sequence = 1;

    public ArtNetOutput(string targetIp, int universe)
    {
        _udp.EnableBroadcast = true;
        var addr = string.IsNullOrWhiteSpace(targetIp) ? IPAddress.Broadcast : IPAddress.Parse(targetIp.Trim());
        _dest = new IPEndPoint(addr, 6454);

        Encoding.ASCII.GetBytes("Art-Net\0").CopyTo(_packet, 0);
        _packet[8] = 0x00; _packet[9] = 0x50;                  // OpDmx (little-endian)
        _packet[10] = 0x00; _packet[11] = 14;                  // protocol version
        _packet[13] = 0x00;                                    // physical port
        _packet[14] = (byte)(universe & 0xFF);                 // sub-uni
        _packet[15] = (byte)((universe >> 8) & 0x7F);          // net
        _packet[16] = 0x02; _packet[17] = 0x00;                // data length 512 (big-endian)
    }

    public void SendFrame(byte[] dmx512)
    {
        _packet[12] = _sequence;
        _sequence = (byte)(_sequence == 255 ? 1 : _sequence + 1);
        Buffer.BlockCopy(dmx512, 0, _packet, 18, 512);
        _udp.Send(_packet, _packet.Length, _dest);
    }

    public void Dispose() => _udp.Dispose();
}

/// <summary>sACN / E1.31 over UDP port 5568. Blank IP multicasts to 239.255.x.x for the universe.</summary>
public sealed class SacnOutput : IDmxOutput
{
    private readonly UdpClient _udp = new();
    private readonly IPEndPoint _dest;
    private readonly byte[] _packet = new byte[638];
    private byte _sequence;

    public SacnOutput(string targetIp, int universe)
    {
        if (universe < 1 || universe > 63999)
            throw new ArgumentException("sACN universe must be 1-63999");

        var addr = string.IsNullOrWhiteSpace(targetIp)
            ? new IPAddress(new byte[] { 239, 255, (byte)(universe >> 8), (byte)(universe & 0xFF) })
            : IPAddress.Parse(targetIp.Trim());
        _dest = new IPEndPoint(addr, 5568);

        var p = _packet;
        // Root layer
        p[0] = 0x00; p[1] = 0x10;                              // preamble size
        p[2] = 0x00; p[3] = 0x00;                              // post-amble size
        Encoding.ASCII.GetBytes("ASC-E1.17\0\0\0").CopyTo(p, 4);
        WriteFlagsLength(p, 16, 638 - 16);
        p[18] = 0x00; p[19] = 0x00; p[20] = 0x00; p[21] = 0x04; // vector: E1.31 data
        Guid.NewGuid().ToByteArray().CopyTo(p, 22);            // CID

        // Framing layer
        WriteFlagsLength(p, 38, 638 - 38);
        p[40] = 0x00; p[41] = 0x00; p[42] = 0x00; p[43] = 0x02; // vector: DMP
        Encoding.ASCII.GetBytes("SlideColours").CopyTo(p, 44); // source name (64 bytes, zero-padded)
        p[108] = 100;                                          // priority
        p[113] = (byte)(universe >> 8);
        p[114] = (byte)(universe & 0xFF);

        // DMP layer
        WriteFlagsLength(p, 115, 638 - 115);
        p[117] = 0x02;                                         // vector: set property
        p[118] = 0xA1;                                         // address & data type
        p[119] = 0x00; p[120] = 0x00;                          // first property address
        p[121] = 0x00; p[122] = 0x01;                          // address increment
        p[123] = 0x02; p[124] = 0x01;                          // property value count: 513
        p[125] = 0x00;                                         // DMX start code
    }

    private static void WriteFlagsLength(byte[] p, int offset, int length)
    {
        p[offset] = (byte)(0x70 | ((length >> 8) & 0x0F));
        p[offset + 1] = (byte)(length & 0xFF);
    }

    public void SendFrame(byte[] dmx512)
    {
        _packet[111] = _sequence++;
        Buffer.BlockCopy(dmx512, 0, _packet, 126, 512);
        _udp.Send(_packet, _packet.Length, _dest);
    }

    public void Dispose() => _udp.Dispose();
}

/// <summary>Enttec DMX USB Pro (and compatibles) on a serial COM port.</summary>
public sealed class EnttecProOutput : IDmxOutput
{
    private readonly SerialPort _port;
    private readonly byte[] _message = new byte[4 + 513 + 1];

    public EnttecProOutput(string comPort)
    {
        _port = new SerialPort(comPort, 115200); // baud rate is ignored by the USB Pro
        _port.Open();

        _message[0] = 0x7E;                       // start of message
        _message[1] = 0x06;                       // label: output only send DMX
        _message[2] = 513 & 0xFF;                 // data length LSB (start code + 512)
        _message[3] = 513 >> 8;                   // data length MSB
        _message[4] = 0x00;                       // DMX start code
        _message[^1] = 0xE7;                      // end of message
    }

    public void SendFrame(byte[] dmx512)
    {
        Buffer.BlockCopy(dmx512, 0, _message, 5, 512);
        _port.Write(_message, 0, _message.Length);
    }

    public void Dispose()
    {
        try { _port.Close(); } catch { }
        _port.Dispose();
    }
}

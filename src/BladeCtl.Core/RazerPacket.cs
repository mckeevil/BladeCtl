namespace BladeCtl.Core;

/// <summary>
/// The 90-byte Razer control packet, exchanged as a HID feature report
/// (prefixed with report ID 0x00 => 91-byte buffer on the wire).
///
/// Layout (verified against OpenRazer driver/razercommon.h and
/// librazerblade BladeStructs.h, both identical):
///   [0]  status        (0x00 new command; response: 0x02 = success)
///   [1]  transaction_id (0x1F for Blade system cmds, 0xFF for chroma cmds on this model)
///   [2-3] remaining_packets (always 0)
///   [4]  protocol_type (always 0)
///   [5]  data_size
///   [6]  command_class
///   [7]  command_id    (bit 7 set = GET/device-to-host)
///   [8-87] args[80]
///   [88] crc = XOR of packet bytes 2..87  (OpenRazer razer_calculate_crc)
///   [89] reserved (0)
/// </summary>
public sealed class RazerPacket
{
    public const int PacketLen = 90;
    public const int BufferLen = 91; // + leading HID report ID byte (0x00)

    public byte Status;
    public byte TransactionId;
    public ushort RemainingPackets;
    public byte ProtocolType;
    public byte DataSize;
    public byte CommandClass;
    public byte CommandId;
    public byte[] Args = new byte[80];
    public byte Crc;
    public byte Reserved;

    public static class StatusCode
    {
        public const byte New = 0x00;
        public const byte Busy = 0x01;
        public const byte Success = 0x02;
        public const byte Failure = 0x03;
        public const byte Timeout = 0x04;
        public const byte NotSupported = 0x05;
    }

    public static RazerPacket Create(byte transactionId, byte commandClass, byte commandId, byte dataSize, params byte[] args)
    {
        var p = new RazerPacket
        {
            Status = StatusCode.New,
            TransactionId = transactionId,
            CommandClass = commandClass,
            CommandId = commandId,
            DataSize = dataSize,
        };
        if (args.Length > 80) throw new ArgumentException("args too long");
        Array.Copy(args, p.Args, args.Length);
        return p;
    }

    /// <summary>
    /// Serialize to the 91-byte feature-report buffer (report id + 90-byte packet), computing CRC.
    /// Most Razer devices use report id 0x00; the Leviathan V2 X uses 0x07 (found 2026-09-05).
    /// </summary>
    public byte[] ToBuffer(byte reportId = 0x00)
    {
        var b = new byte[BufferLen];
        b[0] = reportId;                   // HID report ID
        b[1] = Status;
        b[2] = TransactionId;
        b[3] = (byte)(RemainingPackets >> 8);
        b[4] = (byte)(RemainingPackets & 0xFF);
        b[5] = ProtocolType;
        b[6] = DataSize;
        b[7] = CommandClass;
        b[8] = CommandId;
        Array.Copy(Args, 0, b, 9, 80);
        // CRC: XOR of packet bytes 2..87 inclusive == buffer bytes 3..88 inclusive.
        byte crc = 0;
        for (int i = 3; i <= 88; i++) crc ^= b[i];
        b[89] = crc;
        b[90] = 0x00;
        return b;
    }

    /// <summary>Parse a 91-byte feature-report buffer received from the device.</summary>
    public static RazerPacket FromBuffer(byte[] b)
    {
        if (b.Length < BufferLen) throw new ArgumentException($"buffer too short: {b.Length}");
        var p = new RazerPacket
        {
            Status = b[1],
            TransactionId = b[2],
            RemainingPackets = (ushort)((b[3] << 8) | b[4]),
            ProtocolType = b[5],
            DataSize = b[6],
            CommandClass = b[7],
            CommandId = b[8],
            Crc = b[89],
            Reserved = b[90],
        };
        Array.Copy(b, 9, p.Args, 0, 80);
        return p;
    }

    public string StatusName => Status switch
    {
        StatusCode.New => "NEW",
        StatusCode.Busy => "BUSY",
        StatusCode.Success => "SUCCESS",
        StatusCode.Failure => "FAILURE",
        StatusCode.Timeout => "TIMEOUT",
        StatusCode.NotSupported => "NOT_SUPPORTED",
        _ => $"0x{Status:X2}",
    };

    public override string ToString() =>
        $"status={StatusName} tid=0x{TransactionId:X2} class=0x{CommandClass:X2} cmd=0x{CommandId:X2} ds={DataSize} args[0..7]=[{string.Join(" ", Args[..8].Select(x => x.ToString("X2")))}]";
}

using System.Net.Sockets;

namespace InverterMonitor;

public sealed class ModbusRtuOverTcpClient
{
    public async Task<IReadOnlyDictionary<int, ushort>> ReadHoldingRegistersAsync(
        MonitorSettings settings,
        InverterDefinition definition,
        CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(settings.GatewayHost, settings.GatewayPort, cancellationToken);
        client.ReceiveTimeout = 1500;
        client.SendTimeout = 1500;

        await using var stream = client.GetStream();
        var values = new Dictionary<int, ushort>();

        foreach (var block in RegisterBlock.Create(
                     definition.Registers.Select(d => d.Address),
                     definition.ReadBehavior.MaxBlockSize,
                     definition.ReadBehavior.MaxGapWithinBlock))
        {
            var response = await ReadBlockAsync(stream, settings.SlaveId, block.Start, block.Count, cancellationToken);
            for (var i = 0; i < response.Length; i++)
            {
                values[block.Start + i] = response[i];
            }

            await Task.Delay(definition.ReadBehavior.DelayBetweenBlocksMs, cancellationToken);
        }

        return values;
    }

    private static async Task<ushort[]> ReadBlockAsync(
        NetworkStream stream,
        byte slaveId,
        int start,
        int count,
        CancellationToken cancellationToken)
    {
        var payload = new byte[]
        {
            slaveId,
            3,
            (byte)(start >> 8),
            (byte)start,
            (byte)(count >> 8),
            (byte)count
        };

        var request = AddCrc(payload);
        await stream.WriteAsync(request, cancellationToken);
        await stream.FlushAsync(cancellationToken);

        var expectedLength = 5 + count * 2;
        var buffer = new byte[expectedLength + 16];
        var total = 0;
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(900);

        while (DateTimeOffset.UtcNow < deadline && total < expectedLength)
        {
            if (stream.DataAvailable)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken);
                if (read == 0)
                {
                    break;
                }

                total += read;
                deadline = DateTimeOffset.UtcNow.AddMilliseconds(120);
            }
            else
            {
                await Task.Delay(10, cancellationToken);
            }
        }

        var frame = buffer.AsSpan(0, total).ToArray();
        if (frame.Length < 5 || !HasValidCrc(frame))
        {
            throw new InvalidOperationException($"Invalid or empty Modbus response for 0x{start:X4}: {Convert.ToHexString(frame)}");
        }

        if ((frame[1] & 0x80) != 0)
        {
            throw new InvalidOperationException($"Modbus exception for 0x{start:X4}: {frame[2]}");
        }

        var byteCount = frame[2];
        var result = new ushort[byteCount / 2];
        for (var i = 0; i < result.Length; i++)
        {
            var offset = 3 + i * 2;
            result[i] = (ushort)((frame[offset] << 8) | frame[offset + 1]);
        }

        return result;
    }

    private static byte[] AddCrc(byte[] payload)
    {
        var crc = CalculateCrc(payload);
        var frame = new byte[payload.Length + 2];
        payload.CopyTo(frame, 0);
        frame[^2] = (byte)(crc & 0xFF);
        frame[^1] = (byte)(crc >> 8);
        return frame;
    }

    private static bool HasValidCrc(byte[] frame)
    {
        var expected = (ushort)(frame[^2] | frame[^1] << 8);
        return CalculateCrc(frame.AsSpan(0, frame.Length - 2)) == expected;
    }

    private static ushort CalculateCrc(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFF;
        foreach (var value in data)
        {
            crc ^= value;
            for (var i = 0; i < 8; i++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xA001 : crc >> 1;
                crc &= 0xFFFF;
            }
        }

        return (ushort)crc;
    }

    private sealed record RegisterBlock(int Start, int Count)
    {
        public static IReadOnlyList<RegisterBlock> Create(IEnumerable<int> addresses, int maxBlockSize, int maxGapWithinBlock)
        {
            var sorted = addresses.Distinct().Order().ToArray();
            var blocks = new List<RegisterBlock>();
            var i = 0;

            while (i < sorted.Length)
            {
                var start = sorted[i];
                var end = start;
                i++;

                while (i < sorted.Length && sorted[i] - start < maxBlockSize && sorted[i] - end <= maxGapWithinBlock)
                {
                    end = sorted[i];
                    i++;
                }

                blocks.Add(new RegisterBlock(start, end - start + 1));
            }

            return blocks;
        }
    }
}

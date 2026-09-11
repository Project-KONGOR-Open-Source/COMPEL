namespace COMPEL.Tests.Services.Proxy;

/// <summary>
///     Verifies that the test double accurately enforces the native client's challenge acceptance rules: destination keying and timestamp monotonicity.
/// </summary>
public sealed class ClientChallengeStoreDoubleTests
{
    private static readonly IPEndPoint FirstDestination = new (IPAddress.Parse("127.0.0.1"), 20000);
    private static readonly IPEndPoint SecondDestination = new (IPAddress.Parse("127.0.0.1"), 20001);

    [Test]
    public async Task Initial_Challenge_Packet_Is_Accepted_By_Store()
    {
        ClientChallengeStoreDouble store = new ();
        byte[] packet = BuildChallengePacket(timestamp: 1000, value: 55555, packetQuota: 100);

        bool processed = store.TryProcessChallengePacket(FirstDestination, packet);
        bool found = store.TryGetChallenge(FirstDestination, out uint value, out ushort quota);

        using (Assert.Multiple())
        {
            await Assert.That(processed).IsTrue();
            await Assert.That(found).IsTrue();
            await Assert.That(value).IsEqualTo(55555u);
            await Assert.That(quota).IsEqualTo((ushort)100);
        }
    }

    [Test]
    public async Task Challenge_Packet_With_Same_Or_Older_Timestamp_Is_Rejected()
    {
        ClientChallengeStoreDouble store = new ();
        byte[] firstPacket = BuildChallengePacket(timestamp: 1000, value: 11111, packetQuota: 100);
        byte[] sameTimestampPacket = BuildChallengePacket(timestamp: 1000, value: 22222, packetQuota: 100);
        byte[] olderTimestampPacket = BuildChallengePacket(timestamp: 999, value: 33333, packetQuota: 100);

        store.TryProcessChallengePacket(FirstDestination, firstPacket);

        bool sameProcessed = store.TryProcessChallengePacket(FirstDestination, sameTimestampPacket);
        bool olderProcessed = store.TryProcessChallengePacket(FirstDestination, olderTimestampPacket);

        store.TryGetChallenge(FirstDestination, out uint currentHeld, out _);

        using (Assert.Multiple())
        {
            await Assert.That(sameProcessed).IsFalse();
            await Assert.That(olderProcessed).IsFalse();
            await Assert.That(currentHeld).IsEqualTo(11111u);
        }
    }

    [Test]
    public async Task Challenge_Packet_With_Greater_Timestamp_Replaces_Held_Challenge()
    {
        ClientChallengeStoreDouble store = new ();
        byte[] initialPacket = BuildChallengePacket(timestamp: 1000, value: 11111, packetQuota: 100);
        byte[] updatedPacket = BuildChallengePacket(timestamp: 1001, value: 22222, packetQuota: 100);

        store.TryProcessChallengePacket(FirstDestination, initialPacket);
        bool updated = store.TryProcessChallengePacket(FirstDestination, updatedPacket);

        store.TryGetChallenge(FirstDestination, out uint currentHeld, out _);

        using (Assert.Multiple())
        {
            await Assert.That(updated).IsTrue();
            await Assert.That(currentHeld).IsEqualTo(22222u);
        }
    }

    [Test]
    public async Task Challenges_Are_Stored_Per_Destination_Endpoint()
    {
        ClientChallengeStoreDouble store = new ();
        byte[] firstPacket = BuildChallengePacket(timestamp: 1000, value: 11111, packetQuota: 100);
        byte[] secondPacket = BuildChallengePacket(timestamp: 1000, value: 22222, packetQuota: 100);

        store.TryProcessChallengePacket(FirstDestination, firstPacket);
        store.TryProcessChallengePacket(SecondDestination, secondPacket);

        store.TryGetChallenge(FirstDestination, out uint firstValue, out _);
        store.TryGetChallenge(SecondDestination, out uint secondValue, out _);

        using (Assert.Multiple())
        {
            await Assert.That(firstValue).IsEqualTo(11111u);
            await Assert.That(secondValue).IsEqualTo(22222u);
        }
    }

    private static byte[] BuildChallengePacket(uint timestamp, uint value, ushort packetQuota)
    {
        byte[] packet = new byte[58];
        Span<byte> payload = packet.AsSpan(40);

        payload[0] = 0xFF;
        payload[1] = 0xFF;
        payload[2] = 0x40; // ProxyFlag
        payload[3] = 0x00; // ChallengeType

        BinaryPrimitives.WriteUInt32LittleEndian(payload[4..], timestamp);
        BinaryPrimitives.WriteUInt16LittleEndian(payload[8..], 60);
        BinaryPrimitives.WriteUInt16LittleEndian(payload[10..], packetQuota);
        BinaryPrimitives.WriteUInt16LittleEndian(payload[12..], 100);
        BinaryPrimitives.WriteUInt32LittleEndian(payload[14..], value);

        return packet;
    }
}

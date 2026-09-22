using System.Reflection;
using DotNetty.Transport.Channels;
using DotNetty.Transport.Channels.Embedded;
using GameServer.Network;
using GameServer.Protocol;
using Xunit;

namespace GameServer.Tests;

/// <summary>
/// SessionComponent 패킷 큐 상한 검증.
/// BUG-1 수정: 미인증 상태에서 패킷 큐가 무제한 증가하여 OOM 위험이 있던 문제 해결.
/// 큐 크기가 MaxPacketQueueSize를 초과하면 ProcessPacket이 false를 반환하여 연결을 종료한다.
/// </summary>
public class SessionComponentTests
{
    private const int MaxPacketQueueSize = 200;

    private static readonly FieldInfo PacketQueueCountField =
        typeof(SessionComponent).GetField("_packetQueueCount",
            BindingFlags.NonPublic | BindingFlags.Instance)!;

    [Fact]
    public void ProcessPacket_AcceptsPacket_WhenUnderLimit()
    {
        var channel = new EmbeddedChannel();
        var session = new SessionComponent(channel);

        session.SetEntryHandshakeCompleted();

        var packet = new GamePacket { ReqMove = new ReqMove { X = 10, Y = 20 } };
        var result = session.ProcessPacket(packet);

        Assert.True(result, "큐가 비어있으면 패킷을 수락해야 함");

        var queueCount = (int)PacketQueueCountField.GetValue(session)!;
        Assert.Equal(1, queueCount);
    }

    [Fact]
    public void ProcessPacket_RejectsPacket_WhenOverLimit()
    {
        var channel = new EmbeddedChannel();
        var session = new SessionComponent(channel);

        session.SetEntryHandshakeCompleted();

        for (int i = 0; i < MaxPacketQueueSize; i++)
        {
            var packet = new GamePacket { ReqMove = new ReqMove { X = i, Y = i } };
            var result = session.ProcessPacket(packet);
            Assert.True(result, $"패킷 {i}는 상한 이내이므로 수락되어야 함");
        }

        var overLimitPacket = new GamePacket { ReqMove = new ReqMove { X = 999, Y = 999 } };
        var overLimitResult = session.ProcessPacket(overLimitPacket);

        Assert.False(overLimitResult, "큐 상한 초과 시 패킷을 거부해야 함");

        var queueCount = (int)PacketQueueCountField.GetValue(session)!;
        Assert.Equal(MaxPacketQueueSize, queueCount);
    }

    [Fact]
    public void DrainPackets_DecrementsQueueCount()
    {
        var channel = new EmbeddedChannel();
        var session = new SessionComponent(channel);

        session.SetEntryHandshakeCompleted();

        int handledCount = 0;
        session.PacketHandler = _ => handledCount++;

        const int packetCount = 10;
        for (int i = 0; i < packetCount; i++)
        {
            var packet = new GamePacket { ReqMove = new ReqMove { X = i, Y = i } };
            session.ProcessPacket(packet);
        }

        var queueCountBefore = (int)PacketQueueCountField.GetValue(session)!;
        Assert.Equal(packetCount, queueCountBefore);

        session.DrainPackets();

        var queueCountAfter = (int)PacketQueueCountField.GetValue(session)!;
        Assert.Equal(0, queueCountAfter);
        Assert.Equal(packetCount, handledCount);
    }

    [Fact]
    public void ClearPacketQueue_ResetsQueueCount()
    {
        var channel = new EmbeddedChannel();
        var session = new SessionComponent(channel);

        session.SetEntryHandshakeCompleted();

        for (int i = 0; i < 5; i++)
        {
            var packet = new GamePacket { ReqMove = new ReqMove { X = i, Y = i } };
            session.ProcessPacket(packet);
        }

        var queueCountBefore = (int)PacketQueueCountField.GetValue(session)!;
        Assert.Equal(5, queueCountBefore);

        session.ClearPacketQueue();

        var queueCountAfter = (int)PacketQueueCountField.GetValue(session)!;
        Assert.Equal(0, queueCountAfter);
    }

    [Fact]
    public void ProcessPacket_AcceptsNewPackets_AfterDrain()
    {
        var channel = new EmbeddedChannel();
        var session = new SessionComponent(channel);

        session.SetEntryHandshakeCompleted();

        session.PacketHandler = _ => { };

        for (int i = 0; i < MaxPacketQueueSize; i++)
        {
            var packet = new GamePacket { ReqMove = new ReqMove { X = i, Y = i } };
            session.ProcessPacket(packet);
        }

        session.DrainPackets();

        var newPacket = new GamePacket { ReqMove = new ReqMove { X = 100, Y = 100 } };
        var result = session.ProcessPacket(newPacket);

        Assert.True(result, "드레인 후 새 패킷을 수락해야 함");

        var queueCount = (int)PacketQueueCountField.GetValue(session)!;
        Assert.Equal(1, queueCount);
    }
}

using System.Reflection;
using DotNetty.Transport.Channels;
using DotNetty.Transport.Channels.Embedded;
using GameServer.Network;
using GameServer.Network.Policies;
using GameServer.Protocol;
using Xunit;

namespace GameServer.Tests;

/// <summary>
/// SessionComponent 패킷 큐 상한 검증.
/// BUG-1 수정: 미인증 상태에서 패킷 큐가 무제한 증가하여 OOM 위험이 있던 문제 해결.
/// 큐 크기가 MaxPacketQueueSize를 초과하면 ProcessPacket이 false를 반환하여 연결을 종료한다.
///
/// 테스트 전략:
/// - PacketPairPolicy는 타입별 큐 대기 상한이 작음(ReqMove=5, 대부분=1)
/// - 큐 상한(200) 테스트는 reflection으로 _packetPolicies를 빈 배열로 우회하여 순수 상한만 검증
/// - Drain/Clear 테스트는 정책 범위 내(ReqMove≤5)에서 동작 확인
/// </summary>
public class SessionComponentTests
{
    private const int MaxPacketQueueSize = 200;

    private static readonly FieldInfo PacketQueueCountField =
        typeof(SessionComponent).GetField("_packetQueueCount",
            BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static readonly FieldInfo PacketPoliciesField =
        typeof(SessionComponent).GetField("_packetPolicies",
            BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static void DisablePacketPolicies(SessionComponent session)
    {
        // 정책 우회: 큐 상한만 순수 검증하기 위해 빈 배열로 교체
        PacketPoliciesField.SetValue(session, Array.Empty<IPacketPolicy>());
    }

    [Fact]
    public void ProcessPacket_AcceptsPacket_WhenUnderLimit()
    {
        var channel = new EmbeddedChannel();
        var session = new SessionComponent(channel);

        session.SetEntryHandshakeCompleted();

        var packet = new GamePacket { ReqMove = new ReqMove { Seq = 1, Flags = 0, DtMs = 100 } };
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
        DisablePacketPolicies(session);

        for (int i = 0; i < MaxPacketQueueSize; i++)
        {
            var packet = new GamePacket { ReqMove = new ReqMove { Seq = (uint)i, Flags = 0, DtMs = 100 } };
            var result = session.ProcessPacket(packet);
            Assert.True(result, $"패킷 {i}는 상한 이내이므로 수락되어야 함");
        }

        var queueCountBefore = (int)PacketQueueCountField.GetValue(session)!;
        Assert.Equal(MaxPacketQueueSize, queueCountBefore);

        var overLimitPacket = new GamePacket { ReqMove = new ReqMove { Seq = 999, Flags = 0, DtMs = 100 } };
        var overLimitResult = session.ProcessPacket(overLimitPacket);

        Assert.False(overLimitResult, "큐 상한 초과 시 패킷을 거부해야 함");

        var queueCountAfter = (int)PacketQueueCountField.GetValue(session)!;
        Assert.Equal(MaxPacketQueueSize, queueCountAfter);
    }

    [Fact]
    public void DrainPackets_DecrementsQueueCount()
    {
        var channel = new EmbeddedChannel();
        var session = new SessionComponent(channel);

        session.SetEntryHandshakeCompleted();

        int handledCount = 0;
        session.PacketHandler = _ => handledCount++;

        const int packetCount = 5;
        for (int i = 0; i < packetCount; i++)
        {
            var packet = new GamePacket { ReqMove = new ReqMove { Seq = (uint)i, Flags = 0, DtMs = 100 } };
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

        const int packetCount = 5;
        for (int i = 0; i < packetCount; i++)
        {
            var packet = new GamePacket { ReqMove = new ReqMove { Seq = (uint)i, Flags = 0, DtMs = 100 } };
            session.ProcessPacket(packet);
        }

        var queueCountBefore = (int)PacketQueueCountField.GetValue(session)!;
        Assert.Equal(packetCount, queueCountBefore);

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

        const int packetCount = 5;
        for (int i = 0; i < packetCount; i++)
        {
            var packet = new GamePacket { ReqMove = new ReqMove { Seq = (uint)i, Flags = 0, DtMs = 100 } };
            session.ProcessPacket(packet);
        }

        var queueCountBefore = (int)PacketQueueCountField.GetValue(session)!;
        Assert.Equal(packetCount, queueCountBefore);

        session.DrainPackets();

        var queueCountAfterDrain = (int)PacketQueueCountField.GetValue(session)!;
        Assert.Equal(0, queueCountAfterDrain);

        var newPacket = new GamePacket { ReqMove = new ReqMove { Seq = 100, Flags = 0, DtMs = 100 } };
        var result = session.ProcessPacket(newPacket);

        Assert.True(result, "드레인 후 새 패킷을 수락해야 함");

        var queueCountFinal = (int)PacketQueueCountField.GetValue(session)!;
        Assert.Equal(1, queueCountFinal);
    }
}

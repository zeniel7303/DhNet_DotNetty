using System.Collections.Concurrent;
using Common.Logging;
using DotNetty.Transport.Channels;
using GameServer.Component.Player;
using GameServer.Network.Policies;
using GameServer.Protocol;

namespace GameServer.Network;

public class SessionComponent : IDisposable
{
    private static long _idCounter;

    public long InstanceId { get; } = Interlocked.Increment(ref _idCounter);
    public IChannel Channel { get; }
    public bool IsConnected => Channel.Active;

    public PlayerComponent? Player { get; private set; }

    private readonly ConcurrentQueue<GamePacket> _packetQueue = new();
    private int _disposed;
    private int _disconnectedFlag;
    private int _entryHandshakeCompleted;
    private int _loginStarted;
    private int _registerStarted;

    public SessionComponent(IChannel channel)
    {
        Channel = channel;
        _packetPolicies =
        [
            PacketPairPolicy.Create(t => _packetQueue.Count(p => p.PayloadCase == t)),
            PacketRatePolicy.Create(),
        ];
    }

    // 인증 후 패킷 처리 콜백 — PlayerComponent.Initialize()에서 등록, DetachPlayer()에서 해제
    public Action<GamePacket>? PacketHandler { get; set; }

    // 패킷 정책 배열 — 수신 시 VerifyPolicy, 처리 후 UpdatePolicy 순서로 실행
    private readonly IPacketPolicy[] _packetPolicies;

    public void AttachPlayer(PlayerComponent player) => Player = player;

    public void DetachPlayer()
    {
        Player = null;
        PacketHandler = null;
    }

    // 모든 정책을 순서대로 검증한다.
    // 반환값: true = 적재 성공, false = 정책 위반 (호출자가 연결 종료 및 로그 책임)
    // I/O 이벤트 루프 단일 스레드에서만 호출됨
    public bool ProcessPacket(GamePacket packet)
    {
        var type = packet.PayloadCase;
        foreach (var policy in _packetPolicies)
        {
            var result = policy.VerifyPolicy(type);
            if (!result.Result)
            {
                GameLogger.Warn("SessionComponent", $"패킷 정책 위반 [{result}]");
                return false;
            }
        }
        _packetQueue.Enqueue(packet);
        return true;
    }

    // 워커 틱(100ms)마다 PlayerComponent.Update()에서 호출
    // 패킷 처리 후 모든 정책의 UpdatePolicy를 실행하여 상태를 갱신한다
    public void DrainPackets()
    {
        while (_packetQueue.TryDequeue(out var packet))
        {
            PacketHandler?.Invoke(packet);

            var type = packet.PayloadCase;
            foreach (var policy in _packetPolicies)
            {
                policy.UpdatePolicy(type);
            }
        }
    }

    public Task SendAsync(GamePacket packet) => Channel.WriteAndFlushAsync(packet);

    public bool IsDisconnected => _disconnectedFlag == 1;
    public void SetDisconnectedFlag() => Interlocked.Exchange(ref _disconnectedFlag, 1);

    public bool IsEntryHandshakeCompleted => _entryHandshakeCompleted == 1;
    public void SetEntryHandshakeCompleted() => Interlocked.Exchange(ref _entryHandshakeCompleted, 1);

    // ReqLogin 중복 전송 시 LoginProcessor 병렬 실행 방지
    // 첫 번째 호출만 true 반환 — 이후 호출은 false (로그인 이미 시작됨)
    public bool TrySetLoginStarted() => Interlocked.CompareExchange(ref _loginStarted, 1, 0) == 0;

    // ReqRegister 중복 전송 시 RegisterProcessor 병렬 실행 방지
    public bool TrySetRegisterStarted() => Interlocked.CompareExchange(ref _registerStarted, 1, 0) == 0;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var policy in _packetPolicies)
            policy.Clear();

        // TaskScheduler.Default 필수: 명시하지 않으면 TaskScheduler.Current를 상속하여
        // I/O 이벤트 루프 스레드에 continuation이 예약될 수 있다.
        // CloseAsync() 완료도 같은 I/O 스레드가 담당하므로 데드락 위험이 있음.
        // Default(ThreadPool)를 명시하면 I/O 스레드와 분리되어 안전하다.
        _ = Channel.CloseAsync().ContinueWith(
            t => GameLogger.Error("SessionComponent", "CloseAsync 실패", t.Exception?.InnerException),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }
}

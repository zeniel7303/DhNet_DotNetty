using System.Collections.Concurrent;
using Common.Logging;
using DotNetty.Transport.Channels;
using GameServer.Component.Player;
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

    public SessionComponent(IChannel channel)
    {
        Channel = channel;
    }

    public void AttachPlayer(PlayerComponent player) => Player = player;
    public void DetachPlayer() => Player = null;

    public void EnqueuePacket(GamePacket packet) => _packetQueue.Enqueue(packet);
    public bool TryDequeuePacket(out GamePacket? packet) => _packetQueue.TryDequeue(out packet);
    public Task SendAsync(GamePacket packet) => Channel.WriteAndFlushAsync(packet);

    public bool IsDisconnected => _disconnectedFlag == 1;
    public void SetDisconnectedFlag() => Interlocked.Exchange(ref _disconnectedFlag, 1);

    public bool IsEntryHandshakeCompleted => _entryHandshakeCompleted == 1;
    public void SetEntryHandshakeCompleted() => Interlocked.Exchange(ref _entryHandshakeCompleted, 1);

    // ReqLogin 중복 전송 시 LoginProcessor 병렬 실행 방지
    // 첫 번째 호출만 true 반환 — 이후 호출은 false (로그인 이미 시작됨)
    public bool TrySetLoginStarted() => Interlocked.CompareExchange(ref _loginStarted, 1, 0) == 0;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _ = Channel.CloseAsync().ContinueWith(
            t => GameLogger.Error("SessionComponent", "CloseAsync 실패", t.Exception?.InnerException),
            TaskContinuationOptions.OnlyOnFaulted);
    }
}

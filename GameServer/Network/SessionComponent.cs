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
    private TaskCompletionSource? _pendingTcs;

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

    // HandleAsync에서 PlayerGameEnter await 직전에 등록, finally에서 null로 해제
    internal void SetPendingTcs(TaskCompletionSource? tcs) => Interlocked.Exchange(ref _pendingTcs, tcs);

    // InternalDisconnectSession에서 호출 — 대기 중인 tcs를 취소하여 HandleAsync를 언블로킹
    internal void CancelPendingTcs() => Interlocked.Exchange(ref _pendingTcs, null)?.TrySetCanceled();

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

using System.Collections.Concurrent;
using Common.Logging;
using GameServer.Entities;
using GameServer.Network;

namespace GameServer.Systems;

public class SessionSystem
{
    public static readonly SessionSystem Instance = new();

    private enum EventType
    {
        AddSession,
        RemoveSession,
        PlayerCreated,      // 플레이어 생성 완료 — SessionSystem이 AttachPlayer 수행
        PlayerGameEnter,    // WorkerSystem 등록 + EntryHandshake 완료 신호
        Disconnect,
    }

    private record EventData(EventType Type, SessionComponent Session, object? Data = null)
    {
        public static EventData OfAdd(SessionComponent s)
            => new(EventType.AddSession, s);

        public static EventData OfRemove(SessionComponent s)
            => new(EventType.RemoveSession, s);

        public static EventData OfPlayerCreated(SessionComponent s, Player p)
            => new(EventType.PlayerCreated, s, p);

        public static EventData OfPlayerGameEnter(SessionComponent s, TaskCompletionSource tcs)
            => new(EventType.PlayerGameEnter, s, tcs);

        public static EventData OfDisconnect(SessionComponent s)
            => new(EventType.Disconnect, s);
    }

    private readonly ConcurrentDictionary<long, SessionComponent> _sessions = new();
    private readonly ConcurrentQueue<EventData> _eventQueue = new();

    private Thread? _thread;
    private bool _running;

    public void EnqueueAdd(SessionComponent session)
        => _eventQueue.Enqueue(EventData.OfAdd(session));

    public void EnqueueRemove(SessionComponent session)
        => _eventQueue.Enqueue(EventData.OfRemove(session));

    public void EnqueuePlayerCreated(SessionComponent session, Player player)
        => _eventQueue.Enqueue(EventData.OfPlayerCreated(session, player));

    public void EnqueuePlayerGameEnter(SessionComponent session, TaskCompletionSource tcs)
        => _eventQueue.Enqueue(EventData.OfPlayerGameEnter(session, tcs));

    public void EnqueueDisconnect(SessionComponent session)
        => _eventQueue.Enqueue(EventData.OfDisconnect(session));

    public bool TryGet(long instanceId, out SessionComponent? session)
        => _sessions.TryGetValue(instanceId, out session);

    public void StartSystem()
    {
        if (_thread is { IsAlive: true })
        {
            return;
        }

        _running = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "SessionSystem" };
        _thread.Start();
    }

    public void Stop()
    {
        _running = false;
        _thread?.Join(TimeSpan.FromSeconds(5));
    }

    private void Loop()
    {
        while (_running)
        {
            Thread.Sleep(10);
            ProcessEvent();
        }

        // 종료 시 잔여 이벤트 드레인 — Stop() 이후 EnqueueDisconnect 누락 방지
        while (_eventQueue.Count > 0)
        {
            ProcessEvent();
        }
    }

    private void ProcessEvent()
    {
        var count = _eventQueue.Count;
        if (count == 0)
        {
            return;
        }

        EventData? eventData;
        for (var i = 0; i < count; ++i)
        {
            if (!_eventQueue.TryDequeue(out eventData))
            {
                break;
            }

            try
            {
                switch (eventData!.Type)
                {
                    case EventType.AddSession:
                        _sessions.TryAdd(eventData.Session.InstanceId, eventData.Session);
                        break;

                    case EventType.RemoveSession:
                        _sessions.TryRemove(eventData.Session.InstanceId, out _);
                        break;

                    case EventType.PlayerCreated:
                        InternalPlayerCreated(eventData.Session, (Player)eventData.Data!);
                        break;

                    case EventType.PlayerGameEnter:
                        InternalPlayerGameEnter(eventData.Session, (TaskCompletionSource)eventData.Data!);
                        break;

                    case EventType.Disconnect:
                        InternalDisconnectSession(eventData.Session);
                        break;
                }
            }
            catch (Exception ex)
            {
                GameLogger.Error("SessionSystem", $"ProcessEvent 오류 ({eventData!.Type})", ex);
            }
        }
    }

    // PlayerCreated: session에 player를 부착
    // 세션이 이미 제거된 경우(Disconnect 선처리) player를 즉시 정리하여 누수 방지
    private void InternalPlayerCreated(SessionComponent session, Player player)
    {
        if (_sessions.TryGetValue(session.InstanceId, out var target))
        {
            target.AttachPlayer(player);
            return;
        }

        GameLogger.Warn("SessionSystem", $"PlayerCreated: 세션 없음 — 플레이어 정리 (InstanceId={session.InstanceId})");
        player.ImmediateFinalize();
    }

    // PlayerGameEnter: PlayerSystem에 등록하고 EntryHandshake 완료 신호 전송
    // tcs.SetResult → HandleAsync 재개, 세션/플레이어 없으면 tcs 취소
    private void InternalPlayerGameEnter(SessionComponent session, TaskCompletionSource tcs)
    {
        if (!_sessions.TryGetValue(session.InstanceId, out _))
        {
            tcs.TrySetCanceled();
            return;
        }

        // Disconnect가 PlayerGameEnter 이전에 처리된 경우 — PlayerSystem.Add 실행 차단
        if (session.IsDisconnected)
        {
            tcs.TrySetCanceled();
            return;
        }

        var player = session.Player;
        if (player == null)
        {
            tcs.TrySetException(new InvalidOperationException(
                $"PlayerGameEnter: player null (InstanceId={session.InstanceId})"));
            return;
        }

        PlayerSystem.Instance.Add(player);
        session.SetEntryHandshakeCompleted();
        tcs.TrySetResult();
    }

    // Disconnect: 세션 제거 후 플레이어 정리 경로 선택
    // IsEntryHandshakeCompleted == true  → DisconnectForNextTick (워커 틱에서 처리)
    // IsEntryHandshakeCompleted == false → ImmediateFinalize (즉시 정리) + PendingTcs 취소
    private void InternalDisconnectSession(SessionComponent session)
    {
        _sessions.TryRemove(session.InstanceId, out _);

        // 멱등성 보장 — 중복 Disconnect 이벤트 무시
        if (session.IsDisconnected)
        {
            return;
        }

        session.SetDisconnectedFlag();

        var player = session.Player;
        if (player == null)
        {
            // PlayerCreated 이전 단계 — HandleAsync에서 tcs 대기 중일 수 있음
            session.CancelPendingTcs();
            session.Dispose();
            return;
        }

        if (session.IsEntryHandshakeCompleted)
        {
            // 정상 입장 완료 후 연결 해제 — 즉시 DisconnectAsync 처리
            player.DisconnectForNextTick();
        }
        else
        {
            // PlayerGameEnter 완료 전 연결 해제 — 즉시 정리
            session.CancelPendingTcs();
            player.ImmediateFinalize();
        }
    }
}

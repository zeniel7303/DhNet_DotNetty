using System.Threading.Channels;
using Common.Logging;
using GameServer.Database;
using GameServer.Database.Rows;
using GameServer.Protocol;
using GameServer.Systems;

namespace GameServer.Entities;

public class Room
{
    public ulong RoomId { get; }
    public string Name => $"Room {RoomId}";
    public int PlayerCount => _reservedCount;
    public int Capacity => MaxPlayers;
    public bool IsFull => _reservedCount >= MaxPlayers;

    private const int MaxPlayers = 2;
    private readonly Channel<Func<Task>> _jobChannel = Channel.CreateUnbounded<Func<Task>>();
    private readonly List<Player> _players = new();
    private volatile int _reservedCount = 0;

    // CAS 패턴 — lock 없이 원자적으로 슬롯 예약
    public bool TryReserve()
    {
        int current;
        do
        {
            current = _reservedCount;
            if (current >= MaxPlayers) return false;
        } while (Interlocked.CompareExchange(ref _reservedCount, current + 1, current) != current);
        return true;
    }

    // Enter 실패 또는 Leave 시 예약 해제
    public void ReleaseSlot() => Interlocked.Decrement(ref _reservedCount);

    public Room(ulong roomId)
    {
        RoomId = roomId;
        _ = Task.Run(ProcessJobsAsync);
    }

    private async Task ProcessJobsAsync()
    {
        await foreach (var job in _jobChannel.Reader.ReadAllAsync())
        {
            try
            {
                await job();
            }
            catch (Exception ex)
            {
                GameLogger.Error($"Room:{RoomId}", "JobQueue 오류", ex);
            }
        }
    }

    // Room이 _rooms에서 제거될 때 호출 → ProcessJobsAsync의 await foreach 종료 → GC 수거 가능
    public void Close() => _jobChannel.Writer.TryComplete();

    private void DoAsync(Func<Task> job) => _jobChannel.Writer.TryWrite(job);

    public void Enter(Player player) => DoAsync(async () =>
    {
        if (!player.Session.Channel.Active)
        {
            ReleaseSlot();
            GameLogger.Warn($"Room:{RoomId}", $"입장 취소 (연결 해제): {player.Name}");
            return;
        }
        if (_players.Count >= MaxPlayers)
        {
            ReleaseSlot();
            GameLogger.Warn($"Room:{RoomId}", $"입장 거부 (정원 초과 safety net): {player.Name}");
            LobbySystem.Instance.Lobby.Enter(player);
            await player.Session.SendAsync(new GamePacket
                { ResRoomEnter = new ResRoomEnter { Success = false } });
            return;
        }
        _players.Add(player);
        player.CurrentRoom = this;
        bool successSent = false;
        try
        {
            GameLogger.Info($"Room:{RoomId}", $"입장: {player.Name} ({_players.Count}/{MaxPlayers})");
            DatabaseSystem.Instance.GameLog.RoomLogs.InsertAsync(new RoomLogRow
            {
                player_id  = player.Id,
                room_id    = RoomId,
                action     = "enter",
                created_at = DateTime.UtcNow
            }).FireAndForget("Room");
            await player.Session.SendAsync(new GamePacket
                { ResRoomEnter = new ResRoomEnter { Success = true } });
            successSent = true;
            var noti = new GamePacket
                { NotiRoomEnter = new NotiRoomEnter { PlayerId = player.Id, PlayerName = player.Name } };
            await Task.WhenAll(_players.Where(p => p != player).Select(p => p.Session.SendAsync(noti)));
        }
        catch (Exception ex)
        {
            _players.Remove(player);
            player.CurrentRoom = null;
            ReleaseSlot();
            GameLogger.Warn($"Room:{RoomId}", $"입장 처리 중 오류, 롤백: {player.Name} — {ex.Message}");
            LobbySystem.Instance.Lobby.Enter(player);
            if (!successSent)
            {
                _ = player.Session.SendAsync(new GamePacket
                    { ResRoomEnter = new ResRoomEnter { Success = false } });
            }
        }
    });

    public void Leave(Player player, bool isDisconnect) => DoAsync(async () =>
    {
        if (!_players.Remove(player))
        {
            return;
        }
        ReleaseSlot();
        player.CurrentRoom = null;
        GameLogger.Info($"Room:{RoomId}", $"퇴장: {player.Name} (disconnect={isDisconnect}, 잔여 {_players.Count}명)");
        DatabaseSystem.Instance.GameLog.RoomLogs.InsertAsync(new RoomLogRow
        {
            player_id  = player.Id,
            room_id    = RoomId,
            action     = isDisconnect ? "disconnect" : "exit",
            created_at = DateTime.UtcNow
        }).FireAndForget("Room");
        if (!isDisconnect)
        {
            LobbySystem.Instance.Lobby.Enter(player);
            await player.Session.SendAsync(new GamePacket { ResRoomExit = new ResRoomExit() });
        }
        var noti = new GamePacket
            { NotiRoomExit = new NotiRoomExit { PlayerId = player.Id, PlayerName = player.Name } };
        await Task.WhenAll(_players.Select(p => p.Session.SendAsync(noti)));
        if (_players.Count == 0)
        {
            LobbySystem.Instance.RemoveRoom(RoomId);
        }
    });

    public bool Broadcast(string message)
    {
        var noti = new GamePacket
        {
            NotiRoomChat = new NotiRoomChat { PlayerId = 0, PlayerName = "System", Message = message }
        };
        return _jobChannel.Writer.TryWrite(async () =>
            await Task.WhenAll(_players.Select(p => p.Session.SendAsync(noti))));
    }

    public void Chat(Player sender, string message) => DoAsync(async () =>
    {
        GameLogger.Info($"Room:{RoomId}", $"채팅: {sender.Name}: {message}");
        DatabaseSystem.Instance.GameLog.ChatLogs.InsertAsync(new ChatLogRow
        {
            player_id  = sender.Id,
            room_id    = RoomId,
            channel    = "room",
            message    = message,
            created_at = DateTime.UtcNow
        }).FireAndForget("Room");
        var noti = new GamePacket
        {
            NotiRoomChat = new NotiRoomChat
            {
                PlayerId = sender.Id,
                PlayerName = sender.Name,
                Message = message
            }
        };
        await Task.WhenAll(_players.Select(p => p.Session.SendAsync(noti)));
    });
}

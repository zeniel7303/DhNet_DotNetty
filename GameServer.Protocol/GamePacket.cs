using MessagePack;

namespace GameServer.Protocol;

// ─────────────────────────────────────────────────────────────────────────────
// 새 패킷 타입 추가 시 반드시 아래 세 곳을 동기화해야 한다 (3-place sync rule):
//   1. PacketType enum — 값 추가
//   2. IPacketPayload [Union] 어트리뷰트 — Union(key, typeof(XxxClass)) 추가
//   3. GamePacket.TypeMap — typeof(XxxClass) = PacketType.Xxx 매핑 추가
// ─────────────────────────────────────────────────────────────────────────────
public enum PacketType : byte
{
    None         = 0,
    ReqLogin     = 1,
    ResLogin     = 2,
    ReqRoomEnter = 3,
    ResRoomEnter = 4,
    NotiRoomEnter = 5,
    ReqRoomChat  = 6,
    NotiRoomChat = 7,
    ReqRoomExit  = 8,
    ResRoomExit  = 9,
    NotiRoomExit = 10,
    ReqLobbyChat = 11,
    NotiLobbyChat = 12,
    ReqHeartbeat = 13,
    ResHeartbeat = 14,
    ReqLobbyList = 15,
    ResLobbyList = 16,
    NotiSystem   = 17,
    ReqRegister  = 18,
    ResRegister  = 19,
}

[Union((int)PacketType.ReqLogin,      typeof(ReqLogin))]
[Union((int)PacketType.ResLogin,      typeof(ResLogin))]
[Union((int)PacketType.ReqRoomEnter,  typeof(ReqRoomEnter))]
[Union((int)PacketType.ResRoomEnter,  typeof(ResRoomEnter))]
[Union((int)PacketType.NotiRoomEnter, typeof(NotiRoomEnter))]
[Union((int)PacketType.ReqRoomChat,   typeof(ReqRoomChat))]
[Union((int)PacketType.NotiRoomChat,  typeof(NotiRoomChat))]
[Union((int)PacketType.ReqRoomExit,   typeof(ReqRoomExit))]
[Union((int)PacketType.ResRoomExit,   typeof(ResRoomExit))]
[Union((int)PacketType.NotiRoomExit,  typeof(NotiRoomExit))]
[Union((int)PacketType.ReqLobbyChat,  typeof(ReqLobbyChat))]
[Union((int)PacketType.NotiLobbyChat, typeof(NotiLobbyChat))]
[Union((int)PacketType.ReqHeartbeat,  typeof(ReqHeartbeat))]
[Union((int)PacketType.ResHeartbeat,  typeof(ResHeartbeat))]
[Union((int)PacketType.ReqLobbyList,  typeof(ReqLobbyList))]
[Union((int)PacketType.ResLobbyList,  typeof(ResLobbyList))]
[Union((int)PacketType.NotiSystem,    typeof(NotiSystem))]
[Union((int)PacketType.ReqRegister,   typeof(ReqRegister))]
[Union((int)PacketType.ResRegister,   typeof(ResRegister))]
public interface IPacketPayload { }

[MessagePackObject]
public sealed class GamePacket
{
    [Key(0)] public PacketType     Type    { get; init; }
    [Key(1)] public IPacketPayload? Payload { get; init; }

    private static readonly Dictionary<Type, PacketType> TypeMap = new()
    {
        [typeof(ReqLogin)]      = PacketType.ReqLogin,
        [typeof(ResLogin)]      = PacketType.ResLogin,
        [typeof(ReqRoomEnter)]  = PacketType.ReqRoomEnter,
        [typeof(ResRoomEnter)]  = PacketType.ResRoomEnter,
        [typeof(NotiRoomEnter)] = PacketType.NotiRoomEnter,
        [typeof(ReqRoomChat)]   = PacketType.ReqRoomChat,
        [typeof(NotiRoomChat)]  = PacketType.NotiRoomChat,
        [typeof(ReqRoomExit)]   = PacketType.ReqRoomExit,
        [typeof(ResRoomExit)]   = PacketType.ResRoomExit,
        [typeof(NotiRoomExit)]  = PacketType.NotiRoomExit,
        [typeof(ReqLobbyChat)]  = PacketType.ReqLobbyChat,
        [typeof(NotiLobbyChat)] = PacketType.NotiLobbyChat,
        [typeof(ReqHeartbeat)]  = PacketType.ReqHeartbeat,
        [typeof(ResHeartbeat)]  = PacketType.ResHeartbeat,
        [typeof(ReqLobbyList)]  = PacketType.ReqLobbyList,
        [typeof(ResLobbyList)]  = PacketType.ResLobbyList,
        [typeof(NotiSystem)]    = PacketType.NotiSystem,
        [typeof(ReqRegister)]   = PacketType.ReqRegister,
        [typeof(ResRegister)]   = PacketType.ResRegister,
    };

    /// <summary>payload 타입으로 PacketType을 자동 추론하여 GamePacket을 생성한다.</summary>
    public static GamePacket From<T>(T payload) where T : IPacketPayload
    {
        return new GamePacket { Type = TypeMap[typeof(T)], Payload = payload };
    }

    /// <summary>Payload를 지정 타입으로 캐스팅한다. Payload가 null이거나 타입 불일치 시 예외.</summary>
    public T As<T>() where T : class, IPacketPayload
    {
        if (Payload == null)
            throw new InvalidOperationException($"Payload is null for PacketType={Type}");

        return (T)Payload;
    }
}

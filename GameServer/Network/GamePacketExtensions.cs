using GameServer.Protocol;

namespace GameServer.Network;

public static class GamePacketExtensions
{
    // ReqLogin은 GameServerHandler에서 선처리됨 — Dispatch에 도달하지 않음
    public static (Type? type, object? payload) ExtractPayload(this GamePacket packet) =>
        packet.Type switch
        {
            PacketType.ReqRoomEnter  => (typeof(ReqRoomEnter),  packet.As<ReqRoomEnter>()),
            PacketType.ReqRoomChat   => (typeof(ReqRoomChat),   packet.As<ReqRoomChat>()),
            PacketType.ReqRoomExit   => (typeof(ReqRoomExit),   packet.As<ReqRoomExit>()),
            PacketType.ReqLobbyChat  => (typeof(ReqLobbyChat),  packet.As<ReqLobbyChat>()),
            PacketType.ReqLobbyList  => (typeof(ReqLobbyList),  packet.As<ReqLobbyList>()),
            PacketType.ReqHeartbeat  => (typeof(ReqHeartbeat),  packet.As<ReqHeartbeat>()),
            _                        => (null, null)
        };
}

using GameServer.Protocol;

namespace GameServer.Network;

public static class GamePacketExtensions
{
    // ReqLogin은 GameServerHandler에서 선처리됨 — Dispatch에 도달하지 않음
    public static (Type? type, object? payload) ExtractPayload(this GamePacket packet) =>
        packet.PayloadCase switch
        {
            GamePacket.PayloadOneofCase.ReqRoomEnter => (typeof(ReqRoomEnter), packet.ReqRoomEnter),
            GamePacket.PayloadOneofCase.ReqRoomChat  => (typeof(ReqRoomChat),  packet.ReqRoomChat),
            GamePacket.PayloadOneofCase.ReqRoomExit  => (typeof(ReqRoomExit),  packet.ReqRoomExit),
            GamePacket.PayloadOneofCase.ReqLobbyChat => (typeof(ReqLobbyChat), packet.ReqLobbyChat),
            GamePacket.PayloadOneofCase.ReqHeartbeat => (typeof(ReqHeartbeat), packet.ReqHeartbeat),
            _                                        => (null, null)
        };
}

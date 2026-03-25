using GameServer.Protocol;

namespace GameServer.Network;

public static class GamePacketExtensions
{
    // ReqLogin은 GameServerHandler에서 선처리됨 — Dispatch에 도달하지 않음
    public static (Type? type, object? payload) ExtractPayload(this GamePacket packet) =>
        packet.PayloadCase switch
        {
            GamePacket.PayloadOneofCase.ReqRoomEnter  => (typeof(ReqRoomEnter),  packet.ReqRoomEnter),
            GamePacket.PayloadOneofCase.ReqRoomChat   => (typeof(ReqRoomChat),   packet.ReqRoomChat),
            GamePacket.PayloadOneofCase.ReqRoomExit   => (typeof(ReqRoomExit),   packet.ReqRoomExit),
            GamePacket.PayloadOneofCase.ReqRoomList   => (typeof(ReqRoomList),   packet.ReqRoomList),
            GamePacket.PayloadOneofCase.ReqCreateRoom => (typeof(ReqCreateRoom), packet.ReqCreateRoom),
            GamePacket.PayloadOneofCase.ReqReadyGame  => (typeof(ReqReadyGame),  packet.ReqReadyGame),
            GamePacket.PayloadOneofCase.ReqMove       => (typeof(ReqMove),       packet.ReqMove),
            GamePacket.PayloadOneofCase.ReqAttack     => (typeof(ReqAttack),     packet.ReqAttack),
            GamePacket.PayloadOneofCase.ReqGameChat   => (typeof(ReqGameChat),   packet.ReqGameChat),
            GamePacket.PayloadOneofCase.ReqHeartbeat    => (typeof(ReqHeartbeat),    packet.ReqHeartbeat),
            GamePacket.PayloadOneofCase.ReqChooseWeapon => (typeof(ReqChooseWeapon), packet.ReqChooseWeapon),
            _                                           => (null, null)
        };
}

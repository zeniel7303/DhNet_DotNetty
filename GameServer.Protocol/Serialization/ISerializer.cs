namespace GameServer.Protocol.Serialization;

/// <summary>
/// GamePacket 직렬화/역직렬화 추상화.
/// 직렬화 방식(MessagePack, Protobuf 등)을 교체할 때 이 인터페이스만 구현하면 된다.
/// </summary>
public interface ISerializer
{
    byte[] Serialize(GamePacket packet);

    /// <summary>역직렬화 실패 시 null 반환.</summary>
    GamePacket? Deserialize(byte[] data);
}

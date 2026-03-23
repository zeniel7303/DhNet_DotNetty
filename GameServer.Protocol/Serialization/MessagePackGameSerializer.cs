using MessagePack;

namespace GameServer.Protocol.Serialization;

/// <summary>
/// MessagePack 기반 GamePacket 직렬화 구현체.
/// </summary>
public sealed class MessagePackGameSerializer : ISerializer
{
    public static readonly MessagePackGameSerializer Instance = new();

    private static readonly MessagePackSerializerOptions Options =
        MessagePackSerializerOptions.Standard.WithSecurity(MessagePackSecurity.UntrustedData);

    public byte[] Serialize(GamePacket packet)
        => MessagePackSerializer.Serialize(packet, Options);

    public GamePacket? Deserialize(byte[] data)
    {
        try
        {
            return MessagePackSerializer.Deserialize<GamePacket>(data, Options);
        }
        catch
        {
            return null;
        }
    }
}

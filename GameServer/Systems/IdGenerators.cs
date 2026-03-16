namespace GameServer.Systems;

public static class IdGenerators
{
    public static readonly UniqueIdGenerator Player = new();
    public static readonly UniqueIdGenerator Room = new();
}

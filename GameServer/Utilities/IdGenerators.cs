namespace GameServer.Systems;

public static class IdGenerators
{
    public static readonly UniqueIdGenerator Account = new();
    public static readonly UniqueIdGenerator Room    = new();
    public static readonly UniqueIdGenerator Lobby   = new();
}

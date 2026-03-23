using Common.Logging;

namespace TestClient.Stats;

public static class LoadTestStats
{
    private static long _packetsSent;
    private static long _packetsReceived;
    private static long _errors;
    private static long _connected;
    private static long _disconnected;
    private static long _reconnects;
    private static long _roomCycles;
    private static long _chatSent;
    private static long _chatReceived;

    public static void IncrementSent()         => Interlocked.Increment(ref _packetsSent);
    public static void IncrementReceived()     => Interlocked.Increment(ref _packetsReceived);
    public static void IncrementErrors()       => Interlocked.Increment(ref _errors);
    public static void IncrementConnected()    => Interlocked.Increment(ref _connected);
    public static void IncrementDisconnected() => Interlocked.Increment(ref _disconnected);
    public static void IncrementReconnect()    => Interlocked.Increment(ref _reconnects);
    public static void IncrementRoomCycle()    => Interlocked.Increment(ref _roomCycles);
    public static void IncrementChatSent()     => Interlocked.Increment(ref _chatSent);
    public static void IncrementChatReceived() => Interlocked.Increment(ref _chatReceived);

    public static void PrintSummary()
    {
        long sent         = Interlocked.Read(ref _packetsSent);
        long received     = Interlocked.Read(ref _packetsReceived);
        long errors       = Interlocked.Read(ref _errors);
        long connected    = Interlocked.Read(ref _connected);
        long disconnected = Interlocked.Read(ref _disconnected);
        long reconnects   = Interlocked.Read(ref _reconnects);
        long roomCycles   = Interlocked.Read(ref _roomCycles);
        long chatSent     = Interlocked.Read(ref _chatSent);
        long chatReceived = Interlocked.Read(ref _chatReceived);

        GameLogger.Info("Stats",
            $"Active={connected - disconnected} | Sent={sent} | Recv={received} | ChatSent={chatSent} | ChatRecv={chatReceived} | Errors={errors} | Reconnects={reconnects} | RoomCycles={roomCycles}");
    }
}

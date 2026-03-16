using Common.Logging;

namespace GameClient.Stats;

public static class LoadTestStats
{
    private static long _packetsSent;
    private static long _packetsReceived;
    private static long _errors;
    private static long _connected;
    private static long _disconnected;

    public static void IncrementSent()       => Interlocked.Increment(ref _packetsSent);
    public static void IncrementReceived()   => Interlocked.Increment(ref _packetsReceived);
    public static void IncrementErrors()     => Interlocked.Increment(ref _errors);
    public static void IncrementConnected()  => Interlocked.Increment(ref _connected);
    public static void IncrementDisconnected() => Interlocked.Increment(ref _disconnected);

    public static void PrintSummary()
    {
        long sent       = Interlocked.Read(ref _packetsSent);
        long received   = Interlocked.Read(ref _packetsReceived);
        long errors     = Interlocked.Read(ref _errors);
        long connected  = Interlocked.Read(ref _connected);
        long disconnected = Interlocked.Read(ref _disconnected);

        GameLogger.Info("Stats",
            $"Connected={connected - disconnected} | Sent={sent} | Recv={received} | Errors={errors}");
    }
}

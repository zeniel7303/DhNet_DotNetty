using Common.Logging;

namespace GameServer.Systems;

public class ShutdownSystem
{
    public static readonly ShutdownSystem Instance = new();

    private CancellationTokenSource? _cts;
    private int _requested;

    public bool IsShutdownRequested => _requested == 1;

    // ServerStartup에서 CancellationTokenSource 생성 직후 호출
    public void Initialize(CancellationTokenSource cts) => _cts = cts;

    // 종료 요청 — Interlocked.Exchange로 단일 진입 보장 후 CancellationToken 발행
    public void Request()
    {
        if (Interlocked.Exchange(ref _requested, 1) != 0) return;
        GameLogger.Info("ShutdownSystem", "Shutdown requested");
        _cts?.Cancel();
    }
}

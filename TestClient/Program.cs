using System.Net;
using Common;
using Common.Logging;
using DotNetty.Codecs;
using DotNetty.Codecs.Protobuf;
using DotNetty.Handlers.Logging;
using DotNetty.Transport.Bootstrapping;
using DotNetty.Transport.Channels;
using DotNetty.Transport.Channels.Sockets;
using TestClient.Controllers;
using TestClient.Network;
using TestClient.Scenarios;
using TestClient.Stats;
using GameServer.Protocol;

namespace TestClient;

class Program
{
    private static async Task RunAsync(string[] args)
    {
        Helper.SetConsoleLogger();

        var config = LoadTestConfig.FromArgs(args);
        GameLogger.Info("Config", $"Clients={config.ClientCount}, Scenario={config.Scenario}, Interval={config.ChatIntervalMs}ms");

        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        // 다중 클라이언트: 패킷 로그는 파일에만, Stats만 콘솔 출력
        // 단일 클라이언트: 모든 로그 콘솔 출력, 통계 루프 없음
        var multiClient = config.ClientCount > 1;
        if (multiClient)
        {
            GameLogger.ConsoleFilter = entry => entry.Tag == "Stats" || entry.Tag == "Config";
        }

        // 1000명 이상에서 EventLoop 스레드 수 자동 확장
        int loopCount = config.ClientCount > 500
            ? Math.Min(Environment.ProcessorCount * 4, 64)
            : Environment.ProcessorCount * 2;
        var group = new MultithreadEventLoopGroup(loopCount);
        GameLogger.Info("Config", $"EventLoopGroup threads={loopCount}");

        // 5초마다 통계 출력 (다중 클라이언트 전용)
        if (multiClient)
        {
            _ = Task.Run(async () =>
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(5000, cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    LoadTestStats.PrintSummary();
                }
            }, cts.Token);
        }

        // 암호화 키 조기 검증 (Fail-fast): 잘못된 키를 개별 연결 실패로 오보고하지 않도록
        // 클라이언트 생성 전에 한 번만 파싱하여 설정 오류를 즉시 노출
        byte[]? encKey = null;
        if (!string.IsNullOrEmpty(config.EncryptionKey))
        {
            try
            {
                encKey = Convert.FromBase64String(config.EncryptionKey);
                if (encKey.Length != 16)
                    throw new InvalidOperationException($"EncryptionKey는 16바이트여야 합니다. 현재: {encKey.Length}바이트.");
                GameLogger.Info("Config", "패킷 암호화 활성화 (AES-128-GCM)");
            }
            catch (Exception ex)
            {
                GameLogger.Error("Config", $"--encryption-key 파싱 실패: {ex.Message}", ex);
                return;
            }
        }

        try
        {
            var endpoint = new IPEndPoint(IPAddress.Parse(config.ServerHost), config.ServerPort);

            if (config.ClientCount >= 100 && config.ConnectDelayMs >= 10)
            GameLogger.Info("Config", $"Tip: --clients {config.ClientCount}에서 --delay 5 를 권장합니다 (접속 분산)");

        var tasks = Enumerable.Range(0, config.ClientCount)
                .Select(i => ConnectClientAsync(group, endpoint, i, config, encKey, cts.Token))
                .ToArray();

            await Task.WhenAll(tasks);
        }
        finally
        {
            LoadTestStats.PrintSummary();
            await group.ShutdownGracefullyAsync(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(1));
            await GameLogger.FlushAsync();
        }
    }

    private static async Task ConnectClientAsync(
        MultithreadEventLoopGroup group,
        IPEndPoint endpoint,
        int clientIndex,
        LoadTestConfig config,
        byte[]? encKey,
        CancellationToken token)
    {
        // Thundering Herd 방지: 클라이언트마다 딜레이
        if (clientIndex > 0)
        {
            await Task.Delay(config.ConnectDelayMs * clientIndex, token);
        }

        if (token.IsCancellationRequested)
        {
            return;
        }

        // reconnect-stress: 재접속 루프를 직접 관리
        if (config.Scenario.Equals("reconnect-stress", StringComparison.OrdinalIgnoreCase))
        {
            await RunReconnectLoopAsync(group, endpoint, clientIndex, config, encKey, token);
            return;
        }

        var ctx = new ClientContext { ClientIndex = clientIndex };
        ILoadTestScenario scenario = config.Scenario.ToLower() switch
        {
            "room"             => new RoomScenario(config.PlayerNamePrefix),
            "room-once"        => new RoomOnceScenario(config.PlayerNamePrefix),
            "room-chat"        => new RoomChatScenario(config.PlayerNamePrefix, config.ChatIntervalMs, token),
            "room-loop"        => new RoomLoopScenario(config.PlayerNamePrefix),
            "duplicate-login"  => new DuplicateLoginScenario(config.PlayerNamePrefix),
            _                  => new LobbyChatScenario(config.PlayerNamePrefix, config.ChatIntervalMs, token),
        };

        var bootstrap = new Bootstrap();
        bootstrap
            .Group(group)
            .Channel<TcpSocketChannel>()
            .Option(ChannelOption.TcpNodelay, true)
            .Handler(new ActionChannelInitializer<ISocketChannel>(channel =>
            {
                var pipeline = channel.Pipeline;
                pipeline.AddLast("framing-enc", new LengthFieldPrepender(2));
                pipeline.AddLast("framing-dec", new LengthFieldBasedFrameDecoder(ushort.MaxValue, 0, 2, 0, 2));
                if (encKey != null)
                {
                    pipeline.AddLast("crypto-dec", new AesGcmDecryptionHandler(encKey));
                    pipeline.AddLast("crypto-enc", new AesGcmEncryptionHandler(encKey));
                }
                pipeline.AddLast("protobuf-decoder", new ProtobufDecoder(GamePacket.Parser));
                pipeline.AddLast("protobuf-encoder", new ProtobufEncoder());
                pipeline.AddLast("handler", new GameClientHandler(ctx, scenario));
            }));

        IChannel? channel = null;
        try
        {
            channel = await bootstrap.ConnectAsync(endpoint);
            try
            {
                await Task.Delay(Timeout.Infinite, token);
            }
            catch (OperationCanceledException)
            {
            }
        }
        catch (Exception ex)
        {
            GameLogger.Error($"Client[{clientIndex}]", $"연결 실패: {ex.Message}", ex);
            LoadTestStats.IncrementErrors();
        }
        finally
        {
            if (channel != null)
            {
                await channel.CloseAsync();
                // ctx.Dispose()는 ChannelInactive → GameClientHandler에서 호출됨
            }
            else
            {
                ctx.Dispose(); // 연결 실패 시 ChannelInactive 미발생 → 직접 해제
            }
        }
    }

    /// <summary>
    /// reconnect-stress 전용: 접속 → 시나리오 실행 → 재접속을 반복하는 루프.
    /// Bootstrap을 클라이언트당 1개 생성하고, 채널만 사이클마다 재생성합니다.
    /// </summary>
    private static async Task RunReconnectLoopAsync(
        MultithreadEventLoopGroup group,
        IPEndPoint endpoint,
        int clientIndex,
        LoadTestConfig config,
        byte[]? encKey,
        CancellationToken token)
    {
        // Thundering Herd 방지 (기존 ConnectClientAsync와 동일)
        if (clientIndex > 0)
        {
            try { await Task.Delay(config.ConnectDelayMs * clientIndex, token); }
            catch (OperationCanceledException) { return; }
        }

        var ctx      = new ClientContext { ClientIndex = clientIndex };
        var scenario = new ReconnectStressScenario(
            config.PlayerNamePrefix, config.ChatCount, config.RoomCycles, token);

        // Bootstrap은 클라이언트당 1회 생성 후 재접속 시 재사용
        var bootstrap = new Bootstrap();
        bootstrap
            .Group(group)
            .Channel<TcpSocketChannel>()
            .Option(ChannelOption.TcpNodelay, true)
            .Handler(new ActionChannelInitializer<ISocketChannel>(ch =>
            {
                var pipeline = ch.Pipeline;
                pipeline.AddLast("framing-enc",      new LengthFieldPrepender(2));
                pipeline.AddLast("framing-dec",      new LengthFieldBasedFrameDecoder(ushort.MaxValue, 0, 2, 0, 2));
                if (encKey != null)
                {
                    pipeline.AddLast("crypto-dec", new AesGcmDecryptionHandler(encKey));
                    pipeline.AddLast("crypto-enc", new AesGcmEncryptionHandler(encKey));
                }
                pipeline.AddLast("protobuf-decoder", new ProtobufDecoder(GamePacket.Parser));
                pipeline.AddLast("protobuf-encoder", new ProtobufEncoder());
                pipeline.AddLast("handler",          new GameClientHandler(ctx, scenario));
            }));

        try
        {
            while (!token.IsCancellationRequested && !scenario.IsFinished)
            {
                scenario.BeginCycle();
                IChannel? channel = null;
                try
                {
                    channel = await bootstrap.ConnectAsync(endpoint);
                    // ChannelInactive(→ scenario.OnDisconnected → TCS.SetResult) 또는 취소까지 대기
                    await scenario.WaitForDisconnectAsync().WaitAsync(token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    GameLogger.Error($"Client[{clientIndex}]", $"연결 실패: {ex.Message}", ex);
                    LoadTestStats.IncrementErrors();
                }
                finally
                {
                    if (channel?.Active == true)
                        await channel.CloseAsync();
                }

                if (token.IsCancellationRequested || scenario.IsFinished) break;

                // 재접속 딜레이 + ±200ms jitter (접속 폭발 방지)
                var jitter = Random.Shared.Next(-200, 201);
                var delay  = Math.Max(100, config.ReconnectDelayMs + jitter);
                try { await Task.Delay(delay, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
        finally
        {
            ctx.Dispose();
        }
    }

    private static async Task Main(string[] args) => await RunAsync(args);
}

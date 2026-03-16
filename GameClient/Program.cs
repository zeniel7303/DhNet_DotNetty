using System.Net;
using Common;
using Common.Logging;
using DotNetty.Codecs;
using DotNetty.Codecs.Protobuf;
using DotNetty.Handlers.Logging;
using DotNetty.Transport.Bootstrapping;
using DotNetty.Transport.Channels;
using DotNetty.Transport.Channels.Sockets;
using GameClient.Controllers;
using GameClient.Network;
using GameClient.Scenarios;
using GameClient.Stats;
using GameServer.Protocol;

namespace GameClient;

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

        var group = new MultithreadEventLoopGroup();

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

        try
        {
            var endpoint = new IPEndPoint(IPAddress.Parse(config.ServerHost), config.ServerPort);

            var tasks = Enumerable.Range(0, config.ClientCount)
                .Select(i => ConnectClientAsync(group, endpoint, i, config, cts.Token))
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

        var ctx = new ClientContext { ClientIndex = clientIndex };
        ILoadTestScenario scenario = config.Scenario.ToLower() switch
        {
            "room"      => new RoomScenario(config.PlayerNamePrefix),
            "room-once" => new RoomOnceScenario(config.PlayerNamePrefix),
            "room-chat" => new RoomChatScenario(config.PlayerNamePrefix, config.ChatIntervalMs, token),
            "room-loop" => new RoomLoopScenario(config.PlayerNamePrefix),
            _           => new LobbyChatScenario(config.PlayerNamePrefix, config.ChatIntervalMs, token),
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

    private static async Task Main(string[] args) => await RunAsync(args);
}

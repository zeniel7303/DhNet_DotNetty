using Common;
using Common.Logging;
using DotNetty.Handlers.Logging;
using DotNetty.Transport.Bootstrapping;
using DotNetty.Transport.Channels;
using DotNetty.Transport.Channels.Sockets;

namespace GameServer.Network;

sealed class GameServerBootstrap : IAsyncDisposable
{
    private readonly MultithreadEventLoopGroup _bossGroup = new(1);
    private readonly MultithreadEventLoopGroup _workerGroup = new();
    private readonly GameServerSettings _settings;

    public GameServerBootstrap(GameServerSettings settings)
    {
        _settings = settings;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var bootstrap = new ServerBootstrap()
            .Group(_bossGroup, _workerGroup)
            .Channel<TcpServerSocketChannel>()
            .Option(ChannelOption.SoBacklog, 100)
            .Handler(new LoggingHandler("GAME-LSTN"))
            .ChildHandler(new GamePipelineInitializer());

        var boundChannel = await bootstrap.BindAsync(_settings.GamePort);
        GameLogger.Info("Server", $"GameServer started on port {_settings.GamePort}. Press Ctrl+C to stop.");

        try
        {
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException)
        {
        }

        await boundChannel.CloseAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await Task.WhenAll(
            _bossGroup.ShutdownGracefullyAsync(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(1)),
            _workerGroup.ShutdownGracefullyAsync(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(1)));
    }
}

using Common.Logging;
using DotNetty.Handlers.Logging;
using DotNetty.Transport.Bootstrapping;
using DotNetty.Transport.Channels;
using DotNetty.Transport.Channels.Sockets;

namespace GameServer.Network;

internal sealed class WsServerBootstrap : IAsyncDisposable
{
    private readonly MultithreadEventLoopGroup _bossGroup   = new(1);
    private readonly MultithreadEventLoopGroup _workerGroup = new();
    private readonly int _port;

    public WsServerBootstrap(int port) => _port = port;

    public async Task RunAsync(CancellationToken ct)
    {
        var bootstrap = new ServerBootstrap()
            .Group(_bossGroup, _workerGroup)
            .Channel<TcpServerSocketChannel>()
            .Option(ChannelOption.SoBacklog, 100)
            .Handler(new LoggingHandler("WS-LSTN"))
            .ChildHandler(new WsPipelineInitializer());

        var boundChannel = await bootstrap.BindAsync(_port);
        GameLogger.Info("Server", $"WebSocket server started on port {_port}  (ws://localhost:{_port}/ws)");

        try
        {
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException) { }

        await boundChannel.CloseAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await Task.WhenAll(
            _bossGroup.ShutdownGracefullyAsync(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(1)),
            _workerGroup.ShutdownGracefullyAsync(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(1)));
    }
}

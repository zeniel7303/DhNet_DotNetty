using System.Net;
using DotNetty.Codecs;
using DotNetty.Handlers.Logging;
using DotNetty.Transport.Bootstrapping;
using DotNetty.Transport.Channels;
using DotNetty.Transport.Channels.Sockets;
using Common;

namespace EchoClient
{
    class Program
    {
        private static async Task RunClientAsync()
        {
            var group = new MultithreadEventLoopGroup();

            try
            {
                var bootstrap = new Bootstrap();
                bootstrap
                    .Group(group)
                    .Channel<TcpSocketChannel>()
                    .Option(ChannelOption.TcpNodelay, true)
                    .Handler(new ActionChannelInitializer<ISocketChannel>(channel =>
                    {
                        var pipeline = channel.Pipeline;

                        pipeline.AddLast(new LoggingHandler());
                        pipeline.AddLast("framing-enc", new LengthFieldPrepender(2));
                        pipeline.AddLast("framing-dec", new LengthFieldBasedFrameDecoder(ushort.MaxValue, 0, 2, 0, 2));

                        // NOTE: MemoryPack/MessagePack 인코더/디코더 제거됨 (단순 에코 서버용)
                        // 실제 게임 서버에서는 Protocol Buffers 사용 권장:
                        // - pipeline.AddLast("protobuf-decoder", new ProtobufDecoder(...));
                        // - pipeline.AddLast("protobuf-encoder", new ProtobufEncoder(...));

                        pipeline.AddLast("echo", new EchoClientHandler());
                    }));

                var clientChannel = await bootstrap.ConnectAsync(new IPEndPoint(ClientSettings.Host, ClientSettings.Port));

                Console.ReadLine();

                await clientChannel.CloseAsync();
            }
            finally
            {
                await group.ShutdownGracefullyAsync(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(1));
            }
        }

        private static void Main() => RunClientAsync().Wait();
    }
}
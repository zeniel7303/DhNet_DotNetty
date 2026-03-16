using DotNetty.Codecs;
using DotNetty.Codecs.Protobuf;
using DotNetty.Handlers.Timeout;
using DotNetty.Transport.Channels;
using GameServer.Protocol;

namespace GameServer.Network;

sealed class GamePipelineInitializer : ChannelInitializer<IChannel>
{
    protected override void InitChannel(IChannel channel)
    {
        var pipeline = channel.Pipeline;
        pipeline.AddLast("framing-enc", new LengthFieldPrepender(2));
        pipeline.AddLast("framing-dec", new LengthFieldBasedFrameDecoder(ushort.MaxValue, 0, 2, 0, 2));
        pipeline.AddLast("protobuf-decoder", new ProtobufDecoder(GamePacket.Parser));
        pipeline.AddLast("protobuf-encoder", new ProtobufEncoder());
        pipeline.AddLast("idle", new IdleStateHandler(readerIdleTimeSeconds: 30, writerIdleTimeSeconds: 0, allIdleTimeSeconds: 0));
        pipeline.AddLast("heartbeat", HeartbeatHandler.Instance);
        pipeline.AddLast("handler", new GameServerHandler());
    }
}

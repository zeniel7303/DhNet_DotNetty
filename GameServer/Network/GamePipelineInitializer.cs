using Common;
using DotNetty.Codecs;
using DotNetty.Codecs.Protobuf;
using DotNetty.Handlers.Timeout;
using DotNetty.Transport.Channels;
using GameServer.Protocol;

namespace GameServer.Network;

internal sealed class GamePipelineInitializer : ChannelInitializer<IChannel>
{
    // null = 암호화 비활성화 (EncryptionSettings.Key가 비어있을 때)
    private readonly byte[]? _encKey;

    public GamePipelineInitializer(EncryptionSettings encSettings)
    {
        _encKey = encSettings.IsEnabled ? encSettings.GetKeyBytes() : null;
    }

    protected override void InitChannel(IChannel channel)
    {
        var pipeline = channel.Pipeline;

        // ── 프레이밍 ──────────────────────────────────────────────────
        pipeline.AddLast("framing-enc", new LengthFieldPrepender(2));
        pipeline.AddLast("framing-dec", new LengthFieldBasedFrameDecoder(ushort.MaxValue, 0, 2, 0, 2));

        // ── AES-128-GCM 암호화 레이어 (Key 설정 시에만 활성화) ────────
        // 인바운드:  framing-dec → crypto-dec → protobuf-decoder
        // 아웃바운드: protobuf-encoder → crypto-enc → framing-enc
        if (_encKey != null)
        {
            pipeline.AddLast("crypto-dec", new AesGcmDecryptionHandler(_encKey));
            pipeline.AddLast("crypto-enc", new AesGcmEncryptionHandler(_encKey));
        }

        // ── 직렬화 ────────────────────────────────────────────────────
        pipeline.AddLast("protobuf-decoder", new ProtobufDecoder(GamePacket.Parser));
        pipeline.AddLast("protobuf-encoder", new ProtobufEncoder());

        // ── 연결 관리 ─────────────────────────────────────────────────
        pipeline.AddLast("idle", new IdleStateHandler(readerIdleTimeSeconds: 30, writerIdleTimeSeconds: 0, allIdleTimeSeconds: 0));
        pipeline.AddLast("heartbeat", HeartbeatHandler.Instance);
        pipeline.AddLast("handler", new GameServerHandler());
    }
}

using Common.Crypto;
using Common.Logging;
using DotNetty.Buffers;
using DotNetty.Codecs;
using DotNetty.Transport.Channels;

namespace GameClient.Network;

/// <summary>
/// 인바운드 AES-GCM 복호화 핸들러 (클라이언트 측).
///
/// 파이프라인 위치:
///   [framing-dec] → [crypto-dec: 이 핸들러] → [protobuf-decoder]
///
/// 서버의 AesGcmDecryptionHandler와 동일 로직, 네임스페이스만 다름.
/// </summary>
internal sealed class AesGcmDecryptionHandler : MessageToMessageDecoder<IByteBuffer>
{
    private readonly byte[] _key;

    public AesGcmDecryptionHandler(byte[] key) => _key = key;

    protected override void Decode(IChannelHandlerContext ctx, IByteBuffer msg, List<object> output)
    {
        var encrypted = new byte[msg.ReadableBytes];
        msg.ReadBytes(encrypted);

        var decrypted = AesGcmCryptor.Decrypt(_key, encrypted);

        var buf = ctx.Allocator.Buffer(decrypted.Length);
        buf.WriteBytes(decrypted);
        output.Add(buf);
    }

    public override void ExceptionCaught(IChannelHandlerContext ctx, Exception ex)
    {
        GameLogger.Warn("Crypto", $"복호화 실패 ({ctx.Channel.RemoteAddress}): {ex.Message}");
        ctx.CloseAsync();
    }
}

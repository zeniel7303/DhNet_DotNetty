using Common.Crypto;
using DotNetty.Buffers;
using DotNetty.Codecs;
using DotNetty.Transport.Channels;

namespace GameClient.Network;

/// <summary>
/// 아웃바운드 AES-GCM 암호화 핸들러 (클라이언트 측).
///
/// 파이프라인 위치(아웃바운드는 역방향):
///   [handler] → [protobuf-encoder] → [crypto-enc: 이 핸들러] → [framing-enc] → wire
///
/// 서버의 AesGcmEncryptionHandler와 동일 로직, 네임스페이스만 다름.
/// </summary>
internal sealed class AesGcmEncryptionHandler : MessageToMessageEncoder<IByteBuffer>
{
    private readonly byte[] _key;

    public AesGcmEncryptionHandler(byte[] key) => _key = key;

    protected override void Encode(IChannelHandlerContext ctx, IByteBuffer msg, List<object> output)
    {
        var plaintext = new byte[msg.ReadableBytes];
        msg.ReadBytes(plaintext);

        var encrypted = AesGcmCryptor.Encrypt(_key, plaintext);

        var buf = ctx.Allocator.Buffer(encrypted.Length);
        buf.WriteBytes(encrypted);
        output.Add(buf);
    }
}

using Common.Crypto;
using DotNetty.Buffers;
using DotNetty.Codecs;
using DotNetty.Transport.Channels;

namespace GameServer.Network;

/// <summary>
/// 아웃바운드 AES-GCM 암호화 핸들러.
///
/// 파이프라인 위치(아웃바운드는 역방향):
///   [handler] → [protobuf-encoder] → [crypto-enc: 이 핸들러] → [framing-enc] → wire
///
/// 최종 wire 포맷:
///   [2B length][12B nonce][N bytes ciphertext][16B auth-tag]
///   ↑ framing-enc 추가  ↑ 이 핸들러가 생성하는 범위
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

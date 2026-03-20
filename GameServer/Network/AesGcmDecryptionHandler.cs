using Common.Crypto;
using Common.Logging;
using DotNetty.Buffers;
using DotNetty.Codecs;
using DotNetty.Transport.Channels;

namespace GameServer.Network;

/// <summary>
/// 인바운드 AES-GCM 복호화 핸들러.
///
/// 파이프라인 위치:
///   [framing-dec] → [crypto-dec: 이 핸들러] → [protobuf-decoder]
///
/// 역할:
///   - LengthFieldBasedFrameDecoder가 분리한 프레임(암호화된 바이트)을 받아 복호화
///   - 복호화된 plaintext(protobuf 직렬화 바이트)를 다음 핸들러로 전달
///
/// 오류 처리:
///   - 변조 패킷 (auth-tag 불일치) → AuthenticationTagMismatchException
///   - 잘못된 키 (서버/클라 불일치) → 동일 경로
///   → ExceptionCaught: 경고 로그 + 연결 해제
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

        // ctx.Allocator: DotNetty 관리 버퍼 할당 (풀링 지원, GC 압박 최소화)
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

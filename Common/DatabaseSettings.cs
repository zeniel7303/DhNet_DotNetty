namespace Common;

public class DatabaseSettings
{
    public string Host     { get; set; } = "127.0.0.1";
    public int    Port     { get; set; } = 3306;
    public string Database { get; set; } = "gameserver";
    public string UserId   { get; set; } = "root";
    public string Password { get; set; } = "0000";

    /// <summary>
    /// true: DB 연결 실패 시 서버 시작 중단 (운영 권장).
    /// false: DB 없어도 경고 후 서버 계속 실행 (로컬 개발 기본값).
    /// </summary>
    public bool RequireConnection { get; set; } = false;

    /// <summary>
    /// 로그 데이터를 저장할 별도 DB 이름.
    /// Host/Port/UserId/Password는 게임 DB와 공유한다.
    /// TODO [미래]: LogHost, LogPort 추가 시 별도 서버로 분리 가능.
    /// </summary>
    public string LogDatabase { get; set; } = "gamelog";
}

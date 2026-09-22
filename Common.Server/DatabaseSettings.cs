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

    /// <summary>MySqlConnector 연결 풀 상한. 기본값은 커넥터 기본과 같다.</summary>
    public int MaxPoolSize { get; set; } = 100;

    /// <summary>유휴 상태에서도 유지할 최소 연결 수.</summary>
    public int MinPoolSize { get; set; } = 0;

    /// <summary>새 연결을 기다리는 시간(초). 풀에 자리가 없을 때 적용된다.</summary>
    public int ConnectionTimeoutSeconds { get; set; } = 15;
}

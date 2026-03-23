namespace GameServer.Protocol;

public enum ErrorCode
{
    Success = 0,

    // 시스템 (1000~)
    ServerFull            = 1000,
    DbError               = 1001,
    InvalidUsernameLength = 1002,
    InvalidPasswordLength = 1003,
    UsernameTaken         = 1004,
    InvalidCredentials    = 1005,
    AlreadyLoggedIn       = 1006,

    // 로비 (2000~)
    LobbyFull  = 2000,
    NotInLobby = 2001,

    // 룸 (3000~)
    AlreadyInRoom = 3000,
}

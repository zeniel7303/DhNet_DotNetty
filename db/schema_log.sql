-- GameServer 로그 DB 스키마
-- 실행: mysql -u root -p0000 < db/schema_log.sql

CREATE DATABASE IF NOT EXISTS `gamelog`
    DEFAULT CHARACTER SET utf8mb4
    DEFAULT COLLATE utf8mb4_unicode_ci;

USE `gamelog`;

-- ──────────────────────────────────────────────
-- room_logs: 룸 입퇴장 이력 (로그 데이터)
-- ──────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS `room_logs` (
    `id`         BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    `player_id`  BIGINT UNSIGNED NOT NULL             COMMENT 'gameserver.players.player_id 참조',
    `room_id`    BIGINT UNSIGNED NOT NULL             COMMENT 'Room.RoomId',
    `action`     VARCHAR(16)     NOT NULL             COMMENT 'enter | exit | disconnect',
    `created_at` DATETIME        NOT NULL             COMMENT '이벤트 발생 시각 (UTC)',
    PRIMARY KEY (`id`),
    KEY `ix_room_logs_player` (`player_id`),
    KEY `ix_room_logs_room`   (`room_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='룸 입퇴장 이력 로그';

-- ──────────────────────────────────────────────
-- login_logs: 로그인 세션 이력 (로그 데이터)
-- ──────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS `login_logs` (
    `id`          BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    `player_id`   BIGINT UNSIGNED NOT NULL             COMMENT 'gameserver.players.player_id 참조',
    `player_name` VARCHAR(64)     NOT NULL             COMMENT '로그인 시점의 플레이어 이름',
    `ip_address`  VARCHAR(64)                          COMMENT '접속 IP',
    `login_at`    DATETIME        NOT NULL             COMMENT '로그인 시각 (UTC)',
    `logout_at`   DATETIME                             COMMENT '로그아웃 시각 (UTC). NULL = 비정상 종료',
    PRIMARY KEY (`id`),
    KEY `ix_login_logs_player` (`player_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='로그인 세션 이력';

-- ──────────────────────────────────────────────
-- chat_logs: 채팅 로그 (로그 데이터)
-- ──────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS `chat_logs` (
    `id`         BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    `player_id`  BIGINT UNSIGNED NOT NULL             COMMENT 'gameserver.players.player_id 참조',
    `room_id`    BIGINT UNSIGNED                      COMMENT 'Room.RoomId. NULL = 로비 채팅',
    `channel`    VARCHAR(8)      NOT NULL             COMMENT 'lobby | room',
    `message`    TEXT            NOT NULL             COMMENT '채팅 내용',
    `created_at` DATETIME        NOT NULL             COMMENT '발송 시각 (UTC)',
    PRIMARY KEY (`id`),
    KEY `ix_chat_logs_player` (`player_id`),
    KEY `ix_chat_logs_room`   (`room_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='채팅 로그';

-- ──────────────────────────────────────────────
-- stat_logs: 접속자 수 주기 로그
-- ──────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS `stat_logs` (
    `id`           BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    `player_count` INT UNSIGNED    NOT NULL COMMENT '현재 접속 중인 플레이어 수',
    `created_at`   DATETIME        NOT NULL COMMENT '기록 시각 (UTC)',
    PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='접속자 수 주기 로그';

-- GameServer 게임 데이터 DB 스키마
-- 실행: mysql -u root -p0000 < db/schema_game.sql

CREATE DATABASE IF NOT EXISTS `gameserver`
    DEFAULT CHARACTER SET utf8mb4
    DEFAULT COLLATE utf8mb4_unicode_ci;

USE `gameserver`;

-- ──────────────────────────────────────────────
-- players: 로그인 정보 (게임 데이터)
-- ──────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS `players` (
    `id`          BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    `player_id`   BIGINT UNSIGNED NOT NULL           COMMENT 'IdGenerators.Player.Next() 값',
    `player_name` VARCHAR(64)     NOT NULL,
    `login_at`    DATETIME        NOT NULL            COMMENT '로그인 시각 (UTC)',
    `logout_at`   DATETIME        NULL DEFAULT NULL   COMMENT '로그아웃 시각. NULL = 현재 온라인',
    `ip_address`  VARCHAR(45)     NULL DEFAULT NULL   COMMENT 'IPv4 or IPv6',
    PRIMARY KEY (`id`),
    UNIQUE KEY `ux_players_player_id` (`player_id`),
    KEY `ix_players_name` (`player_name`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='플레이어 로그인 정보';

-- ──────────────────────────────────────────────
-- TODO [미래 Phase A]: accounts 테이블 (계정 인증)
-- TODO [미래 Phase A]: account_bans 테이블 (정지 계정)
-- TODO [미래 Phase D]: player_stats 테이블 (게임 통계)
-- TODO [미래 Phase D]: player_items 테이블 (인벤토리)
-- ──────────────────────────────────────────────

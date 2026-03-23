-- GameServer 게임 데이터 DB 스키마
-- 실행: mysql -u root -p0000 < db/schema_game.sql

CREATE DATABASE IF NOT EXISTS `gameserver`
    DEFAULT CHARACTER SET utf8mb4
    DEFAULT COLLATE utf8mb4_unicode_ci;

USE `gameserver`;

-- ──────────────────────────────────────────────
-- accounts: 계정 인증 정보
-- ──────────────────────────────────────────────
DROP TABLE IF EXISTS `accounts`;
CREATE TABLE `accounts` (
    `account_id`    BIGINT UNSIGNED NOT NULL              COMMENT 'IdGenerators.Account.Next() 값',
    `username`      VARCHAR(64)     NOT NULL,
    `password_hash` VARCHAR(255)    NOT NULL              COMMENT 'Phase 2: 평문. Phase 3: BCrypt 해시.',
    `created_at`    DATETIME        NOT NULL              COMMENT '계정 생성 시각 (UTC)',
    PRIMARY KEY (`account_id`),
    UNIQUE KEY `ux_accounts_username` (`username`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='계정 인증 정보';

-- ──────────────────────────────────────────────
-- players: 로그인 세션 정보 (게임 데이터)
-- ──────────────────────────────────────────────
DROP TABLE IF EXISTS `players`;
CREATE TABLE `players` (
    `id`          BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    `account_id`  BIGINT UNSIGNED NOT NULL               COMMENT 'IdGenerators.Account.Next() 값 (= accounts.account_id)',
    `player_name` VARCHAR(64)     NOT NULL,
    `login_at`    DATETIME        NOT NULL               COMMENT '로그인 시각 (UTC)',
    `logout_at`   DATETIME        NULL DEFAULT NULL      COMMENT '로그아웃 시각. NULL = 현재 온라인',
    `ip_address`  VARCHAR(45)     NULL DEFAULT NULL      COMMENT 'IPv4 or IPv6',
    PRIMARY KEY (`id`),
    UNIQUE KEY `ux_players_account_id` (`account_id`),
    KEY `ix_players_name` (`player_name`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='플레이어 로그인 세션 정보';

-- ──────────────────────────────────────────────
-- TODO [미래 Phase A]: account_bans 테이블 (정지 계정)
-- TODO [미래 Phase D]: player_stats 테이블 (게임 통계)
-- TODO [미래 Phase D]: player_items 테이블 (인벤토리)
-- TODO [미래]: player_id 컬럼 추가 시 1개 계정이 N개 플레이어 소유 가능
-- ──────────────────────────────────────────────

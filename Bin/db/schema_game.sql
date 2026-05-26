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
    `password_hash` VARCHAR(255)    NOT NULL              COMMENT 'BCrypt(workFactor=11) 해시',
    `email`         VARCHAR(255)    DEFAULT NULL          COMMENT '비밀번호 재설정용 이메일 (선택)',
    `created_at`    DATETIME        NOT NULL              COMMENT '계정 생성 시각 (UTC)',
    PRIMARY KEY (`account_id`),
    UNIQUE KEY `ux_accounts_username` (`username`),
    UNIQUE KEY `ux_accounts_email`    (`email`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='계정 인증 정보';

-- ──────────────────────────────────────────────
-- password_reset_tokens: 비밀번호 재설정 토큰
-- ──────────────────────────────────────────────
DROP TABLE IF EXISTS `password_reset_tokens`;
CREATE TABLE `password_reset_tokens` (
    `token_id`   BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    `account_id` BIGINT UNSIGNED NOT NULL,
    `token`      CHAR(64)        NOT NULL                 COMMENT 'SHA-256 hex, 1회용',
    `expires_at` DATETIME        NOT NULL                 COMMENT '유효 만료 시각 (UTC+1h)',
    `used_at`    DATETIME        DEFAULT NULL             COMMENT '사용된 시각 (NULL=미사용)',
    PRIMARY KEY (`token_id`),
    UNIQUE KEY `ux_reset_token` (`token`),
    KEY `ix_reset_account`     (`account_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='비밀번호 재설정 토큰';

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
-- characters: RPG 캐릭터 영속 데이터 (1계정 1캐릭터)
-- ──────────────────────────────────────────────
DROP TABLE IF EXISTS `characters`;
CREATE TABLE `characters` (
    `account_id`  BIGINT UNSIGNED NOT NULL               COMMENT 'accounts.account_id FK',
    `gold`        INT             NOT NULL DEFAULT 0      COMMENT '보유 골드 (세션 간 영속)',
    `updated_at`  DATETIME        NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    PRIMARY KEY (`account_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='RPG 캐릭터 영속 데이터 (인게임 스탯은 게임마다 초기화)';

-- ──────────────────────────────────────────────
-- TODO [미래 Phase A]: account_bans 테이블 (정지 계정)
-- TODO [미래 Phase D]: player_items 테이블 (인벤토리)
-- ──────────────────────────────────────────────

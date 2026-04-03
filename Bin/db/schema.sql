-- 전체 스키마 적용 래퍼
-- 아래 두 파일을 순서대로 실행하거나, 각각 별도로 실행한다.
--
-- mysql -u root -p0000 < db/schema_game.sql
-- mysql -u root -p0000 < db/schema_log.sql
--
-- 또는 이 파일 하나로 실행:
-- mysql -u root -p0000 -e "source db/schema_game.sql; source db/schema_log.sql;"

SOURCE schema_game.sql;
SOURCE schema_log.sql;

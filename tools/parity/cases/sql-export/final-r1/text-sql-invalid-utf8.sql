CREATE TABLE a (v);
INSERT INTO a VALUES (CAST(x'ff' AS TEXT));
CREATE TABLE b (w);
PRAGMA writable_schema=ON;
UPDATE sqlite_master SET sql=sql || CAST(x'202d2d20ff' AS TEXT) WHERE name='b';

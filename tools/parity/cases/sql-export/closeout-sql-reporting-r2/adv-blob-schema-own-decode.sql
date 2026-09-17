CREATE TABLE a (v);
INSERT INTO a VALUES (CAST(x'80' AS TEXT));
PRAGMA writable_schema=ON;
UPDATE sqlite_master SET sql=CAST(sql AS BLOB) WHERE name='a';

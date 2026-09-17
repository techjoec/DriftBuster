CREATE TABLE a (v);
INSERT INTO a VALUES ('x');
PRAGMA writable_schema=ON;
UPDATE sqlite_master SET sql=CAST(sql AS BLOB) WHERE name='a';

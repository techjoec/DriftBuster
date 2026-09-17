CREATE TABLE t (v);
PRAGMA writable_schema=ON;
UPDATE sqlite_master SET sql = CAST(sql AS BLOB) WHERE name = 't';

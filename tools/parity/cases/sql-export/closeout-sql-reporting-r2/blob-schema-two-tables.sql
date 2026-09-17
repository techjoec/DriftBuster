CREATE TABLE t (v);
CREATE TABLE u (w);
INSERT INTO t VALUES ('secret');
INSERT INTO u VALUES ('x');
PRAGMA writable_schema=ON;
UPDATE sqlite_master SET sql=CAST(sql AS BLOB) WHERE name='t';

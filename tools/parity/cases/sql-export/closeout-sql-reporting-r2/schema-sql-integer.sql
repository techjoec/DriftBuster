CREATE TABLE t (v);
INSERT INTO t VALUES (1);
PRAGMA writable_schema=ON;
UPDATE sqlite_master SET sql=5 WHERE name='t';

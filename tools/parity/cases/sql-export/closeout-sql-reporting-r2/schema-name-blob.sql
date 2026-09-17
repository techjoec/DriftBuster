CREATE TABLE t (v);
CREATE TABLE u (w);
INSERT INTO t VALUES (1);
PRAGMA writable_schema=ON;
UPDATE sqlite_master SET name=CAST(name AS BLOB), tbl_name=CAST(tbl_name AS BLOB) WHERE name='t';

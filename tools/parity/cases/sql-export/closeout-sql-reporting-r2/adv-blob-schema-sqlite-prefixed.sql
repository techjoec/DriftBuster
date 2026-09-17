CREATE TABLE a (v);
CREATE TABLE b (w);
INSERT INTO b VALUES ('ok');
PRAGMA writable_schema=ON;
UPDATE sqlite_master SET sql=CAST(sql AS BLOB), name='sqlite_a', tbl_name='sqlite_a' WHERE name='a';

CREATE TABLE t (v);
PRAGMA writable_schema=ON;
UPDATE sqlite_master SET name=5 WHERE name='t';

CREATE TABLE t (v);
PRAGMA writable_schema=ON;
UPDATE sqlite_master SET sql=NULL WHERE name='t';

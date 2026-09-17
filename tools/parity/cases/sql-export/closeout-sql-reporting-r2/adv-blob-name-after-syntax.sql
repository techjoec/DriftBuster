CREATE TABLE "a b" (v);
CREATE TABLE z (w);
PRAGMA writable_schema=ON;
UPDATE sqlite_master SET name=CAST(name AS BLOB), tbl_name=CAST(tbl_name AS BLOB) WHERE name='z';

CREATE TABLE a (
 v
);
CREATE TABLE b (
 w
);
INSERT INTO b VALUES ('
');
PRAGMA writable_schema=ON;
UPDATE sqlite_master SET sql=CAST(sql AS BLOB) WHERE name='a';

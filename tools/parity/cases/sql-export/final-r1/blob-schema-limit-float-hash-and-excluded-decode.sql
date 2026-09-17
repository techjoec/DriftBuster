CREATE TABLE a (v, s);
INSERT INTO a VALUES (1, 'x');
INSERT INTO a VALUES (2, x'00ff');
CREATE TABLE b (w);
INSERT INTO b VALUES (CAST(x'ff' AS TEXT));
PRAGMA writable_schema=ON;
UPDATE sqlite_master SET sql=CAST(sql || ' -- ' || x'ff' AS BLOB) WHERE name='a';

CREATE TABLE t (v, w);
INSERT INTO t VALUES ('a' || char(0) || 'b', CAST(('x' || char(0) || 'y') AS BLOB));
INSERT INTO t VALUES (char(0), '');

CREATE TABLE w (k TEXT PRIMARY KEY, v) WITHOUT ROWID;
INSERT INTO w VALUES ('b', 1);
INSERT INTO w VALUES ('a', 2);
CREATE TABLE r (v);
INSERT INTO r (rowid, v) VALUES (5, 'five');
INSERT INTO r (rowid, v) VALUES (2, 'two');
INSERT INTO r (rowid, v) VALUES (9, 'nine');
DELETE FROM r WHERE rowid = 5;

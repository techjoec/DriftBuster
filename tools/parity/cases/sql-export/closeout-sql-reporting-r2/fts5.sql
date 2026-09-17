CREATE VIRTUAL TABLE f USING fts5(a, b);
INSERT INTO f VALUES ('hello world', 'leaf');
INSERT INTO f VALUES ('second row', 'x');

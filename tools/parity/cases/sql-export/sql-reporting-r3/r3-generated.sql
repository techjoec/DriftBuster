CREATE TABLE g (a INTEGER, b AS (a*2), c AS (a*3) STORED, d TEXT);
INSERT INTO g(a, d) VALUES (1, 'x');
INSERT INTO g(a, d) VALUES (2, 'y');

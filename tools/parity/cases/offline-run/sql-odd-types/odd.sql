CREATE TABLE mixed (v);
INSERT INTO mixed VALUES (NULL), (0), (-1), (3.0), (-0.0), (1e-300), (-1e999), ('text'), (X'DEADBEEF'), (''), ('éè'), (2.5e15);
CREATE TABLE nums (i INTEGER, r REAL, n NUMERIC);
INSERT INTO nums VALUES ('12', '1.25', '007'), (1.0, 2, 'abc'), (NULL, 'x', 1e400);
CREATE TABLE "Quoted_Name" ("select" TEXT, "col two" INTEGER);
INSERT INTO "Quoted_Name" VALUES ('reserved column', 2);

CREATE VIRTUAL TABLE box USING rtree(id, minx, maxx);
INSERT INTO box VALUES (1, 0.5, 2.5);

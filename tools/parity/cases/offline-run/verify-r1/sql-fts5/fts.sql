CREATE VIRTUAL TABLE docs USING fts5(title, body);
INSERT INTO docs VALUES ('config', 'server=alpha');

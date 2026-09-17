CREATE TABLE accounts (id INTEGER PRIMARY KEY, email TEXT, secret TEXT, n REAL); INSERT INTO accounts VALUES (1, 'a@x', 's1', 1.5), (2, 'b@x', 's2', -0.0), (3, NULL, '', 1e300);

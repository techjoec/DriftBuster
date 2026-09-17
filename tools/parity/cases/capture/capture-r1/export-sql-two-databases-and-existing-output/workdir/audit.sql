CREATE TABLE accounts (id INTEGER PRIMARY KEY, email TEXT, secret TEXT, balance REAL);
INSERT INTO accounts (email, secret, balance) VALUES ('alice@example.com', 'token-1', 42.5);
INSERT INTO accounts (email, secret, balance) VALUES ('bob@example.com', 'token-2', 13.75);
CREATE TABLE audit (id INTEGER PRIMARY KEY, payload BLOB);
INSERT INTO audit (payload) VALUES (x'617564697400ff');

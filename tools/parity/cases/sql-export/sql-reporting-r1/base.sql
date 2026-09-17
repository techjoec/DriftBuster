CREATE TABLE accounts (id INTEGER PRIMARY KEY, email TEXT, secret TEXT, balance REAL);
INSERT INTO accounts (email, secret, balance) VALUES ('alice@example.com', 'token-1', 42.5);
INSERT INTO accounts (email, secret, balance) VALUES ('bob@example.com', 'token-2', 13.75);
INSERT INTO accounts (email, secret, balance) VALUES ('carol@example.com', NULL, 0.125);

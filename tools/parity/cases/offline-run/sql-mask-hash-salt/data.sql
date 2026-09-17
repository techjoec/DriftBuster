CREATE TABLE accounts (id INTEGER PRIMARY KEY, email TEXT, secret TEXT, balance REAL, raw BLOB, note);
INSERT INTO accounts (email, secret, balance, raw, note) VALUES ('alice@example.com', 't1', 42.5, X'00FF2722', 'café 😀');
INSERT INTO accounts (email, secret, balance, raw, note) VALUES ('bob@example.com', NULL, 0.1, NULL, 12345678901234567);
INSERT INTO accounts (email, secret, balance, raw, note) VALUES (NULL, 't3', 1e20, X'', 1.5e-7);
INSERT INTO accounts (email, secret, balance, raw, note)
  VALUES ('carol@example.com', 't4', 1e999, X'616263', 'line' || char(10) || 'break' || char(9) || '"q"');
CREATE TABLE Zeta (k TEXT);
INSERT INTO Zeta VALUES ('z');
CREATE TABLE alpha (k INTEGER);
INSERT INTO alpha VALUES (-9223372036854775808);
INSERT INTO alpha VALUES (9223372036854775807);
CREATE VIEW v AS SELECT 1;
CREATE INDEX idx_email ON accounts(email);

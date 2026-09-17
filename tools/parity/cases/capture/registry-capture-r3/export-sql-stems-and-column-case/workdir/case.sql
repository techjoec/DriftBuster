CREATE TABLE Accounts (id INTEGER PRIMARY KEY, Email TEXT, secret TEXT);
INSERT INTO Accounts VALUES (1, 'a@x', 's1');
INSERT INTO Accounts VALUES (2, 'b@x', 's2');
CREATE TABLE MixedCase (k TEXT, v TEXT);
INSERT INTO MixedCase VALUES ('k', 'v');

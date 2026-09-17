CREATE TABLE t (
  v TEXT -- crlf schema
);
-- comment holding a CR that does not end it, ended by CRLF
INSERT INTO t VALUES ('a
b');
INSERT INTO t VALUES ('cd');

CREATE TABLE t (v);
INSERT INTO t VALUES ('a' || char(13) || char(10) || 'b' || char(9) || char(8232) || char(133) || char(127) || char(1) || char(8203) || '\' || char(11) || char(12));

CREATE TABLE u (k TEXT, v TEXT, b BLOB);
INSERT INTO u VALUES ('été', 'naïve ☃ 𝔘', x'e282ac');
INSERT INTO u VALUES ('tab', 'line1' || char(10) || 'line2' || char(9) || 'end', x'0a0d09');
INSERT INTO u VALUES ('nul', 'a' || char(0) || 'b', x'00');

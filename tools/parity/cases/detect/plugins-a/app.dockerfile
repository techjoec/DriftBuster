ARG BASE=alpine:3.20
FROM ${BASE}
RUN apk add --no-cache curl
CMD ["sh"]

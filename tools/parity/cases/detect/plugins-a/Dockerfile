# syntax=docker/dockerfile:1
FROM python:3.12-slim
WORKDIR /app
COPY . .
RUN pip install -e .
ENTRYPOINT ["python", "-m", "driftbuster.cli"]

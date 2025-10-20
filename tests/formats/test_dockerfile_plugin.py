from pathlib import Path

from driftbuster.formats.dockerfile.plugin import DockerfilePlugin


def _detect(name: str, content: str):
    p = Path(name)
    pl = DockerfilePlugin()
    return pl.detect(p, content.encode("utf-8"), content)


def test_dockerfile_detection():
    text = """
    # base image
    FROM python:3.11-slim
    RUN pip install -U pip
    COPY . /app
    WORKDIR /app
    """
    m = _detect("Dockerfile", text)
    assert m is not None
    assert m.format_name == "dockerfile"
    assert m.variant == "generic"


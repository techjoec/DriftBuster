from pathlib import Path

from driftbuster.formats.conf.plugin import ConfPlugin


def test_logstash_pipeline_detection():
    text = """
    input {
      beats {
        port => 5044
      }
    }
    filter {
      mutate { add_field => { "[@metadata][index]" => "logs" } }
    }
    output {
      stdout { codec => rubydebug }
    }
    """
    plugin = ConfPlugin()
    match = plugin.detect(Path("logstash.conf"), text.encode("utf-8"), text)
    assert match is not None
    assert match.format_name == "unix-conf"
    assert match.variant == "logstash-pipeline"
    assert match.confidence >= 0.72

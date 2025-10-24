# YAML Fixtures

This directory contains curated YAML samples used by the structured-text
plugins and regression tests. All payloads are synthetic and scrubbed of any
customer-identifiable content.

## multi-doc-sample.yaml

*Purpose*: Exercises multi-document parsing heuristics (`---` / `...`) used by
`YamlPlugin` so indentation tolerances remain stable across boundaries.

*Source notes*: Adapted from internal integration examples with secrets,
identifiers, and environment references replaced by placeholders. Normalised to
Unix newlines to keep whitespace analysis deterministic.


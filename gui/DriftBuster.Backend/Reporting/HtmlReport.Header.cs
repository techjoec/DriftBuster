namespace DriftBuster.Backend.Reporting;

public static partial class HtmlReport
{
    // The page head, split around the title; every line ends with LF on every platform.
    private const string HeaderBeforeTitle = "<!doctype html>\n<html lang=\"en\">\n<head>\n  <meta charset=\"utf-8\" />\n  <title>";

    private static readonly string HeaderAfterTitle = string.Join(
        '\n',
        "</title>",
        "  <style>",
        "    body { font-family: Arial, sans-serif; margin: 2rem; background: #111; color: #eee; }",
        "    h1, h2 { color: #f6c744; }",
        "    .meta { font-size: 0.9rem; color: #ccc; margin-bottom: 1rem; }",
        "    .warning { border: 1px solid #d9534f; padding: 1rem; margin-bottom: 1.5rem; background: #2a0000; }",
        "    .badge {",
        "      display: inline-block;",
        "      padding: 0.1rem 0.4rem;",
        "      border-radius: 0.25rem;",
        "      font-size: 0.75rem;",
        "      margin-left: 0.5rem;",
        "      background: #f6c744;",
        "      color: #111;",
        "    }",
        "    .match,",
        "    .hunt-section {",
        "      border: 1px solid #333;",
        "      padding: 1rem;",
        "      margin-bottom: 1rem;",
        "      background: #1a1a1a;",
        "    }",
        "    .match h3 { margin-top: 0; }",
        "    table { width: 100%; border-collapse: collapse; margin-top: 0.5rem; }",
        "    th, td { border: 1px solid #333; padding: 0.5rem; text-align: left; }",
        "    .summary-table { margin-bottom: 1rem; }",
        "    .hunt-section ul { margin: 0.5rem 0 0 1rem; }",
        "    .redaction-summary { margin-top: 1.5rem; padding: 1rem; background: #1f1f1f; border: 1px solid #444; }",
        "  </style>",
        "</head>",
        "<body>",
        string.Empty);
}

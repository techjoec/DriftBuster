namespace DriftBuster.Backend.Models;

/// <summary>
/// One profile run; also its <c>metadata.json</c>. <see cref="Baseline"/> is the baseline source path (the first source when the
/// profile names none).
/// </summary>
public sealed record RunProfileRunResult(
    RunProfileDefinition Profile,
    string Timestamp,
    string OutputDir,
    string Baseline,
    IReadOnlyList<RunProfileSourceResult> Sources,
    IReadOnlyList<RunProfileFileResult> Files,
    SecretRunSummary Secrets);

using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

namespace DriftBuster.Backend.Diff;

/// <summary>Line counts of a diff (<see cref="LineDiff.CalculateStats(IReadOnlyList{LineChange})"/>), serialised as <c>added_lines</c>, <c>removed_lines</c> and <c>changed_lines</c>.</summary>
[StructLayout(LayoutKind.Auto)]
public readonly record struct DiffStats(
    [property: JsonPropertyName("added_lines")] int AddedLines,
    [property: JsonPropertyName("removed_lines")] int RemovedLines,
    [property: JsonPropertyName("changed_lines")] int ChangedLines);

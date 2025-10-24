using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

using Microsoft.Extensions.FileSystemGlobbing;

using DriftBuster.Backend.Models;

namespace DriftBuster.Backend
{
    public interface IDriftbusterBackend
    {
        Task<string> PingAsync(CancellationToken cancellationToken = default);

        Task<DiffResult> DiffAsync(IEnumerable<string?> versions, CancellationToken cancellationToken = default);

        Task<HuntResult> HuntAsync(string? directory, string? pattern, CancellationToken cancellationToken = default);

        Task<RunProfileListResult> ListProfilesAsync(string? baseDir = null, CancellationToken cancellationToken = default);

        Task SaveProfileAsync(RunProfileDefinition profile, string? baseDir = null, CancellationToken cancellationToken = default);

        Task<RunProfileRunResult> RunProfileAsync(RunProfileDefinition profile, bool saveProfile, string? baseDir = null, string? timestamp = null, CancellationToken cancellationToken = default);

        Task<OfflineCollectorResult> PrepareOfflineCollectorAsync(
            RunProfileDefinition profile,
            OfflineCollectorRequest request,
            string? baseDir = null,
            CancellationToken cancellationToken = default);

        Task<ServerScanResponse> RunServerScansAsync(
            IEnumerable<ServerScanPlan> plans,
            IProgress<ScanProgress>? progress = null,
            CancellationToken cancellationToken = default);
    }

    [ExcludeFromCodeCoverage]
    public sealed class DriftbusterBackend : IDriftbusterBackend
    {
        private const int HuntSampleSize = 128 * 1024;
        private const string RedactedPlaceholder = "[REDACTED]";
        private const string SecretRulesResourceName = "DriftBuster.Backend.Resources.secret_rules.json";
        private const string MultiServerModule = "driftbuster.multi_server";
        private const string MultiServerSchemaVersion = "multi-server.v1";

        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = true,
            Converters =
            {
                new JsonStringEnumMemberConverter()
            },
        };

        private static readonly Encoding Utf8 = new UTF8Encoding(false, false);
        private static readonly HashSet<string> XmlExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".config",
            ".csproj",
            ".resx",
            ".targets",
            ".vbproj",
            ".xml",
            ".xaml",
            ".xslt",
        };
        private static readonly IReadOnlyList<HuntRuleDefinition> HuntRules = new[]
        {
            new HuntRuleDefinition(
                "server-name",
                "Potential hostnames, server names, or FQDN references",
                "server_name",
                new[] { "server", "host" },
                new[]
                {
                    new Regex(@"\b[a-z0-9_-]+\.(local|lan|corp|com|net|internal)\b", RegexOptions.IgnoreCase | RegexOptions.Multiline),
                }),
            new HuntRuleDefinition(
                "certificate-thumbprint",
                "Likely certificate thumbprints",
                "certificate_thumbprint",
                new[] { "thumbprint", "certificate" },
                new[]
                {
                    new Regex(@"\b[0-9a-f]{40}\b", RegexOptions.IgnoreCase),
                    new Regex(@"\b[0-9a-f]{64}\b", RegexOptions.IgnoreCase),
                }),
            new HuntRuleDefinition(
                "version-number",
                "Version identifiers (semver style)",
                "version",
                new[] { "version" },
                new[]
                {
                    new Regex(@"\b\d+\.\d+\.\d+(?:\.\d+)?\b", RegexOptions.IgnoreCase),
                }),
            new HuntRuleDefinition(
                "install-path",
                "Suspicious installation or directory paths",
                "install_path",
                new[] { "path", "install" },
                new[]
                {
                    new Regex(@"[A-Za-z]:\\\\[\\\\\w\-\. ]+", RegexOptions.IgnoreCase),
                    new Regex(@"/opt/[\w\-\.]+", RegexOptions.IgnoreCase),
                }),
        };

        private static readonly char[] GlobCharacters = { '*', '?', '[' };

        public Task<string> PingAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult("pong");
        }

        public Task<DiffResult> DiffAsync(IEnumerable<string?> versions, CancellationToken cancellationToken = default)
        {
            return Task.Run(() => BuildDiffResult(versions, cancellationToken), cancellationToken);
        }

        public Task<HuntResult> HuntAsync(string? directory, string? pattern, CancellationToken cancellationToken = default)
        {
            return Task.Run(() => BuildHuntResult(directory, pattern, cancellationToken), cancellationToken);
        }

        public Task<RunProfileListResult> ListProfilesAsync(string? baseDir = null, CancellationToken cancellationToken = default)
        {
            return Task.Run(() => RunProfileManager.ListProfiles(baseDir, cancellationToken), cancellationToken);
        }

        public Task SaveProfileAsync(RunProfileDefinition profile, string? baseDir = null, CancellationToken cancellationToken = default)
        {
            return Task.Run(() => RunProfileManager.SaveProfile(profile, baseDir, cancellationToken), cancellationToken);
        }

        public Task<RunProfileRunResult> RunProfileAsync(RunProfileDefinition profile, bool saveProfile, string? baseDir = null, string? timestamp = null, CancellationToken cancellationToken = default)
        {
            return Task.Run(() => RunProfileManager.RunProfile(profile, saveProfile, baseDir, timestamp, cancellationToken), cancellationToken);
        }

        public Task<OfflineCollectorResult> PrepareOfflineCollectorAsync(RunProfileDefinition profile, OfflineCollectorRequest request, string? baseDir = null, CancellationToken cancellationToken = default)
        {
            return Task.Run(() => RunProfileManager.PrepareOfflineCollector(profile, request, baseDir, cancellationToken), cancellationToken);
        }

        public async Task<ServerScanResponse> RunServerScansAsync(
            IEnumerable<ServerScanPlan> plans,
            IProgress<ScanProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (plans is null)
            {
                throw new ArgumentNullException(nameof(plans));
            }

            var planList = plans.Select(ClonePlan).ToList();
            if (planList.Count == 0)
            {
                return new ServerScanResponse
                {
                    Version = MultiServerSchemaVersion,
                    Results = Array.Empty<ServerScanResult>(),
                    Catalog = Array.Empty<ConfigCatalogEntry>(),
                    Drilldown = Array.Empty<ConfigDrilldown>(),
                    Summary = new ServerScanSummary
                    {
                        BaselineHostId = string.Empty,
                        TotalHosts = 0,
                        ConfigsEvaluated = 0,
                        DriftingConfigs = 0,
                        GeneratedAt = DateTimeOffset.UtcNow,
                    },
                };
            }

            foreach (var plan in planList)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (string.IsNullOrWhiteSpace(plan.HostId))
                {
                    plan.HostId = Guid.NewGuid().ToString("N");
                }

                if (string.IsNullOrWhiteSpace(plan.Label))
                {
                    plan.Label = plan.HostId;
                }

                plan.Baseline ??= new ServerScanBaselinePreference();
                plan.Export ??= new ServerScanExportOptions();

                progress?.Report(new ScanProgress
                {
                    HostId = plan.HostId,
                    Status = ServerScanStatus.Queued,
                    Message = "Queued",
                    Timestamp = DateTimeOffset.UtcNow,
                });
            }

            var repositoryRoot = ResolveRepositoryRoot();
            var request = BuildMultiServerRequest(planList, repositoryRoot);
            var response = await ExecuteMultiServerAsync(request, progress, cancellationToken, repositoryRoot).ConfigureAwait(false);

            if (response is null)
            {
                throw new InvalidOperationException("Multi-server runner returned no payload.");
            }

            if (string.IsNullOrWhiteSpace(response.Version))
            {
                response.Version = MultiServerSchemaVersion;
            }

            ValidateMultiServerResponse(response);
            return response;
        }

        private static DiffResult BuildDiffResult(IEnumerable<string?> versions, CancellationToken cancellationToken)
        {
            var versionList = versions?.ToList() ?? new List<string?>();
            if (versionList.Count < 2)
            {
                throw new InvalidOperationException("Provide at least two file paths via 'versions'.");
            }

            var resolved = versionList
                .Select(ResolvePath)
                .ToList();

            var baselinePath = EnsureFile(resolved[0], true);
            var baselineName = Path.GetFileName(baselinePath);
            var baselineContent = ReadText(baselinePath);

            var comparisons = new List<DiffComparison>();

            for (var index = 1; index < resolved.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var candidatePath = EnsureFile(resolved[index], false);
                var candidateName = Path.GetFileName(candidatePath);
                var candidateContent = ReadText(candidatePath);

                var contentType = DetectContentType(baselinePath, candidatePath);
                var canonicalBefore = CanonicaliseContent(baselineContent, contentType);
                var canonicalAfter = CanonicaliseContent(candidateContent, contentType);
                var beforeLines = SplitLines(canonicalBefore);
                var afterLines = SplitLines(canonicalAfter);
                var unifiedDiff = BuildUnifiedDiff(beforeLines, afterLines, baselineName, candidateName, 3);

                var plan = new DiffPlan
                {
                    Before = canonicalBefore,
                    After = canonicalAfter,
                    ContentType = contentType,
                    FromLabel = baselineName,
                    ToLabel = candidateName,
                    Placeholder = RedactedPlaceholder,
                    ContextLines = 3,
                };

                comparisons.Add(new DiffComparison
                {
                    From = baselineName,
                    To = candidateName,
                    Plan = plan,
                    Metadata = new DiffMetadata
                    {
                        LeftPath = baselinePath,
                        RightPath = candidatePath,
                        ContentType = contentType,
                        ContextLines = 3,
                    },
                    UnifiedDiff = unifiedDiff,
                });
            }

            var result = new DiffResult
            {
                Versions = resolved.ToArray(),
                Comparisons = comparisons.ToArray(),
            };

            var summary = BuildSanitizedSummary(result, resolved);
            result.Summary = summary;
            result.RawJson = JsonSerializer.Serialize(result, SerializerOptions);
            result.SanitizedJson = JsonSerializer.Serialize(summary, SerializerOptions);
            return result;
        }

        private static string DetectContentType(string baselinePath, string candidatePath)
        {
            if (IsXmlLike(baselinePath) || IsXmlLike(candidatePath))
            {
                return "xml";
            }

            return "text";
        }

        private static bool IsXmlLike(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            var extension = Path.GetExtension(path);
            return !string.IsNullOrEmpty(extension) && XmlExtensions.Contains(extension);
        }

        private static DiffResultSummary BuildSanitizedSummary(DiffResult result, IReadOnlyList<string?> resolved)
        {
            var versions = resolved
                .Select(path => string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetFileName(path))
                .ToArray();

            var summaries = result.Comparisons
                .Select(BuildComparisonSummary)
                .ToArray();

            return new DiffResultSummary
            {
                GeneratedAt = DateTimeOffset.UtcNow,
                Versions = versions,
                ComparisonCount = summaries.Length,
                Comparisons = summaries,
            };
        }

        private static DiffComparisonSummary BuildComparisonSummary(DiffComparison comparison)
        {
            var plan = comparison.Plan ?? new DiffPlan();
            var metadata = comparison.Metadata ?? new DiffMetadata();

            var canonicalBefore = CanonicaliseContent(plan.Before ?? string.Empty, plan.ContentType);
            var canonicalAfter = CanonicaliseContent(plan.After ?? string.Empty, plan.ContentType);

            var beforeLines = SplitLines(canonicalBefore);
            var afterLines = SplitLines(canonicalAfter);
            var stats = ComputeDiffStats(beforeLines, afterLines);

            return new DiffComparisonSummary
            {
                From = comparison.From ?? string.Empty,
                To = comparison.To ?? string.Empty,
                Plan = new DiffPlanSummary
                {
                    ContentType = plan.ContentType ?? string.Empty,
                    FromLabel = plan.FromLabel,
                    ToLabel = plan.ToLabel,
                    Label = plan.Label,
                    MaskTokens = plan.MaskTokens?.Where(token => !string.IsNullOrWhiteSpace(token)).ToArray()
                        ?? Array.Empty<string>(),
                    Placeholder = plan.Placeholder ?? string.Empty,
                    ContextLines = plan.ContextLines,
                },
                Metadata = new DiffMetadataSummary
                {
                    ContentType = metadata.ContentType ?? string.Empty,
                    ContextLines = metadata.ContextLines,
                    BaselineName = SafeFileName(metadata.LeftPath),
                    ComparisonName = SafeFileName(metadata.RightPath),
                },
                Summary = new DiffChangeSummary
                {
                    BeforeDigest = ComputeDigest(canonicalBefore),
                    AfterDigest = ComputeDigest(canonicalAfter),
                    DiffDigest = ComputeDigest(BuildDiffDigestSeed(canonicalBefore, canonicalAfter)),
                    BeforeLines = beforeLines.Count,
                    AfterLines = afterLines.Count,
                    AddedLines = stats.AddedLines,
                    RemovedLines = stats.RemovedLines,
                    ChangedLines = stats.ChangedLines,
                },
            };
        }

        private static string SafeFileName(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            try
            {
                return Path.GetFileName(path);
            }
            catch
            {
                return path;
            }
        }

        private static string CanonicaliseContent(string value, string? contentType)
        {
            if (string.Equals(contentType, "xml", StringComparison.OrdinalIgnoreCase))
            {
                return CanonicaliseXml(value);
            }

            return CanonicaliseText(value);
        }

        private static string CanonicaliseText(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            var normalised = value.Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace("\r", "\n", StringComparison.Ordinal);
            var lines = normalised.Split('\n');
            for (var index = 0; index < lines.Length; index++)
            {
                lines[index] = lines[index].TrimEnd();
            }

            return string.Join("\n", lines);
        }

        private static readonly Regex XmlDeclarationPattern = new("<\\?xml[^>]*\\?>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        private static string CanonicaliseXml(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var working = value.TrimStart();
            var declarationMatch = XmlDeclarationPattern.Match(working);
            var xmlDeclaration = string.Empty;
            if (declarationMatch.Success)
            {
                xmlDeclaration = declarationMatch.Value;
                working = working[declarationMatch.Length..].TrimStart();
            }

            var doctype = ExtractDoctype(ref working, value);

            try
            {
                var settings = new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Parse,
                    IgnoreComments = false,
                    IgnoreWhitespace = false,
                };

                using var reader = XmlReader.Create(new StringReader(working), settings);
                var document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
                if (document.Root is not null)
                {
                    NormaliseElement(document.Root);
                }

                var serialised = document.Root?.ToString(SaveOptions.DisableFormatting) ?? string.Empty;
                var parts = new List<string>();
                if (!string.IsNullOrEmpty(xmlDeclaration))
                {
                    parts.Add(xmlDeclaration);
                }

                if (!string.IsNullOrEmpty(doctype))
                {
                    parts.Add(doctype);
                }

                parts.Add(serialised);
                return string.Join("\n", parts.Where(part => !string.IsNullOrEmpty(part)));
            }
            catch
            {
                return CanonicaliseText(value);
            }
        }

        private static string ExtractDoctype(ref string working, string original)
        {
            if (!working.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            var builder = new StringBuilder();
            var depth = 0;
            for (var index = 0; index < working.Length; index++)
            {
                var character = working[index];
                builder.Append(character);
                if (character == '[')
                {
                    depth++;
                }
                else if (character == ']')
                {
                    depth = Math.Max(0, depth - 1);
                }
                else if (character == '>' && depth == 0)
                {
                    working = working[(index + 1)..].TrimStart();
                    return builder.ToString();
                }
            }

            working = original;
            return string.Empty;
        }

        private static void NormaliseElement(XElement element)
        {
            var attributes = element.Attributes()
                .OrderBy(attribute => attribute.Name.ToString(), StringComparer.Ordinal)
                .ToList();
            element.RemoveAttributes();
            foreach (var attribute in attributes)
            {
                var value = attribute.Value;
                var trimmed = value.Trim();
                element.SetAttributeValue(attribute.Name, string.IsNullOrEmpty(trimmed) ? trimmed : value);
            }

            if (element.FirstNode is XText firstText)
            {
                var trimmed = firstText.Value.Trim();
                if (string.IsNullOrEmpty(trimmed))
                {
                    firstText.Value = trimmed;
                }
            }

            foreach (var node in element.Nodes().ToList())
            {
                if (node is XElement child)
                {
                    NormaliseElement(child);
                }
                else if (node is XText textNode)
                {
                    var trimmed = textNode.Value.Trim();
                    if (string.IsNullOrEmpty(trimmed))
                    {
                        textNode.Value = trimmed;
                    }
                }
            }
        }

        private static IReadOnlyList<string> SplitLines(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return Array.Empty<string>();
            }

            var lines = new List<string>();
            var start = 0;
            for (var index = 0; index < value.Length; index++)
            {
                if (value[index] != '\n')
                {
                    continue;
                }

                lines.Add(value[start..index]);
                start = index + 1;
            }

            if (start < value.Length)
            {
                lines.Add(value[start..]);
            }

            return lines;
        }

        private static string ComputeDigest(string value)
        {
            using var sha256 = SHA256.Create();
            var data = Utf8.GetBytes(value);
            var hash = sha256.ComputeHash(data);
            return $"sha256:{Convert.ToHexString(hash).ToLowerInvariant()}";
        }

        private static string BuildDiffDigestSeed(string canonicalBefore, string canonicalAfter)
        {
            return $"{canonicalBefore}\n---\n{canonicalAfter}";
        }

        private static string BuildUnifiedDiff(
            IReadOnlyList<string> beforeLines,
            IReadOnlyList<string> afterLines,
            string fromLabel,
            string toLabel,
            int contextLines)
        {
            var matcher = new SequenceMatcher(beforeLines, afterLines);
            var opcodes = matcher.GetOpcodes().ToList();
            if (opcodes.Count == 0)
            {
                return $"--- {fromLabel}\n+++ {toLabel}";
            }

            var groups = GroupOpcodes(opcodes, contextLines);
            if (groups.Count == 0)
            {
                return $"--- {fromLabel}\n+++ {toLabel}";
            }

            var builder = new StringBuilder();
            builder.AppendLine($"--- {fromLabel}");
            builder.AppendLine($"+++ {toLabel}");

            foreach (var group in groups)
            {
                if (group.Count == 0)
                {
                    continue;
                }

                var first = group[0];
                var last = group[^1];
                var aStart = first.I1;
                var aEnd = last.I2;
                var bStart = first.J1;
                var bEnd = last.J2;
                var aLen = Math.Max(aEnd - aStart, 0);
                var bLen = Math.Max(bEnd - bStart, 0);
                builder.AppendLine($"@@ -{aStart + 1},{aLen} +{bStart + 1},{bLen} @@");

                foreach (var opcode in group)
                {
                    switch (opcode.Tag)
                    {
                        case SequenceOperation.Equal:
                            for (var i = opcode.I1; i < opcode.I2; i++)
                            {
                                builder.Append(' ');
                                builder.AppendLine(beforeLines[i]);
                            }

                            break;
                        case SequenceOperation.Delete:
                            for (var i = opcode.I1; i < opcode.I2; i++)
                            {
                                builder.Append('-');
                                builder.AppendLine(beforeLines[i]);
                            }

                            break;
                        case SequenceOperation.Insert:
                            for (var j = opcode.J1; j < opcode.J2; j++)
                            {
                                builder.Append('+');
                                builder.AppendLine(afterLines[j]);
                            }

                            break;
                        case SequenceOperation.Replace:
                            for (var i = opcode.I1; i < opcode.I2; i++)
                            {
                                builder.Append('-');
                                builder.AppendLine(beforeLines[i]);
                            }

                            for (var j = opcode.J1; j < opcode.J2; j++)
                            {
                                builder.Append('+');
                                builder.AppendLine(afterLines[j]);
                            }

                            break;
                    }
                }
            }

            return builder.ToString().TrimEnd('\r', '\n');
        }

        private static List<List<SequenceOpcode>> GroupOpcodes(
            List<SequenceOpcode> opcodes,
            int contextLines)
        {
            var groups = new List<List<SequenceOpcode>>();
            var group = new List<SequenceOpcode>();
            var context = Math.Max(contextLines, 0);
            var doubleContext = context * 2;

            foreach (var opcode in opcodes)
            {
                var tag = opcode.Tag;
                var i1 = opcode.I1;
                var i2 = opcode.I2;
                var j1 = opcode.J1;
                var j2 = opcode.J2;

                if (tag == SequenceOperation.Equal && i2 - i1 > doubleContext)
                {
                    group.Add(new SequenceOpcode(tag, i1, i1 + context, j1, j1 + context));
                    if (group.Count > 0)
                    {
                        groups.Add(group);
                    }

                    group = new List<SequenceOpcode>();
                    i1 = Math.Max(i2 - context, i1);
                    j1 = Math.Max(j2 - context, j1);
                }

                group.Add(new SequenceOpcode(tag, i1, i2, j1, j2));
            }

            if (group.Count > 0)
            {
                groups.Add(group);
            }

            if (groups.Count == 0)
            {
                return groups;
            }

            var first = groups[0];
            if (first.Count > 0 && first[0].Tag == SequenceOperation.Equal)
            {
                var op = first[0];
                var startI = Math.Max(op.I2 - context, op.I1);
                var startJ = Math.Max(op.J2 - context, op.J1);
                first[0] = new SequenceOpcode(op.Tag, startI, op.I2, startJ, op.J2);
            }

            var last = groups[^1];
            if (last.Count > 0 && last[^1].Tag == SequenceOperation.Equal)
            {
                var op = last[^1];
                var endI = Math.Min(op.I1 + context, op.I2);
                var endJ = Math.Min(op.J1 + context, op.J2);
                last[^1] = new SequenceOpcode(op.Tag, op.I1, endI, op.J1, endJ);
            }

            groups.RemoveAll(g => g.All(op => op.Tag == SequenceOperation.Equal));
            return groups;
        }

        private static DiffStats ComputeDiffStats(IReadOnlyList<string> beforeLines, IReadOnlyList<string> afterLines)
        {
            if (beforeLines.Count == 0 && afterLines.Count == 0)
            {
                return new DiffStats();
            }

            var matcher = new SequenceMatcher(beforeLines, afterLines);
            var added = 0;
            var removed = 0;
            var changed = 0;

            foreach (var opcode in matcher.GetOpcodes())
            {
                switch (opcode.Tag)
                {
                    case SequenceOperation.Replace:
                        changed += Math.Max(opcode.I2 - opcode.I1, opcode.J2 - opcode.J1);
                        break;
                    case SequenceOperation.Delete:
                        removed += opcode.I2 - opcode.I1;
                        break;
                    case SequenceOperation.Insert:
                        added += opcode.J2 - opcode.J1;
                        break;
                }
            }

            return new DiffStats(added, removed, changed);
        }

        private readonly record struct DiffStats(int AddedLines, int RemovedLines, int ChangedLines);

        private enum SequenceOperation
        {
            Equal,
            Replace,
            Delete,
            Insert,
        }

        private readonly record struct SequenceOpcode(SequenceOperation Tag, int I1, int I2, int J1, int J2);

        private sealed class SequenceMatcher
        {
            private readonly IReadOnlyList<string> _a;
            private readonly IReadOnlyList<string> _b;
            private readonly Dictionary<string, List<int>> _bIndex;

            public SequenceMatcher(IReadOnlyList<string> a, IReadOnlyList<string> b)
            {
                _a = a ?? throw new ArgumentNullException(nameof(a));
                _b = b ?? throw new ArgumentNullException(nameof(b));
                _bIndex = BuildIndex(b);
            }

            public IEnumerable<SequenceOpcode> GetOpcodes()
            {
                var matchingBlocks = GetMatchingBlocks();
                matchingBlocks.Add((_a.Count, _b.Count, 0));

                var i = 0;
                var j = 0;
                foreach (var (ai, bj, size) in matchingBlocks)
                {
                    if (i < ai && j < bj)
                    {
                        yield return new SequenceOpcode(SequenceOperation.Replace, i, ai, j, bj);
                    }
                    else if (i < ai)
                    {
                        yield return new SequenceOpcode(SequenceOperation.Delete, i, ai, j, j);
                    }
                    else if (j < bj)
                    {
                        yield return new SequenceOpcode(SequenceOperation.Insert, i, i, j, bj);
                    }

                    if (size > 0)
                    {
                        yield return new SequenceOpcode(SequenceOperation.Equal, ai, ai + size, bj, bj + size);
                    }

                    i = ai + size;
                    j = bj + size;
                }
            }

            private static Dictionary<string, List<int>> BuildIndex(IReadOnlyList<string> sequence)
            {
                var index = new Dictionary<string, List<int>>(StringComparer.Ordinal);
                for (var position = 0; position < sequence.Count; position++)
                {
                    var value = sequence[position] ?? string.Empty;
                    if (!index.TryGetValue(value, out var list))
                    {
                        list = new List<int>();
                        index[value] = list;
                    }

                    list.Add(position);
                }

                return index;
            }

            private List<(int, int, int)> GetMatchingBlocks()
            {
                var queue = new Stack<(int alo, int ahi, int blo, int bhi)>();
                queue.Push((0, _a.Count, 0, _b.Count));
                var matchingBlocks = new List<(int, int, int)>();

                while (queue.Count > 0)
                {
                    var (alo, ahi, blo, bhi) = queue.Pop();
                    var (i, j, size) = FindLongestMatch(alo, ahi, blo, bhi);
                    if (size <= 0)
                    {
                        continue;
                    }

                    matchingBlocks.Add((i, j, size));

                    if (alo < i && blo < j)
                    {
                        queue.Push((alo, i, blo, j));
                    }

                    if (i + size < ahi && j + size < bhi)
                    {
                        queue.Push((i + size, ahi, j + size, bhi));
                    }
                }

                matchingBlocks.Sort((x, y) =>
                {
                    var compare = x.Item1.CompareTo(y.Item1);
                    return compare != 0 ? compare : x.Item2.CompareTo(y.Item2);
                });

                return matchingBlocks;
            }

            private (int i, int j, int size) FindLongestMatch(int alo, int ahi, int blo, int bhi)
            {
                var bestI = alo;
                var bestJ = blo;
                var bestSize = 0;
                var j2len = new Dictionary<int, int>();

                for (var i = alo; i < ahi; i++)
                {
                    var newJ2Len = new Dictionary<int, int>();
                    var value = _a[i] ?? string.Empty;
                    if (!_bIndex.TryGetValue(value, out var indices))
                    {
                        j2len = newJ2Len;
                        continue;
                    }

                    foreach (var j in indices)
                    {
                        if (j < blo)
                        {
                            continue;
                        }

                        if (j >= bhi)
                        {
                            break;
                        }

                        var previous = j2len.TryGetValue(j - 1, out var length) ? length + 1 : 1;
                        newJ2Len[j] = previous;
                        if (previous > bestSize)
                        {
                            bestSize = previous;
                            bestI = i - bestSize + 1;
                            bestJ = j - bestSize + 1;
                        }
                    }

                    j2len = newJ2Len;
                }

                while (bestI > alo && bestJ > blo && EqualsAt(bestI - 1, bestJ - 1))
                {
                    bestI--;
                    bestJ--;
                    bestSize++;
                }

                while (bestI + bestSize < ahi && bestJ + bestSize < bhi && EqualsAt(bestI + bestSize, bestJ + bestSize))
                {
                    bestSize++;
                }

                return (bestI, bestJ, bestSize);
            }

            private bool EqualsAt(int indexA, int indexB)
            {
                return string.Equals(_a[indexA], _b[indexB], StringComparison.Ordinal);
            }
        }


        private static ServerScanPlan ClonePlan(ServerScanPlan plan)
        {
            if (plan is null)
            {
                return new ServerScanPlan();
            }

            return new ServerScanPlan
            {
                HostId = plan.HostId,
                Label = plan.Label,
                Scope = plan.Scope,
                Roots = plan.Roots?.ToArray() ?? Array.Empty<string>(),
                Baseline = plan.Baseline is null
                    ? new ServerScanBaselinePreference()
                    : new ServerScanBaselinePreference
                    {
                        IsPreferred = plan.Baseline.IsPreferred,
                        Priority = plan.Baseline.Priority,
                        Role = string.IsNullOrWhiteSpace(plan.Baseline.Role) ? "auto" : plan.Baseline.Role,
                    },
                Export = plan.Export is null
                    ? new ServerScanExportOptions()
                    : new ServerScanExportOptions
                    {
                        IncludeCatalog = plan.Export.IncludeCatalog,
                        IncludeDrilldown = plan.Export.IncludeDrilldown,
                        IncludeDiffs = plan.Export.IncludeDiffs,
                        IncludeSummary = plan.Export.IncludeSummary,
                    },
                ThrottleSeconds = plan.ThrottleSeconds,
                CachedAt = plan.CachedAt,
            };
        }

        private static string ResolveRepositoryRoot()
        {
            var current = new DirectoryInfo(Environment.CurrentDirectory);
            while (current is not null)
            {
                if (File.Exists(Path.Combine(current.FullName, "pyproject.toml")))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }

            current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current is not null)
            {
                if (File.Exists(Path.Combine(current.FullName, "pyproject.toml")))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }

            return Environment.CurrentDirectory;
        }

        private static MultiServerRequest BuildMultiServerRequest(List<ServerScanPlan> plans, string repositoryRoot)
        {
            if (plans is null)
            {
                throw new ArgumentNullException(nameof(plans));
            }

            var cacheDirectory = DriftbusterPaths.GetCacheDirectory("diffs");
            MigrateLegacyDiffCache(repositoryRoot, cacheDirectory);
            return new MultiServerRequest
            {
                Plans = plans,
                CacheDirectory = cacheDirectory,
                SchemaVersion = MultiServerSchemaVersion,
            };
        }

        private async Task<ServerScanResponse> ExecuteMultiServerAsync(
            MultiServerRequest request,
            IProgress<ScanProgress>? progress,
            CancellationToken cancellationToken,
            string repositoryRoot)
        {
            if (request is null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            var pythonExecutable = ResolvePythonExecutable();
            var startInfo = CreatePythonStartInfo(pythonExecutable, repositoryRoot);
            var pythonPath = ResolvePythonPath(repositoryRoot);

            if (!string.IsNullOrWhiteSpace(pythonPath))
            {
                if (startInfo.Environment.TryGetValue("PYTHONPATH", out var configured) && !string.IsNullOrWhiteSpace(configured))
                {
                    if (!configured.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Contains(pythonPath, StringComparer.Ordinal))
                    {
                        startInfo.Environment["PYTHONPATH"] = string.Join(Path.PathSeparator, pythonPath, configured);
                    }
                }
                else
                {
                    var inherited = Environment.GetEnvironmentVariable("PYTHONPATH");
                    startInfo.Environment["PYTHONPATH"] = string.IsNullOrWhiteSpace(inherited)
                        ? pythonPath
                        : string.Join(Path.PathSeparator, pythonPath, inherited);
                }
            }

            startInfo.Environment["PYTHONUNBUFFERED"] = "1";

            var requestJson = JsonSerializer.Serialize(request, SerializerOptions);

            using var process = new Process { StartInfo = startInfo };
            using var cancellationRegistration = cancellationToken.Register(() =>
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                }
            });

            try
            {
                if (!process.Start())
                {
                    throw new InvalidOperationException("Failed to launch Python process for multi-server runner.");
                }

                await process.StandardInput.WriteAsync(requestJson).ConfigureAwait(false);
                await process.StandardInput.WriteAsync(Environment.NewLine).ConfigureAwait(false);
                await process.StandardInput.FlushAsync().ConfigureAwait(false);
                process.StandardInput.Close();

                var stderrTask = Task.Run(() => process.StandardError.ReadToEnd());

                ServerScanResponse? response = null;
                string? line;

                while ((line = await process.StandardOutput.ReadLineAsync().ConfigureAwait(false)) is not null)
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    JsonDocument document;
                    try
                    {
                        document = JsonDocument.Parse(line);
                    }
                    catch (JsonException ex)
                    {
                        throw new InvalidOperationException($"Invalid JSON from multi-server runner: {line}", ex);
                    }

                    using (document)
                    {
                        var root = document.RootElement;
                        if (!root.TryGetProperty("type", out var typeElement))
                        {
                            continue;
                        }

                        var type = typeElement.GetString();
                        if (string.Equals(type, "progress", StringComparison.OrdinalIgnoreCase))
                        {
                            if (progress is not null && root.TryGetProperty("payload", out var payloadElement))
                            {
                                var update = payloadElement.Deserialize<ScanProgress>(SerializerOptions);
                                if (update is not null)
                                {
                                    progress.Report(update);
                                }
                            }
                        }
                        else if (string.Equals(type, "result", StringComparison.OrdinalIgnoreCase))
                        {
                            if (root.TryGetProperty("payload", out var payloadElement))
                            {
                                response = payloadElement.Deserialize<ServerScanResponse>(SerializerOptions);
                            }
                        }
                        else if (string.Equals(type, "error", StringComparison.OrdinalIgnoreCase))
                        {
                            var message = root.TryGetProperty("message", out var messageElement)
                                ? messageElement.GetString()
                                : "Multi-server runner reported an error.";
                            throw new InvalidOperationException(message ?? "Multi-server runner reported an error.");
                        }
                    }
                }

                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

                if (process.ExitCode != 0)
                {
                    var stderr = await stderrTask.ConfigureAwait(false);
                    throw new InvalidOperationException($"Python runner failed with exit code {process.ExitCode}: {stderr}");
                }

                return response ?? new ServerScanResponse
                {
                    Version = MultiServerSchemaVersion,
                    Results = Array.Empty<ServerScanResult>(),
                    Catalog = Array.Empty<ConfigCatalogEntry>(),
                    Drilldown = Array.Empty<ConfigDrilldown>(),
                };
            }
            finally
            {
                if (!process.HasExited)
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                        process.WaitForExit();
                    }
                    catch
                    {
                    }
                }
            }
        }

        private static string ResolvePythonExecutable()
        {
            var overridePath = Environment.GetEnvironmentVariable("DRIFTBUSTER_PYTHON");
            if (!string.IsNullOrWhiteSpace(overridePath))
            {
                return overridePath;
            }

            if (TryLocateExecutable("python3", out var python3))
            {
                return python3;
            }

            if (TryLocateExecutable("python", out var python))
            {
                return python;
            }

            return OperatingSystem.IsWindows() ? "python.exe" : "python3";
        }

        private static bool TryLocateExecutable(string name, out string fullPath)
        {
            if (Path.IsPathRooted(name))
            {
                fullPath = name;
                return File.Exists(name);
            }

            var entries = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

            foreach (var entry in entries)
            {
                var candidate = Path.Combine(entry, name);
                if (File.Exists(candidate))
                {
                    fullPath = candidate;
                    return true;
                }

                if (OperatingSystem.IsWindows())
                {
                    var exeCandidate = candidate.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                        ? candidate
                        : candidate + ".exe";
                    if (File.Exists(exeCandidate))
                    {
                        fullPath = exeCandidate;
                        return true;
                    }
                }
            }

            fullPath = name;
            return false;
        }

        private static string ResolvePythonPath(string repositoryRoot)
        {
            var candidate = Path.Combine(repositoryRoot, "src");
            return Directory.Exists(candidate) ? candidate : string.Empty;
        }

        private static ProcessStartInfo CreatePythonStartInfo(string pythonExecutable, string workingDirectory)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = pythonExecutable,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workingDirectory,
                StandardOutputEncoding = Utf8,
                StandardErrorEncoding = Utf8,
            };

            startInfo.ArgumentList.Add("-m");
            startInfo.ArgumentList.Add(MultiServerModule);
            return startInfo;
        }

        private static void MigrateLegacyDiffCache(string repositoryRoot, string cacheDirectory)
        {
            if (string.IsNullOrWhiteSpace(repositoryRoot))
            {
                return;
            }

            try
            {
                var legacyRoot = Path.Combine(repositoryRoot, "artifacts", "cache", "diffs");
                if (!Directory.Exists(legacyRoot))
                {
                    return;
                }

                if (!Directory.Exists(cacheDirectory))
                {
                    Directory.CreateDirectory(cacheDirectory);
                }

                foreach (var file in Directory.EnumerateFiles(legacyRoot, "*", SearchOption.TopDirectoryOnly))
                {
                    var target = Path.Combine(cacheDirectory, Path.GetFileName(file)!);
                    if (!File.Exists(target))
                    {
                        File.Copy(file, target, overwrite: false);
                    }
                }
            }
            catch
            {
                // Migration is a best-effort convenience for developers; ignore failures.
            }
        }

        private static void ValidateMultiServerResponse(ServerScanResponse response)
        {
            if (response is null)
            {
                throw new ArgumentNullException(nameof(response));
            }

            if (!string.Equals(response.Version, MultiServerSchemaVersion, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Unsupported multi-server schema version '{response.Version}'. Expected '{MultiServerSchemaVersion}'.");
            }

            response.Results ??= Array.Empty<ServerScanResult>();
            response.Catalog ??= Array.Empty<ConfigCatalogEntry>();
            response.Drilldown ??= Array.Empty<ConfigDrilldown>();
            response.Summary ??= new ServerScanSummary
            {
                BaselineHostId = string.Empty,
                TotalHosts = response.Results.Length,
                ConfigsEvaluated = response.Catalog.Length,
                DriftingConfigs = response.Catalog.Count(entry => entry.DriftCount > 0),
                GeneratedAt = DateTimeOffset.UtcNow,
            };

            foreach (var result in response.Results)
            {
                result.Roots ??= Array.Empty<string>();
            }

            foreach (var entry in response.Catalog)
            {
                entry.PresentHosts ??= Array.Empty<string>();
                entry.MissingHosts ??= Array.Empty<string>();
            }

            foreach (var drilldown in response.Drilldown)
            {
                drilldown.Servers ??= Array.Empty<ConfigServerDetail>();
                drilldown.Notes ??= Array.Empty<string>();
            }
        }

        private sealed class MultiServerRequest
        {
            [JsonPropertyName("plans")]
            public List<ServerScanPlan> Plans { get; set; } = new();

            [JsonPropertyName("cache_dir")]
            public string CacheDirectory { get; set; } = string.Empty;

            [JsonPropertyName("schema_version")]
            public string SchemaVersion { get; set; } = MultiServerSchemaVersion;
        }

        private static string EnsureFile(string path, bool isBaseline)
        {
            if (!File.Exists(path))
            {
                if (Directory.Exists(path))
                {
                    throw new InvalidOperationException(isBaseline
                        ? $"Baseline path is not a file: {path}"
                        : $"Comparison path is not a file: {path}");
                }

                throw new FileNotFoundException($"Path does not exist: {path}");
            }

            return path;
        }

        private static string ReadText(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new StreamReader(stream, Utf8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }

        private static HuntResult BuildHuntResult(string? directory, string? pattern, CancellationToken cancellationToken)
        {
            var rootPath = ResolvePath(directory);
            string searchRoot;
            IReadOnlyCollection<string> files;

            if (File.Exists(rootPath))
            {
                searchRoot = Path.GetDirectoryName(rootPath) ?? Path.GetDirectoryName(Path.GetFullPath(rootPath)) ?? Path.GetPathRoot(rootPath)!;
                files = new[] { rootPath };
            }
            else if (Directory.Exists(rootPath))
            {
                searchRoot = rootPath;
                files = EnumerateFilesSafely(rootPath, cancellationToken).ToList();
            }
            else
            {
                throw new FileNotFoundException($"Path does not exist: {rootPath}");
            }

            var hits = new List<HuntHitRecord>();
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryReadSample(file, HuntSampleSize, out var text))
                {
                    continue;
                }

                foreach (var rule in HuntRules)
                {
                    if (!rule.MatchesKeywords(text))
                    {
                        continue;
                    }

                    hits.AddRange(rule.ExtractHits(text, file));
                }
            }

            var trimmedPattern = string.IsNullOrWhiteSpace(pattern) ? null : pattern.Trim();
            if (!string.IsNullOrEmpty(trimmedPattern))
            {
                hits = hits.Where(hit => hit.Excerpt.Contains(trimmedPattern, StringComparison.OrdinalIgnoreCase)).ToList();
            }

            var materialisedHits = hits
                .Select(hit => ToModelHit(hit, searchRoot))
                .ToArray();

            var result = new HuntResult
            {
                Directory = rootPath,
                Pattern = trimmedPattern,
                Count = materialisedHits.Length,
                Hits = materialisedHits,
            };

            result.RawJson = JsonSerializer.Serialize(result, SerializerOptions);
            return result;
        }

        [ExcludeFromCodeCoverage]
        private static IEnumerable<string> EnumerateFilesSafely(string root, CancellationToken cancellationToken)
        {
            var stack = new Stack<string>();
            stack.Push(root);

            while (stack.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = stack.Pop();

                string[] files = Array.Empty<string>();
                string[] directories = Array.Empty<string>();

                try
                {
                    files = Directory.GetFiles(current);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }

                foreach (var file in files)
                {
                    yield return file;
                }

                try
                {
                    directories = Directory.GetDirectories(current);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }

                foreach (var directory in directories)
                {
                    stack.Push(directory);
                }
            }
        }

        private static bool TryReadSample(string path, int sampleSize, out string text)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var length = (int)Math.Min(sampleSize, stream.Length);
            if (length == 0)
            {
                text = string.Empty;
                return true;
            }

            var buffer = ArrayPool<byte>.Shared.Rent(length);
            try
            {
                var read = stream.Read(buffer, 0, length);
                if (ContainsBinaryData(buffer.AsSpan(0, read)))
                {
                    text = string.Empty;
                    return false;
                }

                text = Utf8.GetString(buffer, 0, read);
                return true;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        private static bool ContainsBinaryData(ReadOnlySpan<byte> span)
        {
            foreach (var value in span)
            {
                if (value == 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static HuntHit ToModelHit(HuntHitRecord hit, string searchRoot)
        {
            var relative = TryGetRelativePath(searchRoot, hit.Path);
            return new HuntHit
            {
                Rule = new HuntRuleSummary
                {
                    Name = hit.Rule.Name,
                    Description = hit.Rule.Description,
                    TokenName = hit.Rule.TokenName,
                    Keywords = hit.Rule.Keywords,
                    Patterns = hit.Rule.Patterns.Select(pattern => pattern.ToString()).ToArray(),
                },
                Path = hit.Path,
                RelativePath = relative,
                LineNumber = hit.LineNumber,
                Excerpt = hit.Excerpt,
            };
        }

        private static string TryGetRelativePath(string root, string target)
        {
            try
            {
                var relative = Path.GetRelativePath(root, target);
                if (!relative.StartsWith(".", StringComparison.Ordinal))
                {
                    return relative.Replace(Path.DirectorySeparatorChar, '/');
                }
            }
            catch (ArgumentException)
            {
            }
            catch (NotSupportedException)
            {
            }

            return Path.GetFileName(target);
        }

        private static string ResolvePath(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException("Path is required.");
            }

            var expanded = Environment.ExpandEnvironmentVariables(value);

            if (expanded.StartsWith("~", StringComparison.Ordinal))
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (string.IsNullOrEmpty(home))
                {
                    throw new InvalidOperationException("Unable to resolve '~' because the home directory is unknown.");
                }

                expanded = Path.Combine(home, expanded[1..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            }

            return Path.GetFullPath(expanded);
        }

        private sealed record HuntHitRecord(HuntRuleDefinition Rule, string Path, int LineNumber, string Excerpt);

        [ExcludeFromCodeCoverage]
        private sealed record HuntRuleDefinition(string Name, string Description, string? TokenName, string[] Keywords, Regex[] Patterns)
        {
            public bool MatchesKeywords(string text)
            {
                if (Keywords.Length == 0)
                {
                    return true;
                }

                var lower = text.ToLowerInvariant();
                foreach (var keyword in Keywords)
                {
                    if (!lower.Contains(keyword, StringComparison.Ordinal))
                    {
                        return false;
                    }
                }

                return true;
            }

            public IEnumerable<HuntHitRecord> ExtractHits(string text, string path)
            {
                var lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
                for (var index = 0; index < lines.Length; index++)
                {
                    var line = lines[index];
                    if (string.IsNullOrEmpty(line))
                    {
                        continue;
                    }

                    if (Keywords.Length > 0 &&
                        !Keywords.Any(keyword => line.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        continue;
                    }

                    var matches = Patterns.Length == 0 || Patterns.Any(pattern => pattern.IsMatch(line));
                    if (!matches)
                    {
                        continue;
                    }

                    yield return new HuntHitRecord(this, path, index + 1, line.Trim());
                }
            }
        }

        [ExcludeFromCodeCoverage]
        private static class RunProfileManager
        {
            public static RunProfileListResult ListProfiles(string? baseDir, CancellationToken cancellationToken)
            {
                var root = ProfilesRoot(baseDir);
                var profiles = new List<RunProfileDefinition>();

                if (!Directory.Exists(root))
                {
                    Directory.CreateDirectory(root);
                }

                foreach (var profilePath in Directory.EnumerateFiles(root, "profile.json", SearchOption.AllDirectories)
                             .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var text = File.ReadAllText(profilePath);
                        var profile = JsonSerializer.Deserialize<RunProfileDefinition>(text, SerializerOptions);
                        if (profile is not null)
                        {
                            NormaliseProfile(profile);
                            profiles.Add(profile);
                        }
                    }
                    catch (IOException)
                    {
                    }
                    catch (JsonException)
                    {
                    }
                }

                return new RunProfileListResult
                {
                    Profiles = profiles.ToArray(),
                };
            }

            public static void SaveProfile(RunProfileDefinition profile, string? baseDir, CancellationToken cancellationToken)
            {
                var clean = CloneProfile(profile);
                if (string.IsNullOrWhiteSpace(clean.Name))
                {
                    throw new InvalidOperationException("Profile name is required.");
                }

                var root = ProfilesRoot(baseDir);
                Directory.CreateDirectory(root);

                var directory = Path.Combine(root, SafeName(clean.Name));
                Directory.CreateDirectory(directory);

                var path = Path.Combine(directory, "profile.json");
                cancellationToken.ThrowIfCancellationRequested();
                var json = JsonSerializer.Serialize(clean, new JsonSerializerOptions(SerializerOptions)
                {
                    WriteIndented = true,
                });
                File.WriteAllText(path, json + Environment.NewLine);
            }

            public static RunProfileRunResult RunProfile(RunProfileDefinition profile, bool saveProfile, string? baseDir, string? timestamp, CancellationToken cancellationToken)
            {
                var clean = CloneProfile(profile);
                if (string.IsNullOrWhiteSpace(clean.Name))
                {
                    throw new InvalidOperationException("Profile name is required.");
                }

                if (saveProfile)
                {
                    SaveProfile(clean, baseDir, cancellationToken);
                }

                var root = ProfilesRoot(baseDir);
                var profileDirectory = Path.Combine(root, SafeName(clean.Name));
                Directory.CreateDirectory(profileDirectory);

                var runTimestamp = timestamp ?? DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
                var runDirectory = Path.Combine(profileDirectory, "raw", runTimestamp);
                Directory.CreateDirectory(runDirectory);

                var sources = new List<string>(clean.Sources ?? Array.Empty<string>());
                if (!string.IsNullOrWhiteSpace(clean.Baseline))
                {
                    var baselineIndex = sources.FindIndex(source => string.Equals(source, clean.Baseline, StringComparison.Ordinal));
                    if (baselineIndex > 0)
                    {
                        var baseline = sources[baselineIndex];
                        sources.RemoveAt(baselineIndex);
                        sources.Insert(0, baseline);
                    }
                }

                var files = new List<RunProfileFileResult>();

                for (var index = 0; index < sources.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var source = sources[index];
                    if (string.IsNullOrWhiteSpace(source))
                    {
                        continue;
                    }

                    var matches = CollectMatches(source).ToList();
                    if (matches.Count == 0)
                    {
                        continue;
                    }

                    var destinationRoot = Path.Combine(runDirectory, $"source_{index:00}");
                    Directory.CreateDirectory(destinationRoot);

                    foreach (var match in matches)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (Directory.Exists(match))
                        {
                            foreach (var file in EnumerateFilesSafely(match, cancellationToken))
                            {
                                var entry = CopyFile(source, file, match, destinationRoot);
                                files.Add(entry);
                            }
                        }
                        else if (File.Exists(match))
                        {
                            var baseDirectory = Path.GetDirectoryName(match) ?? Path.GetDirectoryName(Path.GetFullPath(match)) ?? runDirectory;
                            var entry = CopyFile(source, match, baseDirectory, destinationRoot);
                            files.Add(entry);
                        }
                    }
                }

                WriteMetadata(runDirectory, clean, runTimestamp, files, sources);

                return new RunProfileRunResult
                {
                    Profile = clean,
                    Timestamp = runTimestamp,
                    OutputDir = runDirectory,
                    Files = files.ToArray(),
                };
            }

            public static OfflineCollectorResult PrepareOfflineCollector(RunProfileDefinition profile, OfflineCollectorRequest request, string? baseDir, CancellationToken cancellationToken)
            {
                if (request is null)
                {
                    throw new ArgumentNullException(nameof(request));
                }

                var clean = CloneProfile(profile);
                if (string.IsNullOrWhiteSpace(clean.Name))
                {
                    throw new InvalidOperationException("Profile name is required.");
                }

                if (string.IsNullOrWhiteSpace(request.PackagePath))
                {
                    throw new InvalidOperationException("Package path is required.");
                }

                var packagePath = ResolvePath(request.PackagePath);
                var packageDirectory = Path.GetDirectoryName(packagePath);
                if (string.IsNullOrWhiteSpace(packageDirectory))
                {
                    throw new InvalidOperationException("Unable to resolve package directory.");
                }

                Directory.CreateDirectory(packageDirectory);

                var tempRoot = Path.Combine(Path.GetTempPath(), "DriftBusterOfflineCollector", Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
                Directory.CreateDirectory(tempRoot);

                try
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var configFileName = string.IsNullOrWhiteSpace(request.ConfigFileName)
                        ? $"{SafeName(clean.Name)}.offline.config.json"
                        : request.ConfigFileName.Trim();

                    if (!string.IsNullOrWhiteSpace(request.ConfigFileName))
                    {
                        var fileNameOnly = Path.GetFileName(configFileName);
                        if (!string.Equals(configFileName, fileNameOnly, StringComparison.Ordinal))
                        {
                            throw new InvalidOperationException("Config file name must not include path separators.");
                        }

                        if (fileNameOnly.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                        {
                            throw new InvalidOperationException("Config file name contains invalid characters.");
                        }

                        if (string.IsNullOrWhiteSpace(fileNameOnly))
                        {
                            throw new InvalidOperationException("Config file name is required.");
                        }

                        configFileName = fileNameOnly;
                    }

                    if (!configFileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    {
                        configFileName += ".json";
                    }

                    var configPath = Path.Combine(tempRoot, configFileName);
                    var ruleset = LoadSecretRules(baseDir);
                    var payload = BuildOfflineConfigPayload(clean, request.Metadata, ruleset);
                    var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions(SerializerOptions)
                    {
                        WriteIndented = true,
                    });
                    File.WriteAllText(configPath, json + Environment.NewLine);

                    cancellationToken.ThrowIfCancellationRequested();

                    var scriptFileName = "driftbuster-offline-runner.ps1";
                    var scriptSource = ResolveRequiredFile(baseDir, "scripts", scriptFileName);
                    File.Copy(scriptSource, Path.Combine(tempRoot, scriptFileName), overwrite: true);

                    cancellationToken.ThrowIfCancellationRequested();

                    if (File.Exists(packagePath))
                    {
                        File.Delete(packagePath);
                    }

                    ZipFile.CreateFromDirectory(tempRoot, packagePath, CompressionLevel.Optimal, includeBaseDirectory: false);

                    return new OfflineCollectorResult
                    {
                        PackagePath = packagePath,
                        ConfigFileName = configFileName,
                        ScriptFileName = scriptFileName,
                    };
                }
                finally
                {
                    TryDeleteDirectory(tempRoot);
                }
            }

            private static RunProfileDefinition CloneProfile(RunProfileDefinition profile)
            {
                NormaliseProfile(profile);
                return new RunProfileDefinition
                {
                    Name = profile.Name,
                    Description = profile.Description,
                    Baseline = profile.Baseline,
                    Sources = profile.Sources is null ? Array.Empty<string>() : profile.Sources.Where(source => !string.IsNullOrWhiteSpace(source)).Select(source => source.Trim()).ToArray(),
                    Options = new Dictionary<string, string>(profile.Options ?? new Dictionary<string, string>(), StringComparer.Ordinal),
                    SecretScanner = CloneSecretScanner(profile.SecretScanner),
                };
            }

            private static SecretScannerOptions CloneSecretScanner(SecretScannerOptions? options)
            {
                var clone = new SecretScannerOptions();
                if (options?.IgnoreRules is not null)
                {
                    clone.IgnoreRules = options.IgnoreRules
                        .Where(rule => !string.IsNullOrWhiteSpace(rule))
                        .Select(rule => rule.Trim())
                        .Distinct(StringComparer.Ordinal)
                        .ToArray();
                }
                if (options?.IgnorePatterns is not null)
                {
                    clone.IgnorePatterns = options.IgnorePatterns
                        .Where(pattern => !string.IsNullOrWhiteSpace(pattern))
                        .Select(pattern => pattern.Trim())
                        .Distinct(StringComparer.Ordinal)
                        .ToArray();
                }

                return clone;
            }

            private static void NormaliseProfile(RunProfileDefinition profile)
            {
                profile.Sources ??= Array.Empty<string>();
                profile.Options ??= new Dictionary<string, string>(StringComparer.Ordinal);
                profile.SecretScanner = CloneSecretScanner(profile.SecretScanner);
            }

            private static JsonElement LoadSecretRules(string? baseDir)
            {
                var assembly = typeof(DriftbusterBackend).Assembly;
                using var resource = assembly.GetManifestResourceStream(SecretRulesResourceName);
                if (resource is not null)
                {
                    using var embedded = JsonDocument.Parse(resource);
                    return embedded.RootElement.Clone();
                }

                var path = ResolveRequiredFile(baseDir, "src", "driftbuster", "secret_rules.json");
                using var stream = File.OpenRead(path);
                using var document = JsonDocument.Parse(stream);
                return document.RootElement.Clone();
            }

            private static object BuildOfflineConfigPayload(
                RunProfileDefinition profile,
                Dictionary<string, string>? metadata,
                JsonElement secretRules)
            {
                var sources = profile.Sources
                    .Where(source => !string.IsNullOrWhiteSpace(source))
                    .Select(source => new { path = source })
                    .ToArray();

                var baseline = profile.Baseline;
                if (string.IsNullOrWhiteSpace(baseline) && sources.Length > 0)
                {
                    baseline = sources[0].path;
                }

                var meta = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["profile_name"] = profile.Name,
                    ["prepared_at"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                };

                var user = Environment.UserName;
                if (!string.IsNullOrWhiteSpace(user))
                {
                    meta["prepared_by"] = user;
                }

                if (metadata is not null)
                {
                    foreach (var entry in metadata)
                    {
                        if (string.IsNullOrWhiteSpace(entry.Key))
                        {
                            continue;
                        }

                        meta[entry.Key.Trim()] = entry.Value ?? string.Empty;
                    }
                }

                var secretScanner = profile.SecretScanner ?? new SecretScannerOptions();

                return new
                {
                    schema = "https://driftbuster.dev/offline-runner/config/v1",
                    version = "1",
                    profile = new
                    {
                        name = profile.Name,
                        description = profile.Description,
                        baseline,
                        sources,
                        tags = new[] { "offline" },
                        options = profile.Options,
                        secret_scanner = new
                        {
                            ignore_rules = secretScanner.IgnoreRules ?? Array.Empty<string>(),
                            ignore_patterns = secretScanner.IgnorePatterns ?? Array.Empty<string>(),
                            ruleset = secretRules,
                        },
                    },
                    runner = new
                    {
                        compress = true,
                        include_config = true,
                        include_logs = true,
                        include_manifest = true,
                        manifest_name = "manifest.json",
                        log_name = "runner.log",
                        data_directory_name = "data",
                        logs_directory_name = "logs",
                        package_name = $"{SafeName(profile.Name)}-offline-results",
                        cleanup_staging = true,
                    },
                    metadata = meta,
                };
            }

            private static string ResolveRequiredFile(string? baseDir, params string[] segments)
            {
                var relative = Path.Combine(segments);

                static bool Exists(string? candidate) => !string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate);

                if (!string.IsNullOrWhiteSpace(baseDir))
                {
                    var fromBase = Path.Combine(baseDir, relative);
                    if (Exists(fromBase))
                    {
                        return Path.GetFullPath(fromBase);
                    }
                }

                var fromCurrent = Path.Combine(Environment.CurrentDirectory, relative);
                if (Exists(fromCurrent))
                {
                    return Path.GetFullPath(fromCurrent);
                }

                var fromApp = Path.Combine(AppContext.BaseDirectory, relative);
                if (Exists(fromApp))
                {
                    return Path.GetFullPath(fromApp);
                }

                var current = new DirectoryInfo(AppContext.BaseDirectory);
                while (current is not null)
                {
                    var candidate = Path.Combine(current.FullName, relative);
                    if (Exists(candidate))
                    {
                        return Path.GetFullPath(candidate);
                    }

                    current = current.Parent;
                }

                throw new FileNotFoundException($"Unable to locate required asset '{relative}'.");
            }

            private static void TryDeleteDirectory(string? path)
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    return;
                }

                try
                {
                    if (Directory.Exists(path))
                    {
                        Directory.Delete(path, recursive: true);
                    }
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            private static string SafeName(string text)
            {
                var builder = new StringBuilder(text.Length);
                foreach (var character in text)
                {
                    if (char.IsLetterOrDigit(character) || character is '-' or '_')
                    {
                        builder.Append(character);
                    }
                    else
                    {
                        builder.Append('-');
                    }
                }

                return builder.ToString();
            }

            private static string ProfilesRoot(string? baseDir)
            {
                var root = string.IsNullOrWhiteSpace(baseDir) ? Environment.CurrentDirectory : baseDir;
                return Path.Combine(root, "Profiles");
            }

            private static IEnumerable<string> CollectMatches(string source)
            {
                var resolved = ResolvePath(source);
                if (File.Exists(resolved) || Directory.Exists(resolved))
                {
                    return new[] { resolved };
                }

                if (!ContainsGlob(source))
                {
                    throw new FileNotFoundException($"Path does not exist: {resolved}");
                }

                var (baseDirectory, pattern) = SplitGlob(resolved);
                if (!Directory.Exists(baseDirectory))
                {
                    return Array.Empty<string>();
                }

                var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
                matcher.AddInclude(pattern);
                return matcher.GetResultsInFullPath(baseDirectory);
            }

            private static bool ContainsGlob(string value) => value.IndexOfAny(GlobCharacters) >= 0;

            private static (string BaseDirectory, string Pattern) SplitGlob(string absolutePattern)
            {
                var normalized = absolutePattern.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
                var root = Path.GetPathRoot(normalized) ?? string.Empty;
                var remainder = normalized[root.Length..];

                var segments = remainder.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
                var baseSegments = new List<string>();
                var index = 0;

                for (; index < segments.Length; index++)
                {
                    if (segments[index].IndexOfAny(GlobCharacters) >= 0)
                    {
                        break;
                    }

                    baseSegments.Add(segments[index]);
                }

                var baseDirectory = baseSegments.Count > 0
                    ? Path.Combine(root, Path.Combine(baseSegments.ToArray()))
                    : (string.IsNullOrEmpty(root) ? Environment.CurrentDirectory : root);

                baseDirectory = Path.GetFullPath(baseDirectory);

                var patternSegments = segments.Skip(index).ToArray();
                var pattern = patternSegments.Length > 0 ? Path.Combine(patternSegments) : "*";
                pattern = pattern.Replace(Path.DirectorySeparatorChar, '/');

                return (baseDirectory, pattern);
            }

            private static RunProfileFileResult CopyFile(string source, string file, string basePath, string destinationRoot)
            {
                var relative = GetRelativePath(file, basePath);
                var destination = Path.Combine(destinationRoot, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination, overwrite: true);

                var sha = ComputeSha256(destination);
                var size = new FileInfo(destination).Length;

                return new RunProfileFileResult
                {
                    Source = source,
                    Destination = destination.Replace(Path.DirectorySeparatorChar, '/'),
                    Size = size,
                    Sha256 = sha,
                };
            }

            private static string GetRelativePath(string file, string basePath)
            {
                try
                {
                    var relative = Path.GetRelativePath(basePath, file);
                    if (!relative.StartsWith(".", StringComparison.Ordinal))
                    {
                        return relative;
                    }
                }
                catch (ArgumentException)
                {
                }
                catch (NotSupportedException)
                {
                }

                return Path.GetFileName(file);
            }

            private static string ComputeSha256(string path)
            {
                using var sha = SHA256.Create();
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                var hash = sha.ComputeHash(stream);
                return Convert.ToHexString(hash).ToLowerInvariant();
            }

            private static void WriteMetadata(string runDirectory, RunProfileDefinition profile, string timestamp, IReadOnlyCollection<RunProfileFileResult> files, IReadOnlyList<string> orderedSources)
            {
                var baseline = profile.Baseline;
                if (string.IsNullOrWhiteSpace(baseline) && orderedSources.Count > 0)
                {
                    baseline = orderedSources[0];
                }

                var payload = new
                {
                    profile,
                    timestamp,
                    baseline,
                    files = files.Select(file => new
                    {
                        source = file.Source,
                        destination = file.Destination,
                        size = file.Size,
                        sha256 = file.Sha256,
                    }),
                };

                var metadataPath = Path.Combine(runDirectory, "metadata.json");
                var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions(SerializerOptions)
                {
                    WriteIndented = true,
                });
                File.WriteAllText(metadataPath, json + Environment.NewLine);
            }
        }
    }
}

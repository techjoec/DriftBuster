using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace DriftBuster.Gui.ViewModels
{
    /// <summary>
    /// The text a copy puts on the clipboard. Masked values are copied as their marker (<c>•••• (same)</c>), never in clear.
    /// </summary>
    public static class CompareCopy
    {
        private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

        public static string Format(CompareContext context, CompareCopyFormat format, CompareCopyScope scope)
        {
            ArgumentNullException.ThrowIfNull(context);
            var row = context.Row;
            if (row is null)
            {
                return format == CompareCopyFormat.Hex ? Hex(context.Path) : context.Path;
            }

            var cells = scope == CompareCopyScope.ThisValue && context.Cell is not null ? new[] { context.Cell } : row.Cells.ToArray();
            var text = format switch
            {
                CompareCopyFormat.Json => Json(context, cells, scope),
                CompareCopyFormat.Tsv => Tsv(context, cells, scope),
                _ => Text(context, cells, scope),
            };
            return format == CompareCopyFormat.Hex ? Hex(Text(context, cells, scope)) : text;
        }

        private static string Shown(CompareCellViewModel cell) => cell.Value ?? cell.Text;

        private static string Text(CompareContext context, IReadOnlyList<CompareCellViewModel> cells, CompareCopyScope scope) => scope switch
        {
            CompareCopyScope.Setting => context.Key!,
            CompareCopyScope.ThisValue => Shown(cells[0]),
            CompareCopyScope.Values => string.Join(Environment.NewLine, cells.Select(cell => $"{cell.HostLabel}: {Shown(cell)}")),
            _ => string.Join(Environment.NewLine, new[] { $"{context.Path} {context.Key}" }.Concat(cells.Select(cell => $"  {cell.HostLabel}: {Shown(cell)}"))),
        };

        private static string Json(CompareContext context, IReadOnlyList<CompareCellViewModel> cells, CompareCopyScope scope)
        {
            var values = cells.ToDictionary(cell => cell.HostLabel, Shown, StringComparer.Ordinal);
            object payload = scope switch
            {
                CompareCopyScope.Setting => new Dictionary<string, string>(StringComparer.Ordinal) { ["setting"] = context.Key! },
                CompareCopyScope.Values or CompareCopyScope.ThisValue => values,
                _ => new Dictionary<string, object>(StringComparer.Ordinal) { ["file"] = context.Path, ["setting"] = context.Key!, ["values"] = values },
            };
            return JsonSerializer.Serialize(payload, Options);
        }

        private static string Tsv(CompareContext context, IReadOnlyList<CompareCellViewModel> cells, CompareCopyScope scope)
        {
            static string Cell(string text) => text.Replace('\t', ' ').Replace("\r", " ", StringComparison.Ordinal).Replace('\n', ' ');
            return scope switch
            {
                CompareCopyScope.Setting => Cell(context.Key!),
                CompareCopyScope.Values or CompareCopyScope.ThisValue =>
                    string.Join('\t', cells.Select(cell => Cell(cell.HostLabel))) + Environment.NewLine + string.Join('\t', cells.Select(cell => Cell(Shown(cell)))),
                _ => string.Join('\t', new[] { "File", "Setting" }.Concat(cells.Select(cell => Cell(cell.HostLabel)))) + Environment.NewLine
                    + string.Join('\t', new[] { Cell(context.Path), Cell(context.Key!) }.Concat(cells.Select(cell => Cell(Shown(cell))))),
            };
        }

        private static string Hex(string text) => Convert.ToHexString(Encoding.UTF8.GetBytes(text)).Chunk(2).Aggregate(new StringBuilder(), (builder, pair) =>
            builder.Append(builder.Length > 0 ? " " : string.Empty).Append(pair)).ToString();
    }
}

using FinSight.Core.Domain;

namespace FinSight.Core.Import;

/// <summary>
/// Decides what an uploaded file is from its bytes, never from its name alone: a PDF by its <c>%PDF-</c> header, OFX and QFX
/// by their OFX header or root element, and CSV as text whose lines share a delimiter. Anything else (images, spreadsheets,
/// archives, HTML) is not a statement file.
/// </summary>
public static class StatementFileSniffer
{
    /// <summary>How much of the start of the file is inspected.</summary>
    public const int SniffBytes = 8 * 1024;

    public static StatementFileFormat? Detect(ReadOnlySpan<byte> content, string? fileName = null)
    {
        if (content.StartsWith("%PDF-"u8))
        {
            return StatementFileFormat.Pdf;
        }

        var head = content[..Math.Min(content.Length, SniffBytes)];
        if (head.IsEmpty || (!TextDecoding.HasUtf16Bom(head) && LooksBinary(head)))
        {
            return null;
        }

        var text = TextDecoding.Decode(head).TrimStart('﻿', ' ', '\t', '\r', '\n');
        if (IsOfx(text))
        {
            var quicken = text.Contains("<INTU.BID>", StringComparison.OrdinalIgnoreCase)
                || (fileName is not null && fileName.EndsWith(".qfx", StringComparison.OrdinalIgnoreCase));
            return quicken ? StatementFileFormat.Qfx : StatementFileFormat.Ofx;
        }

        // Markup that isn't OFX (an HTML page saved as .csv, XML exports) is not a statement file.
        if (text.StartsWith('<'))
        {
            return null;
        }

        return LooksDelimited(text, truncated: content.Length > SniffBytes) ? StatementFileFormat.Csv : null;
    }

    private static bool IsOfx(string text) =>
        text.StartsWith("OFXHEADER:", StringComparison.OrdinalIgnoreCase)
        || text.StartsWith("<OFX>", StringComparison.OrdinalIgnoreCase)
        || (text.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase)
            && (text.Contains("<?OFX", StringComparison.OrdinalIgnoreCase) || text.Contains("<OFX>", StringComparison.OrdinalIgnoreCase)));

    /// <summary>NUL bytes or more than a few control characters mean a binary file (a spreadsheet, archive or image), not text.</summary>
    private static bool LooksBinary(ReadOnlySpan<byte> bytes)
    {
        var control = 0;
        foreach (var b in bytes)
        {
            if (b == 0)
            {
                return true;
            }

            if (b < 0x20 && b is not ((byte)'\t' or (byte)'\n' or (byte)'\r' or 0x0C))
            {
                control++;
            }
        }

        return control > bytes.Length / 100;
    }

    /// <summary>At least two lines (or the only line) split into two or more fields by the same delimiter, in at least half the lines.</summary>
    private static bool LooksDelimited(string text, bool truncated)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(21).ToList();

        // The inspected head can end mid-line; don't judge by that partial line.
        if (truncated && lines.Count > 1)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        if (lines.Count == 0)
        {
            return false;
        }

        foreach (var delimiter in CsvDelimiters)
        {
            var withDelimiter = lines.Count(l => l.Contains(delimiter, StringComparison.Ordinal));
            if (withDelimiter >= Math.Min(2, lines.Count) && withDelimiter * 2 >= lines.Count)
            {
                return true;
            }
        }

        return false;
    }

    internal static readonly char[] CsvDelimiters = [',', ';', '\t', '|'];
}

using System.Text;

namespace FinSight.Core.Import.Csv;

/// <summary>Splits CSV text into records: quoted fields (with doubled quotes and line breaks inside), CR, LF or CRLF line ends.</summary>
internal static class CsvTable
{
    private const int DelimiterSampleRecords = 64;

    /// <summary>
    /// The file's records with the delimiter that splits them most consistently, blank records dropped and fields trimmed.
    /// Null when no delimiter gives at least two fields per record.
    /// </summary>
    public static (char Delimiter, List<string[]> Rows)? Read(string text, int maxRecords, ParseClock clock)
    {
        var delimiter = DetectDelimiter(text);
        if (delimiter is null)
        {
            return null;
        }

        var rows = new List<string[]>();
        foreach (var record in Records(text, delimiter.Value))
        {
            if (IsBlank(record))
            {
                continue;
            }

            rows.Add(record);
            if (rows.Count > maxRecords)
            {
                throw new ParseLimitExceededException();
            }

            if (rows.Count % 512 == 0)
            {
                clock.Check();
            }
        }

        return (delimiter.Value, rows);
    }

    /// <summary>
    /// Scores each candidate on a sample of records: the share of records with the most common field count, then that count.
    /// A semicolon file with decimal commas splits into more fields on semicolons than on commas, so it wins.
    /// </summary>
    internal static char? DetectDelimiter(string text)
    {
        char? best = null;
        (bool Consistent, int Fields, double Share) bestScore = default;

        foreach (var candidate in StatementFileSniffer.CsvDelimiters)
        {
            var sample = Records(text, candidate).Where(r => !IsBlank(r)).Take(DelimiterSampleRecords).ToList();
            if (sample.Count == 0)
            {
                continue;
            }

            var mode = sample.GroupBy(r => r.Length).OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key).First();
            if (mode.Key < 2)
            {
                continue;
            }

            var share = mode.Count() / (double)sample.Count;
            var score = (share >= 0.6, mode.Key, share);
            if (best is null || score.CompareTo(bestScore) > 0)
            {
                best = candidate;
                bestScore = score;
            }
        }

        return best;
    }

    internal static IEnumerable<string[]> Records(string text, char delimiter)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var quoted = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c != '"')
                {
                    field.Append(c);
                }
                else if (i + 1 < text.Length && text[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = false;
                }

                continue;
            }

            if (c == '"' && !quoted && IsWhiteSpace(field))
            {
                field.Clear();
                inQuotes = true;
                quoted = true;
            }
            else if (c == delimiter)
            {
                fields.Add(field.ToString().Trim());
                field.Clear();
                quoted = false;
            }
            else if (c is '\r' or '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                fields.Add(field.ToString().Trim());
                field.Clear();
                quoted = false;
                yield return fields.ToArray();
                fields.Clear();
            }
            else
            {
                field.Append(c);
            }
        }

        if (field.Length > 0 || fields.Count > 0 || quoted)
        {
            fields.Add(field.ToString().Trim());
            yield return fields.ToArray();
        }
    }

    public static bool IsBlank(string[] record) => record.All(f => f.Length == 0);

    private static bool IsWhiteSpace(StringBuilder builder)
    {
        for (var i = 0; i < builder.Length; i++)
        {
            if (!char.IsWhiteSpace(builder[i]))
            {
                return false;
            }
        }

        return true;
    }
}

using System.Text;

namespace TenhouCsvReader.Services;

internal static class CsvLineParser
{
    public static List<string> Parse(string line)
    {
        var values = new List<string>();
        var current = new StringBuilder(line.Length);
        var inQuotes = false;

        for (var index = 0; index < line.Length; index++)
        {
            var ch = line[index];

            if (ch == '"')
            {
                if (inQuotes && index + 1 < line.Length && line[index + 1] == '"')
                {
                    current.Append('"');
                    index++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }

                continue;
            }

            if (ch == ',' && !inQuotes)
            {
                values.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(ch);
        }

        values.Add(current.ToString());
        return values;
    }
}
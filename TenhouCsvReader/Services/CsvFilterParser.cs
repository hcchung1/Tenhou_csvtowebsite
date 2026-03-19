using System.Globalization;

namespace TenhouCsvReader.Services;

internal enum CsvFilterOperator
{
    Equals,
    NotEquals,
    GreaterThan,
    GreaterThanOrEqual,
    LessThan,
    LessThanOrEqual
}

internal sealed record CsvFilterCondition(
    int ColumnIndex,
    string ColumnName,
    CsvFilterOperator Operator,
    string RawValue,
    double? NumericValue);

internal static class CsvFilterParser
{
    private static readonly (string Token, CsvFilterOperator Operator)[] OperatorTokens =
    [
        (">=", CsvFilterOperator.GreaterThanOrEqual),
        ("<=", CsvFilterOperator.LessThanOrEqual),
        ("!=", CsvFilterOperator.NotEquals),
        ("==", CsvFilterOperator.Equals),
        ("=", CsvFilterOperator.Equals),
        (">", CsvFilterOperator.GreaterThan),
        ("<", CsvFilterOperator.LessThan)
    ];

    public static IReadOnlyList<CsvFilterCondition> Parse(string filterText, IReadOnlyList<string> headers)
    {
        if (string.IsNullOrWhiteSpace(filterText))
        {
            return Array.Empty<CsvFilterCondition>();
        }

        var headerLookup = BuildHeaderLookup(headers);
        var clauses = filterText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (clauses.Length == 0)
        {
            return Array.Empty<CsvFilterCondition>();
        }

        var conditions = new List<CsvFilterCondition>(clauses.Length);

        foreach (var clause in clauses)
        {
            conditions.Add(ParseClause(clause, headerLookup));
        }

        return conditions;
    }

    public static bool IsMatch(IReadOnlyList<string> rowValues, IReadOnlyList<CsvFilterCondition> conditions)
    {
        foreach (var condition in conditions)
        {
            var cellValue = condition.ColumnIndex < rowValues.Count
                ? rowValues[condition.ColumnIndex]
                : string.Empty;

            if (!IsConditionMatch(cellValue, condition))
            {
                return false;
            }
        }

        return true;
    }

    private static CsvFilterCondition ParseClause(string clause, IReadOnlyDictionary<string, int> headerLookup)
    {
        var operatorInfo = FindOperator(clause);
        if (operatorInfo is null)
        {
            throw new FormatException($"Invalid filter clause: '{clause}'. Use formats like prediction = 1 or probability > 0.7");
        }

        var (token, filterOperator, index) = operatorInfo.Value;

        var columnName = clause[..index].Trim();
        var rawValue = clause[(index + token.Length)..].Trim();

        if (string.IsNullOrWhiteSpace(columnName) || string.IsNullOrWhiteSpace(rawValue))
        {
            throw new FormatException($"Invalid filter clause: '{clause}'.");
        }

        rawValue = TrimWrappingQuotes(rawValue);

        if (!headerLookup.TryGetValue(columnName, out var columnIndex))
        {
            var prefixMatches = headerLookup.Keys
                .Where(key => key.StartsWith(columnName, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (prefixMatches.Length == 1)
            {
                columnIndex = headerLookup[prefixMatches[0]];
            }
            else if (prefixMatches.Length > 1)
            {
                throw new KeyNotFoundException(
                    $"Column '{columnName}' is ambiguous in clause '{clause}'. Matches: {string.Join(", ", prefixMatches)}");
            }
            else
            {
                throw new KeyNotFoundException($"Unknown column '{columnName}' in filter clause '{clause}'.");
            }
        }

        var needsNumeric = filterOperator is CsvFilterOperator.GreaterThan
            or CsvFilterOperator.GreaterThanOrEqual
            or CsvFilterOperator.LessThan
            or CsvFilterOperator.LessThanOrEqual;

        double? numericValue = null;

        if (double.TryParse(rawValue, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var parsedNumber))
        {
            numericValue = parsedNumber;
        }

        if (needsNumeric && numericValue is null)
        {
            throw new FormatException($"Filter clause '{clause}' expects a numeric value.");
        }

        return new CsvFilterCondition(columnIndex, columnName, filterOperator, rawValue, numericValue);
    }

    private static (string Token, CsvFilterOperator Operator, int Index)? FindOperator(string clause)
    {
        foreach (var (token, filterOperator) in OperatorTokens)
        {
            var index = clause.IndexOf(token, StringComparison.Ordinal);
            if (index > 0)
            {
                return (token, filterOperator, index);
            }
        }

        return null;
    }

    private static Dictionary<string, int> BuildHeaderLookup(IReadOnlyList<string> headers)
    {
        var lookup = new Dictionary<string, int>(headers.Count, StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < headers.Count; index++)
        {
            if (!lookup.ContainsKey(headers[index]))
            {
                lookup[headers[index]] = index;
            }
        }

        return lookup;
    }

    private static string TrimWrappingQuotes(string value)
    {
        if (value.Length < 2)
        {
            return value;
        }

        var startsWithSingle = value[0] == '\'';
        var startsWithDouble = value[0] == '"';
        var endsWithSingle = value[^1] == '\'';
        var endsWithDouble = value[^1] == '"';

        if ((startsWithSingle && endsWithSingle) || (startsWithDouble && endsWithDouble))
        {
            return value[1..^1];
        }

        return value;
    }

    private static bool IsConditionMatch(string cellValue, CsvFilterCondition condition)
    {
        if (condition.NumericValue is double numericFilterValue &&
            double.TryParse(cellValue, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var numericCellValue))
        {
            return condition.Operator switch
            {
                CsvFilterOperator.Equals => numericCellValue == numericFilterValue,
                CsvFilterOperator.NotEquals => numericCellValue != numericFilterValue,
                CsvFilterOperator.GreaterThan => numericCellValue > numericFilterValue,
                CsvFilterOperator.GreaterThanOrEqual => numericCellValue >= numericFilterValue,
                CsvFilterOperator.LessThan => numericCellValue < numericFilterValue,
                CsvFilterOperator.LessThanOrEqual => numericCellValue <= numericFilterValue,
                _ => false
            };
        }

        if (condition.Operator is CsvFilterOperator.GreaterThan
            or CsvFilterOperator.GreaterThanOrEqual
            or CsvFilterOperator.LessThan
            or CsvFilterOperator.LessThanOrEqual)
        {
            return false;
        }

        return condition.Operator switch
        {
            CsvFilterOperator.Equals => string.Equals(cellValue, condition.RawValue, StringComparison.OrdinalIgnoreCase),
            CsvFilterOperator.NotEquals => !string.Equals(cellValue, condition.RawValue, StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }
}

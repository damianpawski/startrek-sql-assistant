using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using StarTrekSqlAssistant.Web.Services;

namespace StarTrekSqlAssistant.Tests.Fakes;

/// <summary>One tool call the model made, as the fake server saw it.</summary>
public sealed record ToolCallRecord(string Tool, IReadOnlyDictionary<string, object?> Arguments, string Result)
{
    public bool Failed => Result.StartsWith("Error:", StringComparison.Ordinal);
}

/// <summary>
/// An in-process stand-in for Data API builder's SQL MCP Server: the same three
/// tools the model actually uses, over a handful of real rows, enforcing the
/// argument rules DAB enforces.
///
/// Those rules are the assertions the SystemPrompt makes - a date filter needs
/// a full unquoted UTC timestamp, aggregate_records takes one existing column
/// and never an expression, describe_entities returns no fields - so a test can
/// pin the agent's tool-argument shaping against them without Docker, SQL
/// Server or a model. Like DAB, a bad argument comes back as an ordinary tool
/// result, not an exception, so the tool loop feeds it to the model to retry.
/// </summary>
public sealed class FakeDabMcpServer
{
    private static readonly string[] DateColumns = ["begin", "end", "airdate", "remastered_airdate", "release_date"];

    private static readonly string[] NumericColumns =
        ["series_id", "episode_id", "movie_id", "season", "episode_number", "media_set_id", "medium_volume_id", "sequence"];

    private static readonly Regex UtcTimestamp = new(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$", RegexOptions.Compiled);

    private readonly Dictionary<string, List<Dictionary<string, object?>>> _tables = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Series"] =
        [
            Row(("series_id", 1), ("title", "Star Trek: The Original Series"), ("begin", "1966-09-08"), ("end", "1969-06-03")),
            Row(("series_id", 2), ("title", "Star Trek: The Next Generation"), ("begin", "1987-09-28"), ("end", "1994-05-23")),
            Row(("series_id", 3), ("title", "Star Trek: Deep Space Nine"), ("begin", "1993-01-03"), ("end", "1999-06-02")),
            Row(("series_id", 4), ("title", "Star Trek: Voyager"), ("begin", "1995-01-16"), ("end", "2001-05-23")),
            Row(("series_id", 5), ("title", "Star Trek: Strange New Worlds"), ("begin", "2022-05-05"), ("end", null)),
        ],
        ["Episode"] =
        [
            Row(("episode_id", 1), ("series_id", 3), ("title", "Emissary"), ("season", 1), ("episode_number", 1), ("airdate", "1993-01-03")),
            Row(("episode_id", 2), ("series_id", 3), ("title", "Past Prologue"), ("season", 1), ("episode_number", 3), ("airdate", "1993-01-10")),
            Row(("episode_id", 3), ("series_id", 2), ("title", "Encounter at Farpoint"), ("season", 1), ("episode_number", 1), ("airdate", "1987-09-28")),
        ],
        ["Movie"] =
        [
            Row(("movie_id", 1), ("title", "Star Trek: The Motion Picture"), ("release_date", "1979-12-07")),
            Row(("movie_id", 2), ("title", "Star Trek Nemesis"), ("release_date", "2002-12-13")),
        ],
    };

    /// <summary>Every tool call made, in order.</summary>
    public List<ToolCallRecord> Calls { get; } = [];

    public ToolCallRecord LastCall => Calls[^1];

    /// <summary>The tools as the model sees them, ready for <c>ChatOptions.Tools</c>.</summary>
    public IReadOnlyList<AITool> Tools =>
    [
        AIFunctionFactory.Create(ReadRecords, "read_records",
            "Reads rows from an entity. STEP 1: describe_entities -> find entities and their fields."),
        AIFunctionFactory.Create(AggregateRecords, "aggregate_records",
            "Runs count, sum, avg, min or max over one column of an entity."),
        AIFunctionFactory.Create(DescribeEntities, "describe_entities",
            "Lists the entities and their fields."),
    ];

    /// <summary>A provider that hands the agent these tools.</summary>
    public IMcpToolProvider AsToolProvider() => new StubToolProvider(Tools);

    private string ReadRecords(string entity, string? filter = null, string? select = null, int? first = null)
        => Record("read_records",
            new Dictionary<string, object?> { ["entity"] = entity, ["filter"] = filter, ["select"] = select, ["first"] = first },
            () =>
            {
                if (!_tables.TryGetValue(entity, out var rows))
                {
                    return $"Error: no entity named '{entity}'. Entities: {string.Join(", ", _tables.Keys)}.";
                }

                IEnumerable<Dictionary<string, object?>> result = rows;

                if (!string.IsNullOrWhiteSpace(filter) && TryApplyFilter(entity, filter, ref result) is { } filterError)
                {
                    return filterError;
                }

                if (!string.IsNullOrWhiteSpace(select))
                {
                    // DAB parses the select list strictly: a space after a comma
                    // becomes part of the next column name and matches nothing.
                    if (select.Contains(", ", StringComparison.Ordinal))
                    {
                        return "Error: select must be comma-separated with no spaces.";
                    }

                    var columns = select.Split(',', StringSplitOptions.RemoveEmptyEntries);
                    var unknown = columns.FirstOrDefault(c => !ColumnsOf(entity).Contains(c, StringComparer.OrdinalIgnoreCase));
                    if (unknown is not null)
                    {
                        return $"Error: '{entity}' has no column '{unknown}'.";
                    }

                    result = result.Select(r => columns.ToDictionary(c => c, r.GetValueOrDefault));
                }

                if (first is > 0)
                {
                    result = result.Take(first.Value);
                }

                return JsonSerializer.Serialize(new { value = result.ToArray() });
            });

    private string AggregateRecords(string entity, string function, string field, string? filter = null)
        => Record("aggregate_records",
            new Dictionary<string, object?> { ["entity"] = entity, ["function"] = function, ["field"] = field, ["filter"] = filter },
            () =>
            {
                if (!_tables.TryGetValue(entity, out var rows))
                {
                    return $"Error: no entity named '{entity}'.";
                }

                // The prompt's rule: field is one existing column, never an
                // expression such as "end - begin".
                if (!ColumnsOf(entity).Contains(field, StringComparer.OrdinalIgnoreCase))
                {
                    return $"Error: field must be a single column of '{entity}'; '{field}' is not one. " +
                           "Expressions are not supported - read the rows and compute it yourself.";
                }

                if (function is not "count" && !NumericColumns.Contains(field, StringComparer.OrdinalIgnoreCase))
                {
                    return $"Error: {function} needs a numeric column; '{field}' is not numeric.";
                }

                IEnumerable<Dictionary<string, object?>> result = rows;
                if (!string.IsNullOrWhiteSpace(filter) && TryApplyFilter(entity, filter, ref result) is { } filterError)
                {
                    return filterError;
                }

                var values = result.Select(r => r.GetValueOrDefault(field)).Where(v => v is not null).ToArray();

                if (function is not ("count" or "sum" or "avg" or "min" or "max"))
                {
                    return $"Error: unknown function '{function}'.";
                }

                object answer = function switch
                {
                    "count" => values.Length,
                    "sum" => values.Sum(v => Convert.ToDouble(v, CultureInfo.InvariantCulture)),
                    "avg" => values.Average(v => Convert.ToDouble(v, CultureInfo.InvariantCulture)),
                    "min" => values.Min(v => Convert.ToDouble(v, CultureInfo.InvariantCulture)),
                    _ => values.Max(v => Convert.ToDouble(v, CultureInfo.InvariantCulture)),
                };

                return JsonSerializer.Serialize(new { value = new[] { new Dictionary<string, object> { [function] = answer } } });
            });

    /// <summary>
    /// Reproduces the DAB 2.0.9 behaviour this project is built around: the
    /// entity names come back, the field lists are empty.
    /// </summary>
    private string DescribeEntities(bool nameOnly = false)
        => Record("describe_entities",
            new Dictionary<string, object?> { ["nameOnly"] = nameOnly },
            () => JsonSerializer.Serialize(new
            {
                entities = _tables.Keys.Select(name => new { name, fields = Array.Empty<string>() }).ToArray(),
            }));

    /// <summary>Returns an error string, or null after narrowing <paramref name="rows"/>.</summary>
    private string? TryApplyFilter(string entity, string filter, ref IEnumerable<Dictionary<string, object?>> rows)
    {
        foreach (var clause in filter.Split(" and ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = clause.Split(' ', 3, StringSplitOptions.TrimEntries);
            if (parts.Length != 3)
            {
                return $"Error: cannot parse filter clause '{clause}'. Expected: field op value.";
            }

            var (field, op, raw) = (parts[0], parts[1], parts[2]);

            if (!ColumnsOf(entity).Contains(field, StringComparer.OrdinalIgnoreCase))
            {
                return $"Error: '{entity}' has no column '{field}'.";
            }

            if (DateColumns.Contains(field, StringComparer.OrdinalIgnoreCase) && raw != "null" && !UtcTimestamp.IsMatch(raw))
            {
                return $"Error: {field} is a date; the value must be a full UTC timestamp with no quotes, " +
                       $"for example 1990-01-01T00:00:00Z. Got: {raw}";
            }

            var comparand = raw;
            rows = rows.Where(r => Matches(r.GetValueOrDefault(field), op, comparand)).ToList();
        }

        return null;
    }

    private static bool Matches(object? cell, string op, string raw)
    {
        if (raw == "null")
        {
            return op switch { "eq" => cell is null, "ne" => cell is not null, _ => false };
        }

        if (cell is null)
        {
            return false;
        }

        var value = raw.Trim('\'');
        int comparison;

        if (cell is int number && int.TryParse(value, out var target))
        {
            comparison = number.CompareTo(target);
        }
        else if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var date)
            && DateTime.TryParse(cell.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var cellDate))
        {
            comparison = cellDate.CompareTo(date);
        }
        else
        {
            comparison = string.Compare(cell.ToString(), value, StringComparison.OrdinalIgnoreCase);
        }

        return op switch
        {
            "eq" => comparison == 0,
            "ne" => comparison != 0,
            "gt" => comparison > 0,
            "ge" => comparison >= 0,
            "lt" => comparison < 0,
            "le" => comparison <= 0,
            _ => false,
        };
    }

    private IEnumerable<string> ColumnsOf(string entity) => _tables[entity][0].Keys;

    private string Record(string tool, Dictionary<string, object?> arguments, Func<string> run)
    {
        var result = run();
        Calls.Add(new ToolCallRecord(tool, arguments, result));
        return result;
    }

    private static Dictionary<string, object?> Row(params (string Column, object? Value)[] cells) =>
        cells.ToDictionary(c => c.Column, c => c.Value);

    private sealed class StubToolProvider(IReadOnlyList<AITool> tools) : IMcpToolProvider
    {
        public Task<IReadOnlyList<AITool>> GetToolsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(tools);
    }
}

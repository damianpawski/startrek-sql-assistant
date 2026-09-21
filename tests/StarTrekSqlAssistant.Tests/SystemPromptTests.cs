using System.Text.Json;
using System.Text.RegularExpressions;
using StarTrekSqlAssistant.Web.Components.Pages;
using StarTrekSqlAssistant.Web.Services;

namespace StarTrekSqlAssistant.Tests;

/// <summary>
/// The SystemPrompt is the only schema the model ever sees, because DAB's
/// describe_entities returns no fields. That makes it a piece of engineering,
/// not a piece of prose: if it drifts from dab/dab-config.json or from
/// db-init/init.sql, the model is told about columns and entities that are not
/// there, and answers "the database has no such field" for ones that are.
///
/// These tests are the sync check CLAUDE.md asks for by hand ("adding or
/// renaming an entity is a two-file change ... keep them in sync"). The schema
/// is written out in four places - dab-config.json, init.sql, the prompt, and
/// the chat page's empty-state cards - and every one of them is pinned here, so
/// `dotnet test --filter "SystemPrompt"` is the whole drift check.
/// </summary>
public class SystemPromptTests
{
    private static readonly string Prompt = StarTrekPrompt.SystemPrompt;

    [Fact]
    public void The_fixtures_really_loaded()
    {
        // Guards the tests below: a parse that silently found nothing would make
        // every "for each entity" assertion pass vacuously.
        Assert.Equal(6, Repo.DabEntities.Count);
        Assert.Equal(6, Repo.TableColumns.Count);
        Assert.Equal(6, PromptEntities().Count);
        Assert.NotEmpty(HomeTopics.All);
    }

    [Fact]
    public void Every_entity_DAB_exposes_is_described_in_the_prompt()
    {
        foreach (var entity in Repo.DabEntities.Keys)
        {
            Assert.True(
                Prompt.Contains(entity + "(", StringComparison.Ordinal),
                $"dab-config.json exposes '{entity}' but the prompt has no column list for it. " +
                "Adding an entity is a two-file change.");
        }
    }

    [Fact]
    public void The_prompt_invents_no_entity_that_DAB_does_not_expose()
    {
        foreach (var entity in PromptEntities().Keys)
        {
            Assert.True(
                Repo.DabEntities.ContainsKey(entity),
                $"The prompt describes '{entity}', which dab-config.json does not expose. " +
                "The model will call a tool for it and get an error.");
        }
    }

    [Fact]
    public void Every_column_the_prompt_claims_exists_in_the_database()
    {
        foreach (var (entity, columns) in PromptEntities())
        {
            var table = Repo.DabEntities[entity];
            var actual = Repo.TableColumns[table];

            foreach (var column in columns)
            {
                Assert.True(
                    actual.Contains(column, StringComparer.OrdinalIgnoreCase),
                    $"The prompt tells the model {entity} has a '{column}' column, but {table} in " +
                    $"init.sql has only: {string.Join(", ", actual)}.");
            }
        }
    }

    [Fact]
    public void The_prompt_warns_that_describe_entities_returns_nothing()
    {
        // Without this the model follows DAB's own "STEP 1: describe_entities"
        // tool description, sees an empty field list, and answers that the data
        // does not exist. Verified against DAB 2.0.9 for all six entities.
        Assert.Contains("describe_entities returns an empty field list", Prompt);
    }

    [Fact]
    public void The_prompt_says_titles_are_stored_in_full()
    {
        // "Deep Space Nine" eq-filtered against "Star Trek: Deep Space Nine"
        // matches nothing, and the model concludes the show is not in the data.
        Assert.Contains("Star Trek: Deep Space Nine", Prompt);
        Assert.Contains("titles are stored in full", Prompt);
    }

    [Fact]
    public void The_prompt_states_the_date_filter_format_that_DAB_accepts()
    {
        Assert.Matches(@"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z", Prompt);
        Assert.Contains("no quotes", Prompt);
        Assert.Contains("end eq null", Prompt);
    }

    [Fact]
    public void The_prompt_forbids_aggregating_an_expression()
    {
        Assert.Contains("never an expression", Prompt);
    }

    [Fact]
    public void The_prompt_tells_the_model_the_database_is_the_source_of_truth()
    {
        // The whole point of the demo: answers come from SQL Server, not from
        // what the model happens to remember about Star Trek.
        Assert.Contains("the database is the source of truth", Prompt);
    }

    [Fact]
    public void Every_entity_DAB_exposes_is_offered_on_the_page()
    {
        // An entity with no card is one nobody discovers: the empty state is the
        // only place the app says what it can be asked about.
        var offered = HomeTopics.All.SelectMany(topic => topic.Entities).ToHashSet(StringComparer.Ordinal);

        foreach (var entity in Repo.DabEntities.Keys)
        {
            Assert.True(
                offered.Contains(entity),
                $"dab-config.json exposes '{entity}' but no card on the chat page covers it. " +
                "Add it to an existing Topic's Entities, or give it a card of its own.");
        }
    }

    [Fact]
    public void The_page_offers_no_entity_DAB_does_not_expose()
    {
        // The other direction, and the one a rename breaks: a card promising
        // something DAB no longer serves sends a visitor to a sample question
        // the model cannot answer, which reads as the app being broken.
        foreach (var topic in HomeTopics.All)
        {
            foreach (var entity in topic.Entities)
            {
                Assert.True(
                    Repo.DabEntities.ContainsKey(entity),
                    $"The '{topic.Title}' card claims entity '{entity}', which dab-config.json does not " +
                    "expose. Its sample questions cannot be answered.");
            }
        }
    }

    [Fact]
    public void Every_card_carries_an_entity_and_a_question()
    {
        // Guards the two tests above from passing vacuously: a card with no
        // entities is invisible to both of them, and one with no questions is a
        // heading a visitor cannot act on.
        foreach (var topic in HomeTopics.All)
        {
            Assert.NotEmpty(topic.Entities);
            Assert.NotEmpty(topic.Questions);
        }
    }

    /// <summary>The entity name -> column list lines the prompt spells out, e.g. "Series(series_id, title, begin, end)".</summary>
    private static Dictionary<string, string[]> PromptEntities()
    {
        var matches = Regex.Matches(Prompt, @"^\s{2,}(?<entity>[A-Z]\w+)\((?<columns>[^)]*)\)", RegexOptions.Multiline);
        Assert.NotEmpty(matches);

        return matches.ToDictionary(
            m => m.Groups["entity"].Value,
            m => m.Groups["columns"].Value
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(c => Regex.Replace(c, @"\s+", ""))
                .ToArray());
    }
}

/// <summary>
/// Reads the two files the prompt has to agree with. Config and schema are the
/// fixtures here - a test that hardcoded the entity list would go stale in
/// exactly the way it is meant to catch.
/// </summary>
internal static class Repo
{
    /// <summary>The repository root, so a test can read a file the app ships.</summary>
    public static string Root { get; } = FindRoot();

    /// <summary>Entity name (as the model sees it) -> table it reads, from dab-config.json.</summary>
    public static IReadOnlyDictionary<string, string> DabEntities { get; } = ReadDabEntities();

    /// <summary>Table name -> its columns, from init.sql.</summary>
    public static IReadOnlyDictionary<string, string[]> TableColumns { get; } = ReadTableColumns();

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "StarTrekSqlMPC-POC.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Could not locate the repository root.");
    }

    private static Dictionary<string, string> ReadDabEntities()
    {
        using var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "dab", "dab-config.json")));

        return config.RootElement.GetProperty("entities").EnumerateObject().ToDictionary(
            entity => entity.Name,
            entity => entity.Value.GetProperty("source").GetProperty("object").GetString()!.Replace("dbo.", ""));
    }

    private static Dictionary<string, string[]> ReadTableColumns()
    {
        var sql = File.ReadAllText(Path.Combine(Root, "db-init", "init.sql"));

        return Regex.Matches(sql, @"CREATE TABLE dbo\.(?<table>\w+)\s*\((?<body>[^;]*?)\)\s*;", RegexOptions.Singleline)
            .ToDictionary(
                table => table.Groups["table"].Value,
                table => table.Groups["body"].Value
                    .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "")
                    .Select(column => column.Trim('[', ']', ','))
                    .Where(column => column.Length > 0)
                    .ToArray());
    }
}

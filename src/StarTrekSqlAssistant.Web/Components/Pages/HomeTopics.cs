namespace StarTrekSqlAssistant.Web.Components.Pages;

/// <summary>
/// One card on the chat page's empty state: what the model can be asked about,
/// the fields behind it in plain English, and sample questions those fields can
/// actually answer.
/// </summary>
/// <param name="Title">The heading a visitor reads - a label, not an entity name.</param>
/// <param name="Entities">
/// The dab-config.json entities this card stands for. Card titles are written
/// for people ("Home media" covers three entities; "Episodes" is plural where
/// the entity is not), so the link back to the schema cannot be inferred from
/// the title and has to be stated. Stating it is what lets
/// <c>SystemPromptTests</c> fail when an entity is added or renamed and the
/// cards are not updated to match.
/// </param>
/// <param name="Fields">The columns, described the way a visitor would say them.</param>
/// <param name="Questions">Sample questions, each answerable from <paramref name="Fields"/>.</param>
public sealed record Topic(string Title, string[] Entities, string Fields, string[] Questions);

/// <summary>
/// The empty-state cards, and the fourth place this project spells out its
/// schema - after dab/dab-config.json, db-init/init.sql and
/// <see cref="Services.StarTrekPrompt"/>. The first three are pinned against
/// each other, and this is pinned against them too: a card promising an entity
/// DAB no longer exposes sends a visitor to a question the model cannot answer,
/// and an entity with no card is one nobody discovers.
///
/// Public, and in a file of its own rather than inside Home.razor's code block,
/// for the same reason <see cref="Services.StarTrekPrompt"/> is: content a test
/// has to assert against should not be a private member of a component.
/// </summary>
public static class HomeTopics
{
    public static readonly Topic[] All =
    [
        new("Series",
            ["Series", "SeriesSummary"],
            "Title, abbreviation, first and last air date, episode and season counts, how long it ran.",
            [
                "When did Deep Space Nine start and end?",
                "Which Star Trek series has the most episodes?",
                "Which Star Trek series premiered in the 1990s?",
            ]),

        new("Episodes",
            ["Episode", "EpisodeDetail"],
            "Title, series, season, episode number, air date and year, remastered air date, production code, stardate.",
            [
                "How many episodes of The Next Generation are there?",
                "How many Star Trek episodes aired in 1995?",
                "List the season 1 episodes of Strange New Worlds.",
            ]),

        new("Movies",
            ["Movie"],
            "Title, theatrical release date, stardate. Not linked to a series.",
            [
                "List the Star Trek movies released after 2000, newest first.",
                "How many Star Trek movies are in the database?",
            ]),

        new("Home media",
            ["MediaSet", "MediumVolume", "MediumVolumeEpisode", "EpisodeOnDisc"],
            "DVD, remastered DVD, HD DVD and Blu-ray sets per series and season, the discs in each set, and which episodes are on which disc.",
            [
                "What home-media formats were released for The Original Series?",
                "How many discs are in the Next Generation season 3 Blu-ray set?",
                "Which disc of the Next Generation Blu-rays has \"Yesterday's Enterprise\"?",
            ]),
    ];
}

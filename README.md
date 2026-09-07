# Star Trek SQL Assistant

Ask plain-English questions about the Star Trek franchise (series, episodes,
movies, home-media releases) and get answers backed by a real SQL Server
query — not the model guessing from memory.

**Stack:** .NET 10 Blazor Web App (Interactive Server) → local Ollama model →
[Data API builder](https://learn.microsoft.com/en-us/azure/data-api-builder/mcp/overview)'s
SQL MCP Server → SQL Server. The database, DAB, and the web app run in Docker;
Ollama runs natively on your machine so it can use your GPU.

The model never writes SQL. It picks from a small set of MCP tools
(`read_records`, `aggregate_records`, `describe_entities`, ...) that DAB
exposes for the `Series`, `Episode`, `Movie`, `MediaSet`, `MediumVolume`, and
`MediumVolumeEpisode` entities; DAB's own query builder turns that tool call
into a deterministic, parameterized T-SQL query.

## Prerequisites

- Docker and Docker Compose
- [Ollama](https://ollama.com/download) installed and running on the host —
  **not** in a container. A native install gets GPU acceleration; the
  `ollama/ollama` image would fall back to CPU-only inference unless you
  configure GPU passthrough, and would download its own second copy of the
  model. (If you'd rather containerize it anyway, `docs/BuildGuide.md` has
  the drop-in service definition.)
- ~8GB free RAM if you want a comfortable time running SQL Server + a local
  model together
- (Optional) [.NET 10 SDK](https://dotnet.microsoft.com/download) if you want
  to run the Blazor app outside Docker for faster iteration

## Run it

```bash
cp .env.example .env
# edit .env if you want a different SA password or Ollama model

# first run only: pull a tool-calling-capable model into your native Ollama
ollama pull llama3.1
ollama list          # confirm it's there

docker compose up --build -d
```

Then open **http://localhost:8080**.

`OLLAMA_MODEL` has to match a tag `ollama list` actually prints, exactly. If
you already have `llama3.1:8b` but not a bare `llama3.1`, either set
`OLLAMA_MODEL=llama3.1:8b` in `.env` or run `ollama pull llama3.1` — the two
are different tags as far as the API is concerned.

The app reaches your host's Ollama at `http://host.docker.internal:11434` —
`host.docker.internal` is the hostname Docker gives containers for the
machine they're running on. Nothing else is needed as long as Ollama is
listening on its default port.

The first `docker compose up` takes a few minutes: SQL Server has to start,
`sql-init` creates the `StarTrek` database and loads the schema/data, then DAB
and the Blazor app come up. If your first question gets "I can't reach the
database connector yet," DAB likely just wasn't fully warmed up — ask again a
few seconds later, or `docker compose restart blazor-app`.

## What's in here

```
docker-compose.yml          4 services: sqlserver, sql-init, dab, blazor-app
.env.example                SA password + Ollama model/endpoint, copy to .env
db-init/init.sql            Creates the StarTrek DB, schema, and data (runs once)
dab/dab-config.json         DAB entity config — this is what generates the MCP tools
src/StarTrekSqlAssistant.Web/
  Program.cs                 DI wiring: Blazor + the agent service
  Services/StarTrekAgentService.cs   MCP client + Ollama + the tool-calling loop
  Components/Pages/Home.razor        The chat UI
  Dockerfile
```

## The database

`db-init/init.sql` is a SQL Server (T-SQL) port of the SQLite database at
[chungy/startrek-db](https://github.com/chungy/startrek-db) — six tables
(`series`, `episode`, `movie`, `media_set`, `medium_volume`,
`medium_volume_episode`) covering every Star Trek series and episode, all
fourteen films, and their DVD/Blu-ray/HD DVD releases, plus the original
per-series convenience views (`tng`, `ds9`, `voy`, ...). All credit for
compiling the underlying data goes to that project; this port only changes
the SQL dialect (data types, joins, reserved-word column names) to run on SQL
Server instead of SQLite.

## Swapping the model

The app talks to its model through `IChatClient`, so the backend is a config
switch — `MODEL_PROVIDER` in `.env` (`Ollama`, `OpenAI`, or `Anthropic`), or
`Model__Provider` if you're running outside Docker. Only the selected
provider's settings are read.

**Local (default).** Any Ollama model that supports tool calling works —
`ollama pull <model>` on the host, set `OLLAMA_MODEL` in `.env`, then
`docker compose up -d` to recreate `blazor-app` with the new value.

**Hosted.** Set the provider and its key:

```bash
MODEL_PROVIDER=Anthropic
ANTHROPIC_API_KEY=sk-ant-...
ANTHROPIC_MODEL=claude-haiku-4-5     # or claude-sonnet-5, claude-opus-5
```

```bash
MODEL_PROVIDER=OpenAI
OPENAI_API_KEY=sk-...
OPENAI_MODEL=gpt-4o-mini
```

Running the app on the host instead of in Docker, the keys go in user-secrets
rather than `.env`:

```bash
dotnet user-secrets set "Anthropic:ApiKey" "sk-ant-..."   --project src/StarTrekSqlAssistant.Web
```

A hosted provider with no key fails at startup with a message naming the
setting, rather than breaking on the first question. Either way, the tool
list, the tool-calling loop, and the UI are unchanged — only
`Services/ChatClientFactory.cs` knows a vendor exists.

Model choice matters more than it looks. Small local models tend to guess
column names rather than look them up, and answer "the database doesn't have
that field" when they guess wrong; check `docker compose logs -f blazor-app`
to see which tool actually got called before blaming the data.

## Ideas for extending this

- Expose the pre-made per-series views (`tng`, `voy`, `ds9`, ...) as their own
  DAB entities for narrower, purpose-built tools
- Add a "manager" GraphQL/REST role with its own permissions instead of the
  demo's single `anonymous` read-only role
- Swap Ollama for Azure OpenAI, OpenAI, or Anthropic — only
  `StarTrekAgentService`'s constructor changes, since everything downstream
  talks to the shared `IChatClient` abstraction

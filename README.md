# Strazh
Your codebase - is your Knowledge Graph.

**Strazh** is a .NET Core console app to build a Codebase Knowledge Graph from a C# codebase powered by Roslyn analyzer.

**Codebase Knowledge Graph** is a graph-structured dataset with free-form relationships between entities of codebase, semantic models of programming language, project structure and other interlinked knowledges.

![ETL](/Documentation/img/strazh-etl.png)

More details in the article [Codebase Knowledge Graph](https://vladbatushkov.medium.com/204f32b58813?source=friends_link&sk=adc2d577a5fa3ae9886b2dd6eb29b428)

## About this fork

This is a fork of [vladbatushkov/strazh](https://github.com/vladbatushkov/strazh) with significant reliability, data, performance, and usability improvements made while using Strazh to analyze a large set of real-world .NET solutions. Highlights:

**Robustness — the analysis no longer aborts on a single bad project or solution:**
- Projects the build pipeline drops (build failed, unreadable log, or timed out) are represented from fallback data instead of silently vanishing from the graph.
- Unparseable solutions are recorded as build-failed rather than aborting the whole run.
- Unresolvable project references are recovered from instead of being fatal.
- Truncated binlogs are re-read with a short delay, and Buildalyzer's `Could not find build environment` (a missing SDK/workload, e.g. Xamarin/MAUI) is treated as a build failure — neither crashes the run.

**Richer graph data:**
- Project target frameworks are recorded on `Project` nodes.
- Submodule references are detected, so submodule folders are no longer misattributed to the parent repository.

**Performance:**
- Neo4j inserts are batched and parameterized.

**Usability:**
- A directory scan mode analyzes every solution and project beneath a directory in a single build/load pass, instead of requiring separate per-solution and project-sweep runs.

**Progress & diagnostics:**
- The console progress uses `AnsiConsole.Progress` (fixing terminal-state corruption), shows a compact live panel with per-stage timing, and tears down cleanly at the end of a run.
- Per-stage analysis metrics are written to a report file at the end of each run.
- A `progress-harness.cs` tool (see [Development](#development)) exercises the progress display in isolation.
- All build warnings fixed; git detection is worktree-aware.

#### Documentation

[Run with docker-compose](/Documentation/docker-compose-run.md)

[Manual run](/Documentation/manual-run.md)

[Command Line Interface](/Documentation/cli.md)

#### Development

`progress-harness.cs` is a standalone [file-based app](https://learn.microsoft.com/en-us/dotnet/core/sdk/file-based-apps) that drives the console progress display (`SpectreConsoleProgress`) with a simulated analysis workload — a concurrent build/load/insert pipeline with completions scrolling, a deferred-then-recorded project, and a metrics-style block printed after the display tears down. It reproduces the live-display behaviour in a few seconds without running the full pipeline, which is handy when working on the progress UI and its teardown:

```
dotnet run progress-harness.cs -- --total 165 --concurrency 20 --min 60 --max 260
```

The flags (`--total`, `--concurrency`, `--min`/`--max` per-stage delay in ms) tune the workload — raise `--total` and the delays to grow the panel taller and scroll more.

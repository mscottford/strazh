# Strazh
Your codebase - is your Knowledge Graph.

**Strazh** is a .NET Core console app to build a Codebase Knowledge Graph from a C# codebase powered by Roslyn analyzer.

**Codebase Knowledge Graph** is a graph-structured dataset with free-form relationships between entities of codebase, semantic models of programming language, project structure and other interlinked knowledges.

![ETL](/Documentation/img/strazh-etl.png)

More details in the article [Codebase Knowledge Graph](https://vladbatushkov.medium.com/204f32b58813?source=friends_link&sk=adc2d577a5fa3ae9886b2dd6eb29b428)

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

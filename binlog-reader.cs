#!/usr/bin/env dotnet

#:package MSBuild.StructuredLogger@2.3.154

using Microsoft.Build.Logging.StructuredLogger;

if (args.Length == 0)
{
    Console.Error.WriteLine("Usage: dotnet binlog-reader.cs <path-to-binlog> [...]");
    return 1;
}

foreach (var path in args)
{
    if (!File.Exists(path))
    {
        Console.Error.WriteLine($"File not found: {path}");
        continue;
    }

    Console.WriteLine($"\n=== {Path.GetFileName(path)} ===");

    var build = BinaryLog.ReadBuild(path);

    // Collect all Project nodes, gather their errors, then deduplicate by
    // (ProjectFile, TargetFramework) — MSBuild evaluates each project multiple
    // times across target batches, producing duplicate nodes.
    var projects = new List<Project>();
    build.VisitAllChildren<Project>(p => projects.Add(p));

    // Inner builds have TargetFramework set; for single-targeted projects all
    // nodes lack it, so fall back to the full list.
    var innerBuilds = projects.Where(p => !string.IsNullOrEmpty(p.TargetFramework)).ToList();
    var toShow = innerBuilds.Count > 0 ? innerBuilds : projects;

    // Aggregate errors across all nodes for the same (file, TFM) pair.
    var grouped = toShow
        .GroupBy(p => (File: p.ProjectFile ?? p.Name, TFM: p.TargetFramework ?? ""))
        .Select(g =>
        {
            var errors = new List<Error>();
            foreach (var proj in g)
            {
                proj.VisitAllChildren<Error>(e => errors.Add(e));
            }
            return (g.Key.File, g.Key.TFM, Errors: errors.DistinctBy(e => (e.Code, e.Text, e.File, e.LineNumber)).ToList());
        });

    foreach (var byFile in grouped.GroupBy(x => x.File))
    {
        Console.WriteLine($"\n  {Path.GetFileName(byFile.Key)}");
        foreach (var (_, tfm, errors) in byFile.OrderBy(x => x.TFM))
        {
            var label = string.IsNullOrEmpty(tfm) ? "(no TFM)" : tfm;
            var status = errors.Count == 0 ? "✓" : "✗";
            Console.WriteLine($"    {status} {label}");
            foreach (var error in errors)
            {
                var location = string.IsNullOrEmpty(error.File) ? "" : $" ({Path.GetFileName(error.File)}:{error.LineNumber})";
                Console.WriteLine($"        {error.Code}: {error.Text}{location}");
            }
        }
    }

    var overallStatus = build.Succeeded ? "succeeded" : "FAILED";
    Console.WriteLine($"\n  Build {overallStatus}.");
}

return 0;

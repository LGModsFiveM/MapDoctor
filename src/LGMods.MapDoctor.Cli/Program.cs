using LGMods.MapDoctor.Core;
using LGMods.MapDoctor.Core.Report;

Console.OutputEncoding = System.Text.Encoding.UTF8; // so "→" in the details prints properly
if (args.Length == 0)
{
    Console.WriteLine("Usage: mapdoctor <server folder> [--html report.html] [--details]");
    return 1;
}

var folder = args[0];
var htmlIndex = Array.IndexOf(args, "--html");
var showDetails = args.Contains("--details");

if (MapDoctorScanner.FindResourcesFolder(folder) is null)
{
    Console.WriteLine($"Couldn't find any FiveM resources in \"{folder}\". Point it at the folder with server.cfg and a resources folder inside.");
    return 1;
}

var result = MapDoctorScanner.Scan(folder);
Console.WriteLine($"Scanned {result.Resources.Count:N0} resources ({result.StartedMapCount:N0} maps switched on, {result.StreamFileCount:N0} files) in {result.Duration.TotalSeconds:0.0}s");
Console.WriteLine($"Start-up list: {result.StartListFile ?? "not found"}");
Console.WriteLine($"{result.ProblemCount} problems, {result.WarningCount} worth a look, {result.InfoCount} info");
Console.WriteLine();

foreach (var f in result.Findings)
{
    Console.WriteLine($"[{f.Severity}] {f.Title}");
    Console.WriteLine($"   Where: {f.Where}");
    if (showDetails)
        foreach (var d in f.Details.Take(15)) Console.WriteLine($"     - {d}");
}

if (htmlIndex >= 0 && htmlIndex + 1 < args.Length)
{
    File.WriteAllText(args[htmlIndex + 1], HtmlReport.Build(result, "CLI"));
    Console.WriteLine($"\nReport saved to {args[htmlIndex + 1]}");
}
return 0;

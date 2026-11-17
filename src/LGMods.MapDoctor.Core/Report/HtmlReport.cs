using System.Net;
using System.Text;
using LGMods.MapDoctor.Core.Model;
using LGMods.MapDoctor.Core.Text;

namespace LGMods.MapDoctor.Core.Report;

// Self-contained, so it can be sent to a map maker or support as a single file.
public static class HtmlReport
{
    public static string Build(ScanResult result, string appVersion)
    {
        var sb = new StringBuilder();
        sb.Append("""
<!doctype html>
<html lang="en-GB">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>LGMods Map Doctor report</title>
<style>
  :root {
    --bg: #070F11; --surface: #0D1A1D; --surface-2: #12242A;
    --line: rgba(190,245,238,.10); --line-strong: rgba(190,245,238,.22);
    --teal: #5AD2C9; --teal-ink: #06201E; --text: #E8F3F2; --muted: #A9BFBD;
    --problem: #FF6B6B; --warning: #F5B451; --info: #5AD2C9;
  }
  * { box-sizing: border-box; }
  body { margin: 0; background: var(--bg); color: var(--text); font: 15px/1.55 Inter, "Segoe UI", system-ui, sans-serif; }
  main { max-width: 920px; margin: 0 auto; padding: 32px 16px 64px; }
  .brand { font: 700 14px/1 "Bebas Neue", Bahnschrift, Impact, sans-serif; letter-spacing: .08em; color: var(--muted); }
  .brand b { color: var(--text); }
  h1 { font: 400 56px/1 "Bebas Neue", Bahnschrift, Impact, sans-serif; margin: 16px 0 8px; letter-spacing: .01em; }
  h1 span { color: var(--teal); }
  .sub { color: var(--muted); margin: 0 0 24px; }
  .stats { display: flex; flex-wrap: wrap; gap: 8px; margin-bottom: 28px; }
  .stat { background: var(--surface); border: 1px solid var(--line); border-radius: 12px; padding: 10px 14px; color: var(--muted); }
  .stat b { color: var(--text); font-size: 18px; margin-right: 4px; }
  .explain { background: var(--surface-2); border-radius: 14px; padding: 16px 20px; margin-bottom: 8px; color: var(--muted); }
  .explain h3 { color: var(--text); margin: 0 0 6px; font-size: 16px; }
  .group { font: 700 15px/1 "Bebas Neue", Bahnschrift, Impact, sans-serif; color: #7F9997; margin: 28px 0 10px; letter-spacing: .04em; }
  .card { position: relative; background: var(--surface); border: 1px solid var(--line); border-radius: 14px; padding: 16px 20px 16px 38px; margin-bottom: 10px; }
  .dot { position: absolute; left: 18px; top: 23px; width: 8px; height: 8px; border-radius: 50%; background: var(--sev); }
  h2 { font-size: 16px; margin: 0 0 2px; overflow-wrap: anywhere; }
  .where { color: var(--muted); margin: 0 0 10px; font-size: 14px; }
  table { width: 100%; border-collapse: collapse; margin: 10px 0 0; font-size: 13.5px; background: var(--bg-2, #0A1517); border-radius: 10px; overflow: hidden; }
  th { text-align: left; font-weight: 500; color: var(--muted); font-size: 12.5px; padding: 8px 12px; }
  td { border-top: 1px solid var(--line); padding: 8px 12px; overflow-wrap: anywhere; }
  td.right { text-align: right; }
  .mono { font-family: Consolas, monospace; font-size: 12.5px; }
  .muted { color: var(--muted); }
  b.in, b.out { font-size: 12px; padding: 2px 7px; border-radius: 6px; font-weight: 600; }
  b.in { color: var(--teal); background: rgba(90,210,201,.12); }
  b.out { color: var(--problem); background: rgba(255,107,107,.12); }
  ol { margin: 6px 0 0; padding-left: 20px; }
  details { margin-top: 12px; color: var(--muted); }
  summary { cursor: pointer; color: var(--teal); }
  details ul { font: 12.5px/1.5 Consolas, monospace; padding-left: 18px; overflow-wrap: anywhere; }
  .problem { --sev: var(--problem); } .warning { --sev: var(--warning); } .info { --sev: var(--info); }
  footer { color: var(--muted); font-size: 13px; margin-top: 32px; border-top: 1px solid var(--line); padding-top: 16px; }
  a { color: var(--teal); }
</style>
</head>
<body><main>
""");
        sb.Append("<div class=\"brand\"><b>LGMODS</b> · MAP DOCTOR</div>");

        var problems = result.ProblemCount;
        var headline = problems == 0
            ? "<h1>NO PROBLEMS FOUND. <span>SORTED.</span></h1>"
            : $"<h1>{Plain.Count(problems, "PROBLEM").ToUpperInvariant()} <span>FOUND.</span></h1>";
        sb.Append(headline);
        sb.Append($"<p class=\"sub\">Scanned {E(result.ResourcesFolder)} on {result.ScannedAt:d MMMM yyyy 'at' HH:mm}.</p>");

        sb.Append("<div class=\"stats\">");
        Stat(sb, result.ProblemCount, "to fix");
        Stat(sb, result.WarningCount, "worth checking");
        Stat(sb, result.InfoCount, "for info");
        Stat(sb, result.StartedMapCount, "maps checked");
        Stat(sb, result.StreamFileCount, "files checked");
        sb.Append("</div>");

        // Explain clashes once, rather than on every card.
        if (result.Findings.Any(f => f.Kind == FindingKind.ClashingFiles))
        {
            sb.Append("<section class=\"explain\"><h3>How clashes work</h3>" +
                      "<p>When two maps include a file with the same name, only one version can load. The map that starts " +
                      "later wins (<b class=\"in\">In use</b>), and the other map's version is <b class=\"out\">Ignored</b>.</p>" +
                      "<ol><li>Check in game around the ignored map. If it looks right, you can ignore the clash.</li>" +
                      "<li>If it doesn't, ask the makers for versions that work together and send them the file list.</li>" +
                      "<li>Or, if you don't need both maps, turn one off.</li></ol></section>");
        }

        foreach (var group in result.Findings.GroupBy(f => f.Severity))
        {
            sb.Append($"<h4 class=\"group\">{(group.Key switch { Severity.Problem => "TO FIX", Severity.Warning => "WORTH CHECKING", _ => "FOR INFO" })} · {group.Count()}</h4>");
            foreach (var f in group) Card(sb, f);
        }

        sb.Append($"<footer>Made with LGMods Map Doctor {E(appVersion)}. Free from <a href=\"https://store.lgmods.co.uk/\">LGMods</a>. " +
                  "Need a hand? Bring this report to <a href=\"https://discord.gg/lgmods\">discord.gg/lgmods</a>.</footer>");
        sb.Append("</main></body></html>");
        return sb.ToString();
    }

    private static void Card(StringBuilder sb, Finding f)
    {
        var css = f.Severity switch { Severity.Problem => "problem", Severity.Warning => "warning", _ => "info" };
        sb.Append($"<section class=\"card {css}\"><span class=\"dot\"></span>");

        if (f.Kind == FindingKind.ClashingFiles)
        {
            sb.Append($"<h2>{E(string.Join("  ⇄  ", f.Resources.Select(r => r.Name)))}</h2>");
            sb.Append($"<p class=\"where\">{E(f.ShortTitle)} · {E(Plain.Count(f.Files.Count, "file"))}</p>");
            sb.Append("<table><tr><th>Map</th><th></th></tr>");
            foreach (var r in f.Resources)
            {
                var inUse = r == f.Winner;
                sb.Append($"<tr><td>{E(r.Name)}{(r.IsLocked ? " <span class=\"muted\">· locked</span>" : "")}</td>" +
                          $"<td class=\"right\"><b class=\"{(inUse ? "in" : "out")}\">{(inUse ? "In use" : "Ignored")}</b></td></tr>");
            }
            sb.Append("</table><table><tr><th>File</th><th>What it controls in game</th></tr>");
            foreach (var file in f.Files)
                sb.Append($"<tr><td class=\"mono\">{E(file.FileName)}</td><td>{E(file.Meaning)}</td></tr>");
            sb.Append("</table>");
        }
        else
        {
            sb.Append($"<h2>{E(f.Title)}</h2><p class=\"where\">{E(f.Where)}</p>");
            sb.Append($"<p>{E(f.WhatsWrong)}</p><ol>");
            foreach (var step in f.WhatToDo) sb.Append($"<li>{E(step)}</li>");
            sb.Append("</ol>");
        }

        if (f.Details.Count > 0)
        {
            sb.Append($"<details><summary>{(f.Kind == FindingKind.ClashingFiles ? "Where the files are" : "Technical details")}</summary><ul>");
            foreach (var d in f.Details) sb.Append($"<li>{E(d)}</li>");
            sb.Append("</ul></details>");
        }
        sb.Append("</section>");
    }

    private static void Stat(StringBuilder sb, int value, string label) =>
        sb.Append($"<span class=\"stat\"><b>{value:N0}</b>{E(label)}</span>");

    private static string E(string s) => WebUtility.HtmlEncode(s);
}

using LGMods.MapDoctor.Core.Model;

namespace LGMods.MapDoctor.Core.Scanning;

// Works out which resources FXServer really starts, and in what order (checked against a real server's log):
// - If two folders have the same name, only the first one found is used.
// - "ensure [category]" starts everything inside it, sub-categories included, sorted by name with capitals
//   before lower case (digits, then A-Z, then "_", then a-z), not folder by folder.
// - A resource's dependencies start first. If one is missing or can't start, nor can the resource.
//   A resource can stand in for another with "provide".
// - fxmanifest.lua must say it's for GTA V, and any "/gameBuild:N" it needs must be available.
public sealed class StartOrder
{
    // Shipped with FXServer, so never "missing".
    public static readonly HashSet<string> BuiltIn = new(StringComparer.OrdinalIgnoreCase)
    {
        "monitor", "webpack", "yarn", "chat", "mapmanager", "spawnmanager", "sessionmanager",
        "hardcap", "baseevents", "rconlog", "runcode",
    };

    private readonly Dictionary<string, ResourceInfo> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ResourceInfo> _providers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<ResourceInfo> _starting = [];
    private readonly int? _gameBuild;
    private int _next;

    public StartOrder(IReadOnlyList<ResourceInfo> resources, int? gameBuild)
    {
        _gameBuild = gameBuild;
        foreach (var r in resources)
        {
            if (_byName.TryGetValue(r.Name, out var first)) r.UsedInstead = first;
            else _byName[r.Name] = r;
        }
        foreach (var r in _byName.Values)
            foreach (var name in r.Manifest.Provides)
                _providers.TryAdd(name, r);
        Used = resources.Where(r => r.UsedInstead is null).ToList();
    }

    // The first folder found for each name.
    public IReadOnlyList<ResourceInfo> Used { get; }

    public ResourceInfo? Find(string name) => _byName.GetValueOrDefault(name);

    public IEnumerable<ResourceInfo> InCategory(string category) => Used
        .Where(r => r.Categories.Contains(category, StringComparer.OrdinalIgnoreCase))
        .OrderBy(r => r.Name, StringComparer.Ordinal);

    public void Stop(ResourceInfo resource) => resource.StartOrder = null;

    // Dependencies first. Returns false if FXServer would refuse.
    public bool Start(ResourceInfo resource)
    {
        if (resource.StartOrder is not null) return true;
        if (resource.WontStart is not null) return false;

        // Another resource that's already running stands in for this one ("provide"), so FXServer skips it.
        if (_providers.TryGetValue(resource.Name, out var standIn) && standIn != resource && standIn.IsStarted) return true;
        if (!_starting.Add(resource)) return true; // two resources that need each other: FXServer lets it go

        try
        {
            if (!resource.IsForGta5)
                return Refuse(resource, new StartProblem(StartProblemKind.NotForGta5, []));

            var missing = new List<string>();
            var broken = new List<string>();
            var needsBuild = 0;
            foreach (var dependency in resource.Manifest.Dependencies)
            {
                var name = dependency.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
                if (name.StartsWith('/'))
                {
                    // Server features like "/onesync" or "/server:5848". Only the game version is worth checking.
                    if (name.StartsWith("/gameBuild:", StringComparison.OrdinalIgnoreCase)
                        && int.TryParse(name["/gameBuild:".Length..], out var build)
                        && _gameBuild is { } have && build > have)
                        needsBuild = Math.Max(needsBuild, build);
                    continue;
                }

                var target = _byName.GetValueOrDefault(name) ?? _providers.GetValueOrDefault(name);
                if (target is null)
                {
                    if (!BuiltIn.Contains(name)) missing.Add(name);
                    continue;
                }
                if (target != resource && !Start(target)) broken.Add(target.Name);
            }

            if (needsBuild > 0)
                return Refuse(resource, new StartProblem(StartProblemKind.NeedsNewerGame, [needsBuild.ToString()]));
            if (missing.Count > 0)
                return Refuse(resource, new StartProblem(StartProblemKind.MissingNeeds, missing));
            if (broken.Count > 0)
                return Refuse(resource, new StartProblem(StartProblemKind.NeedsBrokenResource, broken));

            resource.StartOrder = _next++;
            return true;
        }
        finally
        {
            _starting.Remove(resource);
        }
    }

    private static bool Refuse(ResourceInfo resource, StartProblem problem)
    {
        resource.WontStart = problem;
        return false;
    }
}

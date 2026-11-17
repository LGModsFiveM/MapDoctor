using System.Numerics;
using LGMods.MapDoctor.Core.Model;
using LGMods.MapDoctor.Core.Reading;
using LGMods.MapDoctor.Core.Text;

namespace LGMods.MapDoctor.Core.Checks;

// Two maps that build an interior (MLO) in the same place even though no file names match, e.g. two police
// stations both replacing Mission Row. Both load, so players get two buildings mixed together.
public static class SameSpotCheck
{
    // How much of the smaller interior has to be inside the other before we call it a clash.
    public const float OverlapNeeded = 0.25f;

    // Metres. Two copies of the same interior this close are the same building twice.
    private const float SamePlace = 5f;

    public sealed record Overlap(InteriorPlacement A, InteriorPlacement B, float Amount);

    public static IEnumerable<Finding> Run(IReadOnlyList<ResourceInfo> resources, CancellationToken cancel = default) =>
        Group(FindOverlaps(resources, cancel));

    public static List<Overlap> FindOverlaps(IReadOnlyList<ResourceInfo> resources, CancellationToken cancel = default, float needed = OverlapNeeded)
    {
        // Only the copy of each file that actually loads: the one from the map that starts last.
        var loaded = resources.Where(r => r.IsStarted)
            .SelectMany(r => r.StreamFiles)
            .Where(f => f.Extension is ".ymap" or ".ytyp")
            .GroupBy(f => f.FileName, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(f => f.Resource.StartOrder).First())
            .OrderBy(f => f.Resource.StartOrder)
            .ToList();

        var shapes = new Dictionary<uint, InteriorShape>();
        foreach (var ytyp in loaded.Where(f => f.Extension == ".ytyp"))
        {
            cancel.ThrowIfCancellationRequested();
            foreach (var shape in Interiors.Shapes(ytyp)) shapes[shape.Interior] = shape;
        }

        var placed = new List<(InteriorPlacement Placement, List<(Vector3 Min, Vector3 Max)>[]? Rooms)>();
        foreach (var ymap in loaded.Where(f => f.Extension == ".ymap"))
        {
            cancel.ThrowIfCancellationRequested();
            foreach (var p in Interiors.Placements(ymap))
                placed.Add((p, shapes.TryGetValue(p.Interior, out var s)
                    ? [Interiors.WorldRooms(p, s, conjugate: false), Interiors.WorldRooms(p, s, conjugate: true)]
                    : null));
        }

        var overlaps = new List<Overlap>();
        for (var i = 0; i < placed.Count; i++)
            for (var j = i + 1; j < placed.Count; j++)
            {
                var (a, boxesA) = placed[i];
                var (b, boxesB) = placed[j];
                if (a.Ymap.Resource == b.Ymap.Resource) continue;

                float amount;
                if (boxesA is not null && boxesB is not null)
                    amount = Math.Min(Shared(boxesA[0], boxesB[0]), Shared(boxesA[1], boxesB[1]));
                else if (a.Interior == b.Interior && Vector3.Distance(a.Position, b.Position) < SamePlace)
                    amount = 1;
                else
                    continue;

                if (amount >= needed && amount > 0) overlaps.Add(new Overlap(a, b, amount));
            }
        return overlaps;
    }

    // Fraction (0 to 1) of the smaller interior's rooms inside the other's. Comparing room by room, not whole
    // buildings, means an add-on built into a gap next to a building doesn't count.
    private static float Shared(List<(Vector3 Min, Vector3 Max)> a, List<(Vector3 Min, Vector3 Max)> b)
    {
        static float Volume(Vector3 size) => Math.Max(0, size.X) * Math.Max(0, size.Y) * Math.Max(0, size.Z);
        var shared = 0f;
        foreach (var ra in a)
            foreach (var rb in b)
                shared += Volume(Vector3.Min(ra.Max, rb.Max) - Vector3.Max(ra.Min, rb.Min));
        var smaller = Math.Min(a.Sum(r => Volume(r.Max - r.Min)), b.Sum(r => Volume(r.Max - r.Min)));
        return smaller <= 1 ? 0 : Math.Min(1, shared / smaller);
    }

    private static IEnumerable<Finding> Group(List<Overlap> overlaps)
    {
        // One finding per spot: interiors that overlap each other, directly or through a third, belong together,
        // so three maps fighting over one spot is one finding, not three.
        var parent = new Dictionary<InteriorPlacement, InteriorPlacement>();
        InteriorPlacement Root(InteriorPlacement p)
        {
            while (parent.TryGetValue(p, out var up) && up != p) p = up;
            return p;
        }
        foreach (var o in overlaps)
        {
            parent.TryAdd(o.A, o.A);
            parent.TryAdd(o.B, o.B);
            var (ra, rb) = (Root(o.A), Root(o.B));
            if (ra != rb) parent[rb] = ra;
        }

        foreach (var group in overlaps.GroupBy(o => Root(o.A)))
        {
            var list = group.ToList();
            var maps = list.SelectMany(o => new[] { o.A.Ymap.Resource, o.B.Ymap.Resource }).Distinct()
                .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
            var names = maps.Select(r => r.Name).ToList();
            var spot = list[0].A.Position;
            var both = maps.Count == 2 ? "Both" : $"All {maps.Count}";

            yield return new Finding
            {
                Kind = FindingKind.SameSpot,
                Severity = Severity.Problem,
                Title = $"{(maps.Count == 2 ? "Two" : maps.Count.ToString())} interiors are built in the same spot",
                ShortTitle = "Interiors in the same spot",
                Where = $"Around the {Plain.JoinAndShort(names, noun: "map")}",
                WhatsWrong =
                    $"{both} maps put an interior in the same place in the GTA world. Their file names are different, " +
                    "so both load, and players get two buildings mixed together: walls and floors flickering or overlapping, " +
                    "doors that lead nowhere, or falling through the floor. This usually means you have two versions of the " +
                    "same building (for example two police stations for the same spot).",
                WhatToDo =
                [
                    $"Go to the spot in game (position {spot.X:0}, {spot.Y:0}, {spot.Z:0}) and see if it looks wrong.",
                    "If it does, decide which of these maps you want there and remove the other from your start-up list.",
                    "If they're meant to work together, ask the makers. One of them may have a version without the overlapping part.",
                ],
                Details = list
                    .SelectMany(o => new[] { o.A, o.B })
                    .Distinct()
                    .OrderBy(p => p.Ymap.Resource.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(p => $"{p.Ymap.Resource.Name}: {p.Ymap.RelativePath} places an interior at {p.Position.X:0.0}, {p.Position.Y:0.0}, {p.Position.Z:0.0}")
                    .Append($"Overlap: {list.Max(o => o.Amount):P0} of the smaller interior")
                    .ToList(),
                Resources = maps,
            };
        }
    }
}

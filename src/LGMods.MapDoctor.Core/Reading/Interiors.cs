using System.Numerics;
using LGMods.MapDoctor.Core.Model;

namespace LGMods.MapDoctor.Core.Reading;

public sealed record InteriorPlacement(StreamFile Ymap, uint Interior, Vector3 Position, Quaternion Rotation);

// Relative to the interior's own centre point, before it's placed.
public sealed record InteriorRoom(string Name, Vector3 Min, Vector3 Max);

// "limbo" (the outside) is left out.
public sealed record InteriorShape(uint Interior, IReadOnlyList<InteriorRoom> Rooms);

public static class Interiors
{
    private static readonly uint MloInstance = MetaFile.Hash("CMloInstanceDef");
    private static readonly uint MloArchetype = MetaFile.Hash("CMloArchetypeDef");
    private static readonly uint Room = MetaFile.Hash("CMloRoomDef");

    // Item sizes and field positions, as GTA lays them out.
    private const int MloInstanceSize = 160, MloArchetypeSize = 240, RoomSize = 112;
    private const int NameField = 8, PositionField = 32, RotationField = 48;
    private const int ArchetypeNameField = 88, RoomsField = 168, RoomNameField = 8, RoomMinField = 32, RoomMaxField = 48;
    private static readonly uint Text = 0x10; // block type GTA uses for plain text

    // Metres. Anything bigger is a broken file, not a building.
    private const float MaxRoomSize = 400;

    public static List<InteriorPlacement> Placements(StreamFile ymap)
    {
        var found = new List<InteriorPlacement>();
        if (MetaFile.Read(ymap.FullPath) is not { } meta) return found;
        try
        {
            foreach (var at in meta.Items(MloInstance, MloInstanceSize))
                found.Add(new InteriorPlacement(ymap, meta.U32(at + NameField), meta.Vector(at + PositionField), meta.Rotation(at + RotationField)));
        }
        catch (InvalidDataException) { } // damaged file: keep what was read
        return found.Where(p => float.IsFinite(p.Position.Length()) && float.IsFinite(p.Rotation.Length())).ToList();
    }

    // GTA's own size fields are usually left at zero for interiors, so the rooms are the reliable measure.
    public static List<InteriorShape> Shapes(StreamFile ytyp)
    {
        var found = new List<InteriorShape>();
        if (MetaFile.Read(ytyp.FullPath) is not { } meta) return found;
        try
        {
            foreach (var at in meta.Items(MloArchetype, MloArchetypeSize))
            {
                var rooms = new List<InteriorRoom>();
                foreach (var room in meta.Array(at + RoomsField, Room, RoomSize))
                {
                    var name = meta.Text(room + RoomNameField, Text);
                    if (name.Equals("limbo", StringComparison.OrdinalIgnoreCase)) continue;
                    var a = meta.Vector(room + RoomMinField);
                    var b = meta.Vector(room + RoomMaxField);
                    var size = b - a;
                    if (!float.IsFinite(size.Length()) || size.X <= 0.1f || size.Y <= 0.1f || size.Z <= 0.1f) continue;
                    if (size.X > MaxRoomSize || size.Y > MaxRoomSize || size.Z > MaxRoomSize) continue;
                    rooms.Add(new InteriorRoom(name, a, b));
                }
                if (rooms.Count > 0) found.Add(new InteriorShape(meta.U32(at + ArchetypeNameField), rooms));
            }
        }
        catch (InvalidDataException) { } // damaged file: keep what was read
        return found;
    }

    // GTA's rotation convention for interiors is ambiguous, so callers check both ways and only trust
    // what holds for both.
    public static List<(Vector3 Min, Vector3 Max)> WorldRooms(InteriorPlacement placement, InteriorShape shape, bool conjugate) =>
        shape.Rooms.Select(r => WorldBox(placement, r.Min, r.Max, conjugate)).ToList();

    private static (Vector3 Min, Vector3 Max) WorldBox(InteriorPlacement placement, Vector3 localMin, Vector3 localMax, bool conjugate)
    {
        var q = conjugate ? Quaternion.Conjugate(placement.Rotation) : placement.Rotation;
        if (q.LengthSquared() < 1e-6f) q = Quaternion.Identity;
        q = Quaternion.Normalize(q);
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (var i = 0; i < 8; i++)
        {
            var corner = new Vector3(
                (i & 1) == 0 ? localMin.X : localMax.X,
                (i & 2) == 0 ? localMin.Y : localMax.Y,
                (i & 4) == 0 ? localMin.Z : localMax.Z);
            var world = Vector3.Transform(corner, q) + placement.Position;
            min = Vector3.Min(min, world);
            max = Vector3.Max(max, world);
        }
        return (min, max);
    }
}

using System.Numerics;
using System.Text.Json;
using LGMods.MapDoctor.Core;
using LGMods.MapDoctor.Core.Model;
using LGMods.MapDoctor.Core.Scanning;
using LGMods.MapDoctor.Core.Fixing;
using LGMods.MapDoctor.Core.Text;

namespace LGMods.MapDoctor.Tests;

public sealed class FakeServer : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "mapdoctor-tests", Guid.NewGuid().ToString("N"));
    public string Resources => Path.Combine(Root, "resources");

    public FakeServer() => Directory.CreateDirectory(Resources);

    public FakeServer Resource(string relativeFolder, string manifest = "fx_version 'cerulean'\ngame 'gta5'\nthis_is_a_map 'yes'\n")
    {
        var dir = Path.Combine(Resources, relativeFolder);
        Directory.CreateDirectory(dir);
        System.IO.File.WriteAllText(Path.Combine(dir, "fxmanifest.lua"), manifest);
        return this;
    }

    public FakeServer File(string relativePath, string contents)
    {
        var path = Path.Combine(Resources, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, contents);
        return this;
    }

    public FakeServer Bytes(string relativePath, byte[] contents)
    {
        var path = Path.Combine(Resources, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllBytes(path, contents);
        return this;
    }

    public FakeServer Config(string text)
    {
        System.IO.File.WriteAllText(Path.Combine(Root, "server.cfg"), text);
        return this;
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
    }
}

public class ManifestParserTests
{
    [Fact]
    public void Reads_map_flag_data_files_and_dependencies()
    {
        var manifest = ManifestParser.Parse("""
            fx_version 'cerulean'
            game 'gta5'
            this_is_a_map "yes"
            -- data_file 'DLC_ITYP_REQUEST' 'stream/commented_out.ytyp'
            --[[ dependency 'also_commented' ]]
            data_file 'DLC_ITYP_REQUEST' 'stream\props.ytyp'
            dependency '/assetpacks'
            dependencies { 'ox_lib', "bob74_ipl" }
            """);

        Assert.True(manifest.ThisIsAMap);
        Assert.Equal([("DLC_ITYP_REQUEST", "stream/props.ytyp")], manifest.DataFiles);
        Assert.Equal(["/assetpacks", "ox_lib", "bob74_ipl"], manifest.Dependencies);
    }

    [Fact]
    public void Missing_map_flag_is_false()
    {
        Assert.False(ManifestParser.Parse("fx_version 'cerulean'\nthis_is_a_map 'no'").ThisIsAMap);
    }
}

public class StartListTests
{
    [Fact]
    public void Follows_exec_and_ignores_comments()
    {
        using var server = new FakeServer()
            .Config("""
                ensure first # comment
                // ensure commented
                exec extra.cfg
                stop third
                """);
        File.WriteAllText(Path.Combine(server.Root, "extra.cfg"), "start \"second\"\nensure [maps]\n");

        var list = StartListReader.Read(Path.Combine(server.Root, "server.cfg"));

        Assert.Equal(
            ["first", "second", "[maps]", "third"],
            list.Commands.Select(c => c.Target));
        Assert.Equal(StartAction.Stop, list.Commands[^1].Action);
    }
}

public class ScanTests
{
    [Fact]
    public void Different_files_with_the_same_name_are_a_clash_and_the_later_map_wins()
    {
        using var server = new FakeServer()
            .Resource("[maps]/estate_a").File("[maps]/estate_a/stream/dt1_02.ybn", "version A")
            .Resource("[maps]/estate_b").File("[maps]/estate_b/stream/sub/dt1_02.ybn", "version B, longer")
            .Config("ensure estate_b\nensure estate_a\n");

        var result = MapDoctorScanner.Scan(server.Root);

        var clash = Assert.Single(result.Findings, f => f.Kind == FindingKind.ClashingFiles);
        Assert.Equal(Severity.Problem, clash.Severity);
        Assert.Contains("fall through floors", clash.Title);
        Assert.Equal("estate_a", clash.Winner?.Name);
        var file = Assert.Single(clash.Files);
        Assert.Equal("dt1_02.ybn", file.FileName);
        Assert.Equal(2, file.Copies.Count);
        Assert.Equal("Falling through floors", clash.ShortTitle);
    }

    [Fact]
    public void Identical_copies_are_not_a_problem()
    {
        using var server = new FakeServer()
            .Resource("one").File("one/stream/prop.ydr", "same")
            .Resource("two").File("two/stream/prop.ydr", "same")
            .Config("ensure one\nensure two\n");

        var result = MapDoctorScanner.Scan(server.Root);

        Assert.DoesNotContain(result.Findings, f => f.Kind == FindingKind.ClashingFiles);
        Assert.Contains(result.Findings, f => f.Kind == FindingKind.IdenticalCopies && f.Severity == Severity.Info);
    }

    [Fact]
    public void Each_maps_own_manifest_ymf_is_not_a_clash()
    {
        using var server = new FakeServer()
            .Resource("mlo_a").File("mlo_a/stream/_manifest.ymf", "manifest A")
            .Resource("mlo_b").File("mlo_b/stream/_manifest.ymf", "manifest B, different")
            .Config("ensure mlo_a\nensure mlo_b\n");

        var result = MapDoctorScanner.Scan(server.Root);

        Assert.DoesNotContain(result.Findings, f => f.Kind is FindingKind.ClashingFiles or FindingKind.IdenticalCopies);
    }

    [Fact]
    public void Clashes_in_maps_that_are_switched_off_are_ignored()
    {
        using var server = new FakeServer()
            .Resource("on").File("on/stream/x.ymap", "a")
            .Resource("off").File("off/stream/x.ymap", "b")
            .Config("ensure on\n");

        var result = MapDoctorScanner.Scan(server.Root);

        Assert.DoesNotContain(result.Findings, f => f.Kind == FindingKind.ClashingFiles);
        Assert.Contains(result.Findings, f => f.Kind == FindingKind.MapsSwitchedOff);
    }

    [Fact]
    public void A_fix_resource_that_starts_last_counts_as_already_fixed()
    {
        using var server = new FakeServer()
            .Resource("a").File("a/stream/x.ymap", "a")
            .Resource("b").File("b/stream/x.ymap", "b")
            .Resource("vertex-hub").File("vertex-hub/stream/x.ymap", "merged")
            .Config("ensure a\nensure b\nensure vertex-hub\n");

        var result = MapDoctorScanner.Scan(server.Root);

        Assert.DoesNotContain(result.Findings, f => f.Kind == FindingKind.ClashingFiles);
        Assert.Contains(result.Findings, f => f.Kind == FindingKind.AlreadyFixed);
    }

    [Fact]
    public void Missing_dependency_and_missing_map_flag_are_reported()
    {
        using var server = new FakeServer()
            .Resource("needy", "fx_version 'cerulean'\ngame 'gta5'\nthis_is_a_map 'yes'\ndependency 'not_installed'\ndependency '/assetpacks'\n")
            .File("needy/stream/a.ymap", "a")
            .Resource("unflagged", "fx_version 'cerulean'\ngame 'gta5'\n")
            .File("unflagged/stream/b.ymap", "b")
            .Config("ensure needy\nensure unflagged\n");

        var result = MapDoctorScanner.Scan(server.Root);

        var dep = Assert.Single(result.Findings, f => f.Kind == FindingKind.MissingDependency);
        Assert.Contains("not_installed", dep.WhatsWrong);
        Assert.DoesNotContain("assetpacks", dep.WhatsWrong);
        Assert.Equal("unflagged", Assert.Single(Assert.Single(result.Findings, f => f.Kind == FindingKind.MissingMapSetting).Resources).Name);
    }

    [Fact]
    public void Works_when_the_resources_folder_itself_is_picked_and_there_is_no_config()
    {
        using var server = new FakeServer().Resource("solo").File("solo/stream/a.ymap", "a");

        var result = MapDoctorScanner.Scan(server.Resources);

        Assert.Single(result.Resources);
        Assert.Contains(result.Findings, f => f.Kind == FindingKind.NoStartList);
    }
}

public class StartOrderTests
{
    private static int Order(ScanResult result, string name) => result.Resources.Single(r => r.Name == name).StartOrder!.Value;

    [Fact]
    public void A_category_starts_by_name_with_capitals_first_and_sub_categories_mixed_in()
    {
        // FXServer sorts everything in a category by name, capitals before lower case, ignoring sub-folders.
        using var server = new FakeServer()
            .Resource("[maps]/b_map").File("[maps]/b_map/stream/x.ymap", "b")
            .Resource("[maps]/[extra]/a_sub").File("[maps]/[extra]/a_sub/stream/x.ymap", "a sub, longer")
            .Resource("[maps]/Z_map").File("[maps]/Z_map/stream/x.ymap", "Z")
            .Config("ensure [maps]\n");

        var result = MapDoctorScanner.Scan(server.Root);

        Assert.True(Order(result, "Z_map") < Order(result, "a_sub"));
        Assert.True(Order(result, "a_sub") < Order(result, "b_map"));
        Assert.Equal("b_map", result.Findings.Single(f => f.Kind == FindingKind.ClashingFiles).Winner?.Name);
    }

    [Fact]
    public void Dependencies_start_first()
    {
        using var server = new FakeServer()
            .Resource("[maps]/aaa_house", "fx_version 'cerulean'\ngame 'gta5'\nthis_is_a_map 'yes'\ndependency 'zzz_estate'\n")
            .File("[maps]/aaa_house/stream/x.ymap", "house")
            .Resource("[maps]/zzz_estate").File("[maps]/zzz_estate/stream/x.ymap", "estate, longer")
            .Config("ensure [maps]\n");

        var result = MapDoctorScanner.Scan(server.Root);

        Assert.Equal("aaa_house", result.Findings.Single(f => f.Kind == FindingKind.ClashingFiles).Winner?.Name);
    }

    [Fact]
    public void A_resource_not_marked_for_gta5_never_starts_and_is_left_out_of_clashes()
    {
        using var server = new FakeServer()
            .Resource("live").File("live/stream/x.ymap", "live")
            .Resource("dead", "-- fx_version 'cerulean'\n-- game 'gta5'\n").File("dead/stream/x.ymap", "dead, longer")
            .Resource("old_style").File("old_style/stream/y.ymap", "old")
            .Config("ensure live\nensure dead\nensure old_style\n");
        File.Move(Path.Combine(server.Resources, "old_style", "fxmanifest.lua"), Path.Combine(server.Resources, "old_style", "__resource.lua"));
        File.WriteAllText(Path.Combine(server.Resources, "old_style", "__resource.lua"), "this_is_a_map 'yes'\n");

        var result = MapDoctorScanner.Scan(server.Root);

        Assert.False(result.Resources.Single(r => r.Name == "dead").IsStarted);
        Assert.True(result.Resources.Single(r => r.Name == "old_style").IsStarted);
        Assert.DoesNotContain(result.Findings, f => f.Kind == FindingKind.ClashingFiles);
        var refused = Assert.Single(result.Findings, f => f.Kind == FindingKind.WontStart);
        Assert.Equal("dead", Assert.Single(refused.Resources).Name);
        Assert.DoesNotContain(result.Findings, f => f.Kind == FindingKind.MapsSwitchedOff);
    }

    [Fact]
    public void A_dependency_another_resource_provides_is_not_missing()
    {
        using var server = new FakeServer()
            .Resource("oxmysql", "fx_version 'cerulean'\ngame 'gta5'\nprovide 'mysql-async'\n")
            .Resource("needs_sql", "fx_version 'cerulean'\ngame 'gta5'\nthis_is_a_map 'yes'\ndependencies { 'mysql-async', 'chat' }\n")
            .File("needs_sql/stream/a.ymap", "a")
            .Config("ensure oxmysql\nensure needs_sql\n");

        var result = MapDoctorScanner.Scan(server.Root);

        Assert.DoesNotContain(result.Findings, f => f.Kind is FindingKind.MissingDependency or FindingKind.WontStart);
        Assert.True(result.Resources.Single(r => r.Name == "needs_sql").IsStarted);
    }

    [Fact]
    public void A_map_that_needs_a_newer_game_version_wont_start()
    {
        using var server = new FakeServer()
            .Resource("new_dlc", "fx_version 'cerulean'\ngame 'gta5'\nthis_is_a_map 'yes'\ndependency '/gameBuild:3095'\n")
            .File("new_dlc/stream/a.ymap", "a")
            .Config("set sv_enforceGameBuild 2944\nensure new_dlc\n");

        var result = MapDoctorScanner.Scan(server.Root);

        var finding = Assert.Single(result.Findings, f => f.Kind == FindingKind.WontStart);
        Assert.Contains("3095", finding.WhatsWrong);
    }

    [Fact]
    public void Only_the_first_of_two_folders_with_the_same_name_runs()
    {
        using var server = new FakeServer()
            .Resource("[a]/estate").File("[a]/estate/stream/x.ymap", "first copy")
            .Resource("[b]/estate").File("[b]/estate/stream/x.ymap", "second copy, longer")
            .Config("ensure [a]\nensure [b]\n");

        var result = MapDoctorScanner.Scan(server.Root);

        Assert.DoesNotContain(result.Findings, f => f.Kind == FindingKind.ClashingFiles);
        var duplicate = Assert.Single(result.Findings, f => f.Kind == FindingKind.DuplicateResourceName);
        Assert.Contains("[a]", duplicate.WhatsWrong);
    }
}

public class MoreChecksTests
{
    [Fact]
    public void Scenario_files_ymt_can_clash()
    {
        using var server = new FakeServer()
            .Resource("a").File("a/stream/downtown.ymt", "a")
            .Resource("b").File("b/stream/downtown.ymt", "b, longer")
            .Config("ensure a\nensure b\n");

        var clash = Assert.Single(MapDoctorScanner.Scan(server.Root).Findings, f => f.Kind == FindingKind.ClashingFiles);
        Assert.Equal("downtown.ymt", clash.Files[0].FileName);
    }

    [Fact]
    public void Two_maps_replacing_the_same_scenario_list_are_reported()
    {
        const string manifest = "fx_version 'cerulean'\ngame 'gta5'\nthis_is_a_map 'yes'\ndata_file 'SCENARIO_POINTS_OVERRIDE_PSO_FILE' 'scenarios/sp_manifest.ymt'\n";
        using var server = new FakeServer()
            .Resource("a", manifest).File("a/scenarios/sp_manifest.ymt", "a")
            .Resource("b", manifest).File("b/scenarios/sp_manifest.ymt", "b")
            .Config("ensure a\nensure b\n");

        var finding = Assert.Single(MapDoctorScanner.Scan(server.Root).Findings, f => f.Kind == FindingKind.ScenarioOverrides);
        Assert.Equal("b", finding.Winner?.Name);
        Assert.Contains("sp_manifest.ymt", finding.WhatsWrong);
    }

    [Fact]
    public void Two_different_copies_inside_one_map_are_reported()
    {
        using var server = new FakeServer()
            .Resource("estate")
            .File("estate/stream/default/lr_cs4_06.ymap", "without car park")
            .File("estate/stream/car_park/lr_cs4_06.ymap", "with car park")
            .File("estate/stream/one/same.ydr", "same")
            .File("estate/stream/two/same.ydr", "same")
            .Config("ensure estate\n");

        var finding = Assert.Single(MapDoctorScanner.Scan(server.Root).Findings, f => f.Kind == FindingKind.SameFileTwice);
        Assert.Equal("estate has two different versions of lr_cs4_06.ymap", finding.Title);
        Assert.All(finding.Details, d => Assert.StartsWith("lr_cs4_06.ymap", d));
    }

    [Fact]
    public void A_clash_marked_fine_comes_back_if_a_new_file_starts_clashing()
    {
        using var server = new FakeServer()
            .Resource("a").File("a/stream/x.ymap", "a")
            .Resource("b").File("b/stream/x.ymap", "b, longer")
            .Config("ensure a\nensure b\n");
        var before = MapDoctorScanner.Scan(server.Root).Findings.Single(f => f.Kind == FindingKind.ClashingFiles).Key;

        server.File("a/stream/y.ybn", "a").File("b/stream/y.ybn", "b, longer");
        var after = MapDoctorScanner.Scan(server.Root).Findings.Single(f => f.Kind == FindingKind.ClashingFiles).Key;

        Assert.NotEqual(before, after);
    }
}

public class MergeTests
{
    private static readonly short[] Shop = [100, 200, 30, 16384, 8, 16, 20, 0];
    private static readonly short[] Bank = [140, 260, 30, 16384, 12, 12, 40, 0];
    private static readonly short[] Tower = [180, 300, 60, 16384, 20, 20, 90, 0];
    private static readonly short[] TowerSavedAgain = [180, 300, 60, 16383, 20, 20, 90, 0]; // same box, re-saved by another tool
    private static readonly short[] Car = [400, 100, 20, 16384, 6, 3, 4, 0];

    // Each map removed the occluders around its own building: estate removed Bank, garage removed Shop.
    private static FakeServer TwoMapsInOneArea() => new FakeServer()
        .Resource("[maps]/estate")
        .Bytes("[maps]/estate/stream/lr_sc1_occl_00.ymap", GameFiles.Occlusion(Shop, Tower, Car))
        .Resource("[maps]/garage")
        .Bytes("[maps]/garage/stream/lr_sc1_occl_00.ymap", GameFiles.Occlusion(Bank, TowerSavedAgain, Car))
        .Config("ensure [maps]\n");

    [Fact]
    public void Combining_keeps_only_what_every_map_kept()
    {
        using var server = TwoMapsInOneArea();
        var copies = MapDoctorScanner.Scan(server.Root).Findings.Single(f => f.Kind == FindingKind.ClashingFiles).Files[0].Copies;

        Assert.True(OcclusionMerge.CanMerge(copies));
        var merged = OcclusionMerge.Merge(copies[1].FullPath, [copies[0].FullPath]);

        // Shop and Bank were each removed by one map, so neither comes back. Tower counts as the same box.
        Assert.Equal(2, merged.KeptBoxes);
        Assert.Equal(3, merged.TotalBoxes);
        Assert.Equal(2, GameFiles.BoxCount(merged.Bytes));
    }

    [Fact]
    public void Maps_that_disagree_completely_get_an_empty_but_valid_file()
    {
        using var server = new FakeServer()
            .Resource("a").Bytes("a/stream/x_occl.ymap", GameFiles.Occlusion(Shop))
            .Resource("b").Bytes("b/stream/x_occl.ymap", GameFiles.Occlusion(Bank))
            .Config("ensure a\nensure b\n");
        var copies = MapDoctorScanner.Scan(server.Root).Findings.Single(f => f.Kind == FindingKind.ClashingFiles).Files[0].Copies;

        var merged = OcclusionMerge.Merge(copies[1].FullPath, [copies[0].FullPath]);

        Assert.Equal(0, merged.Kept);
        Assert.Equal(0, GameFiles.BoxCount(merged.Bytes));
    }

    [Fact]
    public void Files_with_buildings_in_them_are_never_combined()
    {
        using var server = new FakeServer()
            .Resource("a").Bytes("a/stream/x.ymap", GameFiles.Ymap("a_int", new(1, 2, 3)))
            .Resource("b").Bytes("b/stream/x.ymap", GameFiles.Ymap("b_int", new(1, 2, 3)))
            .Config("ensure a\nensure b\n");
        var copies = MapDoctorScanner.Scan(server.Root).Findings.Single(f => f.Kind == FindingKind.ClashingFiles).Files[0].Copies;

        Assert.False(OcclusionMerge.CanMerge(copies));
    }

    [Fact]
    public void Combining_puts_the_same_file_in_every_map_and_undo_puts_the_originals_back()
    {
        using var server = TwoMapsInOneArea();
        var result = MapDoctorScanner.Scan(server.Root);
        var copies = result.Findings.Single(f => f.Kind == FindingKind.ClashingFiles).Files[0].Copies;
        var originals = copies.Select(c => File.ReadAllBytes(c.FullPath)).ToList();
        var winner = copies.Single(c => c.Resource.Name == "garage");

        var plan = Resolver.Plan(result.ResourcesFolder, [], DateTime.Now, [new MergeRequest(winner, copies)]);
        var record = Resolver.Apply(plan, DateTime.Now);

        // Both maps now have the same, combined file, so whichever loads, both buildings show.
        var now = copies.Select(c => File.ReadAllBytes(c.FullPath)).ToList();
        Assert.Equal(now[0], now[1]);
        Assert.Equal(2, GameFiles.BoxCount(now[0]));
        Assert.All(record.Moves, m => Assert.True(File.Exists(m.To)));
        Assert.DoesNotContain(MapDoctorScanner.Scan(server.Root).Findings, f => f.Kind == FindingKind.ClashingFiles);

        Assert.Null(Resolver.WhyCantUndo(Assert.Single(Resolver.FindRecords(result.ResourcesFolder))));
        Resolver.Undo(Assert.Single(Resolver.FindRecords(result.ResourcesFolder)));

        Assert.Equal(originals, copies.Select(c => File.ReadAllBytes(c.FullPath)).ToList());
        Assert.False(Directory.Exists(Resolver.BackupRoot(result.ResourcesFolder)));
    }

    [Fact]
    public void Undo_wont_overwrite_a_combined_file_that_was_changed_since()
    {
        using var server = TwoMapsInOneArea();
        var result = MapDoctorScanner.Scan(server.Root);
        var copies = result.Findings.Single(f => f.Kind == FindingKind.ClashingFiles).Files[0].Copies;
        Resolver.Apply(Resolver.Plan(result.ResourcesFolder, [], DateTime.Now, [new MergeRequest(copies[1], copies)]), DateTime.Now);
        File.WriteAllText(copies[0].FullPath, "the map was updated");

        var saved = Assert.Single(Resolver.FindRecords(result.ResourcesFolder));
        Assert.NotNull(Resolver.WhyCantUndo(saved));
        Assert.Throws<FixException>(() => Resolver.Undo(saved));
        Assert.Equal("the map was updated", File.ReadAllText(copies[0].FullPath));
    }
}

public class WordingAndOddFoldersTests
{
    [Fact]
    public void Lists_read_naturally()
    {
        Assert.Equal("A and B maps", Plain.JoinAndShort(["A", "B"], noun: "map"));
        Assert.Equal("A, B, C and 2 other maps", Plain.JoinAndShort(["A", "B", "C", "D", "E"], noun: "map"));
        Assert.Equal("A, B, C and 2 others", Plain.JoinAndShort(["A", "B", "C", "D", "E"]));
        Assert.Equal("A, B, C, D, E and 2 more", Plain.JoinAndMore(["A", "B", "C", "D", "E", "F", "G"]));
        Assert.Equal("A, B, C, D, E and F", Plain.JoinAndMore(["A", "B", "C", "D", "E", "F"]));
    }

    [Fact]
    public void A_link_back_up_the_folder_tree_doesnt_go_round_forever()
    {
        using var server = new FakeServer().Resource("[maps]/real_map");
        var link = Path.Combine(server.Resources, "[maps]", "[loop]");
        try { Directory.CreateSymbolicLink(link, Path.Combine(server.Resources, "[maps]")); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; } // links need extra rights on some PCs

        var result = MapDoctorScanner.Scan(server.Root);

        Assert.Single(result.Resources, r => r.Name == "real_map");
    }

    [Fact]
    public void Picking_a_category_folder_checks_the_whole_server()
    {
        using var server = new FakeServer()
            .Resource("[maps]/map_a").File("[maps]/map_a/stream/shared.ymap", "a")
            .Resource("[other]/map_b").File("[other]/map_b/stream/shared.ymap", "b")
            .Config("ensure [maps]\nensure [other]\n");

        var result = MapDoctorScanner.Scan(Path.Combine(server.Resources, "[maps]"));

        Assert.NotNull(result.StartListFile);
        Assert.Contains(result.Findings, f => f.Kind == FindingKind.ClashingFiles);
    }
}

public class SameSpotTests
{
    private static readonly (Vector3, Vector3) PoliceRoom = (new(-10, -8, 0), new(10, 8, 6));

    private static FakeServer TwoStations(Vector3 second, float turn = 0) => new FakeServer()
        .Resource("[maps]/station_a")
        .Bytes("[maps]/station_a/stream/station_a.ytyp", GameFiles.Ytyp("station_a_int", PoliceRoom))
        .Bytes("[maps]/station_a/stream/station_a_milo.ymap", GameFiles.Ymap("station_a_int", new(440, -980, 30)))
        .Resource("[maps]/station_b")
        .Bytes("[maps]/station_b/stream/station_b.ytyp", GameFiles.Ytyp("station_b_int", PoliceRoom))
        .Bytes("[maps]/station_b/stream/station_b_milo.ymap", GameFiles.Ymap("station_b_int", second, turn))
        .Config("ensure [maps]\n");

    [Fact]
    public void Two_interiors_with_different_file_names_in_the_same_spot_are_found()
    {
        using var server = TwoStations(new(443, -982, 30), turn: 90);

        var result = MapDoctorScanner.Scan(server.Root);

        var finding = Assert.Single(result.Findings, f => f.Kind == FindingKind.SameSpot);
        Assert.Equal(Severity.Problem, finding.Severity);
        Assert.Equal(["station_a", "station_b"], finding.Resources.Select(r => r.Name));
        Assert.Contains("440", finding.WhatToDo[0]);
        Assert.DoesNotContain(result.Findings, f => f.Kind == FindingKind.ClashingFiles);
    }

    [Fact]
    public void Three_interiors_in_one_spot_are_one_finding()
    {
        using var server = TwoStations(new(441, -981, 30))
            .Resource("[maps]/station_c")
            .Bytes("[maps]/station_c/stream/station_c.ytyp", GameFiles.Ytyp("station_c_int", PoliceRoom))
            .Bytes("[maps]/station_c/stream/station_c_milo.ymap", GameFiles.Ymap("station_c_int", new(439, -979, 30)));

        var finding = Assert.Single(MapDoctorScanner.Scan(server.Root).Findings, f => f.Kind == FindingKind.SameSpot);

        Assert.Equal(["station_a", "station_b", "station_c"], finding.Resources.Select(r => r.Name));
        Assert.StartsWith("3 interiors", finding.Title);
        Assert.Equal("Around the station_a, station_b and station_c maps", finding.Where);
    }

    [Fact]
    public void Interiors_next_to_each_other_are_fine()
    {
        // Station B sits right next door (its rooms start where A's end), like an add-on built alongside.
        using var server = TwoStations(new(460.5f, -980, 30));

        Assert.DoesNotContain(MapDoctorScanner.Scan(server.Root).Findings, f => f.Kind == FindingKind.SameSpot);
    }

    [Fact]
    public void Interiors_in_maps_that_are_switched_off_dont_count()
    {
        using var server = TwoStations(new(440, -980, 30));
        server.Config("ensure station_a\n");

        Assert.DoesNotContain(MapDoctorScanner.Scan(server.Root).Findings, f => f.Kind == FindingKind.SameSpot);
    }

    [Fact]
    public void Broken_or_locked_files_are_skipped_without_failing_the_scan()
    {
        using var server = TwoStations(new(440, -980, 30))
            .File("[maps]/station_b/stream/broken.ymap", "RSC7 not really")
            .File("[maps]/station_b/stream/locked.ytyp", "FXAP encrypted");

        Assert.Single(MapDoctorScanner.Scan(server.Root).Findings, f => f.Kind == FindingKind.SameSpot);
    }
}

public class ResolverTests
{
    private static (FakeServer Server, ScanResult Result) TwoMapsClashing()
    {
        var server = new FakeServer()
            .Resource("[maps]/our_map").File("[maps]/our_map/stream/area_occl_00.ymap", "our version")
            .Resource("[maps]/their_map").File("[maps]/their_map/stream/sub/area_occl_00.ymap", "their version, longer")
            .Config("ensure our_map\nensure their_map\n");
        return (server, MapDoctorScanner.Scan(server.Root));
    }

    [Fact]
    public void Keeping_our_version_moves_theirs_to_a_backup_and_the_clash_goes_away()
    {
        var (server, result) = TwoMapsClashing();
        using var _ = server;
        var clash = Assert.Single(result.Findings, f => f.Kind == FindingKind.ClashingFiles);
        Assert.Equal("their_map", clash.Winner?.Name);

        var theirs = clash.Files[0].Copies.Single(c => c.Resource.Name == "their_map");
        var plan = Resolver.Plan(result.ResourcesFolder, [theirs], new DateTime(2026, 11, 16, 21, 45, 0));
        var record = Resolver.Apply(plan, DateTime.Now);

        Assert.False(File.Exists(theirs.FullPath));
        var backup = Assert.Single(record.Moves).To;
        Assert.True(File.Exists(backup));
        Assert.StartsWith(Path.Combine(server.Root, "mapdoctor-backups"), backup);
        Assert.True(File.Exists(Path.Combine(record.BackupFolder, FixRecord.FileName)));

        var after = MapDoctorScanner.Scan(server.Root);
        Assert.DoesNotContain(after.Findings, f => f.Kind == FindingKind.ClashingFiles);
    }

    [Fact]
    public void Undo_puts_the_file_back_exactly_where_it_was()
    {
        var (server, result) = TwoMapsClashing();
        using var _ = server;
        var theirs = result.Findings.Single(f => f.Kind == FindingKind.ClashingFiles).Files[0].Copies.Single(c => c.Resource.Name == "their_map");
        var record = Resolver.Apply(Resolver.Plan(result.ResourcesFolder, [theirs], DateTime.Now), DateTime.Now);

        Resolver.Undo(record);

        Assert.Equal("their version, longer", File.ReadAllText(theirs.FullPath));
        Assert.False(Directory.Exists(record.BackupFolder));
        Assert.Contains(MapDoctorScanner.Scan(server.Root).Findings, f => f.Kind == FindingKind.ClashingFiles);
    }

    [Fact]
    public void A_fix_made_earlier_can_be_found_and_undone_later()
    {
        var (server, result) = TwoMapsClashing();
        using var _ = server;
        var theirs = result.Findings.Single(f => f.Kind == FindingKind.ClashingFiles).Files[0].Copies.Single(c => c.Resource.Name == "their_map");
        var at = new DateTime(2026, 11, 16, 21, 45, 0);
        Resolver.Apply(Resolver.Plan(result.ResourcesFolder, [theirs], at), at);

        // As if the app was closed and opened again.
        var saved = Assert.Single(Resolver.FindRecords(result.ResourcesFolder));
        Assert.Equal(at, saved.MadeAt);
        Assert.Null(Resolver.WhyCantUndo(saved));

        Resolver.Undo(saved);

        Assert.True(File.Exists(theirs.FullPath));
        Assert.Empty(Resolver.FindRecords(result.ResourcesFolder));
        Assert.False(Directory.Exists(Resolver.BackupRoot(result.ResourcesFolder)));
    }

    [Fact]
    public void A_saved_fix_still_undoes_after_the_server_folder_is_moved()
    {
        var (server, result) = TwoMapsClashing();
        using var _ = server;
        var theirs = result.Findings.Single(f => f.Kind == FindingKind.ClashingFiles).Files[0].Copies.Single(c => c.Resource.Name == "their_map");
        Resolver.Apply(Resolver.Plan(result.ResourcesFolder, [theirs], DateTime.Now), DateTime.Now);

        var moved = server.Root + "-moved";
        Directory.Move(server.Root, moved);
        try
        {
            var resources = Path.Combine(moved, "resources");
            var saved = Assert.Single(Resolver.FindRecords(resources));
            Resolver.Undo(saved);
            Assert.True(File.Exists(Path.Combine(resources, Path.GetRelativePath(result.ResourcesFolder, theirs.FullPath))));
        }
        finally
        {
            Directory.Move(moved, server.Root);
        }
    }

    [Fact]
    public void A_saved_fix_that_points_outside_the_server_is_ignored()
    {
        using var server = new FakeServer().Resource("a");
        var folder = Path.Combine(Resolver.BackupRoot(server.Resources), "2026-11-16_214500");
        Directory.CreateDirectory(folder);
        var outside = Path.Combine(Path.GetTempPath(), "somewhere-else.txt");
        var record = new FixRecord(DateTime.Now, folder, [new FileMove("a", "x.ymap", outside, Path.Combine(folder, "..", "..", "..", "x.ymap"))]);
        File.WriteAllText(Path.Combine(folder, FixRecord.FileName), JsonSerializer.Serialize(record));

        Assert.Empty(Resolver.FindRecords(server.Resources));
    }

    [Fact]
    public void A_fix_cant_be_undone_once_the_map_has_the_file_again()
    {
        var (server, result) = TwoMapsClashing();
        using var _ = server;
        var theirs = result.Findings.Single(f => f.Kind == FindingKind.ClashingFiles).Files[0].Copies.Single(c => c.Resource.Name == "their_map");
        Resolver.Apply(Resolver.Plan(result.ResourcesFolder, [theirs], DateTime.Now), DateTime.Now);
        File.WriteAllText(theirs.FullPath, "an updated version of their map");

        var saved = Assert.Single(Resolver.FindRecords(result.ResourcesFolder));
        Assert.Contains("again", Resolver.WhyCantUndo(saved));
        Assert.Throws<FixException>(() => Resolver.Undo(saved));
        Assert.Equal("an updated version of their map", File.ReadAllText(theirs.FullPath));
    }

    [Fact]
    public void Backup_folders_have_no_spaces_and_two_fixes_in_one_second_get_their_own()
    {
        var (server, result) = TwoMapsClashing();
        using var _ = server;
        var copies = result.Findings.Single(f => f.Kind == FindingKind.ClashingFiles).Files[0].Copies;
        var at = new DateTime(2026, 11, 16, 22, 27, 46);

        var first = Resolver.Apply(Resolver.Plan(result.ResourcesFolder, [copies[0]], at), at);
        var second = Resolver.Plan(result.ResourcesFolder, [copies[1]], at);

        Assert.Equal("2026-11-16_222746", Path.GetFileName(first.BackupFolder));
        Assert.Equal("2026-11-16_222746_2", Path.GetFileName(second.BackupFolder));
        Assert.DoesNotContain(' ', Path.GetFileName(second.BackupFolder));
    }

    [Fact]
    public void A_failed_fix_changes_nothing()
    {
        var (server, result) = TwoMapsClashing();
        using var _ = server;
        var copies = result.Findings.Single(f => f.Kind == FindingKind.ClashingFiles).Files[0].Copies;
        var plan = Resolver.Plan(result.ResourcesFolder, copies, DateTime.Now);
        File.Delete(copies[1].FullPath); // the second move will fail

        Assert.Throws<FixException>(() => Resolver.Apply(plan, DateTime.Now));
        Assert.True(File.Exists(copies[0].FullPath));
        Assert.False(Directory.Exists(Resolver.BackupRoot(result.ResourcesFolder)));
    }

    [Fact]
    public void Every_backup_explains_how_to_put_the_files_back_by_hand()
    {
        var (server, result) = TwoMapsClashing();
        using var _ = server;
        var theirs = result.Findings.Single(f => f.Kind == FindingKind.ClashingFiles).Files[0].Copies.Single(c => c.Resource.Name == "their_map");
        var record = Resolver.Apply(Resolver.Plan(result.ResourcesFolder, [theirs], DateTime.Now), DateTime.Now);

        var readMe = File.ReadAllText(Path.Combine(record.BackupFolder, FixRecord.ReadMeName));
        Assert.Contains("Undo fixes", readMe);
        Assert.Contains("area_occl_00.ymap", readMe);

        Resolver.Undo(record);
        Assert.False(Directory.Exists(record.BackupFolder));
    }

    [Fact]
    public void Undo_all_puts_every_fix_back()
    {
        var (server, result) = TwoMapsClashing();
        using var _ = server;
        var copies = result.Findings.Single(f => f.Kind == FindingKind.ClashingFiles).Files[0].Copies;
        Resolver.Apply(Resolver.Plan(result.ResourcesFolder, [copies[0]], new DateTime(2026, 11, 16, 10, 0, 0)), new DateTime(2026, 11, 16, 10, 0, 0));
        Resolver.Apply(Resolver.Plan(result.ResourcesFolder, [copies[1]], new DateTime(2026, 11, 16, 11, 0, 0)), new DateTime(2026, 11, 16, 11, 0, 0));

        var (undone, problems) = Resolver.UndoAll(result.ResourcesFolder);

        Assert.Equal(2, undone);
        Assert.Empty(problems);
        Assert.All(copies, c => Assert.True(File.Exists(c.FullPath)));
        Assert.False(Directory.Exists(Resolver.BackupRoot(result.ResourcesFolder)));
    }

    [Fact]
    public void The_how_to_put_back_note_is_there_from_the_start()
    {
        var (server, result) = TwoMapsClashing();
        using var _ = server;
        var copies = result.Findings.Single(f => f.Kind == FindingKind.ClashingFiles).Files[0].Copies;
        // A file that's gone by the time we move it makes the fix stop part way and put everything back.
        var plan = Resolver.Plan(result.ResourcesFolder, copies, DateTime.Now);
        File.Delete(copies[1].FullPath);

        Assert.Throws<FixException>(() => Resolver.Apply(plan, DateTime.Now));
        Assert.True(File.Exists(copies[0].FullPath));

        // And a fix that works leaves the note behind.
        var record = Resolver.Apply(Resolver.Plan(result.ResourcesFolder, [copies[0]], DateTime.Now), DateTime.Now);
        Assert.True(File.Exists(Path.Combine(record.BackupFolder, FixRecord.ReadMeName)));
    }

    [Fact]
    public void A_fix_that_cant_be_undone_can_be_forgotten_without_losing_the_backup()
    {
        var (server, result) = TwoMapsClashing();
        using var _ = server;
        var copy = result.Findings.Single(f => f.Kind == FindingKind.ClashingFiles).Files[0].Copies[0];
        var record = Resolver.Apply(Resolver.Plan(result.ResourcesFolder, [copy], DateTime.Now), DateTime.Now);
        File.WriteAllText(copy.FullPath, "the map was updated and has the file again");
        Assert.NotNull(Resolver.WhyCantUndo(Assert.Single(Resolver.FindRecords(result.ResourcesFolder))));

        Resolver.Forget(record);

        Assert.Empty(Resolver.FindRecords(result.ResourcesFolder));
        Assert.True(File.Exists(record.Moves[0].To));
    }

    [Fact]
    public void A_fix_that_stopped_half_way_can_still_be_undone()
    {
        var (server, result) = TwoMapsClashing();
        using var _ = server;
        var copies = result.Findings.Single(f => f.Kind == FindingKind.ClashingFiles).Files[0].Copies;
        var plan = Resolver.Plan(result.ResourcesFolder, copies, DateTime.Now);

        // As if the PC switched off after the first file moved: the list was saved first, marked unfinished.
        Directory.CreateDirectory(Path.GetDirectoryName(plan.Moves[0].To)!);
        File.Move(plan.Moves[0].From, plan.Moves[0].To);
        File.WriteAllText(Path.Combine(plan.BackupFolder, FixRecord.FileName),
            JsonSerializer.Serialize(new FixRecord(DateTime.Now, plan.BackupFolder, plan.Moves, Finished: false)));

        var saved = Assert.Single(Resolver.FindRecords(result.ResourcesFolder));
        Assert.Single(saved.Moves);
        Assert.Null(Resolver.WhyCantUndo(saved));
        Resolver.Undo(saved);

        Assert.True(File.Exists(copies[0].FullPath));
        Assert.True(File.Exists(copies[1].FullPath));
        Assert.False(Directory.Exists(Resolver.BackupRoot(result.ResourcesFolder)));
    }
}

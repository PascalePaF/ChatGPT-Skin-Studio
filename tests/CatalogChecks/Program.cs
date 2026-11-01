using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using ChatGptSkinStudio;

internal static class Program
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); Console.WriteLine("PASS " + message); }
    private static void Main()
    {
        var p = new Preferences(); var forest = Catalog.Create(p, "森林"); var night = Catalog.Create(p, "夜景");
        Catalog.Move(p, new[] { "first", "second" }, forest.Id); Catalog.Move(p, new[] { "second" }, night.Id);
        Check(Catalog.Matches(p, "first", forest.Id) && Catalog.Matches(p, "second", night.Id) && !Catalog.Matches(p, "first", ""), "batch move, group filtering and ungrouped filtering");
        Catalog.Rename(p, forest.Id, "自然"); Check(p.Groups.Single(g => g.Id == forest.Id).Name == "自然" && Catalog.GroupOf(p, "first") == forest.Id, "rename keeps stable membership");
        bool duplicate = false; try { Catalog.Create(p, "自然"); } catch (InvalidOperationException) { duplicate = true; } Check(duplicate, "duplicate group names rejected");
        Catalog.Hide(p, new[] { "first", "first", "builtin-aurora" }); Check(p.HiddenIds.Count == 2, "removal is persistent and idempotent");
        Catalog.DeleteGroup(p, forest.Id); Check(Catalog.GroupOf(p, "first") == "" && Catalog.GroupOf(p, "second") == night.Id && p.HiddenIds.Contains("first"), "deleting a group returns members to ungrouped without deleting wallpapers");
        var roundtrip = JsonSerializer.Deserialize<Preferences>(JsonSerializer.Serialize(p))!; roundtrip.Normalize();
        Check(Catalog.GroupOf(roundtrip, "second") == night.Id && roundtrip.HiddenIds.Count == 2 && roundtrip.AutoScan, "groups, exclusions and automatic scan survive settings roundtrip");
        string workspace = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        string root = Path.Combine(workspace, "build", "validation", "v1.1.4", "scan-fixture-" + Guid.NewGuid().ToString("N")[..8]); Directory.CreateDirectory(root);
        string loose = Path.Combine(root, "loose.png"); File.WriteAllBytes(loose, new byte[] { 0 });
        string project = Path.Combine(root, "project"); Directory.CreateDirectory(project);
        File.WriteAllBytes(Path.Combine(project, "art.png"), new byte[] { 0 });
        File.WriteAllText(Path.Combine(project, "project.json"), "{\"title\":\"fixture\",\"type\":\"image\",\"file\":\"art.png\"}");
        var pref = new Preferences { Directories = new() { root } }; var first = Library.Scan(pref, CancellationToken.None);
        string looseId = "directory-" + Library.Hash(loose);
        Check(first.Items.Count(i => i.Id == looseId) == 1 && first.Items.All(i => i.Id != "directory-" + Library.Hash(Path.Combine(project, "art.png"))), "manual folders discover loose media without duplicating project internals");
        File.WriteAllBytes(Path.Combine(root, "added.mp4"), new byte[] { 0 }); Catalog.Hide(pref, new[] { looseId, "builtin-aurora" });
        var next = Library.Scan(pref, CancellationToken.None);
        Check(next.Items.All(i => i.Id != looseId && i.Id != "builtin-aurora") && next.Items.Any(i => i.Id == "directory-" + Library.Hash(Path.Combine(root, "added.mp4"))), "rescan adds newly downloaded media and does not re-add removed wallpapers");
        Check(File.Exists(loose) && File.Exists(Path.Combine(project, "art.png")), "library removal never deletes external originals");
        pref.HiddenIds.Clear(); Check(Library.Scan(pref, CancellationToken.None).Items.Any(i => i.Id == looseId), "restore exclusions rediscover original source");
        Console.WriteLine("All catalog checks passed. User settings and media were not modified.");
    }
}

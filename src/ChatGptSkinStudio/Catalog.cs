using System;
using System.Collections.Generic;
using System.Linq;

namespace ChatGptSkinStudio;

internal sealed record WallpaperGroup(string Id, string Name)
{
    public override string ToString() => Name;
}

internal static class Catalog
{
    internal static void Normalize(Preferences settings)
    {
        settings.Groups = (settings.Groups ?? new()).Where(g => g is not null && !string.IsNullOrWhiteSpace(g.Id) && !string.IsNullOrWhiteSpace(g.Name))
            .Select(g => g with { Name = g.Name.Trim()[..Math.Min(60, g.Name.Trim().Length)] }).DistinctBy(g => g.Id).Take(200).ToList();
        var ids = settings.Groups.Select(g => g.Id).ToHashSet(StringComparer.Ordinal);
        settings.GroupAssignments = (settings.GroupAssignments ?? new()).Where(p => !string.IsNullOrEmpty(p.Key) && ids.Contains(p.Value))
            .Take(20000).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        settings.HiddenIds = (settings.HiddenIds ?? new()).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.Ordinal).Take(20000).ToList();
    }
    internal static string GroupOf(Preferences settings, string asset) => settings.GroupAssignments.TryGetValue(asset, out string? group) ? group : "";
    internal static bool Matches(Preferences settings, string asset, string filter) => filter == "*" || GroupOf(settings, asset) == filter;
    internal static WallpaperGroup Create(Preferences settings, string name)
    {
        name = CheckName(settings, name, "");
        if (settings.Groups.Count >= 200) throw new InvalidOperationException("最多可创建 200 个分组。");
        var group = new WallpaperGroup(Guid.NewGuid().ToString("N"), name); settings.Groups.Add(group); return group;
    }
    internal static void Rename(Preferences settings, string id, string name)
    {
        int at = settings.Groups.FindIndex(g => g.Id == id); if (at < 0) throw new InvalidOperationException("分组已不存在。");
        settings.Groups[at] = settings.Groups[at] with { Name = CheckName(settings, name, id) };
    }
    private static string CheckName(Preferences settings, string name, string except)
    {
        name = name.Trim();
        if (name.Length == 0 || name.Length > 60) throw new InvalidOperationException("分组名称请输入 1–60 个字符。");
        if (settings.Groups.Any(g => g.Id != except && string.Equals(g.Name, name, StringComparison.CurrentCultureIgnoreCase))) throw new InvalidOperationException("已有同名分组，请换一个名称。");
        return name;
    }
    internal static void DeleteGroup(Preferences settings, string id)
    {
        settings.Groups.RemoveAll(g => g.Id == id);
        foreach (string asset in settings.GroupAssignments.Where(p => p.Value == id).Select(p => p.Key).ToArray()) settings.GroupAssignments.Remove(asset);
    }
    internal static void Move(Preferences settings, IEnumerable<string> assets, string group)
    {
        if (group.Length > 0 && !settings.Groups.Any(g => g.Id == group)) throw new InvalidOperationException("目标分组已不存在。");
        foreach (string asset in assets.Distinct(StringComparer.Ordinal))
            if (group.Length == 0) settings.GroupAssignments.Remove(asset); else settings.GroupAssignments[asset] = group;
    }
    internal static void Hide(Preferences settings, IEnumerable<string> assets)
    {
        foreach (string id in assets.Distinct(StringComparer.Ordinal))
            if (!settings.HiddenIds.Contains(id, StringComparer.Ordinal)) settings.HiddenIds.Add(id);
    }
}

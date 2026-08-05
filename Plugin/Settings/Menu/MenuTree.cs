using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ExileCore.Shared.Nodes;

namespace BeastsV3.Plugin.Settings.Menu;

// Reflection model of the settings tree, built from the [Menu] attributes and reused by
// the renderer. Attribute values are read as constructor arguments so this compiles
// against ExileCore builds that rename the attribute's members.
public sealed class MenuCategory
{
    public string Name { get; init; }
    public List<MenuSection> Sections { get; } = new();
}

public sealed class MenuSection
{
    public string Title { get; init; }
    public string Tooltip { get; init; }
    public MenuGroup Root { get; init; }
}

public sealed class MenuGroup
{
    public string Label { get; init; }
    public string Tooltip { get; init; }
    public string Id { get; init; }
    public bool CollapsedByDefault { get; init; }
    public List<MenuEntry> Entries { get; } = new();

    public bool IsEmpty => Entries.Count == 0;
}

// One row in a group: a leaf node or a nested group. Kept as one type so declaration
// order is preserved.
public sealed class MenuEntry
{
    public MenuItem Item { get; init; }
    public MenuGroup Group { get; init; }
}

public sealed class MenuItem
{
    public string Label { get; init; }
    public string Tooltip { get; init; }
    public string Id { get; init; }
    public object Node { get; init; }

    // The node's owner and property, needed by the hotkey editor to replace the node.
    public object Owner { get; init; }
    public PropertyInfo Property { get; init; }

    // Slider bounds, resolved once at build time.
    public float Min { get; init; }
    public float Max { get; init; }
    public bool HasRange { get; init; }

    // Category and section path, used by the search results list.
    public string Breadcrumb { get; init; }
    public string SearchText { get; init; }
}

public static class MenuTree
{
    public const string HomeCategory = "Home";

    public static List<MenuCategory> Build(BeastsSettings settings)
    {
        var categories = new List<MenuCategory>();
        if (settings == null) return categories;

        foreach (var property in OrderedProperties(typeof(BeastsSettings)))
        {
            var (label, tooltip) = ReadMenu(property);
            if (label == null) continue;

            // The master toggle is drawn in the menu header instead.
            if (property.Name == nameof(BeastsSettings.Enable)) continue;

            var value = TryGetValue(property, settings);
            if (value == null) continue;

            var (categoryName, sectionTitle) = SplitLabel(label);
            var id = property.Name;

            MenuGroup root;
            if (IsNode(value))
            {
                // A node directly off the root still gets its own section.
                root = new MenuGroup { Label = sectionTitle, Tooltip = tooltip, Id = id };
                root.Entries.Add(new MenuEntry
                {
                    Item = BuildItem(value, sectionTitle, tooltip, id, categoryName, sectionTitle,
                        settings, property),
                });
            }
            else
            {
                root = BuildGroup(value, sectionTitle, tooltip, id, categoryName, sectionTitle);
                if (root.IsEmpty) continue;
            }

            var category = categories.FirstOrDefault(c =>
                string.Equals(c.Name, categoryName, StringComparison.OrdinalIgnoreCase));
            if (category == null)
            {
                category = new MenuCategory { Name = categoryName };
                categories.Add(category);
            }

            category.Sections.Add(new MenuSection
            {
                Title = sectionTitle,
                Tooltip = tooltip,
                Root = root,
            });
        }

        return categories;
    }

    // Flattens every leaf in the tree, for the search box.
    public static List<MenuItem> Flatten(IEnumerable<MenuCategory> categories)
    {
        var items = new List<MenuItem>();
        foreach (var category in categories ?? Enumerable.Empty<MenuCategory>())
        foreach (var section in category.Sections)
            Collect(section.Root, items);
        return items;
    }

    private static void Collect(MenuGroup group, List<MenuItem> into)
    {
        if (group == null) return;
        foreach (var entry in group.Entries)
        {
            if (entry.Item != null) into.Add(entry.Item);
            if (entry.Group != null) Collect(entry.Group, into);
        }
    }

    // ---- private -------------------------------------------------------

    // Builds one group by reflecting over an instance's [Menu] properties.
    private static MenuGroup BuildGroup(object instance, string label, string tooltip, string idPrefix,
        string categoryName, string sectionTitle)
    {
        var group = new MenuGroup
        {
            Label = label,
            Tooltip = tooltip,
            Id = idPrefix,
            CollapsedByDefault = IsCollapsedByDefault(instance.GetType()),
        };

        foreach (var property in OrderedProperties(instance.GetType()))
        {
            var (childLabel, childTooltip) = ReadMenu(property);
            if (childLabel == null) continue;

            var value = TryGetValue(property, instance);
            if (value == null) continue;

            var id = $"{idPrefix}.{property.Name}";

            if (IsNode(value))
            {
                group.Entries.Add(new MenuEntry
                {
                    Item = BuildItem(value, childLabel, childTooltip, id, categoryName, sectionTitle,
                        instance, property),
                });
                continue;
            }

            // Anything else with a [Menu] is a nested settings class; primitives are skipped.
            if (!value.GetType().IsClass || value is string) continue;

            var nested = BuildGroup(value, childLabel, childTooltip, id, categoryName, sectionTitle);
            if (!nested.IsEmpty) group.Entries.Add(new MenuEntry { Group = nested });
        }

        return group;
    }

    private static MenuItem BuildItem(object node, string label, string tooltip, string id,
        string categoryName, string sectionTitle, object owner, PropertyInfo property)
    {
        var hasRange = TryGetRange(node, out var min, out var max);
        var breadcrumb = string.Equals(categoryName, sectionTitle, StringComparison.OrdinalIgnoreCase)
            ? categoryName
            : $"{categoryName}  >  {sectionTitle}";

        return new MenuItem
        {
            Label = label,
            Tooltip = tooltip,
            Id = id,
            Node = node,
            Owner = owner,
            Property = property,
            Min = min,
            Max = max,
            HasRange = hasRange,
            Breadcrumb = breadcrumb,
            SearchText = $"{label} {tooltip} {breadcrumb}".ToLowerInvariant(),
        };
    }

    private static bool IsNode(object value) => value
        is ToggleNode or ColorNode or TextNode or ButtonNode or CustomNode or HotkeyNodeV2
        or RangeNode<int> or RangeNode<float>;

    // Properties in declaration order, via metadata token.
    private static IEnumerable<PropertyInfo> OrderedProperties(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
            .OrderBy(p => p.MetadataToken);

    private static object TryGetValue(PropertyInfo property, object instance)
    {
        try { return property.GetValue(instance); }
        catch { return null; }
    }

    private static (string category, string section) SplitLabel(string label)
    {
        var separator = label.IndexOf(':');
        if (separator <= 0 || separator >= label.Length - 1) return (label.Trim(), label.Trim());
        return (label[..separator].Trim(), label[(separator + 1)..].Trim());
    }

    private static (string label, string tooltip) ReadMenu(MemberInfo member)
    {
        foreach (var data in member.GetCustomAttributesData())
        {
            if (data.AttributeType.Name != "MenuAttribute") continue;

            var args = data.ConstructorArguments;
            var label = args.Count > 0 ? args[0].Value as string : null;
            // Some [Menu] overloads take an index as the second argument, not a tooltip.
            var tooltip = args.Count > 1 ? args[1].Value as string : null;
            return (label, tooltip);
        }

        return (null, null);
    }

    private static bool IsCollapsedByDefault(Type type)
    {
        foreach (var data in type.GetCustomAttributesData())
        {
            if (data.AttributeType.Name != "SubmenuAttribute") continue;
            foreach (var named in data.NamedArguments)
            {
                if (named.MemberName == "CollapsedByDefault" && named.TypedValue.Value is bool collapsed)
                    return collapsed;
            }
        }

        return false;
    }

    private static readonly Dictionary<Type, (PropertyInfo min, PropertyInfo max)> RangeCache = new();

    // Reads a RangeNode's bounds by member name, cached per type.
    private static bool TryGetRange(object node, out float min, out float max)
    {
        min = 0f;
        max = 0f;
        if (node is not (RangeNode<int> or RangeNode<float>)) return false;

        var type = node.GetType();
        if (!RangeCache.TryGetValue(type, out var accessors))
        {
            accessors = (FindProperty(type, "Min", "Minimum", "MinValue"),
                         FindProperty(type, "Max", "Maximum", "MaxValue"));
            RangeCache[type] = accessors;
        }

        if (accessors.min == null || accessors.max == null) return false;

        try
        {
            min = Convert.ToSingle(accessors.min.GetValue(node));
            max = Convert.ToSingle(accessors.max.GetValue(node));
        }
        catch
        {
            return false;
        }

        return max > min;
    }

    private static PropertyInfo FindProperty(Type type, params string[] names)
    {
        foreach (var name in names)
        {
            var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (property != null && property.CanRead) return property;
        }

        return null;
    }
}

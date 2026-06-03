using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text.Json;

namespace HttpSpy.App.ViewModels;

/// <summary>
/// A node in the collapsible JSON tree shown in the response inspector.
/// Built recursively from a <see cref="JsonElement"/>.
/// </summary>
public sealed class JsonTreeNode
{
    public string Name { get; init; } = "";
    public string Value { get; init; } = "";
    public string TypeGlyph { get; init; } = "";
    public ObservableCollection<JsonTreeNode> Children { get; } = new();
    public bool HasChildren => Children.Count > 0;

    /// <summary>Single-line label combining the key and (for leaves) its value.</summary>
    public string Display => string.IsNullOrEmpty(Value) ? Name : $"{Name}: {Value}";

    public static IEnumerable<JsonTreeNode> Parse(string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch { return System.Array.Empty<JsonTreeNode>(); }

        using (doc)
        {
            var root = Build("root", doc.RootElement);
            // Surface the root's children directly so the tree isn't doubly nested for objects/arrays.
            return root.HasChildren ? root.Children : new[] { root };
        }
    }

    private static JsonTreeNode Build(string name, JsonElement el)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                var obj = new JsonTreeNode { Name = name, TypeGlyph = "{}", Value = "" };
                foreach (var p in el.EnumerateObject())
                    obj.Children.Add(Build(p.Name, p.Value));
                return obj;
            case JsonValueKind.Array:
                var arr = new JsonTreeNode { Name = $"{name} [{el.GetArrayLength()}]", TypeGlyph = "[]", Value = "" };
                int i = 0;
                foreach (var item in el.EnumerateArray())
                    arr.Children.Add(Build($"[{i++}]", item));
                return arr;
            case JsonValueKind.String:
                return new JsonTreeNode { Name = name, Value = $"\"{el.GetString()}\"", TypeGlyph = "\"\"" };
            case JsonValueKind.Number:
                return new JsonTreeNode { Name = name, Value = el.GetRawText(), TypeGlyph = "#" };
            case JsonValueKind.True:
            case JsonValueKind.False:
                return new JsonTreeNode { Name = name, Value = el.GetRawText(), TypeGlyph = "b" };
            case JsonValueKind.Null:
                return new JsonTreeNode { Name = name, Value = "null", TypeGlyph = "∅" };
            default:
                return new JsonTreeNode { Name = name, Value = el.GetRawText(), TypeGlyph = "" };
        }
    }
}

/// <summary>A single coloured segment of the request timing waterfall.</summary>
public sealed class TimingBar
{
    public string Label { get; init; } = "";
    public double Width { get; init; }
    public string Color { get; init; } = "#4FC3F7";
    public string ValueText { get; init; } = "";
}

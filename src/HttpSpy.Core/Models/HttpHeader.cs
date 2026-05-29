namespace HttpSpy.Core.Models;

/// <summary>A single HTTP header name/value pair. Headers may legally repeat.</summary>
public sealed class HttpHeader
{
    public HttpHeader(string name, string value)
    {
        Name = name;
        Value = value;
    }

    public string Name { get; set; }
    public string Value { get; set; }

    public override string ToString() => $"{Name}: {Value}";
}

/// <summary>
/// An ordered, case-insensitive multi-map of HTTP headers that preserves
/// insertion order and duplicate entries (important for fidelity when replaying).
/// </summary>
public sealed class HeaderCollection : IEnumerable<HttpHeader>
{
    private readonly List<HttpHeader> _items = new();

    public int Count => _items.Count;

    public IReadOnlyList<HttpHeader> Items => _items;

    public void Add(string name, string value) => _items.Add(new HttpHeader(name, value));

    public void Add(HttpHeader header) => _items.Add(header);

    public bool Contains(string name) =>
        _items.Any(h => string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase));

    public string? Get(string name) =>
        _items.FirstOrDefault(h => string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase))?.Value;

    public IEnumerable<string> GetAll(string name) =>
        _items.Where(h => string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase)).Select(h => h.Value);

    public void Set(string name, string value)
    {
        Remove(name);
        Add(name, value);
    }

    public int Remove(string name)
    {
        int removed = _items.RemoveAll(h => string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase));
        return removed;
    }

    public void Clear() => _items.Clear();

    /// <summary>Convenience accessor for the first value of a header.</summary>
    public string? this[string name]
    {
        get => Get(name);
        set
        {
            if (value is null) Remove(name);
            else Set(name, value);
        }
    }

    public HeaderCollection Clone()
    {
        var copy = new HeaderCollection();
        foreach (var h in _items) copy.Add(h.Name, h.Value);
        return copy;
    }

    public IEnumerator<HttpHeader> GetEnumerator() => _items.GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

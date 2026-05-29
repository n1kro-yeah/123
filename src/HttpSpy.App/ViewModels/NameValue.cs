namespace HttpSpy.App.ViewModels;

/// <summary>Simple name/value pair for grids (headers, cookies, query params, form fields).</summary>
public sealed class NameValue
{
    public NameValue(string name, string value)
    {
        Name = name;
        Value = value;
    }

    public string Name { get; set; }
    public string Value { get; set; }
}

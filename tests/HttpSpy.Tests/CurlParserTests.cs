using System.Linq;
using HttpSpy.Core.Util;
using Xunit;

namespace HttpSpy.Tests;

public class CurlParserTests
{
    [Fact]
    public void Parses_simple_get()
    {
        var r = CurlParser.Parse("curl https://example.com/api");
        Assert.Equal("GET", r.Method);
        Assert.Equal("https://example.com/api", r.Url);
        Assert.Empty(r.Body);
    }

    [Fact]
    public void Parses_headers_and_explicit_method()
    {
        var r = CurlParser.Parse("curl -X DELETE -H 'Accept: application/json' -H \"X-Token: abc\" https://api.test/v1/item/3");
        Assert.Equal("DELETE", r.Method);
        Assert.Equal("https://api.test/v1/item/3", r.Url);
        Assert.Contains(r.Headers, h => h.Name == "Accept" && h.Value == "application/json");
        Assert.Contains(r.Headers, h => h.Name == "X-Token" && h.Value == "abc");
    }

    [Fact]
    public void Data_flag_implies_post_and_form_content_type()
    {
        var r = CurlParser.Parse("curl -d 'a=1&b=2' https://example.com/submit");
        Assert.Equal("POST", r.Method);
        Assert.Equal("a=1&b=2", r.Body);
        Assert.Contains(r.Headers, h => h.Name == "Content-Type" && h.Value == "application/x-www-form-urlencoded");
    }

    [Fact]
    public void Json_flag_sets_json_content_type()
    {
        var r = CurlParser.Parse("curl --json '{\"x\":1}' https://example.com/j");
        Assert.Equal("POST", r.Method);
        Assert.Equal("{\"x\":1}", r.Body);
        Assert.Contains(r.Headers, h => h.Name == "Content-Type" && h.Value == "application/json");
    }

    [Fact]
    public void Explicit_content_type_is_not_overridden()
    {
        var r = CurlParser.Parse("curl -H 'Content-Type: text/xml' -d '<a/>' https://e.com");
        Assert.Single(r.Headers.Where(h => h.Name == "Content-Type"));
        Assert.Equal("text/xml", r.Headers.First(h => h.Name == "Content-Type").Value);
    }

    [Fact]
    public void Get_flag_moves_data_into_query_string()
    {
        var r = CurlParser.Parse("curl -G -d q=cats -d safe=1 https://search.test/find");
        Assert.Equal("GET", r.Method);
        Assert.Equal("https://search.test/find?q=cats&safe=1", r.Url);
        Assert.Empty(r.Body);
    }

    [Fact]
    public void User_flag_produces_basic_auth_header()
    {
        var r = CurlParser.Parse("curl -u alice:secret https://example.com");
        var auth = r.Headers.First(h => h.Name == "Authorization").Value;
        Assert.StartsWith("Basic ", auth);
        var decoded = System.Text.Encoding.UTF8.GetString(
            System.Convert.FromBase64String(auth["Basic ".Length..]));
        Assert.Equal("alice:secret", decoded);
    }

    [Fact]
    public void Handles_line_continuations_and_compressed_flag()
    {
        var cmd = "curl 'https://example.com/x' \\\n  -H 'Accept: */*' \\\n  --compressed";
        var r = CurlParser.Parse(cmd);
        Assert.Equal("https://example.com/x", r.Url);
        Assert.Contains(r.Headers, h => h.Name == "Accept");
    }

    [Fact]
    public void User_agent_and_referer_shortcuts()
    {
        var r = CurlParser.Parse("curl -A 'MyAgent/2.0' -e 'https://ref.test' https://example.com");
        Assert.Contains(r.Headers, h => h.Name == "User-Agent" && h.Value == "MyAgent/2.0");
        Assert.Contains(r.Headers, h => h.Name == "Referer" && h.Value == "https://ref.test");
    }
}

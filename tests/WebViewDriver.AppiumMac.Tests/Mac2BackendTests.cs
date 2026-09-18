using System.Text.Json.Nodes;
using OpenQA.Selenium.Interactions;
using WebViewDriver.Selenium.Actions;
using WebViewDriver.Selenium.AppiumMac;
using Xunit;

namespace WebViewDriver.AppiumMac.Tests;

public sealed class KeyMappingTests
{
    [Theory]
    [InlineData('\uE008', "Shift")]
    [InlineData('\uE009', "Control")]
    [InlineData('\uE00A', "Alt")]
    [InlineData('\uE03D', "Meta")]
    [InlineData('\uE007', "Enter")]
    [InlineData('\uE012', "ArrowLeft")]
    [InlineData('\uE00D', " ")]
    public void Maps_webdriver_codepoints_to_key_names(char codePoint, string expected)
        => Assert.Equal(expected, KeyMapping.ToKeyName(codePoint.ToString()));

    [Fact]
    public void Regular_characters_pass_through()
        => Assert.Equal("a", KeyMapping.ToKeyName("a"));

    [Fact]
    public void Annotates_key_actions_in_place()
    {
        var sequences = JsonNode.Parse("""
            [{"type":"key","id":"k","actions":[
              {"type":"keyDown","value":"\uE008"},
              {"type":"keyDown","value":"x"},
              {"type":"pause","duration":0},
              {"type":"keyUp","value":"\uE008"}]}]
            """)!.AsArray();

        KeyMapping.AnnotateKeyActions(sequences);

        var actions = sequences[0]!["actions"]!.AsArray();
        Assert.Equal("Shift", (string?)actions[0]!["wvdKey"]);
        Assert.Equal("x", (string?)actions[1]!["wvdKey"]);
        Assert.Null(actions[2]!["wvdKey"]);
        Assert.Equal("Shift", (string?)actions[3]!["wvdKey"]);
    }
}

/// <summary>Coordinate mapping and translation of the mac2 backend, no Appium server needed.</summary>
public sealed class Mac2ActionsBackendTests
{
    private sealed class FakeMac2Session : IMac2Session
    {
        public ScreenRect WebViewRect { get; set; } = new(200, 100, 1000, 800);
        public IList<ActionSequence>? Performed { get; private set; }
        public bool Released { get; private set; }

        public ScreenRect GetWebViewScreenRect() => WebViewRect;
        public void PerformActions(IList<ActionSequence> sequences) => Performed = sequences;
        public void ReleaseActions() => Released = true;
    }

    private sealed class FakeActionsContext : IActionsContext
    {
        public Task<JsonNode?> ExecuteScriptAsync(string fnBody, JsonArray args, CancellationToken cancellationToken)
        {
            if (fnBody.Contains("innerWidth"))
            {
                return Task.FromResult<JsonNode?>(new JsonObject { ["width"] = 1000.0, ["height"] = 800.0 });
            }

            // Element viewport rect: 100,50 40x20 => center 120,60.
            return Task.FromResult<JsonNode?>(new JsonObject { ["x"] = 100.0, ["y"] = 50.0, ["width"] = 40.0, ["height"] = 20.0 });
        }
    }

    private static JsonArray PointerSequence(params string[] actionJson)
        => JsonNode.Parse($$"""[{"type":"pointer","id":"mouse","parameters":{"pointerType":"mouse"},"actions":[{{string.Join(",", actionJson)}}]}]""")!.AsArray();

    private static List<Dictionary<string, object>> PerformedActions(FakeMac2Session session)
    {
        var dictionary = session.Performed!.Single().ToDictionary();
        return ((IEnumerable<object>)dictionary["actions"]).Cast<Dictionary<string, object>>().ToList();
    }

    [Fact]
    public async Task Element_origin_maps_to_screen_center()
    {
        var session = new FakeMac2Session();
        var backend = new Mac2ActionsBackend(session);
        var sequences = PointerSequence(
            """{"type":"pointerMove","duration":0,"x":0,"y":0,"origin":{"element-6066-11e4-a52e-4f735466cecf":"wvd-1"}}""",
            """{"type":"pointerDown","button":0}""",
            """{"type":"pointerUp","button":0}""");

        await backend.PerformAsync(sequences, new FakeActionsContext(), CancellationToken.None);

        var actions = PerformedActions(session);
        // Element center (120, 60) at scale 1.0 offset by the webview frame (200, 100).
        Assert.Equal("pointerMove", actions[0]["type"]);
        Assert.Equal(320L, Convert.ToInt64(actions[0]["x"]));
        Assert.Equal(160L, Convert.ToInt64(actions[0]["y"]));
        Assert.Equal("pointerDown", actions[1]["type"]);
        Assert.Equal("pointerUp", actions[2]["type"]);
    }

    [Fact]
    public async Task Page_zoom_scales_coordinates()
    {
        // Webview 1000pt wide but innerWidth 2000 CSS px => scale 0.5.
        var session = new FakeMac2Session { WebViewRect = new ScreenRect(0, 0, 1000, 800) };
        var backend = new Mac2ActionsBackend(session);

        var contextWithZoom = new ZoomedContext();
        var sequences = PointerSequence("""{"type":"pointerMove","duration":0,"x":400,"y":200,"origin":"viewport"}""");

        await backend.PerformAsync(sequences, contextWithZoom, CancellationToken.None);

        var actions = PerformedActions(session);
        Assert.Equal(200L, Convert.ToInt64(actions[0]["x"]));
        Assert.Equal(100L, Convert.ToInt64(actions[0]["y"]));
    }

    private sealed class ZoomedContext : IActionsContext
    {
        public Task<JsonNode?> ExecuteScriptAsync(string fnBody, JsonArray args, CancellationToken cancellationToken)
            => Task.FromResult<JsonNode?>(new JsonObject { ["width"] = 2000.0, ["height"] = 1600.0 });
    }

    [Fact]
    public async Task Key_sequences_pass_through_untranslated()
    {
        var session = new FakeMac2Session();
        var backend = new Mac2ActionsBackend(session);
        var sequences = JsonNode.Parse("""
            [{"type":"key","id":"kb","actions":[
              {"type":"keyDown","value":"\uE008"},
              {"type":"keyUp","value":"\uE008"}]}]
            """)!.AsArray();

        await backend.PerformAsync(sequences, new FakeActionsContext(), CancellationToken.None);

        var actions = PerformedActions(session);
        Assert.Equal("keyDown", actions[0]["type"]);
        Assert.Equal("\uE008", actions[0]["value"]);
    }

    [Fact]
    public async Task Release_delegates_to_the_session()
    {
        var session = new FakeMac2Session();
        var backend = new Mac2ActionsBackend(session);
        await backend.ReleaseAsync(new FakeActionsContext(), CancellationToken.None);
        Assert.True(session.Released);
    }
}

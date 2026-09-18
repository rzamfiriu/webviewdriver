using OpenQA.Selenium;
using WebViewDriver.Selenium;
using Xunit;

namespace WebViewDriver.Tests;

public sealed class ScriptsTests
{
    [Theory]
    [InlineData("css selector", ".save", "css", ".save")]
    [InlineData("xpath", "//h1", "xpath", "//h1")]
    [InlineData("tag name", "button", "css", "button")]
    public void Passthrough_locators(string mechanism, string value, string expectedStrategy, string expectedSelector)
    {
        var (strategy, selector) = Scripts.TranslateLocator(mechanism, value);
        Assert.Equal(expectedStrategy, strategy);
        Assert.Equal(expectedSelector, selector);
    }

    [Fact]
    public void Link_text_becomes_exact_xpath()
    {
        var (strategy, selector) = Scripts.TranslateLocator("link text", "Save changes");
        Assert.Equal("xpath", strategy);
        Assert.Equal(".//a[normalize-space(.)='Save changes']", selector);
    }

    [Fact]
    public void Partial_link_text_becomes_contains_xpath()
    {
        var (strategy, selector) = Scripts.TranslateLocator("partial link text", "Save");
        Assert.Equal("xpath", strategy);
        Assert.Contains("contains(normalize-space(.), 'Save')", selector);
    }

    [Fact]
    public void Unknown_mechanism_throws_invalid_selector()
        => Assert.Throws<InvalidSelectorException>(() => Scripts.TranslateLocator("magic", "x"));

    [Theory]
    [InlineData("plain", "'plain'")]
    [InlineData("it's", "\"it's\"")]
    [InlineData("say \"hi\"", "'say \"hi\"'")]
    public void XPath_literals_pick_safe_quotes(string value, string expected)
        => Assert.Equal(expected, Scripts.XPathLiteral(value));

    [Fact]
    public void XPath_literal_with_both_quote_kinds_uses_concat()
    {
        var literal = Scripts.XPathLiteral("a'b\"c");
        Assert.StartsWith("concat(", literal);
        Assert.Contains("'a'", literal);
        Assert.Contains("\"'\"", literal);
        Assert.Contains("'b\"c'", literal);
    }
}

using ServiceNowDesk.Mapping;

namespace ServiceNowDesk.Tests;

public class HtmlTextTests
{
    [Fact]
    public void ArticleHtmlBecomesParagraphsWithoutScriptOrStyle()
    {
        var text = HtmlText.ToReadable(
            "<p>Use <strong>zephyrmail</strong> when Outlook shows a blank folder list.</p>"
            + "<script>alert('xss')</script><style>body{color:red}</style>"
            + "<p>Close Outlook, then start it again.</p>");

        Assert.Contains("zephyrmail", text);
        Assert.Contains("Close Outlook, then start it again.", text);
        Assert.DoesNotContain("alert", text);
        Assert.DoesNotContain("color:red", text);
        Assert.DoesNotContain("<", text);
        Assert.Contains("\n", text);
    }

    [Fact]
    public void ListsBreaksAndEntitiesStayReadable()
    {
        var text = HtmlText.ToReadable("<p>Save &amp; restart</p><ul><li>First step</li><li>Second step</li></ul>");
        Assert.Contains("Save & restart", text);
        Assert.Contains("• First step", text);
        Assert.Contains("• Second step", text);
        Assert.DoesNotContain("<li>", text);
    }
}

using System.Net;
using ServiceNowDesk.Client;
using ServiceNowDesk.Mapping;
using ServiceNowDesk.Services;
using ServiceNowDesk.ViewModels;

namespace ServiceNowDesk.Tests;

public class KnowledgeArticleActionTests
{
    [Fact]
    public void ImageRewriteKeepsTheParagraphAndTurnsImagesIntoLines()
    {
        const string html =
            "<p>Reset the mailbox before you close the ticket.</p>"
            + "<img src=\"https://cdn.example.com/steps.png\">"
            + "<script>alert('xss')</script>"
            + "<img alt=\"diagram\" src=\"sys_attachment.do?sys_id=abc\">"
            + "<style>body{color:red}</style>"
            + "<p>Then ask the caller to sign in again.</p>";

        var instance = new Uri("https://acme.service-now.com/navpage.do");
        var blocks = HtmlText.ToBlocks(html, instance);
        var text = HtmlText.ToReadable(html, instance);

        Assert.Contains("Reset the mailbox before you close the ticket.", text);
        Assert.Contains("Then ask the caller to sign in again.", text);
        Assert.Contains("https://cdn.example.com/steps.png", text);
        Assert.Contains("https://acme.service-now.com/sys_attachment.do?sys_id=abc", text);
        Assert.DoesNotContain("alert", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("color:red", text);
        Assert.DoesNotContain("<", text);

        var links = blocks.Where(block => block.IsLink).ToArray();
        Assert.Equal(2, links.Length);
        Assert.Equal("Image", links[0].Text);
        Assert.Equal("https://cdn.example.com/steps.png", links[0].Url);
        Assert.Equal("Image", links[1].Text);
        Assert.Equal("https://acme.service-now.com/sys_attachment.do?sys_id=abc", links[1].Url);
        Assert.Equal(
            ["Reset the mailbox before you close the ticket.", "Image", "Image", "Then ask the caller to sign in again."],
            blocks.Select(block => block.Text).ToArray());
    }

    [Fact]
    public void RelativeImageWithoutAnInstanceAsksForTheBrowser()
    {
        var blocks = HtmlText.ToBlocks(
            "<p>See the screenshot.</p><img src=\"/sys_attachment.do?sys_id=abc\"><img src=\"sys_attachment.do?sys_id=def\">",
            null);
        var text = HtmlText.ToReadable(
            "<p>See the screenshot.</p><script>alert('xss')</script><img src=\"sys_attachment.do?sys_id=abc\">");

        Assert.Contains("See the screenshot.", text);
        Assert.Contains(HtmlText.BrowserImagePlaceholder, text);
        Assert.DoesNotContain("alert", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<", text);
        Assert.Equal(2, blocks.Count(block => block.Text == HtmlText.BrowserImagePlaceholder));
        Assert.DoesNotContain(blocks, block => block.IsLink);
        Assert.Contains(blocks, block => block.Text.Contains("See the screenshot.", StringComparison.Ordinal));
    }

    [Fact]
    public void SlashAndQueryAttachmentsUseTheInstance()
    {
        var instance = new Uri("https://acme.service-now.com/");
        var blocks = HtmlText.ToBlocks(
            "<p>Before</p><img src=\"/sys_attachment.do?sys_id=abc&amp;sysparm_view=1\"><p>After</p><img src='http://files.example.com/a.png'>",
            instance);
        var links = blocks.Where(block => block.IsLink).ToArray();

        Assert.Equal("https://acme.service-now.com/sys_attachment.do?sys_id=abc&sysparm_view=1", links[0].Url);
        Assert.Equal("http://files.example.com/a.png", links[1].Url);
        Assert.Contains(blocks, block => block.Text == "Before");
        Assert.Contains(blocks, block => block.Text == "After");
    }

    [Fact]
    public async Task CopyNumberUsesTheKnowledgeNumber()
    {
        var desktop = new RecordingDesktopServices();
        var knowledge = new KnowledgeWorkspaceViewModel(desktop);
        using var client = new SampleServiceNowClient();

        await knowledge.OpenAsync(client, "kb-zephyr");
        knowledge.CopyNumberCommand.Execute(null);

        Assert.Equal("KB0001001", Assert.Single(desktop.CopiedText));
        Assert.Equal("Copied KB0001001.", knowledge.ReaderMessage);
        Assert.Contains(HtmlText.BrowserImagePlaceholder, knowledge.Body);
        Assert.Contains(knowledge.Blocks, block => block.Text == HtmlText.BrowserImagePlaceholder && !block.IsLink);
    }

    [Fact]
    public async Task OpenInBrowserUsesTheArticleUrlOrExplainsPracticeMode()
    {
        var desktop = new RecordingDesktopServices();
        var knowledge = new KnowledgeWorkspaceViewModel(desktop);
        using var sample = new SampleServiceNowClient();
        await knowledge.OpenAsync(sample, "kb-zephyr");
        knowledge.OpenInBrowserCommand.Execute(null);
        Assert.Empty(desktop.OpenedUrls);
        Assert.Equal("Connect to a live instance to open this record in the browser.", knowledge.ReaderMessage);

        var handler = new StubHandler((_, _) => Api.Json("""
            {"result":{
              "sys_id":"kb-live",
              "number":"KB0012345",
              "short_description":"Reset a password",
              "text":"<p>Ask the caller to verify.</p><img src=\"https://cdn.example.com/steps.png\">",
              "workflow_state":"published"
            }}
            """));
        using var live = ServiceNowClient.Create(Api.BasicSession(), handler);
        await knowledge.OpenAsync(live, "kb-live");
        knowledge.OpenInBrowserCommand.Execute(null);
        knowledge.OpenArticleLinkCommand.Execute("https://cdn.example.com/steps.png");

        Assert.Equal(
            ["https://example.service-now.com/kb_view.do?sys_kb_id=kb-live", "https://cdn.example.com/steps.png"],
            desktop.OpenedUrls);
        Assert.Equal("Image", Assert.Single(knowledge.Blocks, block => block.IsLink).Text);
    }

    [Fact]
    public void StatsCountParserReadsResultStatsCount()
    {
        Assert.Equal(42, KnowledgeStats.ReadCount("""{"result":{"stats":{"count":"42"}}}"""));
        Assert.Equal(7, KnowledgeStats.ReadCount("""{"result":{"stats":{"count":7}}}"""));
        Assert.Null(KnowledgeStats.ReadCount("""{"result":{"stats":{}}}"""));
        Assert.Null(KnowledgeStats.ReadCount("not json"));
    }

    [Fact]
    public async Task PublishedCountUsesTheStatsQueryAndSampleCountIsPracticeData()
    {
        var handler = new StubHandler((_, _) => Api.Json("""{"result":{"stats":{"count":"128"}}}"""));
        using var live = ServiceNowClient.Create(Api.BasicSession(), handler);
        var count = await live.CountPublishedKnowledgeAsync(CancellationToken.None);

        Assert.Equal(128, count);
        var call = handler.Calls.Single();
        Assert.Contains("/api/now/stats/kb_knowledge", call.PathAndQuery);
        Assert.Contains("sysparm_count=true", call.PathAndQuery);
        Assert.Equal(KnowledgeStats.PublishedQuery, QueryOf(call.PathAndQuery));
        Assert.Equal("Published articles on this instance: 128", KnowledgeStats.Caption(128, practiceData: false));

        using var sample = new SampleServiceNowClient();
        var knowledge = new KnowledgeWorkspaceViewModel();
        knowledge.Attach(sample);
        await knowledge.RefreshPublishedCountAsync();
        Assert.Equal(3, await sample.CountPublishedKnowledgeAsync(CancellationToken.None));
        Assert.Equal("Published articles on this instance: 3 (practice data)", knowledge.PublishedCountText);
    }

    [Fact]
    public async Task StatsFailureLeavesThePublishedCountBlank()
    {
        var handler = new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("""{"error":{"message":"blocked"}}""")
        });
        using var live = ServiceNowClient.Create(Api.BasicSession(), handler);
        var knowledge = new KnowledgeWorkspaceViewModel();
        knowledge.Attach(live);
        await knowledge.RefreshPublishedCountAsync();

        Assert.Null(await live.CountPublishedKnowledgeAsync(CancellationToken.None));
        Assert.Equal("", knowledge.PublishedCountText);
        Assert.Equal("", knowledge.ErrorMessage);
    }

    private static string QueryOf(string pathAndQuery)
    {
        var marker = "sysparm_query=";
        var start = pathAndQuery.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0);
        var encoded = pathAndQuery[(start + marker.Length)..];
        var end = encoded.IndexOf('&');
        if (end >= 0)
            encoded = encoded[..end];
        return Uri.UnescapeDataString(encoded);
    }
}

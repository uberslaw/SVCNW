using ServiceNowDesk.Models;
using ServiceNowDesk.Query;

namespace ServiceNowDesk.Tests;

public class EncodedQueryTests
{
    [Fact]
    public void FreeTextUsesTheServiceNowTextIndex()
    {
        Assert.Equal("123TEXTQUERY321=printer jam", EncodedQuery.TextSearch("printer jam"));
    }

    [Fact]
    public void FullTicketNumberIsAnExactMatch()
    {
        Assert.Equal("number=INC0010002", EncodedQuery.TextSearch("inc0010002"));
        Assert.Equal("number=RITM0010001", EncodedQuery.TextSearch("RITM0010001"));
        Assert.Equal("number=IMS0010001", EncodedQuery.TextSearch("ims0010001"));
        Assert.Equal(DeskSection.WalkUps, EncodedQuery.SectionForNumber("IMS0010001"));
        Assert.Null(EncodedQuery.SectionForNumber("IMPORTANT"));
        Assert.Equal(DeskSection.RequestedItems, EncodedQuery.SectionForNumber("RITM0010001"));
        Assert.Equal(DeskSection.Requests, EncodedQuery.SectionForNumber("REQ0010001"));
        Assert.Equal(DeskSection.Incidents, EncodedQuery.SectionForNumber("INC0010002"));
    }

    [Fact]
    public void PartialNumberAndDigitsStayOnTheNumberField()
    {
        Assert.Equal("numberSTARTSWITHINC001", EncodedQuery.TextSearch("INC001"));
        Assert.Equal("numberLIKE10002", EncodedQuery.TextSearch("10002"));
    }

    [Fact]
    public void CaretInSearchTextCannotAddQueryClauses()
    {
        var clause = EncodedQuery.TextSearch("vpn^active=false^ORpriority=1");
        Assert.DoesNotContain("^", clause);
        Assert.Equal("123TEXTQUERY321=vpn active=false ORpriority=1", clause);
    }

    [Fact]
    public void KnowledgeNumberIsAnExactMatchAndCannotInjectClauses()
    {
        Assert.Equal("number=KB0001234", EncodedQuery.TextSearch("KB0001234"));
        Assert.Equal("number=KB0001234", EncodedQuery.TextSearch("kb0001234"));
        Assert.Equal(DeskSection.Knowledge, EncodedQuery.SectionForNumber("KB0001234"));
        Assert.True(EncodedQuery.IsNumberQuery("KB0001234"));

        var injected = EncodedQuery.TextSearch("KB0001234^workflow_state=published");
        Assert.DoesNotContain("^", injected);
        Assert.Equal("123TEXTQUERY321=KB0001234 workflow_state=published", injected);
        Assert.Equal(
            "123TEXTQUERY321=KB0001234 workflow_state=published^ORDERBYDESCsys_updated_on",
            EncodedQuery.Build(injected, null, null, null));
        Assert.Equal(
            "number=KB0001234^ORDERBYDESCsys_updated_on",
            EncodedQuery.Build(EncodedQuery.TextSearch("KB0001234"), null, null, null));
    }

    [Fact]
    public void UserSearchOrsNameEmailAndUserNameThenRequiresActive()
    {
        const string term = "jordan.lee@example.com";
        Assert.Equal(
            "nameLIKEjordan.lee@example.com^ORemailLIKEjordan.lee@example.com^ORuser_nameLIKEjordan.lee@example.com^active=true",
            EncodedQuery.ActiveUserSearch(term));
        Assert.DoesNotContain("^ORactive=true^", EncodedQuery.ActiveUserSearch(term));

        var exact = EncodedQuery.ActiveUserExact(term);
        Assert.Contains("email=" + term + "^active=true", exact);
        Assert.Contains("nameSTARTSWITH" + term + "^nameENDSWITH" + term + "^active=true", exact);
        Assert.Contains("emailSTARTSWITH" + term + "^emailENDSWITH" + term + "^active=true", exact);
        Assert.Contains("user_nameSTARTSWITH" + term + "^user_nameENDSWITH" + term + "^active=true", exact);
        Assert.Contains("^NQ", exact);
        Assert.DoesNotContain("^ORactive=true^", exact);
    }

    [Fact]
    public void BuildCombinesFiltersAndOrdersByRecentUpdates()
    {
        var query = EncodedQuery.Build(
            EncodedQuery.TextSearch("printer"),
            "assigned_to=javascript:gs.getUserID()",
            EncodedQuery.ActivityClause(ActivityFilter.Open),
            null);

        Assert.Equal(
            "123TEXTQUERY321=printer^assigned_to=javascript:gs.getUserID()^active=true^ORDERBYDESCsys_updated_on",
            query);
    }

    [Theory]
    [InlineData("company.service-now.com", "https://company.service-now.com/")]
    [InlineData("https://company.service-now.com/incident.do?sys_id=abc", "https://company.service-now.com/")]
    public void InstanceUrlDropsThePageAndKeepsTheHost(string input, string expected)
    {
        Assert.Equal(expected, ServiceNowSession.NormalizeInstance(input).AbsoluteUri);
    }

    [Fact]
    public void BasicSignInRequiresAPassword()
    {
        var error = Assert.Throws<ArgumentException>(() => ServiceNowSession.FromSettings(new DeskSettings
        {
            InstanceUrl = "https://company.service-now.com",
            Username = "alex"
        }));
        Assert.Contains("password", error.Message, StringComparison.OrdinalIgnoreCase);
    }
}

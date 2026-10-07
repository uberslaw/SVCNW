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
    public void PlainPhraseStaysOneTextIndexQuery()
    {
        Assert.Equal("123TEXTQUERY321=vpn printer", EncodedQuery.TextSearch("vpn printer"));
        Assert.Equal("123TEXTQUERY321=well-known printer", EncodedQuery.TextSearch("well-known printer"));
        Assert.Equal("123TEXTQUERY321=call +1 555-0100", EncodedQuery.TextSearch("call +1 555-0100"));
        Assert.Equal("123TEXTQUERY321=vpn printer", EncodedQuery.TextSearch("\"vpn printer\""));
    }

    [Fact]
    public void PlusRequiresEveryTerm()
    {
        Assert.Equal(
            "123TEXTQUERY321=vpn^123TEXTQUERY321=printer",
            EncodedQuery.TextSearch("vpn + printer"));
        Assert.Equal(
            "123TEXTQUERY321=vpn^123TEXTQUERY321=printer",
            EncodedQuery.TextSearch("+vpn +printer"));
        Assert.Equal(
            "123TEXTQUERY321=vpn printer^123TEXTQUERY321=jam",
            EncodedQuery.TextSearch("\"vpn printer\" + jam"));
    }

    [Fact]
    public void StarBecomesALikeWildcardOnDescriptionAndJournal()
    {
        Assert.Equal(
            "(short_descriptionLIKEprinter*^ORdescriptionLIKEprinter*^ORwork_notesLIKEprinter*^ORcommentsLIKEprinter*)",
            EncodedQuery.TextSearch("printer*"));
        Assert.Equal(
            "123TEXTQUERY321=vpn^(short_descriptionLIKEprinter*^ORdescriptionLIKEprinter*^ORwork_notesLIKEprinter*^ORcommentsLIKEprinter*)",
            EncodedQuery.TextSearch("vpn + printer*"));
        Assert.Equal(
            "(short_descriptionLIKEprinter*^ORtextLIKEprinter*)",
            EncodedQuery.TextSearch("printer*", SearchFieldSet.Knowledge));
        Assert.DoesNotContain("work_notes", EncodedQuery.TextSearch("printer*", SearchFieldSet.Knowledge));
    }

    [Fact]
    public void TicketPrefixWildcardAlsoLikesTheNumber()
    {
        Assert.Equal("numberSTARTSWITHINC001", EncodedQuery.TextSearch("INC001"));
        Assert.Contains("numberLIKEINC*", EncodedQuery.TextSearch("INC*"));
        Assert.Contains("short_descriptionLIKEinc*", EncodedQuery.TextSearch("inc*"));
        Assert.Contains("numberLIKEINC*", EncodedQuery.TextSearch("inc*"));
    }

    [Fact]
    public void CaretInsideAnAndOrWildcardTermCannotAddAClause()
    {
        var clause = EncodedQuery.TextSearch("vpn + printer^active=false^ORpriority=1");
        Assert.DoesNotContain("^active=false", clause);
        Assert.DoesNotContain("^ORpriority", clause);
        Assert.Equal("123TEXTQUERY321=vpn^123TEXTQUERY321=printer active=false ORpriority=1", clause);

        var wildcard = EncodedQuery.TextSearch("printer*^active=false");
        Assert.DoesNotContain("^active=false", wildcard);
        Assert.Contains("short_descriptionLIKEprinter* active=false", wildcard);
    }

    [Fact]
    public void RecordFiltersUseSysIdsAndOpenedDaysAndIgnoreCarets()
    {
        Assert.Equal(
            "assignment_group=group-cs^assigned_to=sample-user^opened_at>=2026-09-01@00:00:00^opened_at<=2026-09-30@23:59:59",
            EncodedQuery.RecordFilters("group-cs", "sample-user", "2026-09-01", "2026-09-30"));
        Assert.Equal("", EncodedQuery.RecordFilters("group-cs^active=false", "sample-user^ORpriority=1", "2026-09-01^active=false", null));
        Assert.False(EncodedQuery.HasRecordFilter(null, "", "not-a-date", null));
        Assert.True(EncodedQuery.OpenedInRange("2026-09-25", "2026-09-25", "2026-09-25 13:00"));
        Assert.False(EncodedQuery.OpenedInRange("2026-09-26", null, "2026-09-25 13:00"));
    }

    [Fact]
    public void InMemorySearchHonorsAndAndWildcards()
    {
        var fields = new[]
        {
            "Printer jam on floor 3",
            "The HP printer by finance is jammed and the queue is stuck.",
            "Replaced the tray and asked finance to reprint.",
            "The finance queue is still stuck. Can someone call me?"
        };

        Assert.True(EncodedQuery.Matches("vpn printer", "INC0010001", ["The vpn printer is down"]));
        Assert.True(EncodedQuery.Matches("jammed*", "INC0010001", fields));
        Assert.True(EncodedQuery.Matches("reprint*", "INC0010001", fields));
        Assert.True(EncodedQuery.Matches("vpn + jammed*", "INC0010002", ["VPN tunnel", "The printer is jammed"]));
        Assert.False(EncodedQuery.Matches("vpn + jammed*", "INC0010001", fields));
        Assert.True(EncodedQuery.Matches("INC*", "INC0010001", fields));
        Assert.False(EncodedQuery.Matches("INC*", "REQ0010001", ["Request for a printer"]));
        Assert.Equal("number=INC0010002", EncodedQuery.TextSearch("INC0010002"));
        Assert.True(EncodedQuery.Matches("INC0010002", "INC0010002", fields));
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
    [InlineData("https://company.service-now.com/", "https://company.service-now.com/")]
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

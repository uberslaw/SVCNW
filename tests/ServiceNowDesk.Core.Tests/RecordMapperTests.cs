using System.Text.Json;
using ServiceNowDesk.Mapping;
using ServiceNowDesk.Models;

namespace ServiceNowDesk.Tests;

public class RecordMapperTests
{
    [Fact]
    public void MapsDisplayValueObjects()
    {
        using var document = JsonDocument.Parse("{\"result\":" + Api.IncidentObject + "}");
        var record = RecordMapper.Incident(document.RootElement.GetProperty("result"));

        Assert.Equal("inc-printer", record.SysId);
        Assert.Equal("INC0010001", record.Number);
        Assert.Equal("Printer jam", record.ShortDescription);
        Assert.Equal("2", record.State);
        Assert.Equal("In Progress", record.StateLabel);
        Assert.Equal("user-jordan", record.Caller.SysId);
        Assert.Equal("Jordan Lee", record.Caller.Display);
        Assert.Equal("group-cs", record.AssignmentGroup.SysId);
        Assert.True(record.Active);
        Assert.Equal("2026-09-28 10:40:00", record.UpdatedAtValue);
    }

    [Fact]
    public void MapsPlainStringsWhenDisplayValuesAreOff()
    {
        using var document = JsonDocument.Parse("""
            {
              "sys_id": "req-1",
              "number": "REQ0010001",
              "short_description": "New laptop",
              "description": "",
              "request_state": "in_process",
              "priority": "3",
              "special_instructions": "Needs a dock",
              "approval": "approved",
              "stage": "fulfillment",
              "due_date": "",
              "requested_for": "user-jordan",
              "opened_by": "sample-user",
              "opened_at": "2026-09-24 09:00:00",
              "sys_updated_on": "2026-09-28 09:00:00",
              "active": "true"
            }
            """);

        var record = RecordMapper.Request(document.RootElement);
        Assert.Equal("REQ0010001", record.Number);
        Assert.Equal("in_process", record.RequestState);
        Assert.Equal("user-jordan", record.RequestedFor.SysId);
        Assert.Equal("Needs a dock", record.SpecialInstructions);
    }

    [Fact]
    public void ChangeJsonOmitsUnsetFieldsAndClearsReferences()
    {
        var json = ChangeJson.FromIncident(new IncidentChanges
        {
            ShortDescription = "Headset failed",
            ClearAssignedTo = true
        });

        Assert.Contains("\"short_description\":\"Headset failed\"", json);
        Assert.Contains("\"assigned_to\":\"\"", json);
        Assert.DoesNotContain("caller_id", json);
    }

    [Fact]
    public void HtmlAndApiErrorsBecomeReadableExceptions()
    {
        var html = ServiceNowException.FromResponse(200, "<html>login</html>");
        Assert.Contains("web page", html.Message, StringComparison.OrdinalIgnoreCase);

        var denied = ServiceNowException.FromResponse(403, """{"error":{"message":"Insufficient rights","detail":"ACL blocked incident"},"status":"failure"}""");
        Assert.Equal("ServiceNow: ACL blocked incident", denied.Message);

        var oauth = ServiceNowException.FromResponse(401, """{"error":"invalid_client","error_description":"invalid client credentials"}""");
        Assert.Contains("invalid client credentials", oauth.Message);
    }
}

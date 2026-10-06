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
    public void KnowledgeArticleReadsHtmlFromDisplayValue()
    {
        using var document = JsonDocument.Parse("""
            {
              "sys_id": {"value":"kb-zephyr","display_value":"kb-zephyr"},
              "number": {"value":"KB0001001","display_value":"KB0001001"},
              "short_description": {"value":"Blank folders","display_value":"Blank folders"},
              "text": {"value":"<p>plain</p>","display_value":"<p>Use <b>zephyrmail</b></p><script>alert(1)</script>"},
              "topic": "Email",
              "workflow_state": {"value":"published","display_value":"Published"},
              "kb_category": {"value":"cat","display_value":"Email"},
              "kb_knowledge_base": {"value":"base","display_value":"IT"},
              "author": {"value":"sample-user","display_value":"Alex Rivera"},
              "sys_updated_on": {"value":"2026-09-18 14:22:00","display_value":"2026-09-18 14:22:00"},
              "published": {"value":"2026-09-18","display_value":"2026-09-18"}
            }
            """);

        var article = RecordMapper.Knowledge(document.RootElement);
        Assert.Equal("KB0001001", article.Number);
        Assert.Contains("<b>zephyrmail</b>", article.Text);
        Assert.Equal("published", article.WorkflowState);
        Assert.Equal("Published", article.WorkflowStateLabel);
        Assert.Equal("Email", article.Category);
        Assert.Equal("IT", article.KnowledgeBase);
        Assert.Equal("Alex Rivera", article.Author.Display);
        Assert.Equal("sample-user", article.Author.SysId);
        Assert.Equal("2026-09-18 14:22:00", article.UpdatedAtValue);
    }

    [Fact]
    public void JournalReadsDisplayValueAllForWorkNotesAndCustomerComments()
    {
        using var document = JsonDocument.Parse("""
            {
              "result": [
                {
                  "sys_id": {"value":"journal-printer","display_value":"journal-printer"},
                  "name": {"value":"incident","display_value":"incident"},
                  "element": {"value":"work_notes","display_value":"Work notes"},
                  "element_id": {"value":"inc-printer","display_value":"INC0010001"},
                  "value": {"value":"Replaced the tray and asked finance to reprint.","display_value":"Replaced the tray and asked finance to reprint."},
                  "sys_created_on": {"value":"2026-09-28 10:40:00","display_value":"2026-09-28 10:40:00"},
                  "sys_created_by": {"value":"alex.rivera","display_value":"Alex Rivera"}
                },
                {
                  "sys_id": {"value":"journal-printer-comment","display_value":"journal-printer-comment"},
                  "name": {"value":"incident","display_value":"Incident"},
                  "element": {"value":"comments","display_value":"Additional comments"},
                  "element_id": {"value":"inc-printer","display_value":"INC0010001"},
                  "value": {"value":"The finance queue is still stuck. Can someone call me?","display_value":""},
                  "sys_created_on": {"value":"2026-09-28 09:30:00","display_value":"2026-09-28 09:30:00"},
                  "sys_created_by": {"value":"jordan.lee","display_value":"Jordan Lee"}
                },
                {
                  "sys_id": {"value":"journal-label-only","display_value":"journal-label-only"},
                  "name": {"value":"task","display_value":"Task"},
                  "element": {"value":"","display_value":"Work notes"},
                  "value": {"value":"","display_value":"Noted from the activity label."},
                  "sys_created_on": {"value":"2026-09-27 08:00:00","display_value":"2026-09-27 08:00:00"},
                  "sys_created_by": {"value":"casey.ng","display_value":""}
                }
              ]
            }
            """);

        var rows = document.RootElement.GetProperty("result").EnumerateArray().Select(RecordMapper.Journal).ToArray();

        var work = rows[0];
        Assert.Equal("incident", work.Table);
        Assert.Equal("work_notes", work.Kind);
        Assert.Equal("Work note", work.KindLabel);
        Assert.False(work.IsCustomer);
        Assert.Equal("Alex Rivera", work.Author);
        Assert.Equal("2026-09-28 10:40:00", work.CreatedDisplay);
        Assert.Equal("Replaced the tray and asked finance to reprint.", work.Text);

        var comment = rows[1];
        Assert.Equal("incident", comment.Table);
        Assert.Equal("comments", comment.Kind);
        Assert.Equal("Customer comment", comment.KindLabel);
        Assert.True(comment.IsCustomer);
        Assert.Equal("Jordan Lee", comment.Author);
        Assert.Equal("2026-09-28 09:30:00", comment.CreatedDisplay);
        Assert.Equal("The finance queue is still stuck. Can someone call me?", comment.Text);

        var labeled = rows[2];
        Assert.Equal("task", labeled.Table);
        Assert.Equal("work_notes", labeled.Kind);
        Assert.Equal("Work note", labeled.KindLabel);
        Assert.Equal("casey.ng", labeled.Author);
        Assert.Equal("Noted from the activity label.", labeled.Text);
    }

    [Fact]
    public void JournalReadsTaskParentRowsWhenNameIsTask()
    {
        using var document = JsonDocument.Parse("""
            {
              "result": [
                {
                  "sys_id": {"value":"journal-task-work","display_value":"journal-task-work"},
                  "name": {"value":"task","display_value":"task"},
                  "element": {"value":"work_notes","display_value":"Work notes"},
                  "element_id": {"value":"inc-live","display_value":"INC0012345"},
                  "value": {"value":"","display_value":"Replaced the fuser and the printer is back."},
                  "sys_created_on": {"value":"2026-10-06 14:05:00","display_value":"2026-10-06 14:05:00"},
                  "sys_created_by": {"value":"casey.ng","display_value":"Casey Ng"}
                },
                {
                  "sys_id": {"value":"journal-task-comment","display_value":"journal-task-comment"},
                  "name": {"value":"task","display_value":"task"},
                  "element": {"value":"comments","display_value":"Additional comments"},
                  "element_id": {"value":"inc-live","display_value":"INC0012345"},
                  "value": {"value":"The queue is still paused. Please call me.","display_value":""},
                  "sys_created_on": {"value":"2026-10-06 13:40:00","display_value":"2026-10-06 13:40:00"},
                  "sys_created_by": {"value":"jordan.lee","display_value":"Jordan Lee"}
                }
              ]
            }
            """);

        var rows = document.RootElement.GetProperty("result").EnumerateArray().Select(RecordMapper.Journal).ToArray();

        var work = rows[0];
        Assert.Equal("task", work.Table);
        Assert.Equal("work_notes", work.Kind);
        Assert.Equal("Work note", work.KindLabel);
        Assert.False(work.IsCustomer);
        Assert.Equal("Casey Ng", work.Author);
        Assert.Equal("2026-10-06 14:05:00", work.CreatedDisplay);
        Assert.Equal("Replaced the fuser and the printer is back.", work.Text);

        var comment = rows[1];
        Assert.Equal("task", comment.Table);
        Assert.Equal("comments", comment.Kind);
        Assert.Equal("Customer comment", comment.KindLabel);
        Assert.True(comment.IsCustomer);
        Assert.Equal("Jordan Lee", comment.Author);
        Assert.Equal("2026-10-06 13:40:00", comment.CreatedDisplay);
        Assert.Equal("The queue is still paused. Please call me.", comment.Text);
    }

    [Fact]
    public void ActivityHistoryReadsDisplayValueWhenJournalValueIsEmpty()
    {
        using var document = JsonDocument.Parse("""
            {
              "work_notes": {
                "value": "",
                "display_value": "2026-10-06 14:05:00 - Casey Ng (Work notes)\nReplaced the fuser and the printer is back.\n"
              },
              "comments": {
                "value": "",
                "display_value": "2026-10-06 13:40:00 - Jordan Lee (Additional comments)\nThe queue is still paused. Please call me.\n"
              }
            }
            """);

        var notes = RecordMapper.ActivityHistory(document.RootElement);

        Assert.Equal(2, notes.Count);
        Assert.Equal("work_notes", notes[0].Kind);
        Assert.Equal("Work note", notes[0].KindLabel);
        Assert.Equal("Casey Ng", notes[0].Author);
        Assert.Equal("2026-10-06 14:05:00", notes[0].CreatedDisplay);
        Assert.Equal("Replaced the fuser and the printer is back.", notes[0].Text);
        Assert.Equal("comments", notes[1].Kind);
        Assert.Equal("Customer comment", notes[1].KindLabel);
        Assert.True(notes[1].IsCustomer);
        Assert.Equal("Jordan Lee", notes[1].Author);
        Assert.Contains("paused", notes[1].Text);
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

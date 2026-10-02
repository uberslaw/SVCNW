using System.Net;
using System.Text;
using ServiceNowDesk.Client;
using ServiceNowDesk.Models;

namespace ServiceNowDesk.Tests;

internal sealed record CapturedCall(string Method, string PathAndQuery, string Body, string? Scheme, string? Parameter);

internal sealed class StubHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, string, HttpResponseMessage> _respond;
    private readonly object _gate = new();

    public StubHandler(Func<HttpRequestMessage, string, HttpResponseMessage> respond) => _respond = respond;

    public List<CapturedCall> Calls { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        lock (_gate)
        {
            Calls.Add(new CapturedCall(
                request.Method.Method,
                request.RequestUri?.PathAndQuery ?? "",
                body,
                request.Headers.Authorization?.Scheme,
                request.Headers.Authorization?.Parameter));
        }

        return _respond(request, body);
    }
}

internal static class Api
{
    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK, int? total = null)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        if (total is int count)
            response.Headers.TryAddWithoutValidation("X-Total-Count", count.ToString());
        return response;
    }

    public static ServiceNowSession BasicSession() => ServiceNowSession.FromSettings(new DeskSettings
    {
        InstanceUrl = "https://example.service-now.com/navpage.do",
        AuthMode = ServiceNowAuthMode.Basic,
        Username = "alex",
        Password = "secret"
    });

    public const string IncidentObject = """
        {
          "sys_id": {"value": "inc-printer", "display_value": "inc-printer"},
          "number": {"value": "INC0010001", "display_value": "INC0010001"},
          "short_description": {"value": "Printer jam", "display_value": "Printer jam"},
          "description": {"value": "Tray is stuck", "display_value": "Tray is stuck"},
          "state": {"value": "2", "display_value": "In Progress"},
          "priority": {"value": "3", "display_value": "3 - Moderate"},
          "impact": {"value": "3", "display_value": "3 - Low"},
          "urgency": {"value": "2", "display_value": "2 - Medium"},
          "category": {"value": "hardware", "display_value": "Hardware"},
          "subcategory": {"value": "printer", "display_value": "Printer"},
          "contact_type": {"value": "phone", "display_value": "Phone"},
          "caller_id": {"value": "user-jordan", "display_value": "Jordan Lee"},
          "assigned_to": {"value": "sample-user", "display_value": "Alex Rivera"},
          "assignment_group": {"value": "group-cs", "display_value": "Client Services"},
          "opened_at": {"value": "2026-09-28 09:15:00", "display_value": "2026-09-28 09:15"},
          "sys_updated_on": {"value": "2026-09-28 10:40:00", "display_value": "2026-09-28 10:40"},
          "active": {"value": "true", "display_value": "true"},
          "close_code": {"value": "", "display_value": ""},
          "close_notes": {"value": "", "display_value": ""},
          "hold_reason": {"value": "", "display_value": ""}
        }
        """;

    public static string IncidentList(int total = 1) => "{\"result\":[" + IncidentObject + "]}";
}

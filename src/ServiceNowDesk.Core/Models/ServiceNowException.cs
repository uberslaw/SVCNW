using System.Text.Json;
using ServiceNowDesk.Mapping;

namespace ServiceNowDesk.Models;

public sealed class ServiceNowException : Exception
{
    public ServiceNowException(int statusCode, string message, string? detail)
        : base(message)
    {
        StatusCode = statusCode;
        Detail = detail;
    }

    public const string QueryTooLongMessage = "The ServiceNow query was too long.";

    public int StatusCode { get; }
    public string? Detail { get; }

    public static bool IsQueryTooLong(string? text) =>
        !string.IsNullOrWhiteSpace(text)
        && (text.Contains("pagination header", StringComparison.OrdinalIgnoreCase)
            || text.Contains("query is too long", StringComparison.OrdinalIgnoreCase)
            || text.Contains("sysparm_suppress_pagination_header", StringComparison.OrdinalIgnoreCase));

    public static string ShortQueryMessage(ServiceNowException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (IsQueryTooLong(exception.Message) || IsQueryTooLong(exception.Detail))
            return QueryTooLongMessage;
        var text = exception.Message ?? "";
        return text.Length > 180 ? text[..180].TrimEnd() + "…" : text;
    }

    public static ServiceNowException FromResponse(int statusCode, string? body, string? fallback = null)
    {
        if (string.IsNullOrWhiteSpace(body))
            return new ServiceNowException(statusCode, fallback ?? DefaultForStatus(statusCode), null);

        var trimmed = body.TrimStart();
        if (trimmed.StartsWith('<'))
        {
            return new ServiceNowException(
                statusCode,
                "The instance returned a web page instead of API data. Check the URL, and confirm this user can call the ServiceNow Table API.",
                null);
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            string? message = null;
            string? detail = null;
            if (root.TryGetProperty("error_description", out var descriptionElement))
                detail = SnowField.AsString(descriptionElement);

            if (root.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.Object)
                {
                    if (error.TryGetProperty("message", out var messageElement))
                        message = SnowField.AsString(messageElement);
                    if (error.TryGetProperty("detail", out var detailElement) && string.IsNullOrWhiteSpace(detail))
                        detail = SnowField.AsString(detailElement);
                }
                else if (string.IsNullOrWhiteSpace(message))
                {
                    message = SnowField.AsString(error);
                }
            }

            var text = FirstNonEmpty(detail, message);
            if (IsQueryTooLong(text))
                return new ServiceNowException(statusCode, QueryTooLongMessage, text);

            if (string.IsNullOrWhiteSpace(text))
                text = fallback ?? DefaultForStatus(statusCode);
            else
                text = "ServiceNow: " + text;

            return new ServiceNowException(statusCode, text, string.IsNullOrWhiteSpace(detail) ? message : detail);
        }
        catch (JsonException)
        {
            var snippet = body.Length > 240 ? body[..240] : body;
            return new ServiceNowException(statusCode, fallback ?? DefaultForStatus(statusCode), snippet);
        }
    }

    public static string DefaultForStatus(int statusCode) => statusCode switch
    {
        401 => "ServiceNow rejected the sign-in. Check the user name, password, and OAuth client.",
        403 => "This account does not have permission to do that in ServiceNow.",
        404 => "ServiceNow could not find that record.",
        0 => "Could not reach the ServiceNow instance.",
        _ => $"ServiceNow returned status {statusCode}."
    };

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }
}

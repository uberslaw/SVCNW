using ServiceNowDesk.Models;

namespace ServiceNowDesk.Alerts;

/// <summary>
/// Decides when the desktop widget may appear, when a jiggle is due, and which portions caused it.
/// </summary>
public static class AlertJiggleRules
{
    /// <summary>
    /// WhileOpen allows the strip, the hover drop, and the interval drop whether the desk is open or minimized.
    /// OnlyMinimized allows them only while the main window is minimized.
    /// </summary>
    public static bool AllowsDesktopWidget(DesktopWidgetWhen when, bool mainWindowMinimized) =>
        when == DesktopWidgetWhen.WhileOpen || mainWindowMinimized;

    /// <summary>
    /// Persistent mode is due on the interval while any count is still unacknowledged.
    /// The causes are those unacknowledged kinds, even when the counts did not change.
    /// New-until-acknowledged mode ignores the interval.
    /// </summary>
    public static AlertJiggleDecision IntervalDue(JiggleWhen when, IEnumerable<AlertKind> unacknowledged)
    {
        ArgumentNullException.ThrowIfNull(unacknowledged);
        if (when != JiggleWhen.Persistent)
            return AlertJiggleDecision.NotDue;

        var pending = unacknowledged as IReadOnlySet<AlertKind> ?? unacknowledged.ToHashSet();
        var causes = AlertCatalog.All.Where(pending.Contains).ToArray();
        return causes.Length == 0 ? AlertJiggleDecision.NotDue : new AlertJiggleDecision(causes);
    }

    /// <summary>
    /// New-until-acknowledged mode is due when a category count increases.
    /// It is not due again for that same increase. Persistent mode does not drop from the increase itself.
    /// </summary>
    public static AlertJiggleDecision IncreaseDue(JiggleWhen when, AlertPollDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        if (when != JiggleWhen.NewUntilAcknowledged || !decision.HasIncrease)
            return AlertJiggleDecision.NotDue;

        return new AlertJiggleDecision(decision.Increased.ToArray());
    }

    /// <summary>
    /// The highlight is exactly the kinds that caused the current jiggle.
    /// A hover drop with no active jiggle passes <see cref="AlertJiggleDecision.NotDue"/> and highlights nothing.
    /// </summary>
    public static IReadOnlyList<AlertKind> HighlightedKinds(AlertJiggleDecision jiggle)
    {
        ArgumentNullException.ThrowIfNull(jiggle);
        return jiggle.Due ? jiggle.Causes : [];
    }
}

public sealed class AlertJiggleDecision
{
    public static AlertJiggleDecision NotDue { get; } = new([]);

    public AlertJiggleDecision(IReadOnlyList<AlertKind> causes)
    {
        Causes = causes ?? [];
    }

    public IReadOnlyList<AlertKind> Causes { get; }

    public bool Due => Causes.Count > 0;
}

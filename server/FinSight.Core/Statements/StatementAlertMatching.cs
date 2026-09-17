using FinSight.Core.Domain;

namespace FinSight.Core.Statements;

/// <summary>
/// Decides whether an imported statement is the one a statement alert announced. Banks email the alert shortly after
/// the statement closes, so the alert's received date must fall between a few days before and a few weeks after the
/// statement's period end, for the same institution and (when both are known) the same account. Imports that don't name their
/// institution (a CSV without a known layout, an OFX file without an FI element) never match.
/// </summary>
public static class StatementAlertMatching
{
    public const int DaysBeforePeriodEnd = 5;
    public const int DaysAfterPeriodEnd = 25;

    public static bool Matches(Statement alert, Statement imported)
    {
        if (alert.ReceivedAt is not { } received || imported.PeriodEnd is not { } periodEnd
            || string.IsNullOrWhiteSpace(alert.Institution) || string.IsNullOrWhiteSpace(imported.Institution)
            || KnownInstitutions.NormalizeName(alert.Institution) != KnownInstitutions.NormalizeName(imported.Institution))
        {
            return false;
        }

        // An import that names neither its account's last digits nor its type (a bare CSV, say) can't be told apart from the
        // bank's other accounts, so it never clears an alert on its own.
        if (imported.AccountMask is null && imported.AccountType == AccountType.Unknown)
        {
            return false;
        }

        if (alert.AccountMask is not null && imported.AccountMask is not null && alert.AccountMask != imported.AccountMask)
        {
            return false;
        }

        if (alert.AccountType != AccountType.Unknown && imported.AccountType != AccountType.Unknown && alert.AccountType != imported.AccountType)
        {
            return false;
        }

        var day = DateOnly.FromDateTime(received.UtcDateTime);
        return day >= periodEnd.AddDays(-DaysBeforePeriodEnd) && day <= periodEnd.AddDays(DaysAfterPeriodEnd);
    }

    /// <summary>The alert closest in time to the statement's period end among those that match, or null.</summary>
    public static Statement? Closest(IEnumerable<Statement> alerts, Statement imported) =>
        alerts.Where(a => Matches(a, imported))
            .OrderBy(a => Math.Abs(DateOnly.FromDateTime(a.ReceivedAt!.Value.UtcDateTime).DayNumber - imported.PeriodEnd!.Value.DayNumber))
            .ThenBy(a => a.ReceivedAt)
            .FirstOrDefault();
}

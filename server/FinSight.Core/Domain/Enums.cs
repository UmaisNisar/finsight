namespace FinSight.Core.Domain;

public enum StatementStatus
{
    Discovered,
    Downloading,
    Processing,
    Processed,
    Failed,

    /// <summary>An email said a statement is ready but attached no PDF. The user downloads it from the bank and uploads it.</summary>
    AwaitingUpload,

    /// <summary>A statement alert the user dismissed, or one an upload fulfilled. Hidden, and kept so a rescan never recreates it.</summary>
    Dismissed,
}

public enum StatementSourceKind
{
    Gmail,
    ManualUpload,

    /// <summary>Synthetic data generated for demo mode. No real document exists.</summary>
    Demo,
}

public enum DocumentKind
{
    Unknown,
    BankStatement,
    CreditCardStatement,
    IncomeDocument,
}

public enum AccountType
{
    Unknown,
    Chequing,
    Savings,
    CreditCard,
    LineOfCredit,
    Investment,
}

/// <summary>
/// How a transaction affects the user's finances. Transfers move money between the user's
/// own accounts (or pay down their own credit card) and are never income or spending.
/// </summary>
public enum TransactionType
{
    Income,
    Expense,
    Transfer,
}

public enum CategorySource
{
    Default,
    Rule,
    Ai,
    User,
}

public enum GmailConnectionStatus
{
    Active,

    /// <summary>Google rejected the refresh token (revoked, expired or password changed).</summary>
    Expired,
}

public enum JobKind
{
    Sync,
    Process,
    Upload,
}

public enum JobStatus
{
    Queued,
    Running,
    Succeeded,
    Failed,
}

public enum StepStatus
{
    Pending,
    Running,
    Done,
    Failed,
    Skipped,
}

public enum ThemePreference
{
    System,
    Light,
    Dark,
}

namespace FinSight.Infrastructure.Pipeline;

/// <summary>Stable failure codes with the human-readable message shown in the UI. Never exception text.</summary>
public static class StatementFailure
{
    public const string GmailAuthExpired = "gmail_auth_expired";
    public const string GmailNotConnected = "gmail_not_connected";
    public const string AttachmentMissing = "attachment_missing";
    public const string DownloadFailed = "download_failed";
    public const string PasswordProtected = "pdf_password_protected";
    public const string PasswordIncorrect = "pdf_password_incorrect";
    public const string UnsupportedFile = "unsupported_file";
    public const string CsvUnrecognized = "csv_unrecognized";
    public const string OfxUnreadable = "ofx_unreadable";
    public const string MultipleAccounts = "multiple_accounts";
    public const string FileTooLarge = "file_too_large";
    public const string Unreadable = "pdf_unreadable";
    public const string NoTextLayer = "pdf_no_text";
    public const string NoTransactions = "no_transactions_found";
    public const string UploadRequired = "upload_required";
    public const string Unexpected = "unexpected";

    public static string Message(string? code) => code switch
    {
        GmailAuthExpired => "Gmail connection expired. Reconnect your account.",
        GmailNotConnected => "Gmail isn't connected. Connect it to download this statement.",
        AttachmentMissing => "This email or its attachment is no longer in Gmail.",
        DownloadFailed => "The statement couldn't be downloaded from Gmail. Try again in a moment.",
        PasswordProtected => "This PDF is password-protected. Enter its password to unlock it.",
        PasswordIncorrect => "That password didn't unlock this PDF. Check it and try again.",
        UnsupportedFile => "This isn't a file FinSight can read. Upload a PDF, CSV, OFX or QFX statement from your bank.",
        CsvUnrecognized => "FinSight couldn't tell which columns in this CSV hold the dates, descriptions and amounts. Try your bank's OFX, QFX or PDF download instead.",
        OfxUnreadable => "This OFX or QFX file couldn't be read. Download it again from your bank, or try the CSV or PDF version.",
        MultipleAccounts => "This file has transactions from more than one account. Download each account separately and upload them one at a time.",
        FileTooLarge => "This file has too many transactions to read at once. Download a shorter date range.",
        Unreadable => "This file couldn't be opened as a PDF.",
        NoTextLayer => "This statement appears to be a scanned image, so its text couldn't be read. Try a text-based PDF from your bank's website.",
        NoTransactions => "We couldn't extract transactions from this statement. Try reprocessing or upload the statement manually.",
        UploadRequired => "Upload the file again to reprocess this statement. FinSight doesn't keep copies of your files.",
        null => string.Empty,
        _ => "Something went wrong while processing this statement. Try again.",
    };
}

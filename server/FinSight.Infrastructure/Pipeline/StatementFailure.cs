namespace FinSight.Infrastructure.Pipeline;

/// <summary>Stable failure codes with the human-readable message shown in the UI. Never exception text.</summary>
public static class StatementFailure
{
    public const string GmailAuthExpired = "gmail_auth_expired";
    public const string GmailNotConnected = "gmail_not_connected";
    public const string AttachmentMissing = "attachment_missing";
    public const string DownloadFailed = "download_failed";
    public const string PasswordProtected = "pdf_password_protected";
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
        PasswordProtected => "This PDF is password protected. Upload an unlocked copy instead.",
        Unreadable => "This file couldn't be opened as a PDF.",
        NoTextLayer => "This statement appears to be a scanned image, so its text couldn't be read. Try a text-based PDF from your bank's website.",
        NoTransactions => "We couldn't extract transactions from this statement. Try reprocessing or upload the statement manually.",
        UploadRequired => "Upload the PDF again to reprocess this statement. FinSight doesn't keep copies of your files.",
        null => string.Empty,
        _ => "Something went wrong while processing this statement. Try again.",
    };
}

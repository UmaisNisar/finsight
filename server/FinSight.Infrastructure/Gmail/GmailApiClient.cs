using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using FinSight.Core.Statements;

namespace FinSight.Infrastructure.Gmail;

public interface IGmailClient
{
    Task<IReadOnlyList<string>> SearchMessageIdsAsync(string accessToken, string query, int max, CancellationToken cancellationToken);

    Task<EmailCandidate> GetMessageAsync(string accessToken, string messageId, CancellationToken cancellationToken);

    Task<byte[]> DownloadAttachmentAsync(string accessToken, string messageId, string partId, CancellationToken cancellationToken);
}

/// <summary>
/// Minimal Gmail REST client using the read-only scope. Requests only the fields needed to classify
/// a message (headers, snippet and attachment metadata); message bodies are never downloaded.
/// </summary>
public sealed class GmailApiClient(HttpClient http) : IGmailClient
{
    private const string Base = "https://gmail.googleapis.com/gmail/v1/users/me";
    private const string PartFields = "partId,mimeType,filename,body(size,attachmentId)";

    private static readonly string MessageFields =
        $"id,threadId,snippet,internalDate,payload(headers,{PartFields},parts({PartFields},parts({PartFields},parts({PartFields},parts({PartFields})))))";

    public async Task<IReadOnlyList<string>> SearchMessageIdsAsync(string accessToken, string query, int max, CancellationToken cancellationToken)
    {
        var ids = new List<string>();
        string? pageToken = null;

        do
        {
            var url = $"{Base}/messages?q={Uri.EscapeDataString(query)}&maxResults={Math.Min(100, max)}"
                + (pageToken is null ? string.Empty : $"&pageToken={Uri.EscapeDataString(pageToken)}");
            var page = await SendAsync<MessageList>(accessToken, url, cancellationToken);
            ids.AddRange(page.Messages?.Select(m => m.Id) ?? []);
            pageToken = page.NextPageToken;
        }
        while (pageToken is not null && ids.Count < max);

        return ids.Take(max).ToList();
    }

    public async Task<EmailCandidate> GetMessageAsync(string accessToken, string messageId, CancellationToken cancellationToken)
    {
        var message = await GetRawMessageAsync(accessToken, messageId, cancellationToken);
        var headers = message.Payload?.Headers ?? [];
        string Header(string name) => headers.FirstOrDefault(h => string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase))?.Value ?? string.Empty;

        var received = long.TryParse(message.InternalDate, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms)
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
            : DateTimeOffset.MinValue;

        var attachments = Flatten(message.Payload)
            .Where(p => !string.IsNullOrEmpty(p.Filename) && p.PartId is not null)
            .Select(p => new EmailAttachment(p.PartId!, p.Filename!, p.MimeType ?? "application/octet-stream", p.Body?.Size ?? 0))
            .ToList();

        return new EmailCandidate(message.Id, message.ThreadId, Header("Subject"), Header("From"),
            WebUtility.HtmlDecode(message.Snippet ?? string.Empty), received, attachments);
    }

    public async Task<byte[]> DownloadAttachmentAsync(string accessToken, string messageId, string partId, CancellationToken cancellationToken)
    {
        // Attachment ids are not stable between calls, so resolve the part again right before downloading.
        var message = await GetRawMessageAsync(accessToken, messageId, cancellationToken);
        var part = Flatten(message.Payload).FirstOrDefault(p => p.PartId == partId)
            ?? throw new FileNotFoundException("The attachment no longer exists in this email.");

        if (part.Body?.AttachmentId is null)
        {
            throw new FileNotFoundException("The attachment has no downloadable content.");
        }

        var attachment = await SendAsync<AttachmentBody>(accessToken,
            $"{Base}/messages/{Uri.EscapeDataString(messageId)}/attachments/{Uri.EscapeDataString(part.Body.AttachmentId)}", cancellationToken);

        return DecodeBase64Url(attachment.Data ?? string.Empty);
    }

    private Task<Message> GetRawMessageAsync(string accessToken, string messageId, CancellationToken cancellationToken) =>
        SendAsync<Message>(accessToken, $"{Base}/messages/{Uri.EscapeDataString(messageId)}?format=full&fields={Uri.EscapeDataString(MessageFields)}", cancellationToken);

    private async Task<T> SendAsync<T>(string accessToken, string url, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var response = await http.SendAsync(request, cancellationToken);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new GmailAuthExpiredException();
            }

            if ((response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500) && attempt < 4)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(400 * Math.Pow(2, attempt)), cancellationToken);
                continue;
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                throw new FileNotFoundException("The email no longer exists in Gmail.");
            }

            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<T>(cancellationToken)
                ?? throw new HttpRequestException("Empty response from Gmail.");
        }
    }

    private static IEnumerable<MessagePart> Flatten(MessagePart? part)
    {
        if (part is null)
        {
            yield break;
        }

        yield return part;
        foreach (var child in part.Parts ?? [])
        {
            foreach (var nested in Flatten(child))
            {
                yield return nested;
            }
        }
    }

    internal static byte[] DecodeBase64Url(string data)
    {
        var base64 = data.Replace('-', '+').Replace('_', '/');
        base64 = base64.PadRight(base64.Length + ((4 - (base64.Length % 4)) % 4), '=');
        return Convert.FromBase64String(base64);
    }

    private sealed record MessageList(
        [property: JsonPropertyName("messages")] List<MessageRef>? Messages,
        [property: JsonPropertyName("nextPageToken")] string? NextPageToken);

    private sealed record MessageRef([property: JsonPropertyName("id")] string Id);

    private sealed record Message(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("threadId")] string? ThreadId,
        [property: JsonPropertyName("snippet")] string? Snippet,
        [property: JsonPropertyName("internalDate")] string? InternalDate,
        [property: JsonPropertyName("payload")] MessagePart? Payload);

    private sealed record MessagePart(
        [property: JsonPropertyName("partId")] string? PartId,
        [property: JsonPropertyName("mimeType")] string? MimeType,
        [property: JsonPropertyName("filename")] string? Filename,
        [property: JsonPropertyName("headers")] List<Header>? Headers,
        [property: JsonPropertyName("body")] PartBody? Body,
        [property: JsonPropertyName("parts")] List<MessagePart>? Parts);

    private sealed record Header([property: JsonPropertyName("name")] string Name, [property: JsonPropertyName("value")] string Value);

    private sealed record PartBody([property: JsonPropertyName("size")] long Size, [property: JsonPropertyName("attachmentId")] string? AttachmentId);

    private sealed record AttachmentBody([property: JsonPropertyName("data")] string? Data);
}

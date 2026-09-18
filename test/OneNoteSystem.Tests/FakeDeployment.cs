using System.Net;
using System.Text;
using System.Text.Json;

namespace OneNoteSystem.Tests;

public sealed record RecordedRequest(string Method, string Path, string? Authorization, JsonElement Body);

/// <summary>
/// A stand-in for a OneNote System deployment. It reproduces the parts of /api/health,
/// /api/capture, /api/append, and /api/todo that the CLI depends on -- including the exact
/// status codes -- so the tests fail if either side drifts. Being an
/// <see cref="HttpMessageHandler"/> rather than a server, it needs no port and no firewall.
/// </summary>
public sealed class FakeDeployment : HttpMessageHandler
{
    public const string ValidKey = "ons_test_key";
    public const string Url = "https://deployment.test";

    public List<RecordedRequest> Requests { get; } = new();

    public RecordedRequest LastRequest => Requests[^1];

    public bool Healthy { get; init; } = true;
    public bool HasDefaultSection { get; init; } = true;
    public IReadOnlyList<string> KnownPages { get; init; } = new[] { "Quick Inbox" };
    public IReadOnlyList<string> DuplicateTitles { get; init; } = Array.Empty<string>();

    /// <summary>To Do is optional: deployments whose Microsoft connection lacks Tasks.ReadWrite do not serve these routes.</summary>
    public bool Todo { get; init; }

    /// <summary>
    /// Impersonate some other site at this address: "html404" is what an unrelated web app
    /// does with /api/health, and any other string is a JSON service that answers but
    /// identifies itself as something else.
    /// </summary>
    public string? Impersonate { get; init; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.PathAndQuery;
        var bodyText = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        var body = bodyText.Length > 0 ? JsonDocument.Parse(bodyText).RootElement.Clone() : default;
        Requests.Add(new RecordedRequest(
            request.Method.Method, path, request.Headers.Authorization?.ToString(), body));

        if (Impersonate == "html404")
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("<!DOCTYPE html><html><body>Not found</body></html>", Encoding.UTF8, "text/html"),
            };
        }

        if (path == "/api/health")
        {
            if (Impersonate is not null) return Json(HttpStatusCode.OK, new { ok = true, service = Impersonate });
            return Healthy
                ? Json(HttpStatusCode.OK, new { ok = true, service = "onenote-system" })
                : Json(HttpStatusCode.ServiceUnavailable, new { ok = false, error = "DATABASE_URL is not configured." });
        }

        // Every other route authenticates first, exactly as the deployment does.
        if (request.Headers.Authorization?.Parameter != ValidKey)
            return Json(HttpStatusCode.Unauthorized, new { error = "Use Authorization: Bearer YOUR_API_KEY." });

        if (path == "/api/capture")
        {
            if (!HasDefaultSection)
                return Json(HttpStatusCode.Conflict, new { error = "This account has no default OneNote section yet." });

            var title = Text(body, "title") is { Length: > 0 } t ? t : "Untitled capture";
            return Json(HttpStatusCode.Created, new
            {
                ok = true,
                page = new { id = "page-created", title, webUrl = "https://onenote.example/page-created" },
            });
        }

        if (path == "/api/append")
        {
            var content = Text(body, "content") ?? "";
            if (content.Trim().Length == 0)
                return Json(HttpStatusCode.BadRequest, new { error = "content is required." });

            var pageId = Text(body, "pageId");
            if (pageId is not null)
                return Json(HttpStatusCode.OK, new { ok = true, page = new { id = pageId, title = "By id" } });

            var pageTitle = Text(body, "pageTitle") ?? "";
            if (DuplicateTitles.Contains(pageTitle))
            {
                return Json(HttpStatusCode.Conflict, new
                {
                    error = $"More than one page is titled \"{pageTitle}\". Use pageId instead.",
                });
            }
            if (!KnownPages.Contains(pageTitle))
                return Json(HttpStatusCode.NotFound, new { error = $"No page titled \"{pageTitle}\" was found." });

            return Json(HttpStatusCode.OK, new
            {
                ok = true,
                page = new { id = "page-1", title = pageTitle, webUrl = "https://onenote.example/page-1" },
            });
        }

        if (path.StartsWith("/api/todo", StringComparison.Ordinal))
        {
            if (!Todo) return Json(HttpStatusCode.NotFound, new { });

            if (path.StartsWith("/api/todo/lists", StringComparison.Ordinal))
            {
                return Json(HttpStatusCode.OK, new
                {
                    lists = new[]
                    {
                        new { name = "Tasks", isDefault = true },
                        new { name = "Errands", isDefault = false },
                    },
                });
            }

            if (path == "/api/todo/complete")
                return Json(HttpStatusCode.OK, new { ok = true, task = new { id = Text(body, "id"), title = "Renew the domain" } });

            if (request.Method == HttpMethod.Post)
            {
                return Json(HttpStatusCode.Created, new
                {
                    ok = true,
                    task = new
                    {
                        id = "task-1",
                        title = Text(body, "title"),
                        list = Text(body, "list") ?? "Tasks",
                        dueDateTime = Text(body, "dueDate"),
                    },
                });
            }

            return Json(HttpStatusCode.OK, new
            {
                list = "Tasks",
                tasks = new[]
                {
                    new { id = "task-1", title = "Renew the domain", completed = false, dueDateTime = "2026-09-15" },
                },
            });
        }

        return Json(HttpStatusCode.NotFound, new { error = $"HTTP 404 for {path}" });
    }

    private static string? Text(JsonElement body, string name) =>
        body.ValueKind == JsonValueKind.Object
        && body.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static HttpResponseMessage Json(HttpStatusCode status, object payload) => new(status)
    {
        Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
    };
}

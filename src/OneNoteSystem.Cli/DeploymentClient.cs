using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;

namespace OneNoteSystem.Cli;

/// <summary>
/// One HTTP client for every command: timeouts, JSON validation, the Bearer header the
/// deployment expects, and deployment errors mapped onto the CLI's exit codes.
///
/// This is the same request shape the Apple Shortcut sends, so anything that works here
/// works there and vice versa.
/// </summary>
public sealed class DeploymentClient : IDisposable
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    private static readonly JsonSerializerOptions BodyOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly string _apiKey;

    public DeploymentClient(string url, string apiKey, TimeSpan? timeout = null, HttpMessageHandler? handler = null)
    {
        Url = url;
        _apiKey = apiKey;
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _ownsHttp = true;
        _http.Timeout = timeout ?? DefaultTimeout;
    }

    public DeploymentClient(Settings settings, TimeSpan? timeout = null, HttpMessageHandler? handler = null)
        : this(settings.Url, settings.ApiKey, timeout, handler)
    {
    }

    public string Url { get; }

    public Task<JsonElement> GetAsync(string path, CancellationToken ct, bool authenticated = true, params HttpStatusCode[] tolerate) =>
        SendAsync(HttpMethod.Get, path, null, authenticated, tolerate, ct);

    public Task<JsonElement> PostAsync(string path, object body, CancellationToken ct, bool authenticated = true, params HttpStatusCode[] tolerate) =>
        SendAsync(HttpMethod.Post, path, body, authenticated, tolerate, ct);

    private async Task<JsonElement> SendAsync(
        HttpMethod method,
        string path,
        object? body,
        bool authenticated,
        IReadOnlyCollection<HttpStatusCode> tolerate,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, $"{Url}/api{path}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (authenticated) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        if (body is not null)
        {
            request.Content = new StringContent(
                JsonSerializer.Serialize(body, BodyOptions), Encoding.UTF8, "application/json");
        }

        HttpResponseMessage response;
        string text;
        try
        {
            response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new CliException(
                $"no response from {Url} after {_http.Timeout.TotalMilliseconds:0}ms", ExitCode.Network);
        }
        catch (HttpRequestException err)
        {
            // Never interpolate the key; only the URL and the transport reason.
            throw new CliException($"could not reach {Url}: {Reason(err)}", ExitCode.Network);
        }

        using (response)
        {
            JsonElement? parsed = null;
            if (text.Length > 0)
            {
                try
                {
                    using var document = JsonDocument.Parse(text);
                    parsed = document.RootElement.Clone();
                }
                catch (JsonException)
                {
                    parsed = null;
                }
            }

            var isObject = parsed is { ValueKind: JsonValueKind.Object };

            // Some responses carry their meaning in the body of a failure status --
            // /api/health answers 503 with the reason the database is unreachable, and
            // that reason is exactly what `doctor` exists to print.
            var tolerated = tolerate.Contains(response.StatusCode) && isObject;
            if (!response.IsSuccessStatusCode && !tolerated) throw HttpError(response.StatusCode, parsed);

            if (!isObject)
            {
                throw new CliException(
                    $"the deployment returned a non-JSON response (HTTP {(int)response.StatusCode})", ExitCode.Backend);
            }

            return parsed!.Value;
        }
    }

    // The deployment already writes plain-language errors ("No page titled ... was found"),
    // so pass its message through and only choose the exit code here.
    private CliException HttpError(HttpStatusCode status, JsonElement? parsed)
    {
        var message = $"HTTP {(int)status}";
        if (parsed is { ValueKind: JsonValueKind.Object } body
            && body.TryGetProperty("error", out var error)
            && error.ValueKind == JsonValueKind.String)
        {
            message = error.GetString() ?? message;
        }

        return status switch
        {
            HttpStatusCode.Unauthorized => new CliException(
                $"unauthorized: the deployment at {Url} rejected this API key", ExitCode.Auth),
            HttpStatusCode.NotFound => new CliException(message, ExitCode.NotFound),
            HttpStatusCode.Conflict => new CliException(message, ExitCode.Conflict),
            HttpStatusCode.BadRequest => new CliException(message, ExitCode.Usage),
            _ => new CliException(message, ExitCode.Backend),
        };
    }

    private static string Reason(Exception err)
    {
        for (var current = err; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case SocketException socket:
                    return socket.SocketErrorCode switch
                    {
                        SocketError.HostNotFound or SocketError.NoData => "host not found",
                        SocketError.ConnectionRefused => "connection refused",
                        SocketError.TimedOut => "connection timed out",
                        _ => socket.SocketErrorCode.ToString(),
                    };
                case AuthenticationException:
                    return "the TLS handshake failed (check the certificate)";
            }
        }

        return err.Message;
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mandrill.Core.Update;

/// <summary>What a manual update check found (architecture §16.3).</summary>
public enum UpdateCheckOutcome
{
    /// <summary>The latest release is not newer than this build (equal, or this build is newer).</summary>
    UpToDate,

    /// <summary>A newer release exists; see <see cref="UpdateCheckResult.Latest"/>.</summary>
    UpdateAvailable,

    /// <summary>No published release (404, or the latest is a draft or prerelease).</summary>
    NoRelease,

    /// <summary>GitHub refused the request for now (403, 429).</summary>
    RateLimited,

    /// <summary>GitHub could not be reached (DNS, connection, TLS, proxy).</summary>
    Offline,

    /// <summary>No answer within the timeout.</summary>
    TimedOut,

    /// <summary>Any other response or failure.</summary>
    Unexpected,
}

/// <summary>Result of <see cref="UpdateChecker.CheckAsync"/>.</summary>
/// <param name="Outcome">What the check found.</param>
/// <param name="Latest">Normalized latest version (no "v") when the release was read; otherwise null.</param>
public sealed record UpdateCheckResult(UpdateCheckOutcome Outcome, string? Latest = null);

/// <summary>
/// Manual update check (ADR-0016): the only network access in Mandrill. One read-only GET of the GitHub "latest release"
/// per call, sending only User-Agent and Accept; reads <c>tag_name</c>, <c>draft</c>, <c>prerelease</c>. The response body and
/// headers are never logged.
/// </summary>
public sealed partial class UpdateChecker
{
    private const int MaxBodyBytes = 1_048_576;
    private const int MaxVersionLength = 64;
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    private readonly ILogger _logger;
    private readonly HttpMessageHandler? _handler;
    private readonly TimeSpan _timeout;

    /// <summary>Creates the checker.</summary>
    /// <param name="loggerFactory">Logger factory; null disables logging.</param>
    /// <param name="handler">Transport (tests); null for the default handler with the system proxy. Not disposed here.</param>
    /// <param name="timeout">Maximum time for one check (default 10 s).</param>
    public UpdateChecker(ILoggerFactory? loggerFactory = null, HttpMessageHandler? handler = null, TimeSpan? timeout = null)
    {
        _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<UpdateChecker>();
        _handler = handler;
        _timeout = timeout ?? DefaultTimeout;
    }

    /// <summary>The GitHub API endpoint of the latest Mandrill release.</summary>
    public static Uri LatestReleaseApi { get; } = new("https://api.github.com/repos/whisperlip-boop/mandrill/releases/latest");

    /// <summary>The release page opened in the browser (a constant, never a URL from the response).</summary>
    public static Uri ReleasePage { get; } = new("https://github.com/whisperlip-boop/mandrill/releases/latest");

    /// <summary>Asks GitHub for the latest release and compares it with <paramref name="currentVersion"/>. Any thread.</summary>
    /// <param name="currentVersion">This build's version (e.g. "1.0.0").</param>
    /// <param name="ct">Cancels the check (the dialog closed).</param>
    /// <returns>The outcome; never throws except <see cref="OperationCanceledException"/>.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    public async Task<UpdateCheckResult> CheckAsync(string currentVersion, CancellationToken ct)
    {
        string? current = NormalizeVersion(currentVersion);
        if (current is null)
        {
            _logger.LogWarning("Update check skipped: current version {Version} is not parsable", Clip(currentVersion));
            return new UpdateCheckResult(UpdateCheckOutcome.Unexpected);
        }

        _logger.LogInformation("Update check: GET {Host}", LatestReleaseApi.Host);
        // Off the caller's (UI) thread: creating the client and resolving the system proxy (WPAD/PAC) can block for seconds.
        var result = await Task.Run(() => RequestAsync(current, ct), ct).ConfigureAwait(false);
        _logger.LogInformation("Update check result: {Outcome} current {Current} latest {Latest}", result.Outcome, current, result.Latest);
        return result;
    }

    /// <summary>Trims and strips a leading v/V; null unless the rest is a version (1–4 numeric parts, -pre, +build).</summary>
    /// <param name="v">Version text.</param>
    /// <returns>The normalized version, or null.</returns>
    internal static string? NormalizeVersion(string? v)
    {
        string? text = v?.Trim();
        if (text is ['v' or 'V', ..])
        {
            text = text[1..];
        }

        return text is { Length: <= MaxVersionLength } && VersionPattern().IsMatch(text) ? text : null;
    }

    /// <summary>
    /// Compares two versions: missing parts are 0, a prerelease is lower than its release, prereleases compare by SemVer 2.0
    /// §11, build metadata is ignored.
    /// </summary>
    /// <param name="a">First version.</param>
    /// <param name="b">Second version.</param>
    /// <returns>-1, 0 or 1; null when either is not a version or a part overflows.</returns>
    internal static int? CompareVersions(string a, string b)
    {
        if (Parse(a) is not { } x || Parse(b) is not { } y)
        {
            return null;
        }

        for (int i = 0; i < x.Parts.Length; i++)
        {
            if (x.Parts[i] != y.Parts[i])
            {
                return x.Parts[i].CompareTo(y.Parts[i]);
            }
        }

        return (x.Pre, y.Pre) switch
        {
            (null, null) => 0,
            (null, _) => 1,
            (_, null) => -1,
            _ => ComparePrerelease(x.Pre, y.Pre),
        };
    }

    private async Task<UpdateCheckResult> RequestAsync(string current, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_timeout);
        try
        {
            // No cookies and no trace headers by construction: only User-Agent and Accept leave the machine (ADR-0016).
            using var client = _handler is null
                ? new HttpClient(new SocketsHttpHandler { UseCookies = false, ActivityHeadersPropagator = null })
                : new HttpClient(_handler, disposeHandler: false);
            client.Timeout = Timeout.InfiniteTimeSpan; // the linked token is the only timeout, so it maps to TimedOut
            client.MaxResponseContentBufferSize = MaxBodyBytes;
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Mandrill", current));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false);
            switch (response.StatusCode)
            {
                case HttpStatusCode.NotFound:
                    return new UpdateCheckResult(UpdateCheckOutcome.NoRelease);
                case HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests:
                    _logger.LogWarning("Update check: HTTP {Status}", (int)response.StatusCode);
                    return new UpdateCheckResult(UpdateCheckOutcome.RateLimited);
                case not HttpStatusCode.OK:
                    _logger.LogWarning("Update check: HTTP {Status}", (int)response.StatusCode);
                    return new UpdateCheckResult(UpdateCheckOutcome.Unexpected);
            }

            byte[] body = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
            return Evaluate(body, current);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogDebug("Update check cancelled");
            throw;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            _logger.LogWarning("Update check timed out after {Seconds} s", _timeout.TotalSeconds);
            return new UpdateCheckResult(UpdateCheckOutcome.TimedOut);
        }
        catch (HttpRequestException ex) when (ex.HttpRequestError == HttpRequestError.ConfigurationLimitExceeded)
        {
            _logger.LogWarning("Update check: response larger than {Limit} bytes", MaxBodyBytes);
            return new UpdateCheckResult(UpdateCheckOutcome.Unexpected);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning("Update check: request failed ({Error})", ex.HttpRequestError);
            return new UpdateCheckResult(UpdateCheckOutcome.Offline);
        }
        catch (JsonException)
        {
            _logger.LogWarning("Update check: response is not valid JSON");
            return new UpdateCheckResult(UpdateCheckOutcome.Unexpected);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Update check failed unexpectedly");
            return new UpdateCheckResult(UpdateCheckOutcome.Unexpected);
        }
    }

    private UpdateCheckResult Evaluate(byte[] body, string current)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            _logger.LogWarning("Update check: response is not a JSON object");
            return new UpdateCheckResult(UpdateCheckOutcome.Unexpected);
        }

        if (IsTrue(root, "draft") || IsTrue(root, "prerelease"))
        {
            _logger.LogWarning("Update check: the latest release is a draft or prerelease");
            return new UpdateCheckResult(UpdateCheckOutcome.NoRelease);
        }

        string? tag = root.TryGetProperty("tag_name", out var element) && element.ValueKind == JsonValueKind.String ? element.GetString() : null;
        string? latest = NormalizeVersion(tag);
        if (latest is null || CompareVersions(latest, current) is not { } order)
        {
            _logger.LogWarning("Update check: tag {Tag} is not a version", Clip(tag));
            return new UpdateCheckResult(UpdateCheckOutcome.Unexpected);
        }

        if (order < 0)
        {
            _logger.LogInformation("Update check: local {Current} is newer than {Latest}", current, latest);
        }

        return new UpdateCheckResult(order > 0 ? UpdateCheckOutcome.UpdateAvailable : UpdateCheckOutcome.UpToDate, latest);
    }

    private static bool IsTrue(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    // Server-supplied text goes to the log: at most 64 characters, no control, format (bidi) or line/paragraph separators.
    private static string? Clip(string? text) => text is null ? null : new string([.. text.Where(c => !char.IsControl(c)
        && CharUnicodeInfo.GetUnicodeCategory(c) is not (UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)).Take(MaxVersionLength)]);

    private static (int[] Parts, string[]? Pre)? Parse(string version)
    {
        if (NormalizeVersion(version) is not { } text)
        {
            return null;
        }

        int plus = text.IndexOf('+', StringComparison.Ordinal);
        string core = plus >= 0 ? text[..plus] : text;
        int dash = core.IndexOf('-', StringComparison.Ordinal);
        string[]? pre = dash >= 0 ? core[(dash + 1)..].Split('.') : null;
        if (pre is not null && pre.Any(p => p.Length == 0))
        {
            return null;
        }

        string[] numbers = (dash >= 0 ? core[..dash] : core).Split('.');
        int[] parts = new int[4];
        for (int i = 0; i < numbers.Length; i++)
        {
            if (!int.TryParse(numbers[i], NumberStyles.None, CultureInfo.InvariantCulture, out parts[i]))
            {
                return null;
            }
        }

        return (parts, pre);
    }

    // SemVer 2.0 §11.4: numeric identifiers compare numerically and rank below alphanumeric ones; a shorter set ranks lower.
    private static int ComparePrerelease(string[] a, string[] b)
    {
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            bool aNumeric = a[i].All(char.IsAsciiDigit);
            bool bNumeric = b[i].All(char.IsAsciiDigit);
            int order = (aNumeric, bNumeric) switch
            {
                (true, true) => CompareDigits(a[i], b[i]),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.CompareOrdinal(a[i], b[i]),
            };
            if (order != 0)
            {
                return Math.Sign(order);
            }
        }

        return a.Length.CompareTo(b.Length);
    }

    // Digit strings of any length (no int overflow): longer is larger once leading zeros are gone.
    private static int CompareDigits(string a, string b)
    {
        string x = a.TrimStart('0');
        string y = b.TrimStart('0');
        return x.Length != y.Length ? x.Length.CompareTo(y.Length) : string.CompareOrdinal(x, y);
    }

    [GeneratedRegex(@"^[0-9]+(\.[0-9]+){0,3}(-[0-9A-Za-z.-]+)?(\+[0-9A-Za-z.-]+)?\z", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();
}

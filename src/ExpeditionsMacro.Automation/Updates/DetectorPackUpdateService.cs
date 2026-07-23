using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using ExpeditionsMacro.Core.Models;
using ExpeditionsMacro.Vision.Packs;

namespace ExpeditionsMacro.Automation.Updates;

public sealed record DetectorPackUpdate(
    string PackId,
    Version Version,
    Uri DownloadUrl,
    Uri ReleaseUrl,
    DateTimeOffset PublishedAt,
    string AssetName);

public sealed partial class DetectorPackUpdateService : IDisposable
{
    private const string ReleasesApi = "https://api.github.com/repos/Bluffy420/Expedition-Macro/releases?per_page=30";
    private readonly DetectorPackRepository _repository;
    private readonly HttpClient _httpClient;

    public DetectorPackUpdateService(DetectorPackRepository repository)
    {
        _repository = repository;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("ExpeditionsMacro/1.0");
        _httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public async Task<DetectorPackUpdate?> CheckAsync(
        string packId,
        Version installedVersion,
        CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await _httpClient.GetAsync(ReleasesApi, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        JsonElement releases = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken).ConfigureAwait(false);
        List<DetectorPackUpdate> updates = [];
        foreach (JsonElement release in releases.EnumerateArray())
        {
            if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean()) continue;
            Uri releaseUrl = new(release.GetProperty("html_url").GetString()!);
            DateTimeOffset published = release.TryGetProperty("published_at", out JsonElement publishedValue) && publishedValue.ValueKind == JsonValueKind.String
                ? publishedValue.GetDateTimeOffset()
                : DateTimeOffset.MinValue;
            foreach (JsonElement asset in release.GetProperty("assets").EnumerateArray())
            {
                string name = asset.GetProperty("name").GetString() ?? string.Empty;
                Match match = DetectorAssetName().Match(name);
                if (!match.Success || !match.Groups["pack"].Value.Equals(packId, StringComparison.OrdinalIgnoreCase)) continue;
                if (!Version.TryParse(match.Groups["version"].Value, out Version? version) || version <= installedVersion) continue;
                updates.Add(new DetectorPackUpdate(packId, version, new Uri(asset.GetProperty("browser_download_url").GetString()!), releaseUrl, published, name));
            }
        }
        return updates.OrderByDescending(update => update.Version).FirstOrDefault();
    }

    public async Task InstallAsync(DetectorPackUpdate update, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await _httpClient.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        long? total = response.Content.Headers.ContentLength;
        await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using MemoryStream package = new();
        byte[] buffer = new byte[64 * 1024];
        long readTotal = 0;
        while (true)
        {
            int read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            await package.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            readTotal += read;
            if (total is > 0) progress?.Report((double)readTotal / total.Value);
        }
        package.Position = 0;
        await _repository.InstallAsync(package, cancellationToken).ConfigureAwait(false);
        progress?.Report(1);
    }

    public void Dispose() => _httpClient.Dispose();

    [GeneratedRegex("^(?<pack>[a-z0-9-]+)-(?<version>[0-9]+(?:\\.[0-9]+){1,3})\\.zip$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DetectorAssetName();
}

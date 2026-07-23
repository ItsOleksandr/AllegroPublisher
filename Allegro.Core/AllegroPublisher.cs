using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Allegro.Core;

/// <summary>
/// Talks to the official Allegro REST API (https://developer.allegro.pl).
/// Authenticates a single seller account via the OAuth2 <b>device flow</b> and
/// publishes the generated <c>products.csv</c> by updating price and stock on
/// existing offers. Offers are matched by <c>external.id == EAN</c> — the standard
/// way Allegro keys offers to an external inventory system. Products with no
/// matching offer are skipped (creating new offers is out of scope).
///
/// This lives in Allegro.Core so both the admin web app and the console app can use it.
/// Settings/tokens are persisted through <see cref="SaverExtensions.AllegroSettings"/>.
/// </summary>
public class AllegroPublisher
{
    private const string AuthBase = "https://allegro.pl";
    private const string ApiBase = "https://api.allegro.pl";
    private const string ApiMediaType = "application/vnd.allegro.public.v1+json";
    private const string DeviceGrantType = "urn:ietf:params:oauth:grant-type:device_code";

    private readonly HttpClient _http;

    public AllegroPublisher(HttpClient? http = null)
    {
        _http = http ?? new HttpClient();
    }

    public AllegroSettings Settings => SaverExtensions.AllegroSettings.Value;

    /// <summary>Persist client id / secret / currency edited elsewhere.</summary>
    public void SaveSettings() => SaverExtensions.AllegroSettings.Write();

    // ---------------------------------------------------------------- device flow

    public record DeviceAuthorization(string UserCode, string VerificationUri, string DeviceCode, int Interval, int ExpiresIn);

    /// <summary>
    /// Requests a device/user code from Allegro. The caller shows
    /// <see cref="DeviceAuthorization.UserCode"/> + <see cref="DeviceAuthorization.VerificationUri"/>
    /// to the user, then calls <see cref="PollForTokenAsync"/>.
    /// </summary>
    public async Task<DeviceAuthorization> StartDeviceFlowAsync(Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(Settings.ClientId) || string.IsNullOrWhiteSpace(Settings.ClientSecret))
        {
            throw new InvalidOperationException("Set the Client ID and Client Secret first, then save.");
        }

        var request = new HttpRequestMessage(HttpMethod.Post, $"{AuthBase}/auth/oauth/device")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = Settings.ClientId,
            }),
        };
        AddBasicAuth(request);

        var response = await _http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Device authorization failed ({(int)response.StatusCode}): {body}");
        }

        var device = JsonSerializer.Deserialize<DeviceCodeResponse>(body)
                     ?? throw new InvalidOperationException("Empty device authorization response.");

        var uri = device.VerificationUriComplete ?? device.VerificationUri ?? $"{AuthBase}/device";
        log?.Invoke($"Open {uri} and confirm code {device.UserCode}.");

        return new DeviceAuthorization(device.UserCode, uri, device.DeviceCode, device.Interval, device.ExpiresIn);
    }

    /// <summary>Polls the token endpoint until the user approves, is denied, or it times out. Returns true on success.</summary>
    public async Task<bool> PollForTokenAsync(DeviceAuthorization auth, Action<string>? log = null)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(auth.Interval, 5));
        var deadline = DateTime.UtcNow.AddSeconds(auth.ExpiresIn > 0 ? auth.ExpiresIn : 600);

        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(interval);

            var request = new HttpRequestMessage(HttpMethod.Post, $"{AuthBase}/auth/oauth/token")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = DeviceGrantType,
                    ["device_code"] = auth.DeviceCode,
                }),
            };
            AddBasicAuth(request);

            var response = await _http.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                StoreToken(JsonSerializer.Deserialize<TokenResponse>(body)!);
                log?.Invoke("Allegro account connected.");
                return true;
            }

            switch (TryReadError(body))
            {
                case "authorization_pending":
                    continue;
                case "slow_down":
                    interval += TimeSpan.FromSeconds(5);
                    continue;
                case "access_denied":
                    log?.Invoke("Authorization was denied by the user.");
                    return false;
                default:
                    log?.Invoke($"Authorization failed: {body}");
                    return false;
            }
        }

        log?.Invoke("Authorization timed out. Please try connecting again.");
        return false;
    }

    private async Task EnsureValidTokenAsync()
    {
        if (!Settings.IsConnected)
        {
            throw new InvalidOperationException("Not connected to Allegro. Connect the account first.");
        }
        if (DateTime.UtcNow < Settings.AccessTokenExpiresUtc.AddMinutes(-1) && !string.IsNullOrEmpty(Settings.AccessToken))
        {
            return;
        }

        var request = new HttpRequestMessage(HttpMethod.Post, $"{AuthBase}/auth/oauth/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = Settings.RefreshToken,
            }),
        };
        AddBasicAuth(request);

        var response = await _http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Could not refresh the Allegro token ({(int)response.StatusCode}). Reconnect the account. {body}");
        }

        StoreToken(JsonSerializer.Deserialize<TokenResponse>(body)!);
    }

    private void StoreToken(TokenResponse token)
    {
        Settings.AccessToken = token.AccessToken;
        if (!string.IsNullOrEmpty(token.RefreshToken))
        {
            Settings.RefreshToken = token.RefreshToken;
        }
        Settings.AccessTokenExpiresUtc = DateTime.UtcNow.AddSeconds(token.ExpiresIn);
        SaverExtensions.AllegroSettings.Write();
    }

    // ------------------------------------------------------------------- publish

    /// <summary>
    /// Reads <c>products.csv</c> (EAN;Liczba;Cena) and updates price and stock on
    /// the matching Allegro offers. Returns the number of offers updated.
    /// </summary>
    public async Task<int> PublishAsync(Action<string>? log = null)
    {
        await EnsureValidTokenAsync();

        var rows = ReadCsvRows(log);
        log?.Invoke($"Loaded {rows.Count} product rows from {CSVMaker.FileName}.");
        if (rows.Count == 0)
        {
            return 0;
        }

        var offerIdByEan = await ResolveOfferIdsAsync(rows.Select(r => r.Ean), log);

        int updated = 0, skipped = 0, failed = 0;
        foreach (var row in rows)
        {
            if (!offerIdByEan.TryGetValue(row.Ean, out var offer))
            {
                skipped++;
                continue;
            }

            var offerId = offer.Id;
            try
            {
                if (row.Count > 0)
                {
                    await ChangePriceAsync(offerId, row.Price);
                    await ChangeQuantityAsync(offerId, row.Count);
                    if (!offer.IsActive)
                    {
                        await SetOfferActiveAsync(offerId, true);
                        log?.Invoke($"Re-activated offer {offerId} (EAN {row.Ean}).");
                    }
                }
                else if (offer.IsActive)
                {
                    await SetOfferActiveAsync(offerId, false);
                    log?.Invoke($"Ended offer {offerId} (EAN {row.Ean}) - below the stock threshold.");
                }
                else
                {
                    continue;
                }

                updated++;
                log?.Invoke($"Updated offer {offerId} (EAN {row.Ean}) → price {row.Price.ToString(CultureInfo.InvariantCulture)} {Settings.Currency}, stock {row.Count}.");
            }
            catch (Exception e)
            {
                // One bad offer must not stop the remaining ones.
                failed++;
                log?.Invoke($"Failed offer {offerId} (EAN {row.Ean}): {e.Message}");
            }
        }

        log?.Invoke($"Publish finished: {updated} updated, {skipped} skipped, {failed} failed.");
        return updated;
    }

    private static List<CsvRow> ReadCsvRows(Action<string>? log)
    {
        var path = Path.Combine(SaverExtensions.ResourceDirectory, CSVMaker.FileName);
        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"{CSVMaker.FileName} not found. Regenerate the CSV first.");
        }

        var rows = new List<CsvRow>();
        foreach (var line in File.ReadLines(path).Skip(1)) // skip "EAN;Liczba;Cena" header
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }
            var parts = line.Split(';');
            if (parts.Length < 3)
            {
                continue;
            }
            var ean = parts[0].Trim();
            if (ean.Length == 0
                || !int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var count)
                || !decimal.TryParse(parts[2].Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var price))
            {
                log?.Invoke($"Skip malformed CSV line: {line}");
                continue;
            }
            rows.Add(new CsvRow(ean, count, price));
        }
        return rows;
    }

    /// <summary>An offer we matched, plus whether it is currently visible to buyers.</summary>
    private record OfferRef(string Id, string Status)
    {
        public bool IsActive => string.Equals(Status, "ACTIVE", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Maps each EAN to an offer id by querying own offers with external.id == EAN (batched).</summary>
    private async Task<Dictionary<string, OfferRef>> ResolveOfferIdsAsync(IEnumerable<string> eans, Action<string>? log)
    {
        var result = new Dictionary<string, OfferRef>();
        var distinct = eans.Distinct().ToList();

        const int batchSize = 50;
        for (int i = 0; i < distinct.Count; i += batchSize)
        {
            var batch = distinct.Skip(i).Take(batchSize).ToList();
            var query = string.Join("&", batch.Select(e => $"external.id={Uri.EscapeDataString(e)}"));

            var request = CreateApiRequest(HttpMethod.Get, $"{ApiBase}/sale/offers?limit=1000&{query}");
            var response = await _http.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Failed to list offers ({(int)response.StatusCode}): {body}");
            }

            var offers = JsonSerializer.Deserialize<OffersListResponse>(body);
            foreach (var offer in offers?.Offers ?? new List<OfferListItem>())
            {
                var ean = offer.External?.Id;
                if (!string.IsNullOrEmpty(ean) && !string.IsNullOrEmpty(offer.Id))
                {
                    result[ean] = new OfferRef(offer.Id, offer.Publication?.Status ?? "");
                }
            }
        }

        log?.Invoke($"Matched {result.Count} of {distinct.Count} EANs to existing offers.");
        return result;
    }

    private async Task ChangePriceAsync(string offerId, decimal price)
    {
        var payload = new
        {
            modification = new
            {
                type = "FIXED_PRICE",
                price = new { amount = price.ToString(CultureInfo.InvariantCulture), currency = Settings.Currency },
            },
            offerCriteria = new[]
            {
                new { type = "CONTAINS_OFFERS", offers = new[] { new { id = offerId } } },
            },
        };
        await SendCommandAsync($"{ApiBase}/sale/offer-price-change-commands/{Guid.NewGuid()}", payload, "price");
    }

    private async Task ChangeQuantityAsync(string offerId, int quantity)
    {
        var payload = new
        {
            modification = new { changeType = "FIXED", value = quantity },
            offerCriteria = new[]
            {
                new { type = "CONTAINS_OFFERS", offers = new[] { new { id = offerId } } },
            },
        };
        await SendCommandAsync($"{ApiBase}/sale/offer-quantity-change-commands/{Guid.NewGuid()}", payload, "quantity");
    }
    
    public async Task SetOfferActiveAsync(string offerId, bool active)
    {
        var payload = new
        {
            publication = new { action = active ? "ACTIVATE" : "END" },
            offerCriteria = new[]
            {
                new { type = "CONTAINS_OFFERS", offers = new[] { new { id = offerId } } },
            },
        };
        await SendCommandAsync(
            $"{ApiBase}/sale/offer-publication-commands/{Guid.NewGuid()}",
            payload,
            active ? "activate offer" : "end offer");
    }

    private async Task SendCommandAsync(string url, object payload, string what)
    {
        var request = CreateApiRequest(HttpMethod.Put, url);
        var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue(ApiMediaType);
        request.Content = content;

        var response = await _http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"{what} change failed ({(int)response.StatusCode}): {body}");
        }
    }

    private HttpRequestMessage CreateApiRequest(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Settings.AccessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(ApiMediaType));
        return request;
    }

    private void AddBasicAuth(HttpRequestMessage request)
    {
        var raw = Encoding.UTF8.GetBytes($"{Settings.ClientId}:{Settings.ClientSecret}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(raw));
    }

    private static string? TryReadError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("error", out var error) ? error.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------- DTOs

    private record CsvRow(string Ean, int Count, decimal Price);

    private class DeviceCodeResponse
    {
        [JsonPropertyName("device_code")] public string DeviceCode { get; set; } = "";
        [JsonPropertyName("user_code")] public string UserCode { get; set; } = "";
        [JsonPropertyName("verification_uri")] public string? VerificationUri { get; set; }
        [JsonPropertyName("verification_uri_complete")] public string? VerificationUriComplete { get; set; }
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
        [JsonPropertyName("interval")] public int Interval { get; set; }
    }

    private class TokenResponse
    {
        [JsonPropertyName("access_token")] public string AccessToken { get; set; } = "";
        [JsonPropertyName("refresh_token")] public string RefreshToken { get; set; } = "";
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
    }

    private class OffersListResponse
    {
        [JsonPropertyName("offers")] public List<OfferListItem> Offers { get; set; } = new();
    }

    private class OfferListItem
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("external")] public ExternalId? External { get; set; }
        [JsonPropertyName("publication")] public OfferPublication? Publication { get; set; }
    }

    private class OfferPublication
    {
        [JsonPropertyName("status")] public string Status { get; set; } = "";
    }

    private class ExternalId
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
    }
}

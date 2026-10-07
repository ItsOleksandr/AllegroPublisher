using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Allegro.Core;

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
    public void SaveSettings() => SaverExtensions.AllegroSettings.Write();

    public record DeviceAuthorization(string UserCode, string VerificationUri, string DeviceCode, int Interval, int ExpiresIn);
    
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
    
    private static readonly SemaphoreSlim _refreshLock = new(1, 1);

    private async Task EnsureValidTokenAsync()
    {
        await _refreshLock.WaitAsync();
        try
        {
            SaverExtensions.AllegroSettings.Read();

            if (!Settings.IsConnected)
            {
                throw new InvalidOperationException("Not connected to Allegro. Connect the account first.");
            }
            if (DateTime.UtcNow < Settings.AccessTokenExpiresUtc.AddMinutes(-5) && !string.IsNullOrEmpty(Settings.AccessToken))
            {
                return;
            }

            string? lastError = null;
            if (await TryRefreshAsync(e => lastError = e))
            {
                return;
            }
            
            SaverExtensions.AllegroSettings.Read();
            if (DateTime.UtcNow < Settings.AccessTokenExpiresUtc.AddMinutes(-5) && !string.IsNullOrEmpty(Settings.AccessToken))
            {
                return;
            }
            if (!await TryRefreshAsync(e => lastError = e))
            {
                throw new InvalidOperationException($"Could not refresh the Allegro token. Reconnect the account. {lastError}");
            }
        }
        finally
        {
            _refreshLock.Release();
        }
    }
    
    public async Task<bool> KeepAliveAsync(TimeSpan refreshWithin, Action<string>? log = null)
    {
        await _refreshLock.WaitAsync();
        try
        {
            SaverExtensions.AllegroSettings.Read();

            if (!Settings.IsConnected)
            {
                return false;
            }
            if (DateTime.UtcNow < Settings.AccessTokenExpiresUtc - refreshWithin && !string.IsNullOrEmpty(Settings.AccessToken))
            {
                return true; // still fresh enough - leave it alone
            }

            return await TryRefreshAsync(log);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public async Task<bool> TryRefreshAsync(Action<string>? log = null)
    {
        if (string.IsNullOrEmpty(Settings.RefreshToken))
        {
            log?.Invoke("no refresh token stored");
            return false;
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
            log?.Invoke($"Refresh failed ({(int)response.StatusCode}): {body}");
            return false;
        }

        StoreToken(JsonSerializer.Deserialize<TokenResponse>(body)!);
        return true;
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

    public async Task<int> PublishAsync(Action<string>? log = null)
    {
        await EnsureValidTokenAsync();

        var options = SaverExtensions.ListingOptions.Read();
        var listings = BuildListings(SaverExtensions.Products.Read().Values, options);
        log?.Invoke($"{listings.Count} products with an EAN, {listings.Count(r => r.Count > 0)} of them sellable.");
        if (listings.Count == 0)
        {
            return 0;
        }

        var offerIdByEan = await ResolveOfferIdsAsync(listings.Select(r => r.Ean), log);
        var campaigns = await ResolveCampaignOffersAsync(log);
        if (campaigns is null)
        {
            await NotifyAdminAsync("#AllegroApp Could not read Allegro campaigns, so no offer price was changed in this run. " +
                                   "Stock was updated as usual. Check the publish log.", log);
        }

        var listedPacks = SaverExtensions.Bundles.Read();

        int updated = 0, unchanged = 0, frozen = 0, skipped = 0, failed = 0;
        var unlisted = new List<string>();
        foreach (var planned in listings)
        {
            if (!offerIdByEan.TryGetValue(planned.Ean, out var offer))
            {
                skipped++;
                if (planned.Count > 0)
                {
                    unlisted.Add(planned.Ean);
                }
                continue;
            }

            var offerId = offer.Id;
            var price = offer.Price;
            try
            {
                var row = await MatchListedPackAsync(planned, offerId, listedPacks, options, log);
                if (row.Count > 0)
                {
                    var changed = false;
                    if (offer.Price != row.Price)
                    {
                        if (campaigns is null)
                        {
                            frozen++;
                        }
                        else if (campaigns.TryGetValue(offerId, out var campaign))
                        {
                            frozen++;
                            log?.Invoke($"Price frozen on offer {offerId} (EAN {row.Ean}): {campaign.Describe()}, " +
                                        $"keeping {offer.Price?.ToString(CultureInfo.InvariantCulture)} instead of " +
                                        $"{row.Price.ToString(CultureInfo.InvariantCulture)} {Settings.Currency}.");
                        }
                        else
                        {
                            await ChangePriceAsync(offerId, row.Price);
                            price = row.Price;
                            changed = true;
                        }
                    }
                    if (offer.Stock != row.Count)
                    {
                        await ChangeQuantityAsync(offerId, row.Count);
                        changed = true;
                    }
                    if (!offer.IsActive)
                    {
                        await SetOfferActiveAsync(offerId, true);
                        changed = true;
                        log?.Invoke($"Re-activated offer {offerId} (EAN {row.Ean}).");
                    }
                    if (!changed)
                    {
                        unchanged++;
                        continue;
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
                log?.Invoke($"Updated offer {offerId} (EAN {row.Ean}) → price {price?.ToString(CultureInfo.InvariantCulture)} {Settings.Currency}, stock {row.Count}.");
            }
            catch (Exception e)
            {
                // One bad offer must not stop the remaining ones.
                failed++;
                log?.Invoke($"Failed offer {offerId} (EAN {planned.Ean}): {e.Message}");
            }
        }

        new UnlistedTracker().Record(unlisted);

        log?.Invoke($"Publish finished: {updated} updated, {unchanged} unchanged, {frozen} with a frozen price, " +
                    $"{skipped} skipped, {failed} failed. {unlisted.Count} sellable products have no offer.");
        return updated;
    }

    private async Task<ListingRow> MatchListedPackAsync(
        ListingRow row, string offerId, Dictionary<string, int> listedPacks, ListingOptions options, Action<string>? log)
    {
        var listed = listedPacks.TryGetValue(row.Ean, out var known) ? known : 1;
        if (listed == row.Pack || row.Count == 0)
        {
            return row;
        }

        var (status, body) = await GetRawAsync($"/sale/product-offers/{offerId}");
        if (status is < 200 or >= 300)
        {
            throw new InvalidOperationException($"could not read the pack size of the offer ({status})");
        }

        var pack = JsonNode.Parse(body)?["productSet"]?.AsArray().FirstOrDefault()?["quantity"]?["value"]?.GetValue<int>() ?? 1;
        if (pack == row.Pack)
        {
            return row;
        }

        log?.Invoke($"Offer {offerId} (EAN {row.Ean}) is still listed as {DescribePack(pack)}, not {DescribePack(row.Pack)} - " +
                    "priced and stocked as listed until it is converted.");
        return row with
        {
            Pack = pack,
            Count = row.Count > 0 ? options.GetOfferStock(row.Product, pack) : 0,
            Price = options.GetOfferPrice(row.Product, pack),
        };
    }

    private static string DescribePack(int pack) => pack > 1 ? $"a pack of {pack}" : "single units";

    public record CampaignBadge(string OfferId, string CampaignId, string Name, string Status, DateTimeOffset? Until)
    {
        public string Describe() =>
            $"campaign \"{Name}\" ({Status}{(Until is null ? "" : $" until {Until.Value.ToLocalTime():yyyy-MM-dd HH:mm}")})";
    }

    private static readonly HashSet<string> PriceLockingStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "IN_VERIFICATION", "WAITING_FOR_PUBLICATION", "ACTIVE",
    };

    public async Task<Dictionary<string, CampaignBadge>?> ResolveCampaignOffersAsync(Action<string>? log = null)
    {
        await EnsureValidTokenAsync();

        var result = new Dictionary<string, CampaignBadge>();
        const int limit = 1000;
        try
        {
            for (int offset = 0; ; offset += limit)
            {
                var request = CreateApiRequest(HttpMethod.Get,
                    $"{ApiBase}/sale/badges?marketplace.id={Uri.EscapeDataString(Settings.MarketplaceId)}&limit={limit}&offset={offset}");
                var response = await _http.SendAsync(request);
                var body = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                {
                    log?.Invoke($"Could not read campaigns ({(int)response.StatusCode}): {body} - no price is changed in this run.");
                    return null;
                }

                var badges = JsonSerializer.Deserialize<BadgesListResponse>(body)?.Badges ?? new List<BadgeItem>();
                foreach (var badge in badges)
                {
                    var offerId = badge.Offer?.Id;
                    var status = badge.Process?.Status ?? "";
                    if (string.IsNullOrEmpty(offerId) || !PriceLockingStatuses.Contains(status))
                    {
                        continue;
                    }

                    var until = DateTimeOffset.TryParse(badge.Publication?.To, CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal, out var to) ? to : (DateTimeOffset?)null;
                    var campaignId = badge.Campaign?.Id ?? "";
                    result[offerId] = new CampaignBadge(offerId, campaignId,
                        string.IsNullOrWhiteSpace(badge.Campaign?.Name) ? campaignId : badge.Campaign!.Name!, status, until);
                }

                if (badges.Count < limit)
                {
                    break;
                }
            }
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException)
        {
            log?.Invoke($"Could not read campaigns: {e.Message} - no price is changed in this run.");
            return null;
        }

        log?.Invoke($"{result.Count} offers are in a campaign or waiting for one - their prices stay as they are.");
        return result;
    }

    private static async Task NotifyAdminAsync(string text, Action<string>? log)
    {
        try
        {
            if (!await new TelegramNotify().SendAdminAsync(text))
            {
                log?.Invoke("Telegram notification failed.");
            }
        }
        catch (Exception e)
        {
            log?.Invoke($"Telegram notification failed: {e.Message}");
        }
    }

    private static List<ListingRow> BuildListings(IEnumerable<ProductInfo> products, ListingOptions options)
    {
        var listings = new List<ListingRow>();
        foreach (var product in products)
        {
            if (string.IsNullOrWhiteSpace(product.EAN))
            {
                continue;
            }

            listings.Add(new ListingRow(
                product,
                product.EAN,
                options.Includes(product) ? options.GetOfferStock(product) : 0,
                options.GetOfferPrice(product),
                options.GetPackSize(product)));
        }
        return listings;
    }

    
    public record OrphanOffer(string Id, string Ean, string Name, string Reason);
    
    public async Task<List<OrphanOffer>> FindOrphanOffersAsync(Action<string>? log = null)
    {
        await EnsureValidTokenAsync();

        var options = SaverExtensions.ListingOptions.Read();

        var productByEan = new Dictionary<string, ProductInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var product in SaverExtensions.Products.Read().Values)
        {
            if (!string.IsNullOrEmpty(product.EAN))
            {
                productByEan[product.EAN] = product;
            }
        }

        var orphans = new List<OrphanOffer>();
        int total = 0, foreign = 0, unknown = 0;
        const int limit = 100;

        for (int offset = 0; ; offset += limit)
        {
            var request = CreateApiRequest(HttpMethod.Get,
                $"{ApiBase}/sale/offers?publication.status=ACTIVE&limit={limit}&offset={offset}");
            var response = await _http.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Failed to list offers ({(int)response.StatusCode}): {body}");
            }

            var offers = JsonSerializer.Deserialize<OffersListResponse>(body)?.Offers ?? new List<OfferListItem>();
            if (offers.Count == 0)
            {
                break;
            }

            total += offers.Count;
            foreach (var offer in offers)
            {
                var ean = offer.External?.Id ?? "";

                // No external id => listed manually or by another tool. None of our business.
                if (string.IsNullOrEmpty(ean))
                {
                    foreign++;
                    continue;
                }

                // Never parsed => we have no grounds to judge it, so leave it alone.
                if (!productByEan.TryGetValue(ean, out var product))
                {
                    unknown++;
                    continue;
                }

                if (!options.Includes(product))
                {
                    orphans.Add(new OrphanOffer(offer.Id, ean, offer.Name, ExplainRejection(product, options)));
                }
            }

            if (offers.Count < limit)
            {
                break;
            }
        }

        log?.Invoke($"Scanned {total} active offers — {foreign} not listed by this app, {unknown} not in the " +
                    $"parsed catalogue (both left alone), {orphans.Count} no longer pass the listing options.");
        return orphans;
    }

    private static string ExplainRejection(ProductInfo product, ListingOptions options)
    {
        var category = options.CategoriesBlackList
            .FirstOrDefault(rule => product.CategoriesUrls.Any(url => url.Contains(rule)));
        if (category is not null)
        {
            return $"category black list: {category}";
        }

        if (options.EansBlackList.Contains(product.EAN))
        {
            return "EAN black list";
        }

        if (product.EAN.Contains("—"))
        {
            return "invalid EAN";
        }

        if (product.Count < options.MinimalProductCount)
        {
            return $"stock {product.Count} < {options.MinimalProductCount}";
        }

        if (product.Price < options.MinimalPrice)
        {
            return $"price {product.Price.ToString(CultureInfo.InvariantCulture)} < {options.MinimalPrice.ToString(CultureInfo.InvariantCulture)}";
        }

        if (options.IsBundle(product) && product.Count < product.MinOrderQuantity)
        {
            return $"bundle of {product.MinOrderQuantity}, only {product.Count} in stock";
        }

        return "does not pass the listing options";
    }

    /// <summary>Takes the given offers off sale. Returns how many succeeded.</summary>
    public async Task<int> EndOffersAsync(IEnumerable<string> offerIds, Action<string>? log = null)
    {
        await EnsureValidTokenAsync();

        int ended = 0;
        foreach (var offerId in offerIds)
        {
            try
            {
                await SetOfferActiveAsync(offerId, false);
                ended++;
                log?.Invoke($"Ended offer {offerId}.");
            }
            catch (Exception e)
            {
                log?.Invoke($"Failed to end offer {offerId}: {e.Message}");
            }
        }

        log?.Invoke($"Ended {ended} offers.");
        return ended;
    }

    /// <summary>An offer we matched, plus whether it is currently visible to buyers.</summary>
    private record OfferRef(string Id, string Status, decimal? Price, int? Stock)
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
                    var amount = offer.SellingMode?.Price?.Amount;
                    result[ean] = new OfferRef(
                        offer.Id,
                        offer.Publication?.Status ?? "",
                        decimal.TryParse(amount, NumberStyles.Number, CultureInfo.InvariantCulture, out var price) ? price : null,
                        offer.Stock?.Available);
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

    /// <summary>
    /// Issues a read-only GET against the Allegro API and returns the status code with the raw body.
    /// Used by <see cref="BundleProbe"/> to inspect products, categories and existing offers without
    /// parsing them into DTOs first. Never throws on a non-2xx - the caller decides what that means.
    /// </summary>
    public async Task<(int Status, string Body)> GetRawAsync(string path)
    {
        await EnsureValidTokenAsync();

        var url = path.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? path : $"{ApiBase}{path}";
        var response = await _http.SendAsync(CreateApiRequest(HttpMethod.Get, url));
        return ((int)response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// Sends a raw JSON body to the Allegro API and returns the status code with the raw response.
    /// Like <see cref="GetRawAsync"/> it never throws on a non-2xx: callers that are probing what the
    /// API will accept need to read the validation errors, not catch an exception.
    /// </summary>
    public async Task<(int Status, string Body)> SendJsonRawAsync(HttpMethod method, string path, string json)
    {
        await EnsureValidTokenAsync();

        var url = path.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? path : $"{ApiBase}{path}";
        var request = CreateApiRequest(method, url);
        var content = new StringContent(json, Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue(ApiMediaType);
        request.Content = content;

        var response = await _http.SendAsync(request);
        return ((int)response.StatusCode, await response.Content.ReadAsStringAsync());
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

    private record ListingRow(ProductInfo Product, string Ean, int Count, decimal Price, int Pack);

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
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("external")] public ExternalId? External { get; set; }
        [JsonPropertyName("publication")] public OfferPublication? Publication { get; set; }
        [JsonPropertyName("sellingMode")] public OfferSellingMode? SellingMode { get; set; }
        [JsonPropertyName("stock")] public OfferStock? Stock { get; set; }
    }

    private class OfferSellingMode
    {
        [JsonPropertyName("price")] public OfferPrice? Price { get; set; }
    }

    private class OfferPrice
    {
        [JsonPropertyName("amount")] public string? Amount { get; set; }
    }

    private class OfferStock
    {
        [JsonPropertyName("available")] public int? Available { get; set; }
    }

    private class OfferPublication
    {
        [JsonPropertyName("status")] public string Status { get; set; } = "";
    }

    private class ExternalId
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
    }

    private class BadgesListResponse
    {
        [JsonPropertyName("badges")] public List<BadgeItem> Badges { get; set; } = new();
    }

    private class BadgeItem
    {
        [JsonPropertyName("offer")] public BadgeOffer? Offer { get; set; }
        [JsonPropertyName("campaign")] public BadgeCampaignRef? Campaign { get; set; }
        [JsonPropertyName("publication")] public BadgePublication? Publication { get; set; }
        [JsonPropertyName("process")] public BadgeProcess? Process { get; set; }
    }

    private class BadgeOffer
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
    }

    private class BadgeProcess
    {
        [JsonPropertyName("status")] public string? Status { get; set; }
    }

    private class BadgeCampaignRef
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
    }

    private class BadgePublication
    {
        [JsonPropertyName("to")] public string? To { get; set; }
    }
}

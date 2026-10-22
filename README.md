# AllegroApp

Keeps an [Allegro](https://allegro.pl) storefront in sync with a supplier's
catalogue. It scrapes the supplier's product pages, prices them, and pushes
price and stock onto the matching Allegro offers — so a store with thousands of
listings stays current without anyone editing offers by hand.

The work splits into three projects around one shared core:

```
Allegro.Core       Allegro REST API client, pricing rules, packs, settings, notifications
├── Allegro.Console   headless run: scrape → pack → publish (cron / nightly)
└── Allegro.Admin     Blazor Server panel: connect account, tune rules, publish on demand
```

Both entry points call the same `AllegroPublisher`, so the nightly job and the
web panel cannot drift apart in behaviour.

## How it works

**1 — Collect.** `SiteMapExtracter` walks the supplier's XML sitemaps to build a
product URL list. `ProductParcer` then drives a real browser through
[Playwright](https://playwright.dev/dotnet/) — the supplier requires a logged-in
session, and prices differ per account — extracting name, EAN, price, stock and
images. Long runs are the norm, so parsing is resumable: progress is written to
`last_parse.txt` and a crashed or interrupted session can be continued instead
of restarted.

**2 — Price.** `ListingOptions` holds every rule that decides a product's fate: a
tiered multiplier (cheap items get marked up more aggressively than expensive
ones), a minimum stock threshold, a minimum price floor, and category or EAN
blacklists for things that should never be listed.

**3 — Pack.** Some products the supplier will not sell singly — its minimum order
is 10, 50, 500. Listing one unit of those would be a lie, so above the
`BundleFromQuantity` threshold the whole pack becomes the unit of sale.
`BundlePlan` works out which offers that applies to, `BundleConverter` rewrites
them **in place**: `productSet.quantity`, price, stock and title move in a single
request, so a pack is never briefly on sale for the price of one item. The offer
keeps its id, history and search position. Raising the threshold takes those
offers back to single units.

**4 — Publish.** `AllegroPublisher` authenticates the seller account through
Allegro's OAuth2 **device flow**, then matches each row to an existing offer by
`external.id == EAN` — the standard way Allegro keys offers to an external
inventory system — and updates price and stock. Products with no matching offer
are skipped here and listed in the next step. Only a price or stock
that actually differs is sent. Offers in an Allegro campaign (Allegro Days and
other badges, `GET /sale/badges`), or waiting for one, keep their price until the
campaign ends, so a publish never knocks them out of it; stock is still updated.
Setting campaign prices is left to whoever submits the offer. If the campaigns
cannot be read, no price changes in that run. A product the supplier has deleted is set to zero stock,
so its offer ends; any product the parser has not refreshed for
`StaleAfterHours` (48 by default) is published with zero stock too, unless more
than half the catalogue is that old — then the parser is assumed broken, stock is
left alone and Telegram is told.

**4a — Own stock.** The seller's own shelf is kept apart from the supplier's
(`OwnStock`, the *My stock* page): units left over from a minimum-order purchase,
or a product the supplier has dropped but the seller still holds. `StockPlanner`
decides what an offer gets: the supplier's stock as long as the supplier has the
product, otherwise the own stock, priced from its purchase cost through the same
multiplier ladder. A product with own stock is never ended for being stale or
deleted upstream. Every change of quantity is a `StockMove` with a reason, and
the last one can be undone.

**4b — Orders and purchases.** `OrderSync` follows the Allegro order event
stream (`/order/events`, polled every five minutes by the admin panel). A paid
order is split line by line: whatever own stock can cover is written off at once
(`order` moves), the rest lands on the purchase list, which rounds the total need
of all open orders up to the supplier's minimum order. After the trip the seller
presses *Bought*; what the orders need is set aside, the surplus goes to own
stock at the paid price (`purchase`). A cancelled order gives its pieces back.
Needs the `allegro:api:orders:read` scope on the Allegro app.

**5 — List new products.** `OfferCreator` creates an offer for every sellable
product that has none yet: it finds the product's Allegro catalogue card by EAN,
copies delivery, returns, invoice, location and GPSR settings from a template
offer, and takes the category, photos, description and safety information from
the card. Products with no card, several cards, or a category that needs details
only a person can declare (such as medical-device status) are left for Sales Center.

**6 — Rewrite content.** `AutoContent` gives a batch of offers, weakest titles
first, a new AI title and description (`ContentGenerator`, Claude through the
Anthropic API) and applies them (`ContentApplier`), placing gallery photos next to
the text blocks. Drafts whose data disagrees with the supplier wait for review.

**7 — Report.** `TelegramNotify` speaks up when something needs a human: the
supplier login has failed three times running, or one of the steps above failed.
A quiet run sends nothing.

### Keeping the token alive

Allegro access tokens expire. In the web app, `AllegroTokenRefresher` is a
`BackgroundService` that checks every 30 minutes and refreshes anything expiring
within 2 hours. It re-reads the token from disk before refreshing, so it will not
fight the console app over the same stored credentials when both are installed on
one machine.

## Projects

### Allegro.Core

The shared library — no UI, no entry point.

| File | Responsibility |
|---|---|
| `AllegroPublisher.cs` | Allegro REST API client: device flow, token refresh, offer lookup and update |
| `ListingOptions.cs` | Every rule in one place: what gets sold, at what price, singly or as a pack |
| `BundlePlan.cs` | Works out which offers should be packs, and of what size. Writes nothing |
| `BundleConverter.cs` | Applies that plan — the only code that rewrites a live offer |
| `OfferCreator.cs` | Creates offers for products that have none, from their catalogue card and a template offer |
| `OrderSync.cs` | Reads Allegro orders, writes them off against own stock, builds the purchase list and books the purchase |
| `OwnStock.cs`, `StockPlanner.cs` | The seller's own stock with its move journal, and how it combines with the supplier's stock into an offer quantity and price |
| `ContentGenerator.cs`, `ContentApplier.cs`, `AutoContent.cs` | AI titles and descriptions: generate, review, apply |
| `Saver.cs` | Small typed JSON file store used for every settings file |
| `TelegramNotify.cs` | Alerts to a Telegram chat |
| `ProductInfo.cs`, `AllegroSettings.cs` | Shared models |

### Allegro.Console

The unattended runner, driven by command-line flags:

| Flag | Effect |
|---|---|
| *(no flags)* | The full run: scrape → pack → publish |
| `--load_last_session` | Resume the previous, interrupted parse |
| `--start-index=N` | Start from a given position in the URL list |
| `--test-url=<url>` | Parse one product page and print the result — nothing is saved or published |
| `--configure-browser` | Opens a visible browser so the supplier login can be completed once and persisted |
| `--visible` | Runs the browser with a window instead of headless |

Everything to do with Allegro itself — reviewing the pack plan, converting,
scanning for unlisted products, ending offers — lives in the admin panel rather
than behind a flag.

### Allegro.Admin

A Blazor Server panel for the parts a human needs to do: connect the Allegro
account via device flow, edit pricing tiers and blacklists, trigger a publish and
watch a live log of it. Password-protected through cookie auth, with the expected
password read from configuration.

## Requirements

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Playwright browsers — installed on first run, or via `pwsh bin/Debug/net8.0/playwright.ps1 install`
- An Allegro developer application (Client ID and Secret) from
  [apps.developer.allegro.pl](https://apps.developer.allegro.pl/)
- Supplier account credentials

## Configuration

Runtime configuration lives in a `Resources/` directory beside the solution.
**None of it is committed** — `Resources/` and every `*settings.*json` are
gitignored, because these files hold real credentials and tokens.

| File | Contents |
|---|---|
| `allegro_settings.txt` | Client ID, Client Secret, OAuth tokens, currency |
| `creditials.txt` | Supplier login |
| `listing_options.txt` | Pricing tiers, thresholds, blacklists, pack threshold |
| `admin_options.txt` | Admin panel settings |
| `urls.txt`, `urls_black_list.txt` | Product URL list and exclusions |
| `last_parse.txt` | Resume point for an interrupted parse |
| `products_dictionary.txt` | The parsed catalogue — the single source every step reads |
| `bundles.txt` | Which EANs are listed as packs, and of what size |
| `card_lookups.txt` | Catalogue cards found per EAN, so scans do not look them up again |
| `content_drafts.txt` | Generated titles and descriptions and whether they were applied |
| `offer_backups/` | Each offer as it was before the converter rewrote it |

Files are created with defaults on first access, so a fresh checkout starts
empty rather than crashing.

The admin password is read from configuration at `Auth:Password`. Set it through
user secrets or an environment variable rather than a file on disk:

```bash
dotnet user-secrets set "Auth:Password" "<your password>" --project Allegro.Admin
```

## Running

Publish from the panel:

```bash
dotnet run --project Allegro.Admin
```

Or run the full unattended pipeline:

```bash
dotnet run --project Allegro.Console
```

First time only, sign in to the supplier once so the browser profile is stored:

```bash
dotnet run --project Allegro.Console -- --configure-browser
```

## Notes

The scraper targets one specific supplier's page structure. Pointing it at a
different site means rewriting the selectors in `ProductExtracter` — the rest of
the pipeline (pricing, packing, publishing) is independent of where the products
came from.

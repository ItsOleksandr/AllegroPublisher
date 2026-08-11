# AllegroApp

Keeps an [Allegro](https://allegro.pl) storefront in sync with a supplier's
catalogue. It scrapes the supplier's product pages, turns them into a priced
CSV, and pushes price and stock onto the matching Allegro offers — so a store
with thousands of listings stays current without anyone editing offers by hand.

The work splits into three projects around one shared core:

```
Allegro.Core       Allegro REST API client, pricing rules, CSV, settings, notifications
├── Allegro.Console   headless run: scrape → CSV → publish (cron / nightly)
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

**2 — Price.** `CSVMaker` applies the rules in `CSVOptions`: a tiered multiplier
(cheap items get marked up more aggressively than expensive ones), a minimum
stock threshold, a minimum price floor, and category or EAN blacklists for
things that should never be listed.

**3 — Publish.** `AllegroPublisher` authenticates the seller account through
Allegro's OAuth2 **device flow**, then matches each row to an existing offer by
`external.id == EAN` — the standard way Allegro keys offers to an external
inventory system — and updates price and stock. Products with no matching offer
are skipped; creating new offers is deliberately out of scope.

**4 — Report.** `TelegramNotify` sends a summary when a run finishes, so a
failed nightly job is noticed the same day rather than the next week.

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
| `CSVMaker.cs` / `CSVOptions.cs` | Builds the publish CSV and holds the pricing and filtering rules |
| `Saver.cs` | Small typed JSON file store used for every settings file |
| `TelegramNotify.cs` | Run summaries to a Telegram chat |
| `ProductInfo.cs`, `AllegroSettings.cs` | Shared models |

### Allegro.Console

The unattended runner, driven by command-line flags:

| Flag | Effect |
|---|---|
| `--mode-xml=new_parse` | Re-extract product URLs from the supplier sitemaps |
| `--mode-xml=load_last_session` | Resume the previous, interrupted parse |
| `--start-index=N` | Start from a given position in the URL list |
| `--mode=manual` | Interactive run — ignores the URL blacklist |
| `--configure-browser` | Opens a visible browser so the supplier login can be completed once and persisted |

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
| `csv_options.txt` | Pricing tiers, thresholds, blacklists |
| `admin_options.txt` | Admin panel settings |
| `urls.txt`, `urls_black_list.txt` | Product URL list and exclusions |
| `last_parse.txt` | Resume point for an interrupted parse |
| `products.csv` | Generated output |

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
dotnet run --project Allegro.Console -- --mode-xml=new_parse
```

First time only, sign in to the supplier once so the browser profile is stored:

```bash
dotnet run --project Allegro.Console -- --configure-browser
```

## Notes

The scraper targets one specific supplier's page structure. Pointing it at a
different site means rewriting the selectors in `ProductExtracter` — the rest of
the pipeline (pricing, CSV, publishing) is independent of where the products
came from.

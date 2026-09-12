# Stillwatch

**Monitor crypto markets without staring at charts all day.**

Stillwatch adds a mobile subscription product to the existing TradingScanner engine. The landing page is at `/`, the customer app at `/app/`, and the original research workspace at `/legacy/`. This release keeps live charging disabled and shows clearly labeled examples until commercial market-data permission is configured.

## Product

- Home, searchable Scanner, synced Watchlist, Alerts and Account views, with setup evidence, invalidation and cost assumptions.
- ASP.NET Core Identity accounts, secure cookie sessions, CSRF protection, password recovery through configured email, and per-customer persistent preferences.
- Free overview and example analyses. Pro is $29/month, with configurable watchlist/rule/history limits and server-side entitlement enforcement.
- Server-side rules consume the existing shared scanner. Persistent evidence, cooldowns, deduplication, quiet hours, stale-data protection and Web Push delivery diagnostics work independently of an open tab.
- Stripe hosted checkout and customer portal in test mode only. Signed webhooks and verified subscription/invoice state control Pro access; a checkout success URL grants nothing.
- Install manifest, service worker, offline explanation, device notification setup and static-only caching. Private API responses are never cached by the service worker.
- Review drafts for privacy, terms and risk disclosures; account deletion and support access in Account.

The existing market-data connections, analysis engine, scoring criteria, price geometry and safeguards are reused. Customer fee/slippage projections call the existing execution assessor. The global Minnesota/excluded-coin and Kraken+ zero-fee assumptions are no longer defaults. Fee settings are editable estimates, not verified exchange quotes.

## Run locally

Requirements: .NET 8 SDK/runtime, Node 22 and Corepack/pnpm. No API secrets are required for the labeled preview or local account/watchlist flows.

```sh
dotnet restore
dotnet run --project src/TradingScanner.Api -- --environment Development
```

In another terminal:

```sh
cd web
corepack pnpm install --frozen-lockfile
corepack pnpm dev
```

Open `http://localhost:3000`. The API runs on `http://localhost:5080`; the development client sends credentialed requests there. Set `Product__PublicOrigin=http://localhost:3000` for email/checkout links during this two-port workflow. Do not use development cookie settings for public hosting.

For the same-origin static deployment used by Docker, build the frontend with `NEXT_OUTPUT=export` and copy `web/out/` into the API's `wwwroot/`. Docker does this automatically.

```sh
docker compose -f docker-compose.product.yml up --build
```

That optional product compose file exposes the app at `http://localhost:5080` in local Development mode with a named persistent volume. The original compose file for market-engine Postgres/Redis remains available.

## Configuration and launch

See [the configuration example](.env.example) and [setup instructions](docs/product-setup.md). .NET loads environment variables using double underscores; it does **not** read `.env` automatically outside Compose. Never put secrets in `NEXT_PUBLIC_*` variables or source control.

The initial product database is SQLite on persistent disk, separate from the existing optional engine Postgres store. Run one application instance, keep its database and data-protection keys together in backed-up storage, and use HTTPS before inviting external customers. Review migration/worker coordination before scaling to multiple instances.

**Public paid launch is blocked** pending market-data permissions, payment-provider acceptance, business details and jurisdiction review. [Commercial launch review](docs/commercial-launch-review.md) records specific open requirements and official policy sources. Test integration availability is not proof that production charging is approved. This release rejects live Stripe keys/events even if provided.

## Verification

```sh
dotnet test --configuration Release
cd web
corepack pnpm typecheck
corepack pnpm test
corepack pnpm build
```

The test suite retains the original signal, market-data and risk tests and adds customer projection, isolation, billing and notification checks. Provider integration tests still require their actual services/configuration; skipped tests are not evidence of a live integration. Real SMTP delivery, supported-device push and Stripe test transactions need separate end-to-end verification after configuration.

## Existing research tools

Stocks, PrizePicks, paper trading, historical replay and other original tools remain in the repository and `/legacy/`. Their global unauthenticated stores are not exposed by the commercial API. A separate private deployment can opt into the original behavior with `Product:CommercialMode=false`; never set that on a customer-facing deployment. [Archived engine/workspace documentation](docs/legacy-scanner.md) describes the prior private deployment, including its historical personal assumptions, rather than the new product defaults.

Scores describe heuristic evidence, not win probabilities. A setup is conditional. Simulated outcomes are not actual trades. This product does not hold assets, place orders, or request withdrawal permissions.

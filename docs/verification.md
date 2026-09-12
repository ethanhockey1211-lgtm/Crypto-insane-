# Release verification — 12 September 2026

The product was built and checked locally. It has not been publicly deployed, commercially approved or enabled for live payments.

## Automated checks

- Backend: **428 passed, 5 skipped**, 433 total (`dotnet test TradingScanner.sln --configuration Release --no-restore`). The five skips require the optional Postgres test service; the existing GitHub workflow provides that service for CI.
- Frontend: **337 passed**, 18 test files (`corepack pnpm test`). TypeScript checking passed.
- `NEXT_OUTPUT=export corepack pnpm build` passed. The exported site was copied into a published .NET API and exercised on one origin.
- Real Edge browser smoke: **17 passed, 0 failed**, including both temporary-account cleanup checks. Desktop and 360/390 px phone layouts had no horizontal overflow. Screenshots were inspected.
- A separate explicitly seeded local Pro browser fixture passed **8 checks**: rule creation/editing, pause/resume, deletion, score-floor validation, customer isolation and launch restrictions. It bypasses payments only in the named local test database and is **not payment verification**. Both fixture customers were deleted.
- Static landing, app, legal, offline, manifest, service worker and icon routes returned successfully through the published API, including an account URL with query parameters.

## What the checks prove

The browser pass created local test accounts, completed onboarding with an IANA time zone and cost assumptions, saved canonical watchlist symbols, signed in from another browser context, checked customer isolation, saved a support receipt, tested logout and modal keyboard focus, and verified that a checkout-return URL cannot unlock Pro. It inspected the service-worker cache and rendered the offline explanation with the device offline. Temporary accounts were deleted after the run.

Reusable scripts are `web/scripts/product-smoke.mjs` and `web/scripts/product-pro-smoke.mjs`. Read their explicit local-host/configuration requirements before running them. The optional seeded fixture requires Node 24 and opt-in; the application itself does not depend on it.

Backend tests cover password recovery and session invalidation, ownership and limits, signed/duplicate/out-of-order payment events, purchase/failed-payment/cancellation/expiry entitlements, rule matching and inherited conditions, quiet hours and time zones, cooldown/hold state, stale-data suppression, durable delivery retries and immutable issue-time evidence. A Web Push test decrypts the actual locally generated encrypted payload to verify that private rules, symbols and prices are absent from lock-screen text. History deep links enforce ownership and the configured access window.

An existing candle test used wall-clock-dependent historical trades and could race the engine timer. Its isolated fixture now freezes time and retains exact closed/forming-bar assertions. No signal thresholds or production engine behavior were changed to make tests pass.

## External verification still needed

- **Email:** supply a verified SMTP sender and test actual confirmation/recovery delivery and expired links.
- **Payments:** resolve Stripe product eligibility, configure its sandbox product/price, webhook secret and portal, then run actual hosted checkout, renewal-failure and cancellation flows. Current billing tests use a controlled gateway; no real Stripe transaction was made. Live keys/events are rejected by this release.
- **Push:** configure VAPID keys and operator contact, then verify supported physical devices with the app closed, permission denial, network loss, expiry and quiet hours. Local encryption/sender tests and browser service-worker tests do not prove OS delivery or installation.
- **Market data and launch:** resolve commercial exchange-data permissions and jurisdiction requirements, supply business/support details, review the legal drafts and retention/backups, and configure HTTPS with persistent database/key storage before public launch. The preview uses labeled examples and does not start an unapproved exchange feed.

See [product setup](product-setup.md) and [commercial launch review](commercial-launch-review.md) for configuration and official sources. Run one application instance with the initial SQLite schema; future schema changes need reviewed migrations.

# Stillwatch commercial launch review

Status: **development implementation; public paid launch blocked**. Review dated 2026-09-12. No commercial permissions, provider acceptance, production credentials, or legal entity have been supplied. This is an implementation checklist and draft review record, not an approval.

## Market data

The original Kraken and Coinbase adapters, shared feed, analysis engine and risk checks remain in the repository. `Product:MarketDataApproved=false` keeps customer-facing live data and market push alerts disabled. Illustrated examples are explicitly separate from observations and recorded results.

[Kraken's official API guidance](https://docs-legacy.kraken.com/api/docs/guides/global-intro/) states that commercial use of public endpoint data requires permission, including non-personal use. Public accessibility is insufficient evidence of redistribution rights. Obtain written permission covering the subscription product's display, derived analysis, alert payloads, history retention, caching, and intended customer territories. Confirm ongoing attribution and usage obligations. If using another provider, separately verify its terms; switching feeds does not transfer permission.

Public exchange listings do not establish account-level availability. Customer exchange/scope selection is restricted to the deployed shared provider and supported spot/USD scope. There is no custody, private exchange API key, order execution or withdrawal integration.

## Payments

[Stripe's current restricted-business policy](https://stripe.com/legal/restricted-businesses) requires additional review for listed financial activities and crypto exchanges/wallets. Its jurisdiction-specific restrictions include investment/crypto profit guidance tools in Japan. A monitoring subscription should not be assumed accepted merely because it does not execute trades. Ask Stripe to evaluate the actual product, marketing, business jurisdiction and intended customer territories. Acceptance remains unresolved.

The integration accepts **test keys and test events only**. A successful checkout return cannot grant Pro. An authenticated, verified webhook and current subscription state control access. Live charging has no enable switch in this release. Do not replace this with a client-side payment flag.

If Stripe declines this offer, obtain written acceptance from another provider before implementing its live adapter. Do not disguise the business or route around a rejection.

## Business and jurisdiction decisions still required

- Identify legal operator, address, support/privacy contact, registration details and launch territories.
- Obtain qualified review of whether the specific monitoring/conditional-setup service triggers investment-adviser, financial-promotion or other local obligations in those territories.
- Review consumer subscription consent, renewal disclosures, cancellation/refunds, taxes, accessibility, privacy rights and international data transfers for the actual launch scope.
- Review and replace the visibly marked privacy, terms and risk drafts. Do not claim compliance or present draft acceptance as final legal approval.
- Decide refund handling, support response targets, data retention and deletion/backups policy. Configure hosted billing portal cancellation.
- Confirm rights to the eventual name and artwork. Stillwatch is a working product name, without trademark clearance.

## Operational release gate

1. Host behind HTTPS with a canonical `Product:PublicOrigin`, persistent database and data-protection keys, tested backups/restore, and a continuously running worker process.
2. Configure a verified mail sender and test email confirmation/recovery with real test accounts.
3. Configure VAPID credentials; test push on supported desktop browsers, Android and an installed iOS/iPadOS Home Screen app. Device/browser delivery is best effort.
4. Run real provider test checkout, failed renewal, cancellation and expiry; replay duplicate and out-of-order events; verify independent customers cannot access each other's data.
5. Validate database retention, incident diagnostics, load limits and support process. The initial SQLite implementation is for a single application instance on persistent disk. Multi-instance scaling requires a coordinated database/worker migration.
6. Obtain market-data permission, payment acceptance and reviewed business/legal details. Only then consider a separately reviewed production billing change.

Maintain successful and unsuccessful alert observations together. Simulated target/stop outcomes and cost assumptions are not actual customer executions, win probabilities or a promise of results.

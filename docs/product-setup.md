# Product setup and verification

Use `.env.example` as a list of environment variable names. Secret values remain on the API server. The example contains no working credentials. `dotnet run` does not automatically load `.env`; set variables in the shell/host configuration. The product Compose file intentionally starts only the local, unconfigured preview.

## Accounts and persistence

The API creates its initial Identity/customer schema in `Product:DatabasePath`. This includes accounts, watchlists, preferences, billing state, alert evidence and device delivery records. Data-protection keys live in `Product:DataProtectionKeysPath`. Both paths must be persistent, protected and backed up together. Losing encryption keys invalidates sessions and outstanding recovery links.

This release uses SQLite WAL and a single process containing the shared scanner and background notification worker. Use one application instance on a persistent volume. There are no placeholder memory accounts or client-controlled paid flags. Future schema edits require reviewed EF migrations: do not use `EnsureCreated` as a migration system or delete a real customer's database to apply a schema update.

On production hosting set `ASPNETCORE_ENVIRONMENT=Production` and `Product:PublicOrigin` to the canonical HTTPS address. Secure cookies require HTTPS. Deploy API and static UI on the same origin. For two-port local development, the frontend defaults to API port 5080 with credentials, and `Product:PublicOrigin` should point to the frontend at port 3000. Restrict allowed CORS origins to the actual frontend, and preserve query strings on email/checkout redirects.

Set `Product:Mail` with a verified SMTP sender. Account creation and local preferences work when email is unavailable, but the UI reports that recovery/verification is unavailable; billing requires confirmed email. Recovery tokens expire in one hour. Test confirmation, password reset, expiry, lockout, password changes and invalidated sessions before inviting customers. An SMTP configuration value alone is not proof of successful email delivery.

Implementation references: [ASP.NET Core Identity](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/identity?view=aspnetcore-8.0), [anti-forgery](https://learn.microsoft.com/en-us/aspnet/core/security/anti-request-forgery?view=aspnetcore-8.0), and [data-protection key storage](https://learn.microsoft.com/en-us/aspnet/core/security/data-protection/configuration/overview?view=aspnetcore-8.0).

## Test subscriptions

1. Resolve the provider fit questions in the [commercial launch review](commercial-launch-review.md). Test development is not commercial approval.
2. Create a Stripe sandbox/test product with a USD $29 recurring monthly price, and set `Product:Billing:PriceId`, test `SecretKey`, and `Enabled=true`.
3. Register the HTTPS `/api/product/billing/webhook` endpoint, or forward test events locally with Stripe CLI. Put that endpoint's signing secret in `Product:Billing:WebhookSecret`.
4. Subscribe to `checkout.session.completed`, `checkout.session.async_payment_succeeded`, `checkout.session.async_payment_failed`, `customer.subscription.created`, `customer.subscription.updated`, `customer.subscription.deleted`, `invoice.paid`, `invoice.payment_failed`, `invoice.payment_action_required`, and `invoice.finalization_failed`.
5. Configure the **test** customer portal to allow subscription cancellation. Verify hosted checkout and portal URLs open correctly and return to Account.
6. Exercise purchase, incomplete payment, failed renewal, scheduled cancellation, immediate cancellation, expiry, event retries and out-of-order events. Confirm Pro with `/api/product/me` in a fresh authenticated session. Visiting `?checkout=success` must not change entitlements.

The backend pins its Stripe API version and re-fetches current subscription/invoice state rather than treating an old event as current truth. Pro requires the configured price, an active subscription, a paid latest invoice and an unexpired period. Webhook IDs are committed transactionally with their effects. During provider outages events are not acknowledged as successful and must be retried. Monitor webhook failures through the provider dashboard and application logs.

Implementation references: [subscription webhooks](https://docs.stripe.com/billing/subscriptions/webhooks), [webhook signatures and event handling](https://docs.stripe.com/webhooks), [Checkout subscriptions](https://docs.stripe.com/payments/checkout/build-subscriptions), [customer portal](https://docs.stripe.com/customer-management).

## Notifications and installation

Set `Product:Push` with a generated VAPID public/private key pair and a valid operator contact URI as Subject, then enable it. Never reuse example credentials. The browser receives only the public key. Keep the private key stable; rotating it requires device subscriptions to be renewed.

Customers explicitly choose to enable notifications. The app checks support, requests permission, registers the service worker, and saves the resulting device subscription against the signed-in customer. Pro, onboarding, rule/watchlist limits, quiet hours and current entitlement are checked on the server. The test notification button creates a clearly marked test record; it does not claim a real market setup.

On iOS/iPadOS, Web Push is supported for Home Screen web apps starting with 16.4; installation and a direct user action are required. Other browsers/platforms have their own support and power/network limits. Notification permission alone does not prove a device subscription was saved. Push-service acceptance is **not** proof that the device displayed a notification, that the customer read it, or that delivery was instantaneous.

Lock-screen notifications contain a generic monitoring update, an opaque history link and an expiry timestamp. They never include the customer's rule name, symbol, price, direction or fee assumptions. A browser subscription can outlive a login cookie or an account switch, so private evidence is available only after signing in to the owning account. Disable any registered device from Alerts when it should stop receiving even these generic updates.

Alerts expire after the configured interval. The worker suppresses stale/invalidated setups, disabled devices/preferences, expired subscriptions and quiet-hours delivery. Retries use durable records and bounded delays; permanent endpoint expiry prompts re-enrollment. An interrupted network acknowledgement can still cause a duplicate push; stable alert IDs, topics and notification tags reduce repeats. Do not promise exactly-once or guaranteed delivery.

Test closed-tab behavior on real supported devices, expired subscriptions, permission denial, a revoked device, an offline device, quiet hours across midnight/time zones, a stale feed and application restart. Private API responses and account state are not placed in the service-worker cache.

References: [WebKit Web Push for iOS/iPadOS](https://webkit.org/blog/13878/web-push-for-web-apps-on-ios-and-ipados/), [Push API](https://developer.mozilla.org/en-US/docs/Web/API/Push_API), [service-worker notification clicks](https://developer.mozilla.org/en-US/docs/Web/API/ServiceWorkerGlobalScope/notificationclick_event).

## Retention and product meaning

Issue-time alert evidence is saved independently of mutable delivery/outcome diagnostics. No original alert is rewritten to look successful later. The UI distinguishes observations, heuristic scores, conditional setups and quote-based outcome observations from actual executions. This app has no execution integration. Cost assumptions include per-side fees/slippage and observed spread; actual fills and fees may differ.

The configurable history window controls accessible saved history. Confirm and document actual database cleanup/backup retention before launch. Account deletion authenticates the password and closes the test billing customer first, cancelling subscriptions and preventing pending checkout sessions from creating a subscription after local deletion. It then removes the account and owned data. Provider billing records and backups need a reviewed retention/deletion policy in the final privacy document.

Support requests are saved in the customer database with a receipt ID. The deployment operator must review `SupportRequests` using secured administrative database access until a support integration is configured. The preview does not send support email or promise a response time. Supply real support contact details before public launch.

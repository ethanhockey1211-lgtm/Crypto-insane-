# PrizePicks tracker

Open **PrizePicks** beside Crypto scanner and Stocks & ETFs, or use `/#prizepicks`.

The daily board ranks upcoming standard PrizePicks projections by market-implied hit chance. The custom analyzer accepts your own line and either actual recent game results or both sides of the same sportsbook's odds. Build an entry with 2–6 distinct players, inspect the chance that every selection hits, save an entry locally, and enter its result after the games.

## Connect daily picks

The existing .NET API hosts the feed adapter, so both the single Render deployment/static export and standalone web deployment remain supported. No new database or dependency is required.

1. Obtain a [The Odds API](https://the-odds-api.com/) key with player-prop and PrizePicks coverage. Coverage and request quotas depend on the account/plan. Do not paste the provider key into the dashboard or commit it.
2. In the existing API service's environment (Render → service → Environment), set `PrizePicks__ApiKey` to that key and redeploy. The provider key stays on the server. Any previously configured `PrizePicks__AccessToken` is unused and can be removed.
3. Open the PrizePicks tab, choose a league, and select **Refresh daily picks**. No access code or sign-in is required; anyone who can reach the dashboard can use the shared feed.

For local development, `PrizePicks__ApiKey` belongs to the API process, not `web/.env`. The existing `NEXT_PUBLIC_API_URL` points the web app at the API. Never add a provider key to a `NEXT_PUBLIC_*` variable.

Without a configured feed, custom analysis and local entry tracking remain available. There is no generated demo board or fabricated live evidence. A provider account was not configured during implementation; contract tests use fixtures rather than claim live feed verification.

## Coverage and freshness

- NFL passing/rushing/receiving yards, receptions and passing touchdowns; NBA/WNBA points, rebounds, assists, threes and PRA; MLB pitcher strikeouts, hits and total bases.
- “Today” is explicitly America/Chicago, including daylight saving time. Only upcoming games on that calendar date are ranked.
- Each scan requests that league's event list and up to 16 earliest upcoming events. Partial coverage is explicitly labeled with event counts; rankings are only among returned comparisons.
- The feed requests `prizepicks,draftkings,fanduel,betmgm` from the per-event odds endpoint. It does not scrape PrizePicks or request personal account data.
- Shared two-minute cache, serialized upstream scans, one-minute error cooldown, 12-second per-request timeout and 35-second total scan budget bound quota use. Refresh is manual and the existing API rate limit remains in place. Requests do not require a visitor credential. Built-in HTTP logging is removed for this named client because the provider requires the key in its query string; response errors never echo URLs or credentials.
- Source freshness comes from `bookmakers[].markets[].last_update`. Missing timestamps, quotes older than five minutes, and timestamps over one minute in the future are excluded. The UI rechecks time every 15 seconds so old or started picks disappear without refreshing. A failed refresh clears the displayed board.
- Standard half-point lines for the supported integer-valued stats only. Integer lines, Demons/Goblins/alternate markets, incomplete sportsbook pairs, and lines without two distinct matching sportsbooks are excluded from daily ranking.

## What a percentage means

Match event, league, player, stat and **exact numeric line**. For each conventional sportsbook, calculate `pMore = (1 / decimalOver) / ((1 / decimalOver) + (1 / decimalUnder))`. Then average the distinct books' probabilities. Rank the more likely side and show its evidence and book range. That range is disagreement between books, not a confidence interval. PrizePicks indicative prices are never used as forecast probabilities. This is not a backtested or calibrated forecast, and the ranking is not a profitability or expected-value claim.

For custom sportsbook odds, the user must enter both American prices for the same player, market and exact line from one book. A non-half-point line is conservatively treated as conditional on no tie and withheld from the combined all-hit calculation. Editing any custom input clears its old analysis.

For game history, ties are separate from hits and misses and remain in the denominator of strict hits. At least 5 and at most 200 actual completed results are required; DNPs should be excluded. The displayed estimate is `(hits + 1) / (games + 2)`. The historical 95% Wilson sampling interval is not a forecast interval. Injuries, minutes, role, opponent and selection bias are not modeled.

Risk bands are transparent display thresholds: 65%+ lower relative risk; 55–64% moderate; 40–54% high; below 40% very high. They are not calibrated ratings and do not imply safety.

An entry's product of probabilities is shown only for distinct players in identified, different games, with unexpired, unconditional evidence. It assumes independence. Same-game or unidentified-game entries show only mathematical dependence bounds `max(0, sum(p) - (n-1))` through `min(p)`. These bounds still rely on the supplied marginal estimates. Do not treat `1 - allHit` as probability of losing money: Flex, ties, DNPs, Reboots and contest payouts differ. No payout or profit table is hardcoded.

## Entry storage

Up to 100 saved entries and their user-entered outcomes are stored in this browser's local storage. They are not synchronized across devices and disappear if browser data is cleared. Saved estimates are historical snapshots, not refreshed forecasts. The tracker never places an entry, processes money, or settles it automatically.

## Sources and validation

- [Official bookmaker coverage and DFS indicative prices](https://the-odds-api.com/sports-odds-data/bookmaker-apis.html)
- [Official v4 event/odds API](https://the-odds-api.com/liveapi/guides/v4/)
- [Supported markets](https://the-odds-api.com/sports-odds-data/betting-markets.html)
- [Provider update intervals](https://the-odds-api.com/sports-odds-data/update-intervals.html)
- [PrizePicks payouts and ties](https://www.prizepicks.com/help-center/payouts)

Run `dotnet test` from the repo root and `pnpm typecheck`, `pnpm test`, `pnpm build` from `web`. Also verify `NEXT_OUTPUT=export pnpm build` for the single-image deployment. Tests cover access without a code, cache/quota protection, secret redaction, provider parsing, exact line matching, invalid/stale quotes, ties, history samples, and entry dependence.

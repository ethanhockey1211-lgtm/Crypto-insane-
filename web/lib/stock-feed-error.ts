const providerErrors: Readonly<Record<string, string>> = {
  "invalid-credentials": "An Alpaca endpoint URL was entered as a credential. In Render, put the API Key ID in Stocks__ApiKey and its matching Secret Key in Stocks__ApiSecret, then save and deploy. No endpoint setting is needed.",
  "provider-unauthorized": "Alpaca rejected the API credentials (401). Update both Stocks__ApiKey and Stocks__ApiSecret in Render using the same generated pair, then save and deploy. The endpoint URL and your personal access code do not belong in those fields.",
  "provider-forbidden": "Alpaca denied data access (403). Check that Stocks__ApiKey and Stocks__ApiSecret are a matching pair, and that the Alpaca account has IEX data access. Save and deploy after any change in Render.",
  "provider-request": "Alpaca rejected the scanner's data request. Refresh once; if it persists, report this message so the request can be checked.",
  "provider-rate-limit": "Alpaca's data request limit was reached. The scanner will retry on its next 30-second cycle.",
  "provider-unavailable": "Alpaca returned a service error. The scanner will retry on its next 30-second cycle.",
  "provider-timeout": "Alpaca did not respond in time. The scanner will retry on its next 30-second cycle.",
  "provider-network": "The scanner server could not connect to Alpaca. The scanner will retry on its next 30-second cycle.",
  "provider-response": "Alpaca returned data the scanner could not read. Refresh once; if it persists, report this message so the response can be checked.",
};

/** Render only our known diagnostic text, never an upstream body or credential. */
export function stockFeedError(status: number, body: unknown): string {
  if (status === 401 || status === 403) return "Access code rejected. Use the personal Stocks__AccessToken from your Render settings.";
  if (body && typeof body === "object" && "errorCode" in body && typeof body.errorCode === "string"
    && Object.prototype.hasOwnProperty.call(providerErrors, body.errorCode)) {
    return `${providerErrors[body.errorCode]} [${body.errorCode}]`;
  }
  if (status === 429) return "The data request limit was reached. The scanner will retry on its next 30-second cycle.";
  if (status === 503) return "Stock data is not configured. Add the Alpaca keys and personal access code in Render, then save and deploy.";
  if (status === 400) return "The stock watchlist request is invalid. Check your ticker symbols and refresh.";
  return `Stock data could not be loaded (HTTP ${status}). Refresh once; if it persists, report this message so the connection can be checked.`;
}

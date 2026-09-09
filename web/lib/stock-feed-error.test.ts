import { describe, expect, it } from "vitest";
import { stockFeedError } from "./stock-feed-error";

describe("stock feed error diagnostics", () => {
  it.each([
    ["invalid-credentials", "endpoint URL"],
    ["provider-unauthorized", "credentials (401)"],
    ["provider-forbidden", "access (403)"],
    ["provider-request", "data request"],
    ["provider-rate-limit", "request limit"],
    ["provider-unavailable", "service error"],
    ["provider-timeout", "did not respond"],
    ["provider-network", "could not connect"],
    ["provider-response", "could not read"],
  ])("explains %s without reflecting provider messages", (errorCode, expected) => {
    const message = stockFeedError(502, { errorCode, message: "secret-provider-body", rows: ["secret-key"] });
    expect(message).toContain(expected);
    expect(message).toContain(`[${errorCode}]`);
    expect(message).not.toContain("secret");
  });

  it.each([401, 403])("distinguishes personal access rejection (%s) from Alpaca authentication", status => {
    expect(stockFeedError(status, { errorCode: "provider-unauthorized" })).toContain("personal Stocks__AccessToken");
    expect(stockFeedError(status, {})).not.toContain("Alpaca");
  });

  it.each([null, "proxy HTML", {}, { errorCode: "secret-key", message: "secret-key" }, { errorCode: "__proto__" }, { errorCode: "toString" }, { errorCode: 401 }])("handles old servers, non-JSON errors and unknown diagnostics safely: %j", body => {
    expect(stockFeedError(502, body)).toContain("HTTP 502");
    expect(stockFeedError(502, body)).not.toContain("secret-key");
    expect(stockFeedError(502, body)).not.toContain("credentials");
  });

  it("retains useful HTTP fallbacks without structured diagnostics", () => {
    expect(stockFeedError(429, null)).toContain("request limit");
    expect(stockFeedError(503, null)).toContain("not configured");
    expect(stockFeedError(400, null)).toContain("ticker symbols");
    expect(stockFeedError(504, null)).toContain("HTTP 504");
  });
});

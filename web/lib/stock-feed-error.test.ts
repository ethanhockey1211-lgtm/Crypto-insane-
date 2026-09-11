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
    ["provider-http-error", "unexpected HTTP response"],
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


describe("Alpaca request diagnostics", () => {
  it.each(["history", "snapshot"] as const)("identifies the failing %s request and retry count", operation => {
    const message = stockFeedError(502, { errorCode: "provider-unavailable", diagnostic: { httpStatus: 503, operation, attempts: 2,
      requestId: "f6b7c39a-bcc1-4304-83ea-feb940c40c0c" } });
    expect(message).toContain(operation === "history" ? "minute-bar history" : "live snapshot");
    expect(message).toContain("HTTP 503, 2 attempts");
    expect(message).toContain("Request ID: f6b7c39a-bcc1-4304-83ea-feb940c40c0c");
  });

  it("preserves Alpaca's documented 32-character hexadecimal request ID", () => {
    const id = "0d29ba8d9a51ee0eb4e7bbaa9acff223";
    expect(stockFeedError(502, { errorCode: "provider-unavailable", diagnostic: { httpStatus: 500, operation: "history", attempts: 2, requestId: id } })).toContain(`Request ID: ${id}.`);
  });

  it("does not mistake a redirect for a provider outage", () => {
    const message = stockFeedError(502, { errorCode: "provider-http-error", diagnostic: { httpStatus: 302, operation: "history", attempts: 1 } });
    expect(message).toContain("HTTP 302, 1 attempt.");
    expect(message).not.toContain("service error");
  });

  it.each([null, "secret-key", { httpStatus: "secret-key" },
    { httpStatus: 503, operation: "secret-key", attempts: 2 }, { httpStatus: 503, operation: "history", attempts: "secret-key" },
    { httpStatus: 503.5, operation: "history", attempts: 2 }, { httpStatus: 503, operation: "history", attempts: 3 },
    { httpStatus: 503, operation: "history", attempts: 2, requestId: "secret-key" },
  ])("cannot reflect untrusted diagnostic fields: %j", diagnostic => {
    const message = stockFeedError(502, { errorCode: "provider-unavailable", diagnostic, message: "secret-key" });
    expect(message).not.toContain("secret-key");
    expect(message).not.toContain("Request ID:");
  });
});

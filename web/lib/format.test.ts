import { describe, expect, it } from "vitest";
import { fmtPrice } from "./format";

describe("tiny coin prices", () => {
  it("keeps nearby entry, stop and target distinguishable", () => {
    expect([0.00000360, 0.00000359, 0.00000363].map(fmtPrice))
      .toEqual(["0.0000036000", "0.0000035900", "0.0000036300"]);
    expect(fmtPrice(0)).toBe("0.0000000");
    expect(fmtPrice(null)).toBe("—");
  });
});

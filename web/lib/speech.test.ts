import { describe, expect, it } from "vitest";
import { SentenceBuffer } from "./speech";

function sentences(fragments: string[], flush = true) {
  const out: string[] = [];
  const buffer = new SentenceBuffer((s) => out.push(s));
  for (const f of fragments) buffer.push(f);
  if (flush) buffer.flush();
  return out;
}

describe("SentenceBuffer", () => {
  it("emits each sentence as soon as the next one starts", () => {
    const out: string[] = [];
    const buffer = new SentenceBuffer((s) => out.push(s));

    buffer.push("Hello there. How are");
    expect(out).toEqual(["Hello there."]);
    buffer.push(" you? Fine");
    expect(out).toEqual(["Hello there.", "How are you?"]);
  });

  it("keeps the last sentence until flush", () => {
    expect(sentences(["Tell me more"], false)).toEqual([]);
    expect(sentences(["Tell me more"])).toEqual(["Tell me more"]);
  });

  it("handles fragments split anywhere, including inside the punctuation", () => {
    expect(sentences(["Gre", "at!", " Wh", "at did you ", "see?", ' "Wow."', " Then"])).toEqual([
      "Great!",
      "What did you see?",
      '"Wow."',
      "Then",
    ]);
  });

  it("keeps closing quotes and brackets with their sentence", () => {
    expect(sentences(['She said "yes." (Really!) Okay.'])).toEqual(['She said "yes."', "(Really!)", "Okay."]);
  });

  it("does not split decimals or emit empty sentences", () => {
    expect(sentences(["It costs 3.50 euros.   ", "  "])).toEqual(["It costs 3.50 euros."]);
  });
});

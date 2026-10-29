import { describe, expect, it } from "vitest";
import { createSseParser, type SseEvent } from "./sse";

function parse(chunks: string[]) {
  const events: SseEvent[] = [];
  const parser = createSseParser((e) => events.push(e));
  for (const chunk of chunks) parser.push(chunk);
  return events;
}

describe("createSseParser", () => {
  const stream = 'event: delta\ndata: {"text":"Hel"}\n\nevent: delta\ndata: {"text":"lo."}\n\nevent: done\ndata: {"turnId":7,"text":"Hello."}\n\n';

  it("reads named events in order", () => {
    expect(parse([stream])).toEqual([
      { event: "delta", data: '{"text":"Hel"}' },
      { event: "delta", data: '{"text":"lo."}' },
      { event: "done", data: '{"turnId":7,"text":"Hello."}' },
    ]);
  });

  it("gives the same events however the text is split", () => {
    const whole = parse([stream]);
    for (let size = 1; size <= 7; size++) {
      const chunks = stream.match(new RegExp(`[\\s\\S]{1,${size}}`, "g"))!;
      expect(parse(chunks)).toEqual(whole);
    }
    // Split exactly between the two newlines that end an event.
    expect(parse([stream.slice(0, 34), stream.slice(34)])).toEqual(whole);
  });

  it("waits for the blank line before emitting", () => {
    expect(parse(['event: delta\ndata: {"text":"Hi"}\n'])).toEqual([]);
  });

  it("joins multi-line data", () => {
    expect(parse(['event: error\ndata: {"message":\ndata: "The tutor could not reply."}\n\n'])).toEqual([
      { event: "error", data: '{"message":"The tutor could not reply."}' },
    ]);
  });

  it("defaults the event name and skips events without data", () => {
    expect(parse(["event: ping\n\n", 'data: {"a":1}\n\n'])).toEqual([{ event: "message", data: '{"a":1}' }]);
  });
});

export interface SseEvent {
  event: string;
  data: string;
}

/**
 * Incremental parser for the API's server-sent events. Feed it decoded text as it arrives; it calls `onEvent`
 * for each complete event (blocks end with a blank line), however the text was split into chunks.
 * Multi-line `data:` fields are joined as-is (the API sends one JSON object per event), and events without
 * data are skipped.
 */
export function createSseParser(onEvent: (event: SseEvent) => void) {
  let buffer = "";
  return {
    push(chunk: string) {
      buffer += chunk;
      let boundary: number;
      while ((boundary = buffer.indexOf("\n\n")) >= 0) {
        const block = buffer.slice(0, boundary);
        buffer = buffer.slice(boundary + 2);

        let event = "message";
        let data = "";
        for (const line of block.split("\n")) {
          if (line.startsWith("event:")) event = line.slice(6).trim();
          else if (line.startsWith("data:")) data += line.slice(5).trim();
        }
        if (data) onEvent({ event, data });
      }
    },
  };
}

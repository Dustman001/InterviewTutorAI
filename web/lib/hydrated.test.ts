import { createElement } from "react";
import { renderToString } from "react-dom/server";
import { describe, expect, it } from "vitest";
import { useHydrated } from "./hydrated";

function Probe() {
  return createElement("span", null, useHydrated() ? "hydrated" : "server");
}

describe("useHydrated", () => {
  it("is false in the server-rendered HTML, so submit buttons start disabled", () => {
    expect(renderToString(createElement(Probe))).toContain("server");
  });
});

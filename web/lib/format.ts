import type { Session } from "./api";

export const CEFR_LEVELS = ["A1", "A2", "A2+", "B1", "B1+", "B2", "B2+", "C1", "C2"];

export function scenarioTitle(session: Pick<Session, "mode" | "scenario">): string {
  const { scenario } = session;
  if (session.mode === "Interview") {
    const role = scenario.role?.trim() || "Open role";
    return `Interview · ${[scenario.seniority?.trim(), role].filter(Boolean).join(" ")}`;
  }
  return `Conversation · ${scenario.topic?.trim() || "Free topic"}`;
}

export function formatDate(iso: string): string {
  return new Date(iso).toLocaleString(undefined, {
    month: "short",
    day: "numeric",
    hour: "2-digit",
    minute: "2-digit",
  });
}

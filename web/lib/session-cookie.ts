import type { NextResponse } from "next/server";

/** httpOnly cookie that holds the API's JWT. Browser scripts never see the token. */
export const SESSION_COOKIE = "tutorai_token";

/** Where the Next.js server reaches TutorAI.Api. Read at request time. */
export const apiUrl = process.env.TUTORAI_API_URL ?? "http://localhost:5080";

/**
 * Tokens live 15 minutes. Once one has less than this left, a fresh one is swapped in, so an active user
 * stays signed in and an idle one is signed out after at most 15 minutes.
 */
export const REFRESH_WHEN_LEFT_MS = 10 * 60 * 1000;

export interface TokenClaims {
  exp: number;
  role?: string;
}

export interface WebSession {
  /** The token to use for this request, or undefined when signed out. */
  token: string | undefined;
  claims: TokenClaims | null;
  /** A fresh token to store in the cookie. */
  renewed: { token: string; expiresAt: string } | null;
  /** The API refused the token (user disabled, deleted or signed out): clear the cookie. */
  revoked: boolean;
}

/**
 * Reads the token's claims without verifying the signature. Only good for routing decisions (redirect to
 * sign-in, when to refresh); the API verifies every token.
 */
export function readClaims(token: string | undefined): TokenClaims | null {
  if (!token) return null;
  try {
    const payload = token.split(".")[1];
    const json = JSON.parse(atob(payload.replace(/-/g, "+").replace(/_/g, "/")));
    return typeof json.exp === "number" && json.exp * 1000 > Date.now() ? json : null;
  } catch {
    return null;
  }
}

/** The request's session, refreshing the token first when it is close to expiring. */
export async function resolveSession(cookieToken: string | undefined): Promise<WebSession> {
  const claims = readClaims(cookieToken);
  if (!claims || !cookieToken || claims.exp * 1000 - Date.now() >= REFRESH_WHEN_LEFT_MS) {
    return { token: claims ? cookieToken : undefined, claims, renewed: null, revoked: false };
  }

  try {
    const response = await fetch(`${apiUrl}/api/auth/refresh`, {
      method: "POST",
      headers: { authorization: `Bearer ${cookieToken}` },
      cache: "no-store",
    });
    if (response.status === 401) {
      return { token: undefined, claims: null, renewed: null, revoked: true };
    }
    if (response.ok) {
      const data = await response.json();
      return { token: data.token, claims: readClaims(data.token), renewed: data, revoked: false };
    }
  } catch {
    // The API is unreachable; keep using the current token until it expires.
  }
  return { token: cookieToken, claims, renewed: null, revoked: false };
}

export function cookieOptions(expiresAt: string) {
  return {
    httpOnly: true,
    secure: process.env.NODE_ENV === "production",
    sameSite: "lax" as const,
    path: "/",
    expires: new Date(expiresAt),
  };
}

/** Stores a renewed token, or clears a revoked one, on the outgoing response. */
export function applySession(response: NextResponse, session: WebSession): NextResponse {
  if (session.renewed) {
    response.cookies.set(SESSION_COOKIE, session.renewed.token, cookieOptions(session.renewed.expiresAt));
  }
  if (session.revoked) response.cookies.delete(SESSION_COOKIE);
  return response;
}

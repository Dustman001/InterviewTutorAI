import { NextResponse, type NextRequest } from "next/server";
import { apiUrl, applySession, resolveSession, SESSION_COOKIE } from "@/lib/session-cookie";

/**
 * Forwards every /api/* call to TutorAI.Api with the signed-in user's JWT as a Bearer token.
 * Doing this here (rather than with a rewrite) lets the response carry a refreshed token cookie.
 * Responses are streamed through, so the tutor's server-sent events arrive as they are produced.
 */
async function forward(request: NextRequest): Promise<NextResponse> {
  const session = await resolveSession(request.cookies.get(SESSION_COOKIE)?.value);

  const headers = new Headers();
  for (const name of ["content-type", "accept", "idempotency-key"]) {
    const value = request.headers.get(name);
    if (value) headers.set(name, value);
  }
  // Only the cookie's token is forwarded; a browser-supplied Authorization header is ignored.
  if (session.token) headers.set("authorization", `Bearer ${session.token}`);

  const hasBody = request.method !== "GET" && request.method !== "HEAD";
  let upstream: Response;
  try {
    upstream = await fetch(new URL(request.nextUrl.pathname + request.nextUrl.search, apiUrl), {
      method: request.method,
      headers,
      body: hasBody ? await request.arrayBuffer() : undefined,
      // When the learner interrupts the tutor, stop the API's stream too.
      signal: request.signal,
      cache: "no-store",
      redirect: "manual",
    });
  } catch {
    if (request.signal.aborted) return new NextResponse(null, { status: 499 });
    return applySession(
      NextResponse.json({ error: "The API is unavailable. Is TutorAI.Api running?" }, { status: 502 }),
      session,
    );
  }

  const responseHeaders = new Headers();
  for (const name of ["content-type", "cache-control", "location", "x-accel-buffering"]) {
    const value = upstream.headers.get(name);
    if (value) responseHeaders.set(name, value);
  }

  const response = applySession(
    new NextResponse(upstream.body, { status: upstream.status, headers: responseHeaders }),
    session,
  );
  // The API refused our token (user disabled, deleted, or signed out). Drop the cookie, or /login would
  // keep sending the user back to pages whose API calls fail.
  if (upstream.status === 401 && session.token) response.cookies.delete(SESSION_COOKIE);
  return response;
}

export const GET = forward;
export const POST = forward;
export const PUT = forward;
export const PATCH = forward;
export const DELETE = forward;

import { NextResponse } from "next/server";
import { apiUrl, cookieOptions, SESSION_COOKIE } from "@/lib/session-cookie";

/** Signs in against TutorAI.Api and keeps the returned JWT in an httpOnly cookie. */
export async function POST(request: Request) {
  let body: { username?: string; password?: string };
  try {
    body = await request.json();
  } catch {
    return NextResponse.json({ error: "Enter a username and password." }, { status: 400 });
  }

  let response: Response;
  try {
    response = await fetch(`${apiUrl}/api/auth/login`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ username: body.username, password: body.password }),
      cache: "no-store",
    });
  } catch {
    return NextResponse.json({ error: "The API is unavailable. Is TutorAI.Api running?" }, { status: 502 });
  }

  const data = await response.json().catch(() => null);
  if (!response.ok) {
    return NextResponse.json(
      { error: typeof data?.error === "string" ? data.error : `Sign-in failed (${response.status}).` },
      { status: response.status },
    );
  }

  const result = NextResponse.json({ user: data.user });
  result.cookies.set(SESSION_COOKIE, data.token, cookieOptions(data.expiresAt));
  return result;
}

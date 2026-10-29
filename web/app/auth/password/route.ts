import { NextResponse, type NextRequest } from "next/server";
import { apiUrl, cookieOptions, SESSION_COOKIE } from "@/lib/session-cookie";

/**
 * Changes the signed-in user's password. The API signs out every other session and returns a fresh
 * token for this one, which replaces the cookie so this browser stays signed in.
 */
export async function POST(request: NextRequest) {
  const token = request.cookies.get(SESSION_COOKIE)?.value;
  if (!token) {
    return NextResponse.json({ error: "Your sign-in has expired." }, { status: 401 });
  }

  let body: { currentPassword?: string; newPassword?: string };
  try {
    body = await request.json();
  } catch {
    return NextResponse.json({ error: "Enter your current and new password." }, { status: 400 });
  }

  let response: Response;
  try {
    response = await fetch(`${apiUrl}/api/auth/password`, {
      method: "POST",
      headers: { "Content-Type": "application/json", authorization: `Bearer ${token}` },
      body: JSON.stringify({ currentPassword: body.currentPassword, newPassword: body.newPassword }),
      cache: "no-store",
    });
  } catch {
    return NextResponse.json({ error: "The API is unavailable. Is TutorAI.Api running?" }, { status: 502 });
  }

  const data = await response.json().catch(() => null);
  if (!response.ok) {
    const result = NextResponse.json(
      { error: typeof data?.error === "string" ? data.error : `Changing the password failed (${response.status}).` },
      { status: response.status },
    );
    if (response.status === 401) result.cookies.delete(SESSION_COOKIE);
    return result;
  }

  const result = NextResponse.json({ user: data.user });
  result.cookies.set(SESSION_COOKIE, data.token, cookieOptions(data.expiresAt));
  return result;
}

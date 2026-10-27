import { NextResponse } from "next/server";
import type { NextRequest } from "next/server";
import { applySession, resolveSession, SESSION_COOKIE } from "@/lib/session-cookie";

/**
 * Pages only (/api/* goes through app/api/[...path]/route.ts):
 * keeps an active user's token fresh, sends signed-out visitors to /login, and keeps non-admins out of
 * /users. A convenience, not the security boundary; the API checks every request.
 */
export async function proxy(request: NextRequest) {
  const { pathname, search } = request.nextUrl;

  if (pathname.startsWith("/auth/")) {
    return NextResponse.next();
  }

  const session = await resolveSession(request.cookies.get(SESSION_COOKIE)?.value);
  const signedIn = session.claims !== null;

  let response: NextResponse;
  if (pathname === "/login") {
    response = signedIn ? NextResponse.redirect(new URL("/", request.url)) : NextResponse.next();
  } else if (!signedIn) {
    const login = new URL("/login", request.url);
    if (pathname !== "/") login.searchParams.set("next", pathname + search);
    response = NextResponse.redirect(login);
  } else if (pathname.startsWith("/users") && session.claims?.role !== "Admin") {
    response = NextResponse.redirect(new URL("/", request.url));
  } else {
    response = NextResponse.next();
  }

  return applySession(response, session);
}

export const config = {
  matcher: ["/((?!api/|_next/static|_next/image|favicon.ico|.*\\.(?:svg|png|jpg|ico)$).*)"],
};

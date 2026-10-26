# Sign-in with ASP.NET Core Identity

ASP.NET Core Identity on Entity Framework Core and SQLite, with two single-page apps:

- **`/login`**: the login app, `Client/login/`. The only page anyone can open.
- **everything else**: the main app, `Client/`. Signed-out visitors are sent to `/login`; signed-in
  users get the page with their name, roles and claims passed in.

It follows the "backend for frontend" pattern: the session lives on the server, the browser holds
only an HttpOnly cookie that refers to it, and the app calls its API on the same origin. No token,
password or other credential ever reaches JavaScript, so a script injected into the page has
nothing to steal.

```bash
dotnet dev-certs https --trust   # once per machine: the app is HTTPS only
nite watch                       # or: dotnet run
```

Open <https://localhost:7655> and sign in as **admin** / **admin** (in the `Admin` role) or
**benson** / **hedges**. The first run creates `app.db` and the two users from the initial
migration.

> [!WARNING]
> Those passwords are published here, so the app **refuses to start outside Development** while
> either user still has one. Change them (or delete the users) before deploying anything built
> from this sample.

## How it fits together

| Piece | Where |
|---|---|
| Users, roles and claims | `Data/Seed.cs`, applied by the migration in `Data/Migrations`. The password hashes are constants made with `PasswordHasher`, so the migration never changes; seeding skips Identity's password rules, which would reject passwords this short at sign-up. `Security/DemoUsers.cs` is the check that stops a deployment with them. |
| Session cookie | `__Host-session`: HttpOnly, Secure, `SameSite=Strict`, signed out after 30 idle minutes. The cookie holds only a random key; the session itself is kept on the server (`Security/ServerSideSessions.cs`, encrypted with Data Protection), so signing out ends it even for a copy of the cookie. A changed password or role reaches signed-in sessions within a minute. |
| Pages | Everything needs a signed-in user unless it says otherwise (a fallback authorization policy). `MapNiteSpa("login/index.html", path: "/login").AllowAnonymous()` and the built client files are the exceptions. |
| What the page knows | `NiteOptions.ClientConfig` in `Program.cs` writes a form token and, once signed in, the user's name, roles and claims (without Identity's security stamp) into each page. The app reads them with `import { config } from "nite:config"`; `Client/nite-env.d.ts` types them. None of it is a credential. Pages carrying it are sent `Cache-Control: private, no-store`. |
| Forged requests | Every `POST` and `DELETE` must carry the form token in an `X-CSRF-Token` header, checked against the `__Host-csrf` cookie (`Security/Csrf.cs`); `Client/api.ts` adds it. `SameSite=Strict` already stops most cross-site requests; the token covers the rest, such as a sibling subdomain. |
| Sign-in | `POST /account/login` checks the password and starts the session; the login app then goes back to `ReturnUrl`. One answer for a wrong password, an unknown user and a locked account, in about the same time. Ten attempts a minute per address, and five wrong passwords lock the account for 15 minutes. `POST /account/logout` ends the session. |
| API | `/api/*`: notes per user in SQLite, and `/api/users` for the `Admin` role. Signed-out calls get 401, not a redirect, and `Client/api.ts` sends the user to `/login`. |
| Headers | `Security/SecurityHeaders.cs` sends a strict Content Security Policy with a fresh nonce per request, so only the page's own scripts run; `NiteOptions.CspNonce` puts the nonce on the scripts and styles NiteFx writes, in Development (hot updates, the error overlay) as well as in production. Also `nosniff`, `X-Frame-Options: DENY`, a referrer policy, a permissions policy, and cross-origin opener and resource policies, plus HSTS and HTTPS redirection outside Development. |

## Before you build on it

- **Sessions are in memory** (`AddDistributedMemoryCache`), so a restart signs everyone out and
  they are not shared between servers. Use a shared `IDistributedCache`, such as Redis or SQL
  Server, for more than one instance.
- **Data Protection keys** encrypt the sessions and form tokens. On more than one server, or in
  containers, persist them somewhere shared (`AddDataProtection().PersistKeysTo...`).
- **Behind a proxy**, the rate limit sees the proxy's address. Configure forwarded headers
  (`UseForwardedHeaders`) for the proxies you trust.
- **SQLite** suits a sample. Switch the provider and recreate the migration for anything else.
- **HSTS** is left off for `localhost` by ASP.NET Core, so you will only see the header on a real
  host name.

To recreate the migration after changing the model: `dotnet ef migrations add <Name> -o Data/Migrations`.

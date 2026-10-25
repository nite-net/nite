# Sign-in with ASP.NET Core Identity

ASP.NET Core Identity on Entity Framework Core and SQLite, with two single-page apps:

- **`/login`**: the login app, `Client/login/`. The only page anyone can open.
- **everything else**: the main app, `Client/`. Signed-out visitors are sent to `/login`; signed-in
  users get the page with their claims and a bearer token for the API passed in.

```bash
nite watch      # or: dotnet run
```

Sign in as **admin** / **admin** (in the `Admin` role) or **benson** / **hedges**. The first run
creates `app.db` and the two users from the initial migration.

## How it fits together

| Piece | Where |
|---|---|
| Users, roles and claims | `Data/Seed.cs`, applied by the migration in `Data/Migrations`. The password hashes are constants made with `PasswordHasher`, so the migration never changes; seeding skips Identity's password rules, which would reject passwords this short at sign-up. |
| Pages | Protected by Identity's cookie and a fallback authorization policy: everything needs a signed-in user unless it says otherwise. `MapNiteSpa("login/index.html", path: "/login").AllowAnonymous()` is the exception. |
| Sign-in | `POST /account/login` (anonymous) checks the password and sets the cookie; the login app then goes back to `ReturnUrl`. `POST /account/logout` clears it. |
| Claims and token | `NiteOptions.ClientConfig` in `Program.cs` writes the user's name, roles and claims (without Identity's security stamp) and a bearer token, valid for an hour, into each page. The app reads them with `import { config } from "nite:config"`; `Client/nite-env.d.ts` types them. |
| API | `/api/*` takes bearer tokens only (the cookie alone gets 401): notes per user in SQLite, and `/api/users` for the `Admin` role. `Client/api.ts` sends the token, and reloads the page for a fresh one when it expires. |

Built client files are public (`MapStaticAssets().AllowAnonymous()`): the login page needs its
own, and they hold no data. Pages carrying claims are sent `Cache-Control: private, no-cache`.

To recreate the migration after changing the model: `dotnet ef migrations add <Name> -o Data/Migrations`.

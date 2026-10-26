# Nite

TypeScript and React for ASP.NET Core without a Node.js toolchain. `src/Nite` is the library,
`src/Nite.Cli` the `nite` tool, `src/Nite.Templates` the `dotnet new` templates, `tests/Nite.Tests`
the tests, and `samples/` runnable apps. The docs site is a separate repo,
[nite-net/nite-net.github.io](https://github.com/nite-net/nite-net.github.io).

## Building and testing

```bash
export PATH=$HOME/.dotnet:$HOME/.dotnet/tools:$PATH DOTNET_ROOT=$HOME/.dotnet
dotnet build Nite.slnx               # everything, samples included; keep it warning-free
dotnet test tests/Nite.Tests
dotnet run --project src/Nite.Cli -- <command>   # the CLI from source
```

- `dotnet ef` needs `DOTNET_ROOT` set (above) to find the runtime.
- To check a sample in Production, `dotnet publish` it and run the published output.
  `dotnet run` outside Development does not load static web assets, so `MapStaticAssets` answers
  compressed requests with an empty `200` and module scripts fail with a MIME type of `""`.
- Generated output (`wwwroot/_nite`, `app.db`, logs) is not committed; delete it after testing.

## Versions and releases

The version comes from the git tag: CI builds `v0.8.0` as 0.8.0 and publishes to NuGet. Leave
`<Version>` in `Directory.Build.props` alone. Tag pushes do not work from cloud sessions, so the
maintainer pushes tags. Record changes in the docs repo's `docs/changelog.md`.

## Writing

- **Nite** is a proper noun: capitalize it in prose, including docs and comments. Lowercase is
  for code and commands only (`nite watch`, `nite.json`, `/_nite`, `nite:config`).
- The `(n)ite` in the logo is a visual joke; do not write the name that way.

## Stopping processes you started

Never `pkill -f` (or `pgrep -f | kill`) with a pattern that appears in your own command line: `-f`
matches full command lines, including the shell running the command, so it kills itself (exit
code 144). Keep the PID of what you start and kill exactly that:

```bash
dotnet run --project samples/Nite.Spa & echo $! > app.pid
kill "$(cat app.pid)"
```

If you must match by pattern, bracket one character so the pattern does not match itself:
`pkill -f '[N]ite.Spa'`. That only protects the pattern's own text: a command that also
mentions the name elsewhere (a `cd` into its folder, a path) still matches, so prefer the PID.

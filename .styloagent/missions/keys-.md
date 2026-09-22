# keys- : minted API keys

You own the change that moves principals out of configuration and into a Host store, and the
`stylomail key create|list|revoke` CLI that mints them. The design already exists and the guards below
are requirements that were ruled on, not suggestions.

## Why this is a separate lane

`ingress-` built the rest of the management surface and deliberately did not start this one. Its
reasoning, which I am adopting: this changes the **authentication path for both the HTTP surface and
the SMTP submission listener**, so it has to land whole. A half-built credential path is a hole rather
than a gap. You are starting from a clean context on purpose. Do not rush it.

## Read first, in this order

1. `.styloagent/channel/saved-context/ingress--context.md`, the section on the ruling. `ingress-`
   recorded it verbatim and it is the authority for this lane.
2. `docs/console-management-design.md`, the sections "Nouns", "The key CLI", and decision 3 under
   "The three decisions this rests on".
3. `.styloagent/spec.md` section 11 for the storage and privacy rules, and the secret handling block at
   the top of `.gitignore`.
4. `src/StyloMail.Host/Auth/PrincipalDirectory.cs` and `HostAuthentication.cs` for what exists today.

## The guards, all of them requirements

- **Precedence is total, and never merged.** Store first, environment as fallback. Where a principal
  exists in both, the store entry wins **wholesale**: do not union privileges and do not union keys
  across the two sources. A union is how an environment entry silently re-widens a privilege an
  operator deliberately narrowed.
- **Two sources is acceptable only because it is visible.** `key list` must report, per principal,
  which source resolved it: `store` or `environment`. Do not schedule a deprecation. Environment
  principals are frozen as read-only: not editable and not revocable from the CLI or the console.
- **`key revoke` refuses on an environment principal, and names the configuration that owns it.** A
  refusal that does not say where to go is a no-op with better manners.
- **The digest is a slow KDF, not a bare hash.** A single SHA-256 of an API key is offline-crackable
  from the store, and a store of crackable digests is a plaintext store with extra steps. Per-key salt,
  iterated or memory-hard, and compare in constant time.
- **Revocation takes effect immediately.** If resolution is cached anywhere, state the cache lifetime
  in the design and make `revoke` evict. A revocation a cache serves past its moment is the same defect
  one layer down.
- **`key create` prints the value once, to stdout, and to nothing else.** No file-writing flag, no
  `--output`, never a log, never stderr, and print a line saying it will not be shown again. The value
  is never recoverable and only the digest is stored. `key list` shows principal, tenant and
  privileges, and never the key or its digest.

## Constraints

- **Never print, log, or commit a credential value.** Do not read or reference `jevkey.pvt`. If you
  need a test key, generate one in the test.
- **Do not run `git add` or `git commit`.** That is mine. Leave your work in the tree and report; I
  verify the solution and commit the lane.
- **Work only in your lane.** `src/StyloMail.Host` files for the principal store, the CLI and the auth
  path. `ingress-` still owns the rest of the Host, so do not refactor around yourself and do not edit
  the management routes, the listings or the delivery worker. If you need a change in another project,
  report it instead of making it.
- **Never `git commit --amend` or `git reset`** in this tree. Several agents work in it concurrently.
- Build with `export DOTNET_ROOT=/usr/local/share/dotnet` and
  `export PATH="/usr/local/share/dotnet:$PATH"`. The solution is `StyloMail.slnx`. Analyzers are
  **errors**. Judge your own work with
  `dotnet test tests/StyloMail.Host.Tests/StyloMail.Host.Tests.csproj`; watching the whole solution is
  my job.

## Done when

- The store holds digests only, with the KDF and constant-time comparison above, and resolution is
  store-first with wholesale precedence.
- The three CLI verbs behave as specified, including the refusal on an environment principal.
- The **SMTP submission listener and the HTTP surface both authenticate through the same resolved
  principal**, and you have shown that by probing a running host rather than by reading the composition
  root. `docs/running.md` tells you how to run it.
- Tests are green and you have reported to `overview-` with: files created, test count, exactly what
  you probed and what came back, and anything you could not verify.

Report anything you find that belongs to another lane rather than fixing it yourself.
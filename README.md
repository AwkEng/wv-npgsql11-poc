# Wolverine + Npgsql 11 repro

Minimal repro for `WolverineFx.Postgresql` failing to load on Npgsql 11.0.0.

Npgsql 11.0.0 removes `NpgsqlConnectionStringBuilder.Multiplexing` and
`.WriteCoalescingBufferThresholdBytes`. `PostgresqlMessageStore.Describe()` still reads both, so the
type fails to JIT and the host dies before it starts:

```
System.MissingMethodException: Method not found: 'Boolean Npgsql.NpgsqlConnectionStringBuilder.get_Multiplexing()'.
   at Wolverine.Postgresql.PostgresqlMessageStore.Describe()
```

No Postgres server is required — the failure happens before the driver opens a socket.

`ISSUE.md` is the write-up intended for the Wolverine issue tracker.

## Reproduce

```bash
dotnet run                    # Npgsql 11.0.0 -> MissingMethodException, exit 42
dotnet run -p:NPG=10.0.4      # Npgsql 10.0.4 -> reaches the connection attempt, exit 0
```

With Npgsql 10.0.4 the run gets as far as the connection attempt and reports
`SocketException: No connection could be made because the target machine actively refused it`,
which is the expected outcome when nothing is listening on the (deliberately unreachable) port in
`Program.cs`. With Npgsql 11.0.0 it never gets that far.

Static code generation fails the same way, which is what makes it a build break as well as a
startup break:

```bash
dotnet run -- codegen write   # Npgsql 11.0.0 -> same MissingMethodException
```

## Fix (verified, both directions)

`TryGetValue(string, out object)` is part of the connection string builder's public surface on every
Npgsql version — Npgsql declares it on both 10.0.4 and 11.0.0 — and it answers the keyword on the versions
that still know it. Reading the two options through that dictionary instead of the removed properties keeps
the diagnostics entries and the LISTEN guard on Npgsql 10.x and simply drops them on 11.0.0, where the
token no longer exists. The assembly still compiles against Npgsql 10.0.3 and loads on 11.0.0 — **no
dependency change needed**.

```csharp
if (builder.TryGetValue("multiplexing", out var multiplexing) && multiplexing is true) { /* guard */ }
```

Two builds, one connection string. This repo's console app pins the **published, unpatched**
`WolverineFx.Postgresql` 6.64.2, so it is the repro; the second set comes from the same `Program.cs` against
the patched branch through a `ProjectReference`, which is not in this repo because it points at a local
checkout.

```text
unpatched 6.64.2 (this repo), Npgsql 10.0.4      -> Describe() OK, entries present
  Multiplexing                         False  type=Boolean raw=Boolean  subject=Npgsql.NpgsqlConnectionStringBuilder.Multiplexing
  WriteCoalescingBufferThresholdBytes  1000  type=Numeric raw=Int32  subject=Npgsql.NpgsqlConnectionStringBuilder.WriteCoalescingBufferThresholdBytes

unpatched 6.64.2 (this repo), Npgsql 11.0.0      -> REPRODUCED: MissingMethodException get_Multiplexing()

patched branch, Npgsql 10.0.4                    -> Describe() OK, entries present (lines identical to above)

patched branch, Npgsql 11.0.0                    -> Describe() OK
  Multiplexing                         <absent>
  WriteCoalescingBufferThresholdBytes  <absent>
```

The two "entries present" blocks are byte-for-byte identical, which is the fidelity check that matters here:
the fix rebuilds each `OptionsValue` by hand from the connection string dictionary rather than through
`OptionsValue.Read`, so `Value`, `Type`, `RawValue`'s CLR type and `Subject` all have to come out the same.
They do, because `TryGetValue` hands back the typed value rather than a string and `WriteValue` derives
`PropertyType.Boolean` / `PropertyType.Numeric` from it.

Deletion is the other option and works too. On 11.0.0 the two are indistinguishable, since the driver knows
neither keyword and the entries are absent either way. The difference is confined to 10.x, where deletion
also removes the two diagnostics entries and the queue-notifications guard, so it is not
behaviour-preserving there.

An IL patch also works for consumers who cannot wait for a release: the two fatal reads are
compiler-generated expression-tree `ldtoken`s inside `Describe()` (not `callvirt`), and retargeting just
those two tokens to same-return-type Npgsql 11 properties lets the host start and `codegen write`
complete. `Wolverine.Postgresql` is not strong-named (`PublicKeyToken=null`), so no re-signing is needed.
There is no runtime shim: `Describe()` is `public virtual` on a non-sealed class, but
`PostgresqlMessageStore` is `internal` and Wolverine registers the store itself, so subclassing is out.

## Related: a connection string that still says Multiplexing=true

Separate from the JIT failure, on Npgsql 11 the keyword is rejected when the builder is constructed:

```text
new NpgsqlConnectionStringBuilder("Host=h;Multiplexing=true")
  ArgumentException: Couldn't set multiplexing (Parameter 'multiplexing')
 ---> KeyNotFoundException: The given key was not present in the dictionary.
```

`Describe()` builds a builder from the connection string, so an app carrying the keyword in its own
string hits this on 11 whatever Wolverine does. Users upgrading need to drop the keyword.

## Scope

Only two members are missing, both diagnostic-only, and they are read from one method:

```csharp
// src/Persistence/Wolverine.Postgresql/PostgresqlMessageStore.cs, Describe()
descriptor.Properties.Add(OptionsValue.Read(builder, x => x.Multiplexing));
descriptor.Properties.Add(OptionsValue.Read(builder, x => x.WriteCoalescingBufferThresholdBytes));
```

Checked by scanning every Npgsql member each consumer assembly references, against Npgsql 11.0.0 GA
and 11.0.0-preview.1:

| assembly | members missing on Npgsql 11 |
| --- | --- |
| `Wolverine.Postgresql` 6.20.0, 6.30.0, 6.40.0, 6.44.0, 6.47.0 … 6.64.2, 7.0.0-alpha.1 … alpha.3 | `Multiplexing`, `WriteCoalescingBufferThresholdBytes` |
| `Wolverine` (core) 6.64.2 | none |
| `Marten` 10.0.0-alpha.7 | `Multiplexing` |
| `Aspire.Npgsql.EntityFrameworkCore.PostgreSQL` 17.0.0-preview.1 | none |
| `Npgsql.EntityFrameworkCore.PostgreSQL` 10.0.3 and 11.0.0-rc.3 | `Multiplexing` |
| `Npgsql.EntityFrameworkCore.PostgreSQL` 11.0.0 GA | none |

`Weasel.Postgresql` reads `get_Multiplexing` from 10.0.0-alpha.4 through alpha.13 (not in 9.49.0, 9.50.0, or
10.0.0-alpha.1 … alpha.3), but from `masterTakesSynchronousIo()` behind the public
`FindOrCreateDatabaseSynchronously()`, which no Wolverine code path calls — so it is not part of *this*
startup failure. `Marten` reaches the same member on its own, though: `ShardedTenancy.isMultiplexing()`, and
`SingleServerMultiTenancy.GetTenant` on an uncached tenant goes through the Weasel path above (Marten #5985).
Reproducible with no database, on a closed port (this is a second app, not this repo's console program):

```csharp
var store = DocumentStore.For(o => o.MultiTenantedWithSingleServer(master));
store.Tenancy.GetTenant("green");
```

```text
Npgsql 11.0.0 -> MissingMethodException … get_Multiplexing()
Npgsql 10.0.4 -> reaches the socket: SocketException, connection refused
```

So a Wolverine app using `Wolverine.Marten` with single-server multi-tenancy has a second Npgsql-11 blocker
that this fix does not clear. See `ISSUE.md`.

`ISSUE.md` is the write-up intended for the Wolverine issue tracker.

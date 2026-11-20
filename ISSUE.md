# Wolverine.Postgresql cannot start on Npgsql 11

Filed upstream as https://github.com/JasperFx/wolverine/issues/5725 with PR
https://github.com/JasperFx/wolverine/pull/5726. This file is the issue body verbatim; the repro in this repo
is the console app in `Program.cs`.

---

**Wolverine:** 6.64.2 (also 7.0.0-alpha.1/-alpha.2/-alpha.3, and current `main` / `7.0` branches)
**Npgsql:** 11.0.0, GA 2026-11-20. Documented removal on the driver side, not a Npgsql bug.
**Impact:** blocks host startup *and* the build for any app with Postgres persistence or transport.
**Repro:** https://github.com/AwkEng/wv-npgsql11-poc — two-file console app, no database required.
**Patch:** https://github.com/AwkEng/wolverine/tree/gh-5725-npgsql-11-multiplexing — one commit against `main`, 2 files, +33/-3.

Npgsql 11.0.0 removes `NpgsqlConnectionStringBuilder.Multiplexing` and
`.WriteCoalescingBufferThresholdBytes` ([npgsql#6457](https://github.com/npgsql/npgsql/pull/6457),
listed under "Removed multiplexing" in the [11.0 breaking
notes](https://www.npgsql.org/doc/release-notes/11.0.html)). `PostgresqlMessageStore.Describe()` still
reads both, so the method fails to JIT and the host dies before it starts:

```
System.MissingMethodException: Method not found: 'Boolean Npgsql.NpgsqlConnectionStringBuilder.get_Multiplexing()'.
   at Wolverine.Postgresql.PostgresqlMessageStore.Describe()
   ...
   at Wolverine.Postgresql.Transport.PostgresqlTransport..ctor(...)   // store.Describe().DatabaseUri()
```

The line number in the trace (1017 at tag V6.64.2, the opening brace of `Describe()`) is where JIT token
resolution fails, not where the reads are — the reads are at 1077/1078. The whole method body is compiled
up front, so one unresolvable token fails the method.

The breaking-change text asks consumers to "remove any assignments to the corresponding connection string
builder properties", which is exactly what the change below does — so the fix direction follows the driver's
own guidance rather than working around it.

## Reproduce

```bash
dotnet run                      # Npgsql 11.0.0  -> MissingMethodException, exit 42
dotnet run -p:NPG=10.0.4        # Npgsql 10.0.4  -> reaches the connection attempt, exit 0
```

No Postgres server is needed: with 10.0.4 the run reaches the socket and fails with
`SocketException: No connection could be made because the target machine actively refused it`,
which is the expected "no server here" outcome. With 11.0.0 it never gets that far.

The 10.0.4 run also prints the two diagnostics entries the control reads out of `Describe()`, which are what
a fix that deletes the two reads would drop on that driver version:

```text
Describe() OK  engine=PostgreSQL host=127.0.0.1 port=5499
  Multiplexing                         False  type=Boolean raw=Boolean  subject=Npgsql.NpgsqlConnectionStringBuilder.Multiplexing
  WriteCoalescingBufferThresholdBytes  1000  type=Numeric raw=Int32  subject=Npgsql.NpgsqlConnectionStringBuilder.WriteCoalescingBufferThresholdBytes
```

The *Options considered* section below compares these field by field against the patched branch.

## Where

`src/Persistence/Wolverine.Postgresql/PostgresqlMessageStore.cs`, in `Describe()`. Two lines,
both diagnostic-only:

```csharp
descriptor.Properties.Add(OptionsValue.Read(builder, x => x.Multiplexing));                          // 1077 at tag V6.64.2
descriptor.Properties.Add(OptionsValue.Read(builder, x => x.WriteCoalescingBufferThresholdBytes));    // 1078 at tag V6.64.2
```

`Describe()` is not lazily reached from a diagnostics endpoint only. In 6.64.2 it is called from
`PostgresqlMessageStore.ServerId` (`DatabaseServerId.For(Describe())`) and from the
`PostgresqlTransport` constructor (which sets `Name` from `Describe().DatabaseUri()`), so it runs
during host startup in every app that turns on Postgres durability. In an app using the Wolverine
Aspire integration this also fails the build, because the `codegen write` step runs the same host
startup as a pre-build target.

`PostgresqlQueueNotificationManager.RegisterAsync` has a third read of `Multiplexing`, used to
refuse multiplexed data sources for LISTEN sessions. It is behind the opt-in queue-notifications
feature, so it is not in the repro. It needs a decision rather than a port: multiplexing is gone from
the driver, but the guard is still meaningful on the 10.x line, and `docs/guide/durability/postgresql.md`
documents that behaviour — which is why the patch below keeps it working on 10.x instead of deleting it.

## A second, separate trap for apps

Independent of the JIT failure, on Npgsql 11 the *keyword* is rejected when a connection string is parsed:

```csharp
new NpgsqlConnectionStringBuilder("Host=h;Multiplexing=true");
// ArgumentException: Couldn't set multiplexing (Parameter 'multiplexing')
```

`Describe()` builds a builder from the incoming connection string, so an app carrying
`Multiplexing=true` in its own configuration throws on 11 whatever Wolverine does. Apps upgrading must
delete the keyword even from strings where it was `false` — as the driver's notes say, "even if
multiplexing was disabled".

## Verified scope

Scanned every Npgsql member each consumer assembly references, against Npgsql 11.0.0 GA and
11.0.0-preview.1. Nothing beyond these two members is missing anywhere in the stack:

| assembly | members missing on Npgsql 11 |
| --- | --- |
| `Wolverine.Postgresql` 6.20.0, 6.30.0, 6.40.0, 6.44.0, 6.47.0 … 6.64.2, 7.0.0-alpha.1 … alpha.3 | `Multiplexing`, `WriteCoalescingBufferThresholdBytes` |
| `Wolverine` (core) 6.64.2 | none |
| `Aspire.Npgsql.EntityFrameworkCore.PostgreSQL` 17.0.0-preview.1 | none |
| `Marten` 10.0.0-alpha.7 | `Multiplexing` |
| `Npgsql.EntityFrameworkCore.PostgreSQL` 10.0.3 | `Multiplexing` |
| `Npgsql.EntityFrameworkCore.PostgreSQL` 11.0.0 | none |

`Marten` reads only `Multiplexing`, in its own `ShardedTenancy.isMultiplexing()` and again through Weasel on
the synchronous tenant-resolution path - see *Weasel and Marten* below.

`Npgsql.EntityFrameworkCore.PostgreSQL` reads `Multiplexing` in **10.0.3** and in **11.0.0-rc.3**, and not in
**11.0.0 GA** -- so any app on the driver's 11 line with an older provider meets this failure a third time,
independently of Wolverine. Worth naming because the Aspire Npgsql integration (17.0.0-preview.1, the only 17.x
published) still asks for `Npgsql.EntityFrameworkCore.PostgreSQL` `11.0.0-rc.3` on its own, so an app can be
pulled onto an affected provider without asking for it. Fixing Wolverine alone does not make the stack
Npgsql-11-ready; the provider line and Marten have to move as well.

No Npgsql 11 build ever had the property — `11.0.0-preview.1` and `11.0.0` both lack it (10.0.4
still has it), and it is not inherited from `DbConnectionStringBuilder` (the chain is
`NpgsqlConnectionStringBuilder → DbConnectionStringBuilder → Object`). So there is nothing to stay on
inside the 11 line, and no Wolverine build to move to inside 6.x/7.x.

`TryGetValue(string, out object)` is on the connection string builder's public surface in both versions
(Npgsql declares it itself on 10.0.4 and on 11.0.0): on 10.0.x it answers the `multiplexing` and
`writecoalescingbufferthresholdbytes` keywords, on 11.0.0 it returns false because the driver no longer
knows them. That is the one read path that works across both.

### Weasel and Marten

`Weasel.Postgresql` reads `get_Multiplexing` from **10.0.0-alpha.4** onward (through alpha.13, the version
`main` pins); `9.49.0`, `9.50.0`, `10.0.0-alpha.1`, `-alpha.2` and `-alpha.3` do not. `Marten`
10.0.0-alpha.7 reads it too, in its own `ShardedTenancy.isMultiplexing()` (*Npgsql refuses synchronous I/O
when multiplexing is on*), and it depends on Weasel `10.0.0-alpha.13`.

That reference is not reachable from Wolverine's own code paths, though: it sits in
`masterTakesSynchronousIo()`, reached only from the public
`PostgresqlDatabase.Maintenance.FindOrCreateDatabaseSynchronously()`, which no Wolverine source file calls
(`Wolverine.Marten` uses the async `IStoreStorage.FindOrCreateDatabase`). The test run below confirms it
empirically — nothing on the Postgres test path throws `MissingMethodException` while Weasel alpha.13 is
loaded on Npgsql 11. So Wolverine's two reads are the blocker for Wolverine apps; Weasel's is a separate
report for the Weasel repo, worth making because it is public API that will break for anyone who calls it
from a prebuilt assembly.

`Marten` is the concrete case, and it has its own repro (no database needed, closed port):

```csharp
var store = DocumentStore.For(o => o.MultiTenantedWithSingleServer(master));
store.Tenancy.GetTenant("green");   // uncached tenant -> getTenantSynchronously
```

```text
Npgsql 11.0.0 -> MissingMethodException: Method not found:
                 'Boolean Npgsql.NpgsqlConnectionStringBuilder.get_Multiplexing().'   (exit 42)
Npgsql 10.0.4 -> reaches the socket: SocketException, connection refused             (no such failure)
```

`SingleServerMultiTenancy.GetTenant` is the public entry (Marten #5985 added the synchronous path, and its own
doc comment names the `Multiplexing` fallback). So an app on `Wolverine.Marten` with single-server
multi-tenancy has two independent Npgsql-11 blockers, not one: the two Wolverine reads above, and this one.
Fixing Wolverine alone would not unblock it.

## Upstream state

Nothing upstream addresses this yet. Picking over every commit between the `7.0` branch and current
`main` finds no commit adding or removing a `Multiplexing` reference, no commit touching the `Npgsql` pin,
no PR in any state about it (the only hits are the two that *added* the queue-notifications feature), and
no `npgsql`-named branch upstream. `main` still pins Npgsql 10.0.3, and the two `Describe()` lines are
byte-identical to the `7.0` branch.

The published test suite also resolves Npgsql 10.0.3 even when it runs on `net11.0` — the `PostgresqlTests`
graph has 93 libraries, no EF Core assembly in any of them, and Npgsql 10.0.3 — so Npgsql 11 is not
exercised anywhere upstream.

Since the branch was pushed, five more commits have landed on `main` (GH-5676 and GH-5707 queue-transport
flake fixes, GH-5709 saga fault handling, and their merges). None of them touch
`PostgresqlMessageStore.cs` or `PostgresqlQueueNotificationManager.cs`, so the branch still applies cleanly.

On the Npgsql side the removal looks permanent rather than accidental:
[npgsql/npgsql#6731 "What replaces multiplexing?"](https://github.com/npgsql/npgsql/issues/6731)
is open — a maintainer notes that single-connection multiplexing "is not currently possible in the
driver" — and [npgsql/npgsql#4496 "Reimplement multiplexing via NpgsqlDataSource.CreateCommand"](https://github.com/npgsql/npgsql/issues/4496)
is still open.

## Evidence that the patch is safe

Two `dotnet test` runs of `PostgresqlTests` on `net11.0`, same 778-character class filter (the 10 classes
that failed in an earlier full run), each against its own freshly created Postgres container:

| arm | tests | failed |
| --- | --- | --- |
| `origin/main`, unpatched | 81 | 6 |
| `gh-5725-npgsql-11-multiplexing`, patched | 81 | 6 |

Diffed by test name, the two sets are **identical** — nothing fails only on the patched arm, nothing fails
only on the control. The 6 are environmental: no broker containers were running in either arm, so they are
`BrokerInitializationException` plus timer-ordering and `HasUnconfirmedPop` timing assertions.

A third run forces the graph onto Npgsql 11.0.0 (a direct `PackageReference` with `VersionOverride` in the
test project, since `CentralPackageTransitivePinningEnabled` is deliberately unset) with the patch applied and
Weasel still at alpha.13: **0 `MissingMethodException`**, 7 failures of 81, covering `PostgresqlTests` and the
5 source projects in its closure -- `MartenTests` and `PolecatTests` consume Marten as a package and are
outside that arm, six
of them the same environmental set. The seventh,
`recurring_timer_agents_across_nodes.pausing_stops_the_agent_cluster_wide_and_mirrors_onto_the_row`,
passes 2 of 3 runs in isolation on the same build, so it is timing-sensitive rather than Npgsql-related.

That run needed one test-side change. Across all 6,772 tracked `.cs` files only three name either member --
the two `Wolverine.Postgresql` sites above and `PostgresqlTests/Transport/queue_notifications.cs`, which sets
`Multiplexing = true` to exercise the LISTEN guard and so cannot compile against 11 at all. Since a compile
error from this removal can only appear where source names the member, that test is the only one a CI matrix
adding Npgsql 11 has to guard (the `Wolverine.Postgresql` hits are the ones this patch removes).

## Options considered

**Read the options through the connection string dictionary (recommended, and what the branch does).**
`TryGetValue(string, out object)` is public on both driver versions, so the fix compiles against the pinned
Npgsql 10 and runs on 11 unchanged, and needs no dependency change anywhere. It preserves the behaviour of
both call sites: on 10.0.x the descriptor still reports `Multiplexing` and
`WriteCoalescingBufferThresholdBytes`, the queue-notifications guard still rejects a multiplexed data source
and its test still passes, and on 11.0.0 the entries are simply absent because the driver has no such
option. Verified with the repro above, same patched binary on both drivers:

```text
Npgsql 10.0.4 -> Describe() OK   Multiplexing=False   WriteCoalescingBufferThresholdBytes=1000
Npgsql 11.0.0 -> Describe() OK   Multiplexing=<absent> WriteCoalescingBufferThresholdBytes=<absent>
```

Because the recommended fix rebuilds each entry by hand rather than through `OptionsValue.Read`, the
reconstruction was checked against the published, unpatched `WolverineFx.Postgresql` 6.64.2 field by field —
not just the displayed value but the descriptor's typed shape. Same connection string, same `Describe()` call,
both builds:

```text
published 6.64.2, original OptionsValue.Read(builder, x => x.Multiplexing):
  Multiplexing                         False  type=Boolean raw=Boolean  subject=Npgsql.NpgsqlConnectionStringBuilder.Multiplexing
  WriteCoalescingBufferThresholdBytes  1000  type=Numeric raw=Int32  subject=Npgsql.NpgsqlConnectionStringBuilder.WriteCoalescingBufferThresholdBytes

this branch, TryGetValue + hand-built OptionsValue:
  Multiplexing                         False  type=Boolean raw=Boolean  subject=Npgsql.NpgsqlConnectionStringBuilder.Multiplexing
  WriteCoalescingBufferThresholdBytes  1000  type=Numeric raw=Int32  subject=Npgsql.NpgsqlConnectionStringBuilder.WriteCoalescingBufferThresholdBytes
```

`TryGetValue` hands back the typed value rather than a string, which is what makes this work: `WriteValue`
derives `PropertyType.Boolean` / `PropertyType.Numeric` from it, so the entries are identical on `Value`,
`Type`, `RawValue`'s CLR type and `Subject`. On 11.0.0 both entries are absent because the driver knows
neither keyword:

```text
Npgsql 11.0.0 -> Multiplexing=<absent>  WriteCoalescingBufferThresholdBytes=<absent>
```

**Delete the two reads.** Works, and is the smallest diff. On Npgsql 11 the two options are indistinguishable,
since the driver knows neither keyword and the entries are absent either way; the difference is confined to
10.x, where deletion also removes the two diagnostics entries and the LISTEN guard, so the
`notifications_reject_multiplexed_data_sources` test has to go with it and
`docs/guide/durability/postgresql.md` stops describing the shipped behaviour.

Neither can be done from consumer code: the reads sit inside Wolverine's own method, so nothing runs early
enough to intercept them. `Describe()` is `public virtual` on a non-sealed class, but
`PostgresqlMessageStore` is `internal` (only `Wolverine.Marten` and three test assemblies get
`InternalsVisibleTo`) and the base constructor calls `Describe()`, so subclassing is out.

A build-step IL patch does work for a consumer that cannot wait for a release: because the two reads are
expression-tree `ldtoken`s (from the `OptionsValue.Read(builder, x => ...)` lambdas) rather than calls,
retargeting just those two tokens to same-return-type Npgsql 11 properties with Mono.Cecil is enough for the
host to start and for `codegen write` to complete on 11.0.0. `Wolverine.Postgresql` is not strong-named, so
no re-signing is needed — but it is a build-step patch, not something an app can do at runtime.

Dependency moves do not help on the Wolverine side: every published `Wolverine.Postgresql` checked, from
6.20.0 through 7.0.0-alpha.3, reads both properties.

## What we are asking

No urgency, and staying on Npgsql 10 is a perfectly reasonable answer — this is a heads-up with a patch
attached, filed because Npgsql 11.0.0 GA landed on 2026-11-20 and apps using the Aspire Npgsql integration
get pulled onto it by the EF provider, so more reports are coming.

`AwkEng/wolverine@gh-5725-npgsql-11-multiplexing` is open if useful: one commit against `main`, 2 files, +33/−3,
no dependency change, and an identical set of `PostgresqlTests` failures before and after. Happy to rework it
as a deletion of the two reads instead, retarget it at `7.0`, or drop it entirely if you would rather write
the fix yourself. Separately, `Weasel.Postgresql` reads the same member from 10.0.0-alpha.4 onward behind the
public `FindOrCreateDatabaseSynchronously()`; that looks like a report for its own repo, and we have not
filed one.

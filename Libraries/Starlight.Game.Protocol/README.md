# Starlight.Game.Protocol

Game protocol schemas for the custom protobuf compiler/runtime. The build-time
generator (`Starlight.Protobuf.Compiler`, wired in as an analyzer) turns these
`.proto` files into POCOs, serializers and a per-version registry. Each `.proto`
is tagged in the `.csproj` with an `SLProtoRole`:

- `Base\**\*.proto` → **Base** — the canonical, de-obfuscated schema.
- `Protobuf\**\*.proto` → **Version** — a real, obfuscated protocol dump.
- `extra.proto` → **Independent** — server-only packets with no version dump.

## Updating the protos

There are two layers, correlated **by message name and field name** — never by
field number.

**Base protos (`Base/`)** define the contract: canonical message names, the
de-obfuscated field names and their types. Field numbers here are structural
only; the real wire IDs live in the version dumps. Only fields you actually want
surfaced belong here — a field present in Base becomes a POCO property. To add or
rename a property, edit the Base message (e.g. `Base/player.proto`) and use the
real, readable field name.

**Version protos (`Protobuf/`)** are the per-version wire layout. Each message
mirrors a Base message by name and carries that version's obfuscated field IDs
(the `// 0x..` comments are the original offsets). A version field whose name
matches a Base field is wired to that property; fields with no Base counterpart
are preserved as unknowns on deserialize and never re-emitted. When the game
updates, drop in the new dump and keep the message/field names aligned with Base.

After editing, rebuild — the generator regenerates `*.Serializers.g.cs` and
`*.Registry.g.cs` under `obj/generated/`.

## Adding a new version

Each version is a self-contained Version group identified by its package name:

1. Create the version's `.proto` files under `Protobuf/` and tag them `Version`
   in the `.csproj` (the existing `Protobuf\**\*.proto` glob already does this).
2. Include a `_meta.proto` declaring the version package, e.g. `package v67;`.
   This is required — the compiler derives the version name (`V67`) and namespace
   (`Starlight.Game.Protocol.V67`) from it, and errors if it's missing.
3. Add the version's message dumps, mirroring Base message and field names so the
   compiler can correlate them. Messages tagged with a `CmdId` enum/`// CmdId:`
   comment are registered for command routing; `GetPlayerTokenReq` and `PingReq`
   are treated as the first-packet messages used for version detection.

Rebuild to emit `V67ProtocolRegistry` alongside the existing registries.

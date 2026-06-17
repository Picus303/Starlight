Canonical base schema. Defines message names, canonical (de-obfuscated)
field names and types. Field numbers here are structural only -- the real
per-version wire ids live in the version dumps under Protobuf/ and are
correlated back to these fields by name.

Only non-obfuscated fields are listed: a field present here becomes a POCO
property; version fields with no canonical counterpart are captured as
unknown on deserialize and never emitted on serialize.

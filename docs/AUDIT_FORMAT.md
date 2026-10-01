# Audit event format

`audit.jsonl` contains one JSON object per line. The application uses a typed
`AuditEvent` envelope and a versioned JSON schema:
[audit-event.schema.json](audit-event.schema.json). This is the project's audit
format; it is not a CloudEvents or OpenTelemetry export.

```json
{
  "schema_version": 1,
  "timestamp": "2026-10-01T16:00:00+00:00",
  "run_id": "example-run",
  "event": "task_retried",
  "actor": "orchestrator",
  "task_id": "run_tests",
  "attempt": 2,
  "backoff_s": 0.5,
  "error": "Test process exited unsuccessfully"
}
```

The example is pretty-printed; each stored event occupies a single physical line,
including when a detail string contains a newline.

| Field | C# type | Meaning |
|---|---|---|
| `schema_version` | `int` | Envelope version, currently 1 |
| `timestamp` | `DateTimeOffset` | Recording time; new events use UTC |
| `run_id` | `string` | Nonblank run identifier |
| `event` | `string` | Nonblank event name, such as `task_started` |
| `actor` | `string` | Nonblank actor, such as `orchestrator` |
| `task_id` | `string?` | Task identifier, or null for run-level events |

Event names remain extensible strings so agents can introduce events without
changing a central enum. Event-specific details use `Dictionary<string,
JsonElement>` in C#, preserving JSON numbers, booleans, arrays, objects, and null.
They serialize as additional root properties through `JsonExtensionData`.
They cannot replace any envelope field; reserved-name checks ignore case.

The existing `Log(..., details: Dictionary<string, object?>)` input is retained
for callers. Its values are immediately serialized into detached JSON data.
The logger stores `List<AuditEvent>` and exposes `IReadOnlyList<AuditEvent>`.
Consumers use `entry.Event`, `entry.TaskId`, and, for example,
`entry.Details["attempt"].GetInt32()` rather than casting dictionary values.

## Compatibility and guarantees

Existing field names, event names, and root-level detail fields remain unchanged.
`schema_version` is additive. Timestamps now use UTC with a numeric offset and
fractional precision where available; consumers should parse them as instants.
Historical JSON can be deserialized as `AuditEvent`; an omitted version defaults
to 1, although historical lines do not satisfy the new schema's required fields.
Consumers that reject unknown fields must allow `schema_version`.

Input detail data must not be modified while `Log` is serializing it. After
`Log` returns, changing the input, returned event details, or event snapshots
cannot mutate recorded events. Writes and reads are synchronized within one
logger instance. File lines and in-memory events share recording order. A failed
file append raises an exception and does not add an in-memory event.

Use one logger per run directory. There is no cross-process file lock, crash-safe
write guarantee, or cryptographic tamper detection. Append-only application
behavior alone does not make the file tamper-evident.

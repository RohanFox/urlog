# ADR index

One file per decision: `NNNN-title.md` with Context, Options, Decision, Consequences.

- 0001-case-isolation.md: per-case isolation measured on 26.8: `events_v` as SQL SECURITY DEFINER on the per-request setting, grants on views only, the raw-SQL gate mandatory on both paths because an inline `SETTINGS` overrides the request parameter; row-policy fallback measured and not taken
- 0002-clickhouse-driver.md: driver and wire protocol: `ClickHouse.Client` was wrong; `ClickHouse.Driver` TCP client chosen, limitation on custom settings recorded
- 0003-query-transport.md: HTTP client for the query path (per-request `SQL_allowed_cases`), TCP client for inserts; session `SET` rejected; the isolation tests cover (a) gate rejects user SETTINGS, (b) inline SETTINGS overrides the parameter when the gate is bypassed

# Genesis Showcase

A single-page, dark-mode showcase for **Genesis / 同构数据引擎** — a one-table, metadata-driven data kernel.

No framework. No build step. No external assets. Open it and it works.

## View it

From the repository root:

```powershell
python -m http.server 8099 --bind 127.0.0.1
```

Then open:

```text
http://127.0.0.1:8099/showcase/
```

You can also open `showcase/index.html` directly in a browser, but the links to `../docs/*` are relative to the repository root, so serving from the root is recommended.

## What is on the page

| Section | What it does |
|---|---|
| Hero | One row, eight roles, infinite projections. Animated node/row visual. |
| Thesis | The one-primitive argument: one row, two axes, data generating data. |
| Live demo | Pick a scenario, hit generate, watch `SystemJson` → `UniversalEntity` rows → a working form + table. Add records and see them written into the pool view. |
| Anatomy | Role inspector (1–8), Graph vs Snapshot, five-engine spectrum slider, AES-GCM vs Plain. |
| Proof | Animated 148× counter, throughput bars, the 100K B+tree reversal, five-engine table, three war stories (B1 / B10 / B38). |
| Timeline | 8 milestones across 26 rounds, with quotes from the whitepaper and update log. |
| Closing | The invitation: how many tables does your next system need? |

## Honest notes

- The live demo is a **front-end simulation**. It uses the same `Role=8` schema shape as Genesis, but the data is local. The real path is `POST /api/genesis` with `{"action":"GetSystemSchema"}` against `Genesis.Gateway`.
- Numbers are quoted from `docs/GenesisWhitepaper.md` and `docs/UPDATE_LOG.md` (local SQL Server 2019 / Windows 10 / .NET 10). They are not TPC-C and not cloud benchmarks. The latest CAS/history hot path still needs a whitepaper 3.0 refresh.
- The site is intentionally a **research artifact portfolio**, not a product landing page. It should read like an architect's experiment log, not a SaaS brochure.

## Files

```text
showcase/
  index.html
  assets/
    styles.css
    app.js
  README.md
```

## Customize

- Scenario data, roles, engine numbers, chart data: `assets/app.js` (`SYSTEMS`, `ROLES`, `ENGINES`, `THROUGHPUT`, `REVERSAL`).
- Colors, type, spacing: `assets/styles.css` (`:root`).
- Copy / sections: `index.html`.

## Links

- [Whitepaper](../docs/GenesisWhitepaper.md)
- [Update log](../docs/UPDATE_LOG.md)
- [Phase 2 semantic spec](../docs/Phase2SemanticSpec.md)

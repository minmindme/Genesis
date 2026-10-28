# Genesis · 同构数据引擎 / Isomorphic Data Engine

> **一生二，二生三，三生万物。** 一张表、一种行结构，生长出任何业务形态——元数据、引擎指令、安全策略、命令树、前端界面，全部是池中数据。
>
> **One begets two, two begets three, three begets all things.** One table, one row structure, grows into any business shape — metadata, engine directives, security policies, command trees, front-end UI — all are just rows in the pool.

[![License](https://img.shields.io/badge/license-attribution-blue)](LICENSE)
[![Tests](https://img.shields.io/badge/tests-81%20passed-brightgreen)](docs/UPDATE_LOG.md)
[![Build](https://img.shields.io/badge/build-0%20warning%200%20error-blue)](#快速开始--quick-start)

[中文](#这是什么--what-is-this) ｜ [English](#what-is-this--这是什么)

> ⚠️ **项目状态 / Project Status**
>
> **本项目处于萌芽阶段（早期原型）**——架构已闭环、语义已定型，但仍是一个活跃演进中的实验性项目：API 可能随任何一轮迭代调整，请勿用于严肃生产环境。
> **This project is in its infancy (early prototype)** — the architecture is closed-loop and the semantics are settled, but it remains an actively evolving experimental project: APIs may change with any iteration. Do not use it for serious production workloads.
>
> **数据库兼容性 / Database compatibility**：当前**仅适配 SQL Server 2019**（唯一经过 26 轮完整实测的引擎）。`StorageProvider` 抽象为未来适配其他数据库预留了插槽（InMemory/FASTER 为进程内引擎，非 SQL 数据库），但**尚无其他 SQL 数据库（MySQL/PostgreSQL 等）的适配**——移植前请自行评估。
> **Currently only SQL Server 2019 is supported** (the only engine fully tested across all 26 rounds). The `StorageProvider` abstraction reserves slots for other databases (InMemory/FASTER are in-process engines, not SQL databases), but **no other SQL database (MySQL/PostgreSQL, etc.) has been adapted yet** — evaluate carefully before porting.

---

## 这是什么 / What is this

Genesis 是一个**单表同构数据引擎**：数据库里只有一张 `UniversalEntity` 表（9 列），业务数据、元数据、引擎指令、安全策略、CLI 命令、前端界面描述——全部是这张表里结构相同的行。引擎自身没有业务逻辑，只做"读数据、按数据执行"。

**换存储引擎、换加密算法、换存储形态、建新业务、长出新界面——全部是改几行数据，零代码、零编译、零重启。**

Genesis is a **single-table isomorphic data engine**: the database contains only one `UniversalEntity` table (9 columns). Business data, metadata, engine directives, security policies, CLI commands, and front-end UI descriptions — all are rows of the same structure in this single table. The engine itself has no business logic; it only "reads data and acts on it."

**Swapping storage engines, encryption algorithms, storage modes, creating new business types, growing new UI — all are just editing a few rows. Zero code, zero compilation, zero restart.**

## 核心特性 / Core Features

| 特性 / Feature | 说明 / Description |
|---|---|
| **单表同构 / Single-table isomorphism** | Role 体系一张表承载一切：1=类型元数据 / 2=业务数据（含边） / 4=引擎指令 / 5=安全策略 / 7=命令树 / 8=界面描述。One table via a Role system: 1=type metadata, 2=business data (incl. edges), 4=engine directives, 5=security policies, 7=command tree, 8=UI descriptions |
| **五引擎光谱 / Five-engine spectrum** | 磁盘表 / BulkCopy / 内存优化表 / 进程内字典 / FASTER KV——`StorageProvider` 一行指令切换，语义三引擎一致（累计提速 148 倍）。Disk table / BulkCopy / memory-optimized table / in-process dictionary / FASTER KV — switched by one `StorageProvider` directive row; semantics consistent across engines (148× cumulative speedup) |
| **三轨路由 / Three-track routing** | 快照轨（SNAP1 整包）/ 强类型图（CSharpType 绑定）/ 动态图（UniversalObject）——`StorageMode` 一行指令分轨。Snapshot track (SNAP1 whole-object) / strongly-typed graph (CSharpType binding) / dynamic graph (UniversalObject) — routed by one `StorageMode` directive row |
| **安全同构 / Security as data** | AES-GCM 透明加密（AGC1:/PLN1: 前缀版本化）、租户隔离（不可见不可写不可删）、限流、Token 鉴权——全部是 Role=5 策略行。AES-GCM transparent encryption (AGC1:/PLN1: prefix versioning), tenant isolation (invisible = unwritable & undeletable), rate limiting, token auth — all Role=5 policy rows |
| **CRUD + 历史 / CRUD + history** | UpsertAsync（Version CAS 乐观锁，并发一胜一败）+ Role=6 历史行（覆盖留痕，`GetHistory` 审计链）。UpsertAsync (Version CAS optimistic locking, concurrent writers: one wins, one conflicts) + Role=6 history rows (audit trail via `GetHistory`) |
| **意图总线 / Intent bus** | `POST /api/genesis` 唯一入口，双路由（Role=7 工作流 / 内置数据动作）；自愈热重载 + Dry-run 前置；冲突 409。Single entry point with dual routing (Role=7 workflows / built-in data actions); self-healing hot reload + Dry-run pre-check; conflicts return 409 |
| **UI 同构 / UI as data** | Role=8 一行描述 + 通用渲染器——前端界面也是池中数据。One Role=8 row + a generic renderer — even the front-end UI lives in the pool |
| **一句话建系统 / One JSON, one system** | ImportSystem：一段 JSON 长出实体 + 边 + 表单（11 行元数据建出学生管理系统）。ImportSystem: a single JSON grows entities + edges + forms (11 metadata rows build a student management system) |

## 快速开始 / Quick Start

**前置 / Prerequisites**：.NET 10 SDK、**SQL Server 2019**（唯一适配的数据库版本；本地实例，`sa` 账号或 `GENESIS_SQL_CONN` 环境变量注入连接串）。

```powershell
# 1. 建库（src/Emoking.DAL/Sql/UniversalEntity.sql）或直接跑测试（引擎空池自举创世）
#    Create the database, or just run the tests (the engine bootstraps genesis rows on an empty pool)
dotnet run --project src/Emoking.DAL.Tests/Emoking.DAL.Tests.csproj

# 2. 启动网关（唯一 HTTP 入口 + 静态前端托管）/ Start the gateway (single HTTP entry + static front-end)
dotnet run --project src/Genesis.Gateway
```

一句话建系统 / One JSON builds a system:

```powershell
# sqlcmd 插一行 Role=7 命令定义后 / after inserting one Role=7 command definition via sqlcmd —
POST /api/genesis  { "action": "ImportSystem",
  "args": { "SystemJson": "{\"Entities\":[...],\"EdgeTypes\":[...],\"Uis\":[...]}" } }
# → 类型 + 边 + 表单整体长出 → 浏览器 StudentForm 可用 → 全程零代码零编译零重启
# → Types + edges + forms grow as a whole → StudentForm is live in the browser → zero code, zero build, zero restart
```

## 仓库结构 / Repository Layout

```text
src/
  Emoking.DAL/           # 引擎内核 / engine kernel: single-table model, gateway, three engines, policy store, intent bus, command executor
  Emoking.DAL.Tests/     # 81 个测试（真库集成 + 每测试类独立数据库）/ 81 tests (real-DB integration + per-class isolated databases)
  Genesis.Gateway/       # 哑巴 HTTP 网关 / dumb HTTP gateway (POST /api/genesis — the only endpoint)
  Genesis.Web/           # 通用前端渲染器 / generic front-end renderer (zero business code)
  Genesis.CLI/           # 哑巴命令行壳子 / dumb CLI shell
docs/
  GenesisWhitepaper.md   # 架构白皮书 2.0 / architecture whitepaper 2.0 (philosophy vs. measured reality)
  UPDATE_LOG.md          # 26 轮完整工程日志 / 26 rounds of engineering logs (B1-B41 bug ledger)
  Phase2SemanticSpec.md  # 结构化动作解释器的最小语义规格 / minimal semantic spec for the structured action interpreter
showcase/                # 单页展示（dark-mode，纯静态）/ single-page showcase (dark-mode, purely static)
```

## 设计对账 / Philosophy vs. Measured Reality

Genesis 的每一句"理论上"都必须变成表格里的实测数字——白皮书中维护着一张**哲学 vs 实测对账表**：哪些宣称被证实（结构同构消灭 JOIN、引擎同化可行、索引即数据——B+ 树反超内存全扫 30 倍）、哪些被修正（"零计算"修正为"计算便宜到可忽略，I/O 往返才是价格"）、哪些外部预测偏差达 26 倍。

Every "in theory" in Genesis must become a measured row in a table — the whitepaper maintains a **philosophy vs. reality ledger**: what was confirmed (isomorphism eliminates JOINs; engine assimilation works; indexes-as-data — the B+ tree beats in-memory full scans by 30×), what was corrected ("zero computation" refined to "computation is cheap; round-trips are the price"), and where external predictions missed by up to 26×. **See the [whitepaper](docs/GenesisWhitepaper.md).**

## 工程纪律 / Engineering Discipline

- 0 警告 0 错误（全解决方案）/ 0 warnings, 0 errors (entire solution)
- 81 个测试（真库集成 + 每类独立数据库隔离 + 基准门禁）/ 81 tests (real-DB integration + per-class database isolation + benchmark gating)
- 先红后绿的修复纪律；B1–B41 全量 BUG 台账公开 / red-before-green fix discipline; full B1–B41 bug ledger public
- 每轮一个提交，[UPDATE_LOG.md](docs/UPDATE_LOG.md) 完整可溯 / one commit per round, fully traceable in [UPDATE_LOG.md](docs/UPDATE_LOG.md)

## 许可 / License

**署名许可（允许商用）/ Attribution License (commercial use permitted)**——**永久、全球、免版税**，明确允许商业使用（含生产部署与闭源集成）；**唯一核心义务是署名**：您的产品/项目须显著注明"基于 Genesis 同构数据引擎（© 2026 minmindme）"并链接本仓库，修改件须标注"基于 Genesis 修改"。禁止移除署名与虚假背书。见 [LICENSE](LICENSE)。

**Perpetual, worldwide, royalty-free** — commercial use is expressly permitted (including production deployments and closed-source integration); **the sole core obligation is attribution**: your product/project must prominently state "Built on the Genesis Isomorphic Data Engine (© 2026 minmindme)" with a link to this repository, and modified works must be marked "Modification of Genesis". Removing attribution and false endorsement are prohibited. See [LICENSE](LICENSE).

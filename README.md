# Genesis · 同构数据引擎

> **一生二，二生三，三生万物。** 一张表、一种行结构，生长出任何业务形态——元数据、引擎指令、安全策略、命令树、前端界面，全部是池中数据。

[![License](https://img.shields.io/badge/license-private-red)](LICENSE)
[![Tests](https://img.shields.io/badge/tests-81%20passed-brightgreen)](docs/UPDATE_LOG.md)
[![Build](https://img.shields.io/badge/build-0%20warning%200%20error-blue)](#快速开始)

## 这是什么

Genesis 是一个**单表同构数据引擎**：数据库里只有一张 `UniversalEntity` 表（9 列），业务数据、元数据、引擎指令、安全策略、CLI 命令、前端界面描述——全部是这张表里结构相同的行。引擎自身没有业务逻辑，只做"读数据、按数据执行"。

**换存储引擎、换加密算法、换存储形态、建新业务、长出新界面——全部是改几行数据，零代码、零编译、零重启。**

## 核心特性

| 特性 | 说明 |
|---|---|
| **单表同构** | Role 体系一张表承载一切：1=类型元数据 / 2=业务数据（含边） / 4=引擎指令 / 5=安全策略 / 7=命令树 / 8=界面描述 |
| **五引擎光谱** | 磁盘表 / BulkCopy / 内存优化表 / 进程内字典 / FASTER KV——`StorageProvider` 一行指令切换，语义三引擎一致（累计提速 148 倍） |
| **三轨路由** | 快照轨（SNAP1 整包）/ 强类型图（CSharpType 绑定）/ 动态图（UniversalObject）——`StorageMode` 一行指令分轨 |
| **安全同构** | AES-GCM 透明加密（AGC1:/PLN1: 前缀版本化）、租户隔离（不可见不可写不可删）、限流、Token 鉴权——全部是 Role=5 策略行 |
| **CRUD + 历史** | UpsertAsync（Version CAS 乐观锁，并发一胜一败）+ Role=6 历史行（覆盖留痕，`GetHistory` 审计链） |
| **意图总线** | `POST /api/genesis` 唯一入口，双路由（Role=7 工作流 / 内置数据动作）；自愈热重载 + Dry-run 前置；冲突 409 |
| **UI 同构** | Role=8 一行描述 + 通用渲染器——前端界面也是池中数据 |
| **一句话建系统** | ImportSystem：一段 JSON 长出实体 + 边 + 表单（11 行元数据建出学生管理系统） |

## 快速开始

**前置**：.NET 10 SDK、SQL Server 2019+（本地实例，`sa` 账号或 `GENESIS_SQL_CONN` 环境变量注入连接串）。

```powershell
# 1. 建库（src/Emoking.DAL/Sql/UniversalEntity.sql）或直接跑测试（引擎空池自举创世）
# 2. 运行测试（81 个，含真库集成）
dotnet run --project src/Emoking.DAL.Tests/Emoking.DAL.Tests.csproj

# 3. 启动网关（唯一 HTTP 入口 + 静态前端托管）
dotnet run --project src/Genesis.Gateway
```

一句话建系统：

```powershell
# sqlcmd 插一行 Role=7 命令定义后——
POST /api/genesis  { "action": "ImportSystem",
  "args": { "SystemJson": "{\"Entities\":[...],\"EdgeTypes\":[...],\"Uis\":[...]}" } }
# → 类型 + 边 + 表单整体长出 → 浏览器 StudentForm 可用 → 全程零代码零编译零重启
```

## 仓库结构

```text
src/
  Emoking.DAL/           # 引擎内核：单表模型、网关、三引擎、策略存储、意图总线、命令执行器
  Emoking.DAL.Tests/     # 81 个测试（真库集成 + 每测试类独立数据库）
  Genesis.Gateway/       # 哑巴 HTTP 网关（POST /api/genesis 唯一端点）
  Genesis.Web/           # 通用前端渲染器（零业务代码）
  Genesis.CLI/           # 哑巴命令行壳子
docs/
  GenesisWhitepaper.md   # 架构白皮书 2.0（哲学 vs 实测对账）
  UPDATE_LOG.md          # 26 轮完整工程日志（B1-B41 BUG 台账）
  Phase2SemanticSpec.md  # 结构化动作解释器的最小语义规格
showcase/                # 单页展示（dark-mode，纯静态）
```

## 设计对账（哲学 vs 实测）

Genesis 的每一句"理论上"都必须变成表格里的实测数字——白皮书中维护着一张**哲学 vs 实测对账表**：哪些宣称被证实（结构同构消灭 JOIN、引擎同化可行、索引即数据——B+ 树反超内存全扫 30 倍）、哪些被修正（"零计算"修正为"计算便宜到可忽略，I/O 往返才是价格"）、哪些外部预测偏差达 26 倍。**见 [白皮书](docs/GenesisWhitepaper.md)。**

## 工程纪律

- 0 警告 0 错误（全解决方案）
- 81 个测试（真库集成 + 每类独立数据库隔离 + 基准门禁）
- 先红后绿的修复纪律；B1–B41 全量 BUG 台账公开
- 每轮一个提交，[UPDATE_LOG.md](docs/UPDATE_LOG.md) 完整可溯

## 许可

**私人使用许可**——允许个人学习、研究与非公开环境运行；**禁止一切商业使用与再分发**。见 [LICENSE](LICENSE)。商业授权请通过仓库 Issue 联系作者。

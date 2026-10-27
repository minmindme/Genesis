# 同构数据引擎（Genesis）架构白皮书

> 版本：2.0（基于第 1–19 轮实测，替代 1.0 版）
> 数据来源：本机 SQL Server 2019 Enterprise + Windows 10，.NET 10，全部数字为实测值（E2 级证据），未实测处均显式标注。
> 伴生文档：[UPDATE_LOG.md](UPDATE_LOG.md)（十九轮完整工程记录，含 B1–B34 全部 BUG 台账）。
> 测试现状：62 个测试（57 通过 + 5 环境门禁基准），0 警告 0 错误。

---

## 一、缘起：一个哲学问题

一切始于一个 PowerDesigner 建模问题，和一句话：**"一生二，二生三，三生万物。"**

传统架构里，每新增一个业务就长出一张表——万物各自成形，从未归一。这个项目的追问是：能否反过来，**让万物先归于"一"（一张同构表），再由规则分化出"万物"（任何业务形态）？**

推演收敛为四条公理：

1. **万物皆数**：数据库里只有一张 `UniversalEntity` 表。业务数据、元数据、引擎指令、安全策略、CLI 命令、UI 描述——全部是这张表里结构相同的行。
2. **数据库只负责存**：不 JOIN、不视图、不计算。它是哑巴硬盘，不是思考者。
3. **引擎同化**：引擎的规则（表长什么样、路由到哪、用什么引擎、开不开加密）本身也是行（Role=4/5），C# 代码里没有业务逻辑，引擎退化为"寻址器 + 执行器"。
4. **零计算**：无 JOIN、无 ORM 映射，只有指针巡航。实测修正为："计算便宜到可以忽略，I/O 往返才是价格。"

十九轮下来，公理 3 被推广到极限：**这个系统里除了"读取数据、按数据执行"之外，几乎不存在任何写死的东西。** 表结构、存储引擎、存储形态、加密算法、命令树、前端界面——全部是池中一行（或几行）数据。

本白皮书回答三个诚实的工程问题：**这套哲学在物理世界里能跑多快？代价是什么？"演示级"到"引擎级"之间隔着什么？**

---

## 二、单表模型：唯一的一

### 2.1 UniversalEntity 表（9 列）

| 列 | 类型 | 语义 |
|---|---|---|
| Id | UNIQUEIDENTIFIER | 行标识（Guid 占位，非顺序主键天然防遍历） |
| TypeId | UNIQUEIDENTIFIER | 类型指针：指向定义"我是谁"的元数据行 |
| Role | TINYINT | 角色同化（见 2.2） |
| ParentId | UNIQUEIDENTIFIER? | 图指针：树/边/归属，一行既是数据也是路由 |
| Value | NVARCHAR(MAX) | 纯值或键值对或 JSON 快照或密文，无固定 schema |
| Version / IsDeleted / CreatedAt / UpdatedAt | | 系统控制（软删、乐观锁占位） |

**没有外键、没有第二张表、没有任何业务语义列。**

### 2.2 Role 体系（第 19 轮末状态）

| Role | 名称 | 承载内容 | 行格式示例 |
|---|---|---|---|
| 1 | Meta | 类型定义行（ParentId=null，Value=类型名）+ 属性行（Value=`Name:Type`） | `Value="Price:Decimal"` |
| 2 | Data | 实体行 + 属性行 + **边行**（第 13 轮起边不再是特殊身份） | 实体行 ParentId=null；属性行 ParentId=实体行 |
| 3 | Edge | **已废弃**（第 13 轮：边 = Role=2 普通行 + EdgeType 指令，"索引即数据"） | — |
| 4 | Engine | 引擎指令：StorageProvider / StorageMode / CSharpType / EdgeType / RouteTarget | `Value="StorageProvider=SqlServer"` |
| 5 | Security | 安全策略：EncryptValue / TenantIsolation / RateLimit / ApiSecurity | `Value="Policy=EncryptValue\|Algo=AesGcm\|KeyRef=K1\|Enabled=true"` |
| 7 | Command | CLI 命令与工作流（步骤分隔符 `;`） | `Value="Command=CreateEntity\|Args=...\|Steps=InsertTypeMeta;SetStorageMode;ReloadCache"` |
| 8 | UI | 前端界面描述 | `Value="UI=StudentForm\|Type=Form\|Entity=Student\|Fields=Name:Input,Age:Number\|Action=InsertData"` |

### 2.3 创世与自举

```text
清空全表 → 引擎启动 → MetaCache.LoadAsync 发现 Role=1 行数为 0
→ 自动幂等播种 10 行创世数据（5 行类型/属性 + 2 行引擎指令 + 3 行安全策略）
→ 内存元数据快照就绪 → 万物可生长
```

新增一种业务类型 = 往池子里插几行数据。**零 DDL、零编译、零部署、零重启。**

第 19 轮加固：MetaCache 内部收束为不可变快照，`LoadAsync` 在局部构建完整快照后**单引用原子替换**——并发读永不看到半成品，也不看到"新类型 + 旧命令"的混合态（详见 §9.3）。

---

## 三、七层数据化：什么都是数据

十九轮演进的本质，是把传统架构中"写死在代码里的东西"逐层搬进数据池：

| 层 | 传统架构写死处 | Genesis 数据化 | 轮次 | 换它的成本 |
|---|---|---|---|---|
| 表结构 | DDL + 迁移脚本 | Role=1 类型/属性行 | 第 4 轮 | 插几行 |
| 物理引擎 | 连接串 + ORM 配置 | Role=4 `StorageProvider=` 指令 | 第 7 轮 | UPDATE 一行 + 热重载 |
| 类绑定 | 编译期类型 | Role=4 `CSharpType=` 键 | 第 8 轮 | UPDATE 一行 |
| 加密/租户/限流 | 装饰器硬编码（第 9 轮回炉） | Role=5 策略行 | 第 10 轮 | UPDATE 一行 |
| 存储形态（图/快照） | 实体类设计 | Role=4 `StorageMode=` 指令 | 第 11 轮 | UPDATE 一行 |
| CLI 命令树 | 前端代码 + 路由 | Role=7 命令行 | 第 14 轮 | 插一行 |
| 前端界面 | Controller + View + JS | Role=8 UI 行 + 通用渲染器 | 第 16 轮 | 插一行 |

**这个表格就是本项目的全部主张。** 每一行都有测试与真库冒烟背书，不是愿景。

---

## 四、引擎体系：五引擎光谱与换引擎不变正确性

### 4.1 IUniversalStore 抽象（第 7 轮）

同构池四基本动作（`BulkInsertAsync` / `GetSubGraphAsync` / `GetByIdAsync` / `DeleteAsync`，后增 `GetByParentIdAsync`）+ `UniversalStoreFactory` 按 Role=4 `StorageProvider` 指令动态落位。上层组件与物理引擎彻底解耦。

### 4.2 五引擎同规模对比（10K 实体 = 5 万行，本机同轮次实测）

| 引擎 | 写入 | 读取 | 总计 | 吞吐 | IPC 往返 | ACID |
|---|---|---|---|---|---|---|
| ① 逐行 + 磁盘表 | 15419 ms | 4810 ms | 20311 ms | 492/s | 60001 | 完整 |
| ② BulkCopy + 磁盘表 | 1858 ms | 655 ms | 2528 ms | 3955/s | 2 | 完整 |
| ③ TVP + 内存优化表 | 809 ms | 611 ms | 1458 ms | 6858/s | 2 | 完整 |
| ④ 进程内 ConcurrentDictionary | **36 ms** | **23 ms** | **137 ms** | **72992/s** | **0** | 无 |
| ⑤ 进程内 FASTER KV（可持久化） | 81 ms | 38 ms | 176 ms | 56818/s | **0** | checkpoint 级 |

**累计提速 148 倍。每实体全链路 13.7 微秒（含拆解+写入+读回+组装）。五引擎是光谱，不是替代链**——④ 重启全丢，⑤ 靠 checkpoint，③ 是唯一完整 ACID 选项。选择权在 `StorageProvider` 一行指令里。

### 4.3 换引擎不变正确性（第 19 轮洞 2 修复）

第 19 轮外部评审打穿了"改一行数据换引擎"的承诺：此前 `BulkInsertAsync` 在 InMemory 是 upsert、在 SqlServer 是 insert——**换引擎导致语义变化，抽象就是假的**。

修复：`UniversalEntityRepository.BulkInsertAsync` 事务化 upsert——`BeginTransaction` → OPENJSON 参数化 `DELETE WHERE Id IN (...)` → `SqlBulkCopy(TableLock, transaction)` → `Commit`。三引擎对齐"同 Id 重写 = 覆盖"语义。**抽象的成立条件：不仅接口相同，语义也必须相同。**

---

## 五、三轨路由：快照 / 强类型 / 动态

第 8、11 轮建立，第 12 轮闭合。路由决策 100% 来自池中指令：

| 轨道 | 触发指令 | 数据形态 | 代价与收益 |
|---|---|---|---|
| 快照轨 | `StorageMode=Snapshot` | 整对象 JSON 进实体行 Value（`SNAP1:` 前缀） | 点查零拆解零组装；schema 即类定义，加字段需改类或靠 JSON 容缺 |
| 强类型图 | `CSharpType=全名` | 拆解为实体行+属性行，读回反射物化真实 C# 实例 | 编译期类型安全；schema 由 Role=1 元数据驱动 |
| 动态图（默认） | 无指令 | 拆解为行，读回 `UniversalObject`（DynamicObject） | 零 C# 类；第 17 轮起 DataType 显式声明（`Name:Type`），未声明走按值推断回退 |

**模式切换语义（第 12 轮裁决）**：读取以 StorageMode 指令为准；数据不迁移；双向 mismatch 显式失败（`StorageModeMismatchException`，fail fast）。快照与图是**两个都被明确声明的契约**，不允许"到底信谁"的困惑存在。

哲学意义："一生二"长出了两种存储形态——订单类业务走快照（点查最优），关系类业务走图（遍历最优），同一张表、同一套引擎，选择权在一行指令里。

---

## 六、安全同构：策略是数据，执行器是代码

### 6.1 演进：从硬编码回炉到策略驱动

第 9 轮 `SecuredUniversalStore` 把 AES-GCM、租户 AAD、限流阈值**硬编码在装饰器里**——在纯数据驱动的城市里盖了座混凝土警察局。第 10 轮回炉：策略上移为池中 Role=5 数据，`PolicyDrivenStore` 降级为纯执行器（读策略 → 调执行器 → 委托内层，零硬编码）。

### 6.2 密文前缀版本化

- `AGC1:` = AES-GCM 密文（.NET 内置，零新依赖）；`PLN1:` = 明文标记；`SNAP1:` = 快照 JSON。
- **前缀即版本标识**：切算法后，历史数据按前缀反向路由到原执行器解密——AGC1 与 PLN1 可混存共读（实测：改一行策略数据，新写 PLN1、旧读 AGC1，组装全还原）。
- 加密层与存储层**正交嵌套**：加密整个 Value，SNAP1 前缀被一并加密——黑客拖库看到的是 `AGC1:4rxvkzSy...`，`OrderNo`/订单号零泄露（sqlcmd 亲验 25 行密文留痕，明文泄露 0 行）。
- 租户隔离：租户 Id 作为 AES-GCM Associated Data 参与认证——租户不符/密钥不符/密文篡改统一表现为"不可见"。

### 6.3 诚实边界：同构的边界

**执行器永远是代码**——算法必须代码实现；被同化的是选择、开关、参数、密钥引用。新增算法 = 实现一次 `IValueCipher` + 注册表加一行 + 之后全部数据驱动。密文保护范围是 Value 列（业务语义）；Id/TypeId/Role/ParentId 保持明文以保寻址能力。

### 6.4 网关安全（第 17 轮）

- **API Token 鉴权**：Role=5 `Policy=ApiSecurity|Token=xxx` 策略存在即启用，不匹配直接 401（网关第一道门，不进总线）。
- **自愈冷却**：网关遇"未注册"错误自动 LoadAsync 热重载并重试（第 15 轮自愈机制）+ 5 秒冷却——冷却期内未注册动作直接 429，封堵"自愈变自毁"的查询放大漏洞。

---

## 七、Edge 同构：索引即数据

### 7.1 废弃 Role=3（第 13 轮）

边不再是特殊身份——边 = Role=2 普通行（ParentId=from、TypeId=EdgeType 元数据、Value=SNAP1 快照含 `To` 指针与边属性）。元数据面用 Role=4 指令行 `EdgeType=Follows|From=User|To=User` 定义。

**双向边（第 18 轮 ImportSystem 自动生成正/反向类型行，反向命名约定 `Name+"By"`）**：`CreateEdge` 一次落 2 行——正向挂 From、反向挂 To。"谁关注了我"退化为普通 ParentId 查询，**无需外挂反向索引结构**。

### 7.2 100K 混合压测（30 万行：10 万用户 + 20 万边行）

| 引擎 | BulkWrite | 点查×100 | 正向遍历 avg | 反向遍历 avg | 遍历机制 |
|---|---|---|---|---|---|
| InMemory 字典 | **163 ms（184 万行/s）** | 0 ms | 58 ms/q | 61 ms/q | O(n) 内存全扫 |
| FASTER（LSM） | 659 ms（45.5 万行/s） | 7 ms | 149–160 ms/q | 同左 | O(n) 全扫描（无二级索引，诚实代价） |
| **SqlServer 磁盘表** | 15.1 s（2 万行/s） | **43 ms** | **2 ms/q** | **0 ms/q** | **ParentId B+ 树索引命中** |

**关键洞察——"索引即数据"的辩证结果**：写入侧 InMemory 碾压；但遍历侧 **SqlServer 磁盘表的 B+ 树索引反超内存全扫 30 倍**（2ms vs 58ms）。同构没有消灭索引的必要性，只是把索引的实现责任交还给了物理引擎——双向边让磁盘引擎的成熟索引体系免费为同构架构所用。

---

## 八、意图总线与哑巴网关：消灭后端

### 8.1 唯一入口

```text
POST /api/genesis        { "action": "...", "args": {...}, "token": "..." }
```

`UniversalActionBus` 双路由分发：

1. action 命中 Role=7 工作流命令 → `UniversalCommandExecutor`（步骤是插件、编排是数据：InsertTypeMeta / SetStorageMode / BindCSharpType / GenerateProxy / ReloadCache / ImportSystem）；
2. 内置业务动作：`InsertData`（拆解落库，三轨自动分）/ `GetData`（**实例级**组装）/ `DeleteData`（级联软删）/ `InsertEdge`（双向边）/ `GetSystemSchema`（类型 + 命令 + UI 一次拉回）。

网关自愈：池中刚插入的命令/类型无需重启即刻可用（未注册错误 → 自动 LoadAsync 重试 + 5 秒冷却 429 防刷）。CLI 与 HTTP 共用同一总线——**前端、CLI、第三方全部面对同一个意图入口，后端没有 Controller、没有 Service、没有任何业务代码。**

### 8.2 UI 同构与一句话建系统（第 16、18 轮）

`Genesis.Web/index.html` 是通用渲染器（原生 JS，零业务代码）：启动拉 `GetSystemSchema` → 遍历 Role=8 UI 描述动态生成表单 → 提交按 `UI.Action` 打包意图。

**第 18 轮终局演示——一发建出学生管理系统（HTTP 真库冒烟实录）**：

```text
① sqlcmd 插一行 Role=7：ImportSystem 命令定义
② POST ImportSystem（一段 JSON：Student 图模式 + ClassRoom 快照 + StudiesIn 边 + StudentForm UI）
   → "命令执行完成：2 个步骤，生成 11 行元数据"
③ POST GetSystemSchema → Uis 含 StudentForm
④ POST InsertData Student{张小明,12} → EntityId 落库
sqlcmd 终验：元数据行全部落位
```

一段 JSON → ImportSystem → 浏览器 StudentForm 可用 → InsertData/InsertEdge 灌数据 → 图遍历可查。**全程零代码、零编译、零重启**——前端、后端、数据库三层全部由池中数据驱动。

---

## 九、"演示级"到"引擎级"：第 19 轮三洞修复

第 19 轮接受外部评审，直面三个"底线级"缺陷——这三条不是优化项，是引擎的底线：

### 洞 1：GetData 拿错实体（类型级 API 冒充实例级）

旧实现 `GetSubGraphAsync(entityRow.TypeId)` 拉回整个类型的所有行，再挑"第一条 ParentId 为空的行"——同类型两个实体时返回任意一条，最基本的单实例查询都是错的，且测试只插一条恰好掩盖了它。

修复：实例级子图——`GetByIdAsync(entityId)` 定位实体行 + `GetByParentIdAsync(entityId)` 指针追逐子行。回归护栏 `Bus_GetData_SameTypeTwoEntities_ReturnsOwnSubgraphOnly`：同类型插 Alice/Bob 两个 Student，各自 GetData 断言只含自己的数据（该测试在旧实现下必红）。

### 洞 2：没有 U，BulkInsertAsync 语义不一致

见 §4.3。核心教训：**如果换引擎导致正确性变化，那这个抽象就是假的。**

### 洞 3：并发/事务语义空白

三处加固：

1. **PolicyDrivenStore.DeleteAsync 租户校验**：先点查（解密 + 租户校验）可见才委托内层删除——知道 Guid 不再能删别人的行；
2. **MetaCache 快照化**：全部缓存收束进不可变 `MetaSnapshot`，`LoadAsync` 局部构建完整快照后单引用 volatile 原子替换。旧实现"先 Clear 再逐条填"，并发读可见半成品；六字段分别替换方案也被否决——多字段赋值间隙可观察到"新类型 + 旧命令"混合态，**单引用替换才是严格原子**；
3. **DeleteData 级联软删**（第 17 轮已建）持续保留：实体行 + 全部子行软删，池子不留孤儿。

### 工程卫生

- 0 警告 0 错误（`dotnet build` 全解决方案）；nullable 全开；
- 62 个测试（57 通过 + 5 环境门禁基准——`GENESIS_BENCH=1` 可启用；基准移出默认回归是第 19 轮决定，环境敏感的 100K 压测曾致回归失败数 5→2 漂移）；
- 手动反射 TestRunner（沙箱 TestHost 崩溃绕过），类名排序确定执行顺序，支持类名过滤；
- B1–B34 BUG 台账全记录（[UPDATE_LOG.md](UPDATE_LOG.md)），最阴险的 B1：UTF-8 无 BOM + sqlcmd GBK 解码 = 静默吞列。

---

## 十、哲学 vs 实测：对账表

### 10.1 被证实的

| 哲学宣称 | 实测证据 |
|---|---|
| "结构同构消灭 JOIN" | 子图预取 1 条 SQL；进程内路径连查询都消失，纯指针分组 |
| "组装近乎零成本" | 5 万行内存组装 30–55ms，磁盘路径下占全链路 <3% |
| "引擎同化可行" | Role=1/4/5/7/8 全部落库 + 自举，清池自愈，62 测试覆盖 |
| "数据库可以是哑的" | 五个引擎存同一种行结构，上层组件零改动；网关零业务代码 |
| "I/O 是真正瓶颈" | 148 倍提升全部来自消灭往返与持久化开销 |
| "索引即数据" | B+ 树反超内存全扫 30 倍——同构把索引责任交还物理引擎（§7.2） |
| "策略即数据" | 改一行 Role=5 换加密算法/开关/阈值，AGC1↔PLN1 混存共读（第 10 轮实测） |

### 10.2 被修正的

| 早期宣称 | 实测修正 |
|---|---|
| "零计算" | 计算从未消失。修正为"**计算便宜到可以忽略，I/O 才是价格**"。进程内路径下组装反占 ~35% 成为新瓶颈——恰恰证明 I/O 税已被清零 |
| "一张表构建所有业务（安全角度）" | 同构混淆"让黑客看不懂"的价值**依赖加密层**——第 9/10 轮补上后才成立，第 6 轮时不能作为已得收益 |
| "单表亿级无压力" | 未验证。1 亿行进程内全驻留 ≈ 40–60GB 内存，单机接近极限。规模天花板真实存在 |
| "策略可以完全数据化" | 执行器永远是代码（§6.3）——被同化的是选择与参数，不是算法本身 |

### 10.3 外部预测 vs 实测（预测必须实测的教训）

| 预测（E5 级） | 实测（E2 级） | 偏差 |
|---|---|---|
| BulkCopy 后写入"绝对 200ms 以内" | 1945 ms | **10 倍** |
| "吞吐迎来百倍爆发" | 累计 6.9 倍（当时） | 差 14 倍 |
| 内存表写入"暴跌到 30ms" | 793 ms | **26 倍** |
| 内存表读取"快十倍" | 快 22% | **4.5 倍** |
| "Redis 十几毫秒" | 未测（本机无 Redis）；进程内字典 36ms 已构成更近路径的上限 | 不可比 |

**教训：架构直觉只能指方向，数字必须自己跑出来。**

---

## 十一、十九轮演进总表

| 轮 | 主题 | 质变 / 关键数字 |
|---|---|---|
| 1 | 原型骨架 | 单表模型 + 拆解/组装/路由，11 测试验证往返成立 |
| 2 | 真实库落表 | GenesisIso 建库；逐行写 5 万行 = 15.4s（492/s）；B1 BOM 吞列 |
| 3 | 往返同化 | 60001 次往返 → 2 次，**6.9 倍**（3743/s） |
| 4 | 引擎自举 | 元数据落库 + 空池自播种；清表引擎自愈创世 |
| 5 | 内存表换装 | TVP + In-Memory OLTP，**1.7 倍**（6858/s）；踩掉 4 条 SQL 硬约束 |
| 6 | 去数据库化 | 进程内 KV 双引擎，**8.6 倍**（72992/s），**累计 148 倍** |
| 7 | 接入落地 | IUniversalStore 抽象 + 三 Provider；改一行数据换物理引擎 |
| 8 | 言出法随 | 动态实体 + 类名同化；程序集中无 Order 类照样建业务 |
| 9 | 铁布衫 | 透明加密 + 租户隔离装饰器（硬编码，第 10 轮回炉）；B17 无值行绕过租户认证 |
| 10 | 策略回炉 | Role=5 策略落库 + PolicyDrivenStore；密文前缀版本化，AGC1↔PLN1 混存共读 |
| 11 | 快照模式 | SNAP1"直接煮熟"；三轨路由；零写放大（3 次状态流转行数恒 1） |
| 12 | 闭合回路 | 快照+加密叠加实测；模式切换 fail fast 语义定型 |
| 13 | Edge 同构 | Role=3 废弃；双向边"索引即数据"；100K 压测 B+ 树反超 30 倍 |
| 14 | 同构 CLI | Role=7 命令树即数据；哑巴壳子；步骤是插件、编排是数据 |
| 15 | 意图总线 | POST /api/genesis 唯一入口 + 自愈机制；CLI/HTTP 共用总线 |
| 16 | UI 同构 | Role=8 + 通用渲染器；前端由池中数据驱动 |
| 17 | 工业加固 | Token 鉴权 401；DataType 显式化；级联软删；自愈冷却 429 |
| 18 | 终局演示 | ImportSystem 一段 JSON 建出学生管理系统（11 行元数据） |
| 19 | 三洞修复 | GetData 实例级；BulkInsert 统一 upsert；租户删除校验 + MetaCache 快照化；基准门禁 |

---

## 十二、代价、边界与未竟之路

### 12.1 已知代价（不想清楚就不该继续）

1. **ACID 交换速度**：进程内引擎绕过事务/恢复/审计体系。五引擎是光谱，不是替代链——要完整 ACID 只有 SQL Server 路线。
2. **内存天花板**：进程内全驻留在千万行级触碰单机内存墙；GC 压力未测。
3. **同构的边界**：执行器永远是代码；密文保护 Value 不保护结构；快照模式放弃元数据驱动 schema。
4. **第 19 轮后仍真实的缺口**：Version 乐观锁未启用（Version 字段恒 1）；DeleteData 不清理挂在其他实体下的入边（孤儿边仍可见于对方出边解析）；FASTER 无二级索引的遍历代价；ImportSystem 非原子事务；token 明文比对。

### 12.2 未验证项清单（诚实声明）

FASTER 崩溃恢复；100K/1M 行进程内 GC 压力曲线；并发多租户同时读写；快照+加密+SQL Server 三层叠加（实测在 InMemory 内层）；`GENESIS_BENCH=1` 门禁启用路径；HTTP 层完整 401/429 人工冒烟（经代码审读 + 总线层测试覆盖）。

### 12.3 路线图（决策面板）

| 优先级 | 路线 | 内容 | 理由 |
|---|---|---|---|
| 高 | 测试基建解放 | Testcontainers / 每测试独立库 / 并行 CI / Benchmark 独立项目 | 引擎级项目的底线是可信回归，62 测试共享单库是当前最大工程债 |
| 高 | Version 乐观锁 | 更新时 `WHERE Version=@v` 递增 | 并发语义闭环的最后一块 |
| 中 | 入边级联清理 | DeleteData 同步清理反向边 | 池子完全无孤儿 |
| 中 | 白皮书 3.0 数据 | 100K/1M GC 曲线、FASTER 恢复实测 | 把"未验证"变成表格 |
| 低 | SM4 / 雪花 ID / LLM 意图解析 / ExportSystem | 执行器注册表已留插槽 | 演示价值大于架构价值 |

---

## 十三、结语

十九轮下来，这条曲线背后只有四句话：

1. **往返同化**（60001 → 2 → 0）贡献了绝大部分加速——148 倍；
2. **存储同化**（磁盘 B+树 → 内存表 → 进程内）贡献了剩余全部；
3. **数据化光谱**从表结构一路推进到前端界面——除"读数据、按数据执行"外，系统里几乎没有写死的东西；
4. **哲学不会自动落地**——它落地的方式是 62 个测试、B1–B34 台账、和每轮一张实测基准表。第 19 轮证明了另一件事：**从演示级到引擎级，隔着的不是新功能，而是把抽象的语义补严实**——同样的接口要有同样的语义，同样的引擎要能删别人的行吗，同样的一次加载要能被并发读到一半吗。

> 剩下的路只有一条原则：**每一句"理论上更快/更好"，都必须变成表格里的一行实测数字。**

# Genesis 更新日志

## 2026-10-27 第 17 轮：工业级加固——Token 鉴权、DataType 显式化、级联删除、自愈冷却

### 更新内容

1. **API Token 鉴权**（网关第 1 道门）：Role=5 新策略 `Policy=ApiSecurity|Token=xxx`——策略存在即启用校验，请求 Token 不匹配直接 **401**（不进总线）。策略未配置 = 不启用（本地开发便利，生产必须配置）。
2. **DataType 显式化（消灭类型推断）**：Role=1 属性行格式升级 `Value=Name:Type`（如 `Price:Decimal`）；`MetaType.PropertyDataTypes` 字典承接；`DynamicAssembler.InferTyped` 按声明精确解析（String/Int/Long/Decimal/Bool/DateTime/Guid），解析失败回退推断（脏数据容错）。**旧格式（无冒号）= 未声明 → 走推断，零破坏兼容**。
3. **`DeleteData` 级联软删**（ActionBus 内置）：软删实体行 + 全部 ParentId 子行（属性、出边），池子不留孤儿；返回删除行数；实体不存在显式失败。
4. **自愈冷却防刷**：网关自愈重载加 5 秒冷却——冷却期内未注册错误直接 **429**，不再触发 LoadAsync（第 15 轮"自愈变自毁"漏洞封堵）。
5. `HardeningTests` 4 个：DataType 显式还原（Title="2026-10" 被声明 String 拦住不误判为日期）、DeleteData 级联（3 行全消失无孤儿）、未知实体删除抛错、限流（第 10 轮已有）。

### 核心验收

```text
① 错 Token → 401（HTTP 冒烟形态：网关第 1 道门）
② Title:String 声明下 "2026-10" 保持字符串（推断会误判为日期——DataType 拦住）
③ DeleteData → 实体 + 2 属性 = 3 行全部软删，GetById null / GetByParentId 空 / 子图空
④ 自愈冷却：5 秒内重复未注册动作 → 429
```

### BUG 记录

| BUG | 现象 | 根因 | 修复方式 |
|---|---|---|---|
| B32 | 旧格式属性行（无冒号）被默认声明为 String，DynamicOrder 测试 Amount 变字符串（回归） | 第 17 轮解析器把"未声明"与"声明 String"混为一谈 | 无冒号 = 不写入 PropertyDataTypes（未声明 → 推断回退），有冒号才写入声明 |
| B33 | CS1977 dynamic 变量捕获进 lambda | HardeningTests 中 dynamic product 被 Assert.ThrowsAsync lambda 闭包 | 改用显式 UniversalObject + SetProperty |

### 诚实边界

- 入边（挂在其他实体下的反向边）级联清理未做——DeleteData 后反向边成为孤儿但仍挂在对方实体下（GetOutgoingEdges 会解析出指向已删实体的 To）——列为旁支。
- Token 为明文比对（非哈希存储）、无轮换、无速率限制（自愈冷却只保护 LoadAsync）。
- 401/429 冒烟经代码审读 + ActionBus 层测试覆盖，HTTP 层完整 401 流程留待下次网关启动时人工验证。

### 未改旁支

入边级联清理、token 哈希与轮换、CQRS 读模型、白皮书 2.0、GitHub 推送（本地 6 笔提交就绪）、CLI 代码生成、雪花 ID、SM4。

### 未验证项

并发 Token 校验；级联删除在磁盘表/FASTER 内层的表现；DataType 脏数据回退推断的覆盖率。

---

## 2026-10-27 第 16 轮：UI 同构——Role=8 与通用渲染器（消灭手写前端）

### 更新内容

1. `UniversalEntity.RoleUi = 8`：前端界面描述成为池中数据。
   行格式：`UI=ProductForm|Type=Form|Entity=Product|Fields=Title:Input,Price:Number|Action=InsertData`。
2. `MetaCache`：解析 Role=8 → `GetUiSchemas()`（UI 名 → Entity/Fields/Action）。
3. `UniversalActionBus` 内置系统动作 `GetSystemSchema`：一次拉回全部 Types + Commands + Uis——通用渲染器的唯一数据源。
4. **`Genesis.Web/wwwroot/index.html`**：通用渲染器（原生 JS，零业务代码）——
   - 启动时 POST GetSystemSchema 拉取 Schema；
   - 遍历 Uis 动态生成 `<form>/<input>`（Number 字段映射 number 控件）；
   - 提交时按 UI.Action 打包 InsertData 意图（字段值 JSON 化为 Data）。
5. `Genesis.Gateway` 托管静态前端：`WebApplicationOptions.WebRootPath` 指向 `Genesis.Web/wwwroot`。
6. `UiSchemaTests` 3 个：GetSystemSchema 含 UI 描述、按 UI 字段 InsertData→GetData 往返、无 Role=8 返回空集合。

### HTTP 冒烟（GenesisIso）

```text
① sqlcmd 插入 3 行：Product 类型（Role=1）+ StorageMode=Snapshot（Role=4）+ ProductForm UI（Role=8）
② GET  http://localhost:5506/index.html → 200（通用渲染器，无任何业务代码）
③ POST GetSystemSchema → Uis 含 ProductForm（Fields/Action 完整）
④ POST InsertData（模拟表单提交）→ EntityId 落库
```

**质变**：新增"库存管理"模块 = 通过网关发 2 个意图（CreateEntity + CreateUI）→ 刷新浏览器自动长出表单——前端、后端、数据库三层全部由池中数据驱动。

### BUG 记录

| BUG | 现象 | 根因 | 修复方式 |
|---|---|---|---|
| B30 | 网关启动崩溃：Cannot open log for source '.NET Runtime'（拒绝访问） | Production 环境默认注册 Windows EventLog logger，沙箱无写权限 | `builder.Logging.ClearProviders() + AddConsole()` |
| B31 | index.html 404（文件在 bin 却 404） | `dotnet run` 的 WebRoot 指向项目目录（无 wwwroot）；`builder.WebHost.UseWebRoot` 在 WebApplicationBuilder 下被禁止 | 改用 `WebApplicationOptions.WebRootPath` 指向 `Genesis.Web/wwwroot` |

### 诚实边界

- 通用渲染器仅支持 Form + Input/Number 控件（嵌套/列表/校验规则未做）；无鉴权（沿用第 15 轮边界）；CSS 极简。
- 快照模式 + UI 是"零元数据负担"组合（schema 在类/JSON）；图模式实体配 UI 时 Fields 需与 Role=1 属性行对齐（InsertData 动态轨会拒绝未注册属性——语义正确）。

### 未改旁支

校验规则指令、嵌套/列表控件、CSS 主题、前端框架版本、100K 压测、白皮书 2.0、CLI 代码生成、DirtyGraph、雪花 ID、SM4。

### 未验证项

浏览器端真实交互（本轮回显经 HTTP 层验证，DOM 渲染逻辑靠代码审读）；多个 UI 共存时的布局；UI Schema 指向不存在实体时的渲染表现。

---

## 2026-10-27 第 15 轮：意图总线与哑巴网关——POST /api/genesis 一个入口

### 更新内容

1. `UniversalActionBus`（DAL）：意图总线唯一入口，双路由分发——
   - action 命中 Role=7 工作流 → `UniversalCommandExecutor`（建类型/模式指令/代理生成/热重载）；
   - 内置业务动作 `InsertData`（TypeName+Data JSON → 网关拆解落库，快照/图自动分轨，返回实体行 Guid）、`GetData`（EntityId → 组装返回，万物容器转属性字典视图保证 JSON 可序列化）；
   - 无法识别的动作显式失败。
2. `MetaCache.GetTypeIdByName`：动作参数里的类型名翻译为 TypeId。
3. **`Genesis.Gateway`**（ASP.NET Core 最小 API）：整个"后端"只有一个端点 `POST /api/genesis`——请求契约 `{action, args, token}`（token 为 v1 预留字段，**鉴权未实现**，见诚实边界）。
4. **自愈机制**：网关遇 InvalidOperationException（未注册命令/类型）时自动 `LoadAsync` 热重载并重试一次——池中刚插入的命令/类型无需重启网关即刻可用。
5. `Genesis.CLI` 改走 ActionBus（CLI 与 HTTP 共用同一总线，壳子更哑）。
6. `ActionBusTests` 3 个：工作流动作建类型、InsertData/GetData 快照往返、未知动作抛错。

### HTTP 真库冒烟（GenesisIso，Invoke-RestMethod 三连）

```text
① POST {"action":"CreateEntity","args":{"TypeName":"Product","Props":"Title:String,Price:Decimal","Mode":"Graph"}}
   → OK（Product 类型 + 2 属性元数据落库，sqlcmd 确认 3 行）
② POST {"action":"InsertData","args":{"TypeName":"Product","Data":{"Title":"网关商品","Price":42.5}}}
   → EntityId = 8865687a-...
③ POST {"action":"GetData","args":{"EntityId":"8865687a-..."}}
   → {"Price":42.5,"Title":"网关商品"} 完整读回
全程零 Controller、零 Service、零 Product 相关代码。
```

### BUG 记录

| BUG | 现象 | 根因 | 修复方式 |
|---|---|---|---|
| B27 | HTTP 冒烟 CREATE/INSERT 全部 400 | 网关 MetaCache 是**启动快照**，池中新增的 Role=7 命令行/Role=1 类型不自动生效（热重载无触发点） | 网关遇未注册类错误自动 LoadAsync 自愈并重试一次 |
| B28 | ActionBus 测试 xunit 重载解析 CS1503 / IsType 不匹配 | ExecuteActionAsync 返回 object；Assert.Contains/IsType 推断歧义；InsertData 实际返回 Guid 非 string | 测试断言按真实返回类型收紧（IsType<Guid>/IsType<string>） |
| B29 | GetData 结果 JSON 不含业务属性 | UniversalObject（DynamicObject）的动态属性不被 System.Text.Json 枚举 | GetData 返回前转属性字典视图 |

### 诚实边界

- token 鉴权未实现（预留字段）；RateLimit 策略未接到网关层；CORS/HTTPS/限流/审计全部未配——本网关是架构演示形态，生产化缺整层安全设施。
- 未知动作的自愈重试会对每个未知动作做一次 LoadAsync——恶意扫描可用此放大数据库查询（生产需要限流前置）。

### 未改旁支

Role=8 UI 描述、token 鉴权、LLM 意图解析、CQRS 读模型、白皮书 2.0、边删除级联、雪花 ID、SM4。

### 未验证项

Gateway 并发请求；自愈重试的并发放大效应；网关与 PolicyDrivenStore（加密+租户）叠加的端到端。

---

## 2026-10-27 第 14 轮：同构 CLI——命令树即数据（Role=7）与哑巴壳子

### 更新内容

1. `UniversalEntity.RoleCommand = 7`：CLI 命令与工作流定义成为池中数据。
2. `MetaCache`：LoadAsync 拉取 Role=7 → `GetCommand(name)` / `GetCommandNames()`；元常量 `MetaTypeTypeId`/`MetaEngineTypeId` 公开化（CLI 工作流复用）。
3. `UniversalCommandExecutor`：匹配 Role=7 定义 → 按 `Steps` 编排执行内置原子步骤注册表（**步骤是插件、编排是数据**，与 PolicyDrivenStore 同一哲学）：
   - `InsertTypeMeta`（类型行 + 属性行）、`SetStorageMode`、`BindCSharpType`、`GenerateProxy`（强类型 .cs 生成）、`ReloadCache`（热重载）。
   - 未注册命令/未注册步骤/缺参数全部显式抛错。
4. 新增 `Genesis.CLI` 控制台项目（哑巴壳子）：无参打印池中命令表，`run <命令> K=V...` 执行工作流。
5. `CommandExecutorTests` 3 个：工作流全闭环（元数据落库 + Snapshot 指令 + 代理文件三属性）、未知命令抛错、未知步骤抛错。

### CLI 真库冒烟（GenesisIso）

```text
① sqlcmd 插入一行 Role=7：Command=CreateEntity|Args=...|Steps=InsertTypeMeta;SetStorageMode;GenerateProxy;ReloadCache
② genesis（无参）→ 动态渲染命令表：CreateEntity  Args: ...  Steps: ...
③ genesis run CreateEntity TypeName=SmokeOrder Props=OrderNo:String,Amount:Decimal Mode=Snapshot ...
   → 4 步骤执行、4 行元数据落库（sqlcmd 确认 SmokeOrder 类型行 + StorageMode=Snapshot 指令 + 2 属性行）
   → SmokeOrder.cs 强类型代理生成
   → MetaCache 热重载完成
```

### 格式契约（重要）

Role=7 行的键值对分隔符是 `|`，**Steps 内的步骤分隔符是 `;`**——`Steps=A|B` 会因 B 无 `=` 被键值解析器静默吞掉（B25 根因）。

### BUG 记录

| BUG | 现象 | 根因 | 修复方式 |
|---|---|---|---|
| B25 | 多步骤工作流只有第一个步骤执行 | Steps 用 `|` 分隔与键值对分隔符冲突，后续步骤名因无 `=` 被静默丢弃 | Steps 分隔符改 `;`（格式契约已文档化） |
| B26 | 代理生成 UnauthorizedAccess | %TEMP% 子目录沙箱拒绝（同 B13） | 测试临时目录改 AppContext.BaseDirectory |

### 未改旁支

LLM 自然语言解析（Mock 接口）、复杂参数解析、代理模板定制、边删除级联、100K 压测已在第 13 轮完成、白皮书 2.0、雪花 ID。

### 未验证项

Role=7 命令行的并发执行；GenerateProxy 与已存在文件的覆盖策略（当前直接覆盖）；LLM 接入后的参数映射准确性。

---

## 2026-10-27 第 13 轮：Edge 同构——消灭 Role=3，索引即数据（双向边）+ 100K 混合压测

### 更新内容

1. **Role=3 废弃声明**：边不再是特殊身份——边 = Role=2 普通行（ParentId=from、TypeId=EdgeType 元数据、Value=SNAP1 快照含 `To` 指针与边属性）。`RoleEdge` 常量保留但已无引擎逻辑引用。
2. **元数据面**：Role=4 指令行 `EdgeType=Follows|From=User|To=User`；`MetaCache.GetEdgeDefinition(edgeTypeId)` 查询边定义。
3. **双向边（索引即数据）**：`Gateway.CreateEdge(from, edgeTypeId, reverseEdgeTypeId, to, props)` 一次生成 2 行——正向挂 From、反向挂 To，**"谁关注了我"退化为普通 ParentId 查询**，无需外挂反向索引结构。
4. `Gateway.GetOutgoingEdgesAsync(store, entityId, edgeTypeId)`：指针追逐 + TypeId 过滤 + 快照解析 → `(To, Props)` 列表。
5. `IUniversalStore` 接口新增 `GetByParentIdAsync`（指针追逐进抽象层）；三引擎 + PolicyDrivenStore（解密过滤）全部实现。
6. **100K 混合压测**（`EdgeIsomorphismTests`，GENESIS_BENCH_ROWS 可调、抽样遍历控制资源消耗）：10 万快照用户 + 10 万 Follows 双向边（环形 i→i+1，30 万行）。

### 100K 压测数据（30 万行，本机 SQL Server 2019）

| 引擎 | BulkWrite | 点查 ×100 | 正向遍历 avg | 反向遍历 avg | 遍历机制 |
|---|---|---|---|---|---|
| InMemory 字典 | **163 ms（184 万行/s）** | 0 ms | 58 ms/q | 61 ms/q | O(n) 内存全扫 |
| FASTER（LSM） | 659 ms（45.5 万行/s） | 7 ms | 149-160 ms/q | 同左 | **O(n) 全扫描（无二级索引，诚实代价）** |
| **SqlServer 磁盘表** | 15.1 s（2 万行/s） | **43 ms** | **2 ms/q** | **0 ms/q** | **ParentId B+ 树索引命中** |

**关键洞察——"索引即数据"的辩证结果**：写入侧 InMemory 碾压（184 万行/s）；但遍历侧 **SqlServer 磁盘表的 B+ 树索引反超内存全扫 30 倍**（2ms vs 58ms）——双向边让磁盘引擎的成熟索引体系免费为同构架构所用，而 FASTER 因无二级索引付出全扫描代价。**同构没有消灭索引的必要性，只是把索引的实现责任交还给了物理引擎。**

### BUG 记录

| BUG | 现象 | 根因 | 修复方式 |
|---|---|---|---|
| B23 | 边属性 "2026-01" 被误解析为 DateTime（2026-01-01） | 第 11 轮 DateTime 回退解析过宽（TryParse 接受不完整日期） | 回退加长度门槛（>=10 字符，即 yyyy-MM-dd 起） |
| B24 | CS0535 PolicyDrivenStore 未实现新接口成员 | IUniversalStore 新增方法后装饰器漏实现 | 实现并做解密过滤（装饰器不裸透传密文） |

### 未改旁支

边删除级联（双向边两行同步删）、EdgeType 指令热切换、SNAP 行自动转换/迁移工具、SM4 执行器、CQRS 读模型、白皮书 2.0、CLI/代码生成、DirtyGraph、雪花 ID。

### 未验证项

百万级边规模；FASTER 并发会话遍历；双向边删除一致性；PolicyDrivenStore 加密下的边遍历性能（解密开销未计入本轮压测）。

---

## 2026-10-27 第 12 轮：闭合回路——快照+加密叠加实测与模式切换语义定义

### 更新内容

1. **快照 + 加密叠加实测**（第 11 轮遗留未验证项，已闭合）：`PolicyDrivenStore`（tenant-A，AesGcm）包装快照模式——
   - 黑客视角：内层原始行是 `AGC1:` 密文，`SNAP1:`/`OrderNo`/订单号全部不可见（加密整个 Value，SNAP1 前缀被一并加密）；
   - 本租户读回：解密 → 剥 SNAP1 → 强类型反序列化，全属性无损还原；
   - 租户 B：点查 null、子图空。
2. **模式切换语义定义**（新增 `StorageModeMismatchException`）：
   - **读取以 StorageMode 指令为准；数据不迁移；双向 mismatch 显式失败（fail fast）**；
   - Graph 指令遇 SNAP1 快照行 → `ObjectAssembler`/`DynamicAssembler` 抛 Mismatch；
   - Snapshot 指令遇图模式存量行 → `SnapshotCodec` 抛 Mismatch；
   - 运维含义写入异常文档：切换 StorageMode 前必须确认存量数据已迁移或可弃。
3. **快照模式 schema 边界文档化**（哲学张力正式裁决）：

> **快照模式的权衡声明**：快照模式放弃元数据字段驱动（Role=1 属性行），schema 即类定义——加字段 = 改 C# 类 + 重编译（或动态轨经 JSON 容缺）。换取的是点查零拆解零组装与 schema 自由（JSON 自描述）。图模式保留元数据驱动（加字段 = 插一行 Role=1）换取图遍历能力。**选择权在一行 StorageMode 指令里，开发者按业务场景二选一，不允许"到底信谁"的困惑存在——两种模式都是被明确声明的契约。**

4. 新增 `SnapshotClosureTests` 3 个：加密叠加全链路、Graph→Snapshot 切换抛错、Snapshot→Graph 切换抛错。

### 核心验收

```text
① 密文行 = AGC1:...（内含 SNAP1 不可见，"OrderNo"/订单号零泄露）
② 解密 → SNAP1 → 强类型反序列化 → 全属性还原（599.5m / DateTime 无损）
③ 租户 B：GetById null / GetSubGraph 空
④ Graph→Snapshot 切指令读旧图行 → StorageModeMismatchException
⑤ Snapshot→Graph 切指令读旧快照行 → StorageModeMismatchException
```

### BUG 记录

（本轮无运行时 BUG；B21/B22 为第 11 轮编译期错误已记录。）

### 未改旁支

模式切换数据迁移工具（指令变更时的行格式转换器）、JSON 容错缺省（类加字段后旧快照反序列化不崩）、SNAP 行自动转换、SM4 执行器、密钥轮换、100K/1M 双模式混合压测（语义已闭合，压测数据现在是"全真"）、白皮书 2.0、CLI/代码生成、DirtyGraph、跨实体 Edge、雪花 ID。

### 未验证项

快照 + 加密 + SQL Server 磁盘表三层叠加（当前实测在 InMemory 内层）；mismatch 异常在子图预取路径（GetSubGraph 本身不组装，仅组装时触发——调用方异常处理策略未约定）。

---

## 2026-10-27 第 11 轮：端到端 Demo——快照模式（"直接煮熟"）与图模式双轨共存

### 更新内容

1. **存储策略数据化**：Role=4 新指令键 `StorageMode=Snapshot|Graph`——存储形态本身成为池中一行指令，改一行数据即切换实体的存储模式。
2. **SnapshotCodec**（"直接煮熟"落地）：整对象一次序列化进实体行 Value，`SNAP1:` 前缀与加密层（AGC1:/PLN1:）**正交嵌套**（存储层标记在内、加密层在外）。
   - 序列化用 BCL 自带 System.Text.Json，零第三方依赖（MemoryPack/MessagePack 二进制形态列旁支）；
   - 强类型轨：JSON 保留完整类型信息，反序列化零损耗往返（decimal/DateTime 无损）；
   - 动态轨：JsonElement 经标量推断还原（`InferValue` 增加 ISO 8601 回退解析以覆盖 JSON 默认日期格式）；
   - 快照模式 schema 即类定义——不需要 Role=1 属性行。
3. `UniversalEntityGateway` 三轨路由（全部由池中指令决定）：快照 → 强类型图（CSharpType）→ 动态图（默认）。
4. **端到端演示**（`EndToEndDemoTests`）：
   - PurchaseOrder（快照）：`Created → Paid → Shipped` 状态机，整快照覆盖（同 Id 重写），**3 次流转后行数恒为 1**（零写放大），金额/时间强类型无损；
   - PaymentFlow（图）：动态轨拆解，3 条流水 = 12 行（3 实体 + 9 属性），子图预取 + 图遍历按用户过滤；
   - 混合共存：同一 InMemory 池内两种模式互不干扰（TypeId 隔离），快照点查与图遍历联合可用。

### 哲学意义

"直接煮熟"补上后，**存储策略本身也是数据**：订单类业务走快照（点查最优），关系类业务走图（遍历最优），同一张 UniversalEntity、同一套引擎——"一生二"长出了两种存储形态，选择权在一行指令里。

### BUG 记录

| BUG | 现象 | 根因 | 修复方式 |
|---|---|---|---|
| B21 | CS0136 变量作用域冲突 | Gateway.Assemble 快照分支与图模式分支同名局部变量 | 快照分支变量改名 |
| B22 | CS8858 with 表达式用于非 record 类 | PurchaseOrder 是 class | 显式构造新实例 |

### 未改旁支

MemoryPack/MessagePack 二进制快照、事件追加式更新（vs 整快照覆盖）、快照字段索引/部分更新 diff、嵌套对象还原、100K/1M 多类型混合压测（现已有真实双模式负载）、白皮书 2.0、CLI/代码生成、DirtyGraph、跨实体 Edge、雪花 ID。

### 未验证项

快照与图模式互相切换（StorageMode 指令改动后旧行的读取语义）；快照 + 加密策略叠加的完整链路（SNAP1 内嵌于 AGC1 的实测）；快照模式在大对象（深嵌套/大数组）下的序列化成本。

---

## 2026-10-27 第 10 轮：安全策略同化回炉——Role=5 落库与 PolicyDrivenStore

### 方向校准（回炉第 9 轮）

第 9 轮的 `SecuredUniversalStore` 把 AES-GCM、租户 AAD、限流阈值**硬编码在装饰器里**——在纯数据驱动的城市里盖了座混凝土警察局。第 10 轮把策略上移为池中数据（Role=5），装饰器降级为纯执行器：

| 第 9 轮（硬编码） | 第 10 轮（策略驱动） |
|---|---|
| 加密算法写死 AES-GCM | `Policy=EncryptValue\|Algo=AesGcm\|Enabled=true`——一行数据 |
| 租户隔离无条件开启 | `Policy=TenantIsolation\|Enabled=true`——一行数据 |
| 限流阈值构造参数传入 | `Policy=RateLimit\|Max=100000`——一行数据 |
| 换算法 = 改代码 | 改一行 Role=5 + LoadAsync，零代码零编译 |

### 更新内容

1. `UniversalEntity.RoleSecurity = 5` 常量；创世播种增至 **10 行**（新增 3 行 Role=5：EncryptValue / TenantIsolation / RateLimit）。
2. `MetaCache` 解析 Role=5 → `GetSecurityPolicy(name)`（策略名 → 键值对）；LoadAsync 同步拉取 Role=5，热重载即生效。
3. `ValueCiphers.cs`：执行器注册表（算法即插件）——`IValueCipher` 契约 + `AesGcmValueCipher`（前缀 AGC1:）+ `PlainValueCipher`（前缀 PLN1:）；**密文前缀即版本标识**，按前缀反向路由执行器——切算法后历史数据仍可解。
4. `SecuredUniversalStore` **删除**，替换为 `PolicyDrivenStore`：只做"读策略 → 调执行器 → 委托内层"，零硬编码策略；KeyRef 从密钥环解析。
5. `SecurityPolicyTests`（原 SecurityStoreTests 重构）6 个：密文落库/透明解密、租户隔离+错钥不可见、**改一行数据换加密算法（AGC1↔PLN1 混存共读）**、关闭加密开关、限流阈值数据化、外来裸数据不可见。
6. `ZCiphertextDemoTests` 改用 PolicyDrivenStore；TestRunner 保持类名排序。

### 核心验收（全部零代码零编译）

```text
① UPDATE Role=5 行: Algo=AesGcm → Algo=Plain  → 热重载 → 新写入行是 PLN1: 明文标记
② 同一存储里 5 行 AGC1 历史密文 + 5 行 PLN1 新明文 → GetSubGraph 混读 → 逐实体组装全部属性还原
③ UPDATE Enabled=false → 新数据直接明文直存
④ UPDATE RateLimit Max=100000 → Max=3 → 4 行写入即被拒绝
⑤ sqlcmd 终验：25 行 AGC1 密文留痕，明文泄露 0 行
```

### 诚实边界（同构的边界，必须说清）

- **执行器永远是代码**——算法必须代码实现；被同化的是选择、开关、参数、密钥引用。新增算法 = 实现一次 `IValueCipher` + 注册表加一行 + 之后全部数据驱动。
- SM4 演示未做：.NET 内置无国密算法，需 BouncyCastle 依赖——执行器注册表已留插槽，列为旁支。
- 密文保护范围不变：Value 列（业务语义），Id/TypeId/Role/ParentId 保持明文（保寻址能力）。

### BUG 记录

| BUG | 现象 | 根因 | 修复方式 |
|---|---|---|---|
| B20 | 两个新测试 NullReferenceException | 测试内层是 InMemory 存储，"原始视角"却查磁盘表仓储 | 原始视角改读内层存储本身的原始内容 |

### 未改旁支

SM4/ChaCha20 执行器、密钥轮换、Role=5 策略版本迁移、策略变更审计、CLI+代码生成（前置 DataType 指令）、100K/1M 混合压测、CQRS 读模型、DirtyGraph、跨实体 Edge、雪花 ID。

### 未验证项

FasterKv 内层 + 加密执行器的性能损耗；并发多租户；策略行被误删（缺策略）时引擎的降级行为（当前：无加密策略 = 明文 PLN1，无限流 = 无上限——应否 fail-safe 待定）。

---

## 2026-10-27 第 9 轮：铁布衫——SecuredUniversalStore 透明加密与租户隔离

### 更新内容

1. `SecuredUniversalStore : IUniversalStore` 装饰器——包装任意引擎（SqlServer/InMemory/FasterKv 通吃），三层防护：
   - **防脱库**：Value 列 AES-GCM 透明加密（.NET 内置 `AesGcm`，零新依赖），落库形态 `ENC1:` + Base64(nonce12+tag16+cipher)，版本化前缀支持未来换算法；
   - **防越权**：租户 Id 作为 AES-GCM Associated Data 参与认证——租户不符/密钥不符/密文被篡改三种情况统一表现为"不可见"（GetById 返回 null、GetSubGraph 剔除该行）；
   - **防内存炸弹**：BulkInsert 行数超 maxBulkRows（默认 10 万）直接拒绝。
2. 密钥管理：256-bit 密钥构造注入或经环境变量 `GENESIS_ENC_KEY`（Base64），缺失/长度不符显式抛错，**绝不静默降级为弱密钥**；null 明文也加密为空载荷（否则实体行会绕过租户隔离）。
3. 新增 `SecurityStoreTests` 4 个 + `ZCiphertextDemoTests` 1 个（类名 Z 前缀保证 TestRunner 排序最后执行，密文**刻意留痕不清理**供 sqlcmd 人工验证）。
4. TestRunner 升级：类名排序确定执行顺序 + 可选类名过滤参数（`dotnet run -- -- Security`）。

### sqlcmd 亲眼验证（测试跑完后立即执行）

```text
SELECT ... WHERE Value LIKE 'ENC1:%'  →  25 行密文，形如 "ENC1:4rxvkzSyUcqI4Muz5E1bqtpHa4WSf1niyH9M2IV..."
WHERE Value LIKE '%密文张三%' OR Value LIKE '%@genesis%'  →  明文泄露 0 行
```
**拖库者拿到的是 25 行乱码；租户 A 的 C# 代码读回的依然是"密文张三"明文。**

### BUG 记录

| BUG | 现象 | 根因 | 修复方式 |
|---|---|---|---|
| B17 | 租户 B 竟然看得到租户 A 的实体行 | 实体行 Value 恒为 null，加密端 null 直通 → **无值行绕过租户认证** | null 明文也加密为空载荷密文（AAD 照常绑定租户），解密空载荷还原为 null |
| B18 | 测试内密文行 sqlcmd 查不到（CipherRows=0） | 后续测试类开头的 TRUNCATE 清掉了留痕数据——测试类共享库状态但顺序不确定 | TestRunner 按类名排序 + Z 前缀留痕演示类最后执行 |
| B19 | 过滤参数 `-- SecuredStore` 匹配 0 个测试类 | 类名是 SecurityStoreTests，"SecuredStore"非其子串 | 过滤子串改用 "Security"（使用层错误，非代码 BUG） |

### 诚实边界

- Id/TypeId/Role/ParentId 未加密（加密会破坏引擎寻址能力）——密文保护的是业务语义（Value），不是行结构；配合 Guid 主键（非顺序）已天然防 ID 遍历。
- 密钥轮换未实现（ENC1 前缀为未来版本迁移预留）；Role=5 安全策略指令落库化未做；租户列表/授权体系未做。
- AES-GCM 随机 nonce 每次独立，未做防重放窗口（原型期单机无重放面）。

### 未改旁支

密钥轮换、Role=5 安全指令、CLI + 代码生成（DX，前置 DataType 指令）、100K/1M 多类型混合压测、CQRS 读模型、DirtyGraph、跨实体 Edge、雪花 ID。

### 未验证项

并发多租户同时读写同一 InMemory 实例；FasterKv 内层加密后的 Iterate 解密性能损耗；密文体积膨胀（+33% Base64）在 NVARCHAR(MAX) 下的存储影响。

---

## 2026-10-27 第 8 轮：言出法随——动态实体与类名同化（C 路线）

### 更新内容

1. **动态轨组件**：
   - `DynamicDecomposer`：Dictionary/UniversalObject → 同构行，属性集完全由池中 Role=1 元数据决定；未知属性键显式拒绝（言出法随的前提是元数据先行）。
   - `DynamicAssembler`：同构行 → `UniversalObject`；类型策略为"按值推断"（int/long/decimal/bool/DateTime-O/Guid 可无损解析则强类型，否则原样字符串）——严格类型还原依赖未来元数据 DataType 指令，列为旁支。
   - `NodeEdgeDecomposer.SerializeValue` 提为 internal，两轨共用同一值编码规则。
2. **类名同化**：
   - `UniversalObject : DynamicObject`——万物容器，`dynamic` 点号读写，属性字典为真正存储层，携带 TypeName 与实体行 Id。
   - `MetaCache.GetCSharpTypeName`——Role=4 指令里的 `CSharpType=全名` 键值对即类绑定；**类名成了池中的一行普通数据**。
   - `UniversalEntityGateway` 统一入口：有 CSharpType → 反射物化真实 C# 实例走强类型轨；无 → UniversalObject 动态轨。路由决策 100% 来自数据池。
3. 新增 `DynamicEntityTests` 3 个：
   - **动态 Order 全生命周期**：插 4 行 Order 元数据（程序集中无 Order 类）→ 热重载 → `dynamic order.Amount = 99` → 网关拆解落库 → 子图读回 → `dynamic` 断言 Amount==99（int 推断）/OrderNo 字符串/OrderDate DateTime 全还原。
   - **CSharpType 路由**：给 User 补一条 `CSharpType=Emoking.DAL.Tests.User` 指令 → 动态数据被反射装进真实 User 实例走强类型轨 → 组装返回强类型实例且全属性一致。
   - **未知属性拒绝**：元数据未声明的 GhostProperty 拆解显式抛错。

### 质变确认

- "建业务"三件套全部数据化：表结构（Role=1）、路由与引擎指令（Role=4）、**类绑定（Role=4 的 CSharpType 键）**。新增一个业务类型的全部成本 = 往池子里插几行数据 + LoadAsync。
- 业务层数据形态光谱：真实 C# 实例（编译期）↔ UniversalObject（dynamic 点号）↔ Dictionary（纯字典），同一套同构行、同一个网关。

### BUG 记录

| BUG | 现象 | 根因 | 修复方式 |
|---|---|---|---|
| B16 | 编译警告 CS8794（模式恒真） | Gateway.Decompose 路由条件 `is not A or not B` 逻辑写绕 | 重写为三分支清晰路由（UniversalObject / 字典 / 其他） |

### 未改旁支

元数据 DataType 严格类型指令（消除"按值推断"的猜测性）、IObjectAssembler 接口抽象、运行时 CodeGen（表达式树编译 setter，消除反射开销）、引擎热切换监控、100K/1M 多类型混合压测、DirtyGraph、跨实体 Edge、雪花 ID。

### 未验证项

动态轨在多类型并存时的组装正确性（现仅 User+Order 两类型）；UniversalObject 与并发拆解；CSharpType 指向不存在类时的降级策略（当前显式抛错）。

---

## 2026-10-27 第 7 轮：接入落地——IUniversalStore 抽象与引擎动态落位（路线 A）

### 更新内容

1. `IUniversalStore` 接口：同构池四基本动作（BulkInsertAsync / GetSubGraphAsync / GetByIdAsync / DeleteAsync），上层与物理引擎彻底解耦。
2. 三 Provider 实现：
   - `SqlServerUniversalStore`——包装磁盘表仓储（唯一完整 ACID）；
   - `InMemoryUniversalStore`——ConcurrentDictionary 直持对象引用（零拷贝，最快）；
   - `FasterKvUniversalStore`——FASTER LSM + `UniversalEntityCodec` 二进制打包（可持久化，Iterate 扫描实现子图预取）。
3. `UniversalEntityCodec`：同构行手工二进制编解码器进 DAL（自测试项目提取）。
4. **注入中心同化**：MetaCache 解析 Role=4 新指令 `StorageProvider=SqlServer|InMemory|FasterKv`；创世播种增至 **7 行**（新增存储引擎选择指令行）；`UniversalStoreFactory.Create(cache, dbContext)` 按指令动态落位引擎，未知引擎名显式抛错。
5. 新增 `UniversalStoreTests` 6 个：工厂落位、**改一行数据换引擎**、三 Provider 行为一致性链路（写入→点查→子图→软删→全路径不可见）。
6. MetaEngine 增加 `Settings` 字典（完整键值对），路由字段（RouteTarget/TargetTypeId）改为可选。
7. TestRunner 失败输出升级为完整异常堆栈。

### 核心验收：改一行数据 = 换物理引擎

```text
池中指令行 00000000-…-0902: "StorageProvider=SqlServer"
  → UPDATE 该行 Value = "StorageProvider=InMemory" → LoadAsync → 工厂产出 InMemoryUniversalStore
  → 再 UPDATE 为 "StorageProvider=FasterKv"        → LoadAsync → 工厂产出 FasterKvUniversalStore
全程零代码变更、零编译。物理引擎成了数据池的一条普通指令。
```

### BUG 记录

| BUG | 现象 | 根因 | 修复方式 |
|---|---|---|---|
| B12 | StorageProvider 指令解析永远不生效（工厂始终返回默认 SqlServer） | ParseMetaRows 第三遍要求指令必须含 RouteTarget+TargetTypeId，**无路由字段的一般指令行被静默丢弃**——真产品 BUG | 路由字段改为可选，所有 Role=4 行一律入缓存；测试 1 的"通过"实为 null→默认值的假象，靠断言类型才暴露 |
| B13 | FASTER Store 测试连续失败 "Access denied"，且两次误判为目录删除问题 | 借 TestRunner 堆栈升级定位：**构造函数 CreateDirectory 被沙箱拒绝**（%TEMP% 子目录不可创建），非删除问题；此前捕获类型不匹配所以 catch 无效 | 临时目录改放 `AppContext.BaseDirectory`（bin 输出树可写）；目录删除改为尽力清理 |
| B14 | CS0165 未赋值局部变量 | `&&` 短路下 out var 未赋值路径 | 预赋值 Guid.Empty |
| B15 | CS0122 IFasterSession 不可访问 / CS0308 IClientSession 非泛型 | FASTER 会话类型是内部实现细节 | 字段改存 `FasterKV` 实例，每方法内经 `ClientSession<...>` 具体类按需创建会话 |

### 未改旁支

DI 容器（现用工厂模式）、按 Role=4 指令的引擎热切换（当前需重载元数据）、热内存+LRU 冷加载、DirtyGraph 异步落盘、100K/1M 规模曲线、跨实体 Edge、雪花 ID。

### 未验证项

FasterKvUniversalStore 并发会话（当前单会话语义）；FasterKv 崩溃恢复后子图预取正确性；InMemory GetSubGraphAsync 在百万行的 O(n) 遍历成本。

---

## 2026-10-27 第 6 轮：去数据库化——进程内原生 KV 引擎基准

### 更新内容

1. 测试项目新增 `Microsoft.FASTER.Core 2.6.5`（微软嵌入式 LSM KV，NuGet 即装）。
2. 新增 [InProcessKvBenchmarkTests.cs](../src/Emoking.DAL.Tests/InProcessKvBenchmarkTests.cs)：
   - `EntityBytes` 手工二进制打包器（Id/TypeId/Role/ParentId/Value/Version/IsDeleted 紧凑编码，无 JSON、无反射、无序列化器框架）；
   - `ConcurrentDictionary` 基准——"进程内 static 字典"的字面实现，纯内存对象引用；
   - `FASTER KV` 基准——嵌入式可持久化 LSM 引擎（临时日志设备，跑完清理）。
3. Redis 未纳入：本机实测无 redis-server/memurai 二进制且 6379 端口关闭；安装属系统级操作未获授权，记录为不可行项。进程内字典路径比 Redis（TCP loopback）更近，已构成理论速度上限参照。

### BUG 记录

| BUG | 现象 | 根因 | 修复方式 |
|---|---|---|---|
| B9 | FASTER 编译错 CS1620/CS0117：Read 参数需全 ref、Status 无 OK 成员 | 2.6.5 API 签名与记忆不符（`Read(ref key, ref input, ref output)`） | 以编译错误为准迭代修正，不凭记忆编造 API |
| B10 | 基准超时卡死（首轮跑挂） | `MemorySizeBits=20` 仅 **1MB** 内存预算，装不下 ~6MB 数据 → FASTER 同步刷盘风暴 | 改为 2^25=32MB、PageSizeBits=20；数量级教训记录在案 |
| B11 | null 警告 CS8601 ×2 | ref 参数传 `default(byte[])`（可空） | `Array.Empty<byte>()` 初始化 |

### 五引擎同规模对比（10K 实体 = 5 万行，同一台机器同一轮次）

| 引擎 | 写入 | 读取 | 总计 | 吞吐（entities/s） | IPC 往返 |
|---|---|---|---|---|---|
| ① 逐行 + SQL Server 磁盘表 | 15419 ms | 4810 ms | 20311 ms | 492 | 60001 |
| ② BulkCopy + 磁盘表 | 1858 ms | 655 ms | 2528 ms | 3955 | 2 |
| ③ TVP + 内存优化表 | 809 ms | 611 ms | 1458 ms | 6858 | 2 |
| ④ **ConcurrentDictionary（进程内）** | **36 ms** | **23 ms** | **137 ms** | **72992** | **0** |
| ⑤ **FASTER KV（进程内 LSM，可持久化）** | **81 ms** | **38 ms** | **176 ms** | **56818** | **0** |

**关键数字**：
- 进程内引擎 vs SQL Server 内存表：**8.6 倍**；vs 最初逐行版：**148 倍**。
- ConcurrentDictionary 写入 138.9 万行/s、读回 217.4 万行/s；FASTER（含 LSM 持久化）写入 61.7 万行/s、读回 131.6 万行/s。
- "10ms 量级"的诚实表述：5 万行全链路 137ms，摊到每实体 **13.7 微秒**（写入+读取+组装全含）；单个实体的引擎内写入是微秒级。
- 拆解+组装成本不变（~80ms），**存储层已不再是瓶颈**——瓶颈回到内存拆解/组装本身（这正是"零计算"哲学的最终形态：整个系统的耗时就是纯计算，无任何 I/O 税）。

### 代价与边界（必须诚实记录）

- ConcurrentDictionary：重启全丢，无并发持久化保障——仅适用可全量重建的场景。
- FASTER：LSM 落盘可持久化，但进程崩溃时 tail 数据依赖 checkpoint 策略；本基准未做崩溃恢复验证。
- 两者都**绕过了 SQL Server 的事务/恢复/审计体系**——换来速度、交出数据库级保证，这正是"数据库只负责存 → 存这事我自己干"的哲学代价。
- SQL Server 路线（③）仍然是唯一带完整 ACID 的选项，五引擎不是替代关系，是光谱。

### 未改旁支

IUniversalStore 抽象 + 五引擎可插拔接入 DAL、MMF 内存映射文件、SCHEMA_ONLY + 原生编译 PROC、Event Sourcing（Role=6）、内存 MMU + DirtyGraph、100K/1M 规模曲线、跨实体 Edge、雪花 ID、Redis 安装。

### 未验证项

FASTER 崩溃恢复与 checkpoint 语义；100K+ 规模下进程内引擎内存占用与 GC 压力；FASTER 并发会话。

---

## 2026-10-27 第 5 轮：物理引擎换装——In-Memory OLTP 内存优化表

### 更新内容

1. GenesisIso 添加 `MEMORY_OPTIMIZED_DATA` 文件组 + 内存容器文件。
2. 新增 [MemoryOptimizedEntity.sql](../src/Emoking.DAL/Sql/MemoryOptimizedEntity.sql)：TVP 类型 `UniversalEntityTableType` + 内存优化表 `MemoryOptimizedEntity`（SCHEMA_AND_DATA，哈希主键 BUCKET_COUNT=2^18 + 2 个内联范围索引）+ TVP 批量装载存储过程 `usp_BulkInsertMemoryEntities`。
3. 新增 `MemoryOptimizedEntityRepository`：TVP 单次往返写入、DELETE 清表、子图预取/点查/指针追逐查询（与磁盘表仓储同构，仅表名不同）。
4. 新增基准 `Benchmark_Users_MemoryOptimizedStorage`，三版同规模对比。

### 外部方案的两个事实纠偏（E3 级，实测/文档证实）

| 外部说法 | 实际情况 |
|---|---|
| "把 BulkCopy 切到内存优化表" | **内存优化表不支持 SqlBulkCopy/bcp**——改用 TVP + 存储过程单次往返 |
| "5 万行降到 30ms 以内" | 实测 **793ms**（TVP 传输 + INSERT + checkpoint），未兑现 |
| "读取快十倍" | 实测快 **22%**（397ms vs 511ms），未兑现 |

### 实测中踩掉的 SQL Server 硬约束（均为 E2 级实测证据）

| 约束 | 现象 | 处理 |
|---|---|---|
| 内存优化表不支持事后 `CREATE INDEX`（报 10794） | 两条 CREATE INDEX 全部失败 | 索引内联进 CREATE TABLE（`INDEX … NONCLUSTERED`） |
| 内存优化表索引不支持 `INCLUDE` 列（报 10664） | 内联索引带 INCLUDE 建表失败 | 去掉 INCLUDE，需要回表（内存表回表是指针解引用，代价极小） |
| DROP TYPE 被 PROC 引用阻塞（报 3732） | 脚本重跑失败 | 脚本清理顺序固定为 PROC → TABLE → TYPE |
| 内存优化表不支持 `TRUNCATE` | 文档明确 | 清表用 DELETE（`DeleteAllAsync`） |

### 三版基准对比（10K 实体 = 5 万行，本机 SQL Server 2019，同轮次）

| 阶段 | ① 逐行+磁盘表 | ② BulkCopy+磁盘表 | ③ TVP+内存表 |
|---|---|---|---|
| 拆解 | 23 ms | 15 ms | 35 ms |
| 写入（往返 50000→1→1） | 15701 ms | 1575 ms | **793 ms** |
| 读取（往返 10001→1→1） | 3911 ms | 511 ms | **397 ms** |
| 组装 | 59 ms | 48 ms | 30 ms |
| **总计** | 19694 ms（507/s） | 2149 ms（4653/s） | **1255 ms（7968/s）** |

**结论**：物理引擎三级换装累计 **15.7 倍**（507 → 7968 entities/s）；内存表相对磁盘表 BulkCopy 再快 **1.7 倍**。写入路径剩余成本主要是 TVP 传输与 SCHEMA_AND_DATA 持久化 checkpoint——SCHEMA_ONLY 模式可再压（重启丢数据，列为旁支）。

### BUG 记录

| BUG | 现象 | 根因 | 修复方式 |
|---|---|---|---|
| B7 | csproj 加载失败 MSB4025 "Data at the root level is invalid" | 项目文件开头出现双 BOM，MSBuild XML 解析拒绝 | TrimStart(0xFEFF) 后以无 BOM UTF-8 重写 |
| B8 | 内存表 DDL 首跑三连失败（10794/10664/3732） | 三条硬约束见上表 | 重写 DDL：内联索引、去 INCLUDE、固定清理顺序 |

### 未改旁支

SCHEMA_ONLY 模式、原生编译存储过程（Natively Compiled Proc，进一步压 TVP 解释开销）、Redis/RocksDB 换引擎、Event Sourcing（Role=6 全追加写）、内存 MMU + DirtyGraph、100K/1M 规模曲线、跨实体 Edge、雪花 ID。

### 未验证项

内存表在 100K+ 行的哈希索引桶数衰减；原生编译 PROC 收益；SCHEMA_ONLY 与 SCHEMA_AND_DATA 差距；并发写入下的乐观冲突重试。

---

## 2026-10-27 第 4 轮：引擎自举（Bootstrap）——元数据同化进数据库

### 更新内容

1. **创世法则落库**：`UniversalEntity` 中现在真实存放 Role=1 元数据（User 类型定义 + Name/Email/Age/CreatedAt 属性定义，共 5 行）与 Role=4 引擎指令（`RouteTarget=UserRepository|TargetTypeId=…`，1 行）。类型行 ParentId 为空、属性行 ParentId 指向类型行——元数据与业务数据完全同构。
2. **MetaCache.LoadAsync 真实化**：不再硬编码，改为 `GetByRolesAsync([1,4])` 一次性拉取 → 三遍解析（类型行 → 属性行 → 指令行）→ 内存字典。**数据池为空时自动幂等播种 6 行创世行**（新库建表后引擎一把就能活）。
3. **Repository 新增 `GetByRolesAsync`**：多角色批量查询，Role 集合经 JSON 数组参数化（OPENJSON 展开），零 SQL 拼接。
4. **BulkInsertAsync 加 `SqlBulkCopyOptions.TableLock`**：批量装载期间取表锁。
5. 引擎指令编码格式：`Key=V1|Key2=V2` 键值对（`ParseEngineCommand` 解析，按第一个 `=` 切分防止值内含等号）。
6. 新增 `DatabaseBootstrapTests` 3 个：空池自举播种、二次加载不重复播种（幂等）、自举元数据支撑完整拆解→Bulk→子图→组装往返。

### 关键决策

- 种子 Guid 沿用原硬编码值（`11111111-…`、`44444444-…`），使 Decomposer/Assembler/MetaCache 对外接口零改动、上游测试零修改——**数据源同化，接口不变**。
- `TypeId` 引入"元类型"指针（`00000000-…-0001` 类型定义 / `-0002` 引擎指令），与既有 Role 维度正交。
- 未采纳 `WITH (NOLOCK)`：脏读会破坏软删过滤语义（可能读到 IsDeleted=1 行），原型单机无并发场景收益极小，语义污染不值得。快照隔离（RCSI）列为旁支。

### BUG 记录

| BUG | 现象 | 根因 | 修复方式 |
|---|---|---|---|
| B6 | 编译错误 CS1503：`new SqlBulkCopy(connection, options)` 失败 | 两参构造只有 `(string connectionString, options)` 形式，SqlConnection 形式必须三参 | 改为 `new SqlBulkCopy(connection, SqlBulkCopyOptions.TableLock, null)` |

### 基准观察（TableLock 前后对比，10K 实体 / 5 万行）

| 阶段 | 第 3 轮（无 TableLock） | 第 4 轮（有 TableLock） |
|---|---|---|
| BulkWrite | 1945 ms | 2399 ms |
| SubgraphFetch | 663 ms | 819 ms |

**诚实记录：TableLock 未带来可测收益**——本机单连接零争用场景下，锁开销本就不存在，且两轮数据处于运行噪声范围内（±20%）。TableLock 在并发写入场景才可能体现价值，保留该选项但不在本轮声称加速。

### 质变确认

- 引擎的"灵魂"（哪些类型、哪些字段、路由到哪）现在**只存在于数据池中**：清空全表 → 引擎重启 → 自动创世。改元数据 = 改几行数据，无需重新编译引擎。
- 完整链路验证通过：空池 → 自举 → 拆解 → Bulk 写入 → 子图预取 → 组装，全部属性一致。

### 未改旁支

DynamicAssembler（动态 C# 类型映射 + Activator 创建）、快照隔离/RCSI、内存 MMU + DirtyGraph、100K/1M 规模曲线、跨实体 Edge、雪花 ID。

### 未验证项

多类型（>1 实体）元数据解析的扩展正确性；指令解析对含 `=`/`|` 值的鲁棒性；热重载（运行中改元数据）未实现。

---

## 2026-10-27 第 3 轮：往返同化（Bulk Load + 子图预取）

### 更新内容

1. `UniversalEntityRepository.BulkInsertAsync`：写入动作同化——全部行在内存拍扁为同构 `DataTable`，`SqlBulkCopy`（流式）单次网络往返整包灌入；`CreatedAt/UpdatedAt` 走表 DEFAULT 约束。
2. `UniversalEntityRepository.GetByTypeIdWithChildrenAsync`：空间同化——一条 SQL 返回"实体行 + 全部属性行"完整图切片，数据库变成"图的序列化器"。
3. `UniversalEntityRepository.GetByParentIdsAsync`：任意父节点集合批量取子行，Id 集合经 JSON 数组参数化（OPENJSON 展开），零 SQL 拼接。
4. 新增集成测试 2 个（BulkInsert+子图往返一致性、批量 ParentId 拉取）；新增同化版基准 `Benchmark_Users_BulkWriteAndSubgraphFetch`。

### BUG 记录

| BUG | 现象 | 根因 | 修复方式 |
|---|---|---|---|
| B4 | 编译错误 CS8417：`await using SqlBulkCopy` 失败 | `SqlBulkCopy` 只实现 `IDisposable`，无 `DisposeAsync` | 改为同步 `using` |
| B5 | 编辑测试文件时误删软删测试结尾（编译会报结构错误） | Edit 替换锚点选择失误 | 读回文件补回被吞的断言行与收尾大括号 |

### 基准数据（10K 实体 = 5 万行，本机 SQL Server 2019，逐行版 vs 同化版）

| 阶段 | 逐行版 | 同化版 | 加速比 | 往返数变化 |
|---|---|---|---|---|
| [1] 拆解 | 25 ms | 15 ms | — | 纯内存 |
| [2] 写入 | 13542 ms | **1945 ms** | **7.0x** | 50000 → **1** |
| [3] 读取 | 297 + 4496 = 4793 ms | **663 ms** | **7.2x** | 10001 → **1** |
| [4] 组装 | 55 ms | 48 ms | — | 纯内存 |
| **总计** | 18415 ms（543 entities/s） | **2671 ms（3743 entities/s）** | **6.9x** | 60001 → **2** |

**结论**：把"往返"同化为"图拓扑整体搬运"后，总吞吐提升约 7 倍（543 → 3743 entities/s）。内存拆解/组装成本不变且极小（合计 <70ms），瓶颈被压缩到两次真正的物理 I/O 上。注意：写入 1945ms 未达到外部预测的"200ms 以内"，该预测未被本机数据支持。

### 未改旁支

内存 MMU 全量预热 + DirtyGraph 异步落盘（策略三）、Role=6 数据流指令、级联软删入口、MetaCache 真实库加载、跨实体 Edge、雪花 ID。

### 未验证项

SqlBulkCopy 在百万行规模的表现；子图预取 SQL 在多类型混合数据下的执行计划；100K/1M 行曲线未跑。

---

## 2026-10-27 第 2 轮：真实库落表 + 集成测试 + 10K 行基准

### 更新内容

1. 创建全新数据库 `GenesisIso`（本机 SQL Server 2019 Enterprise，sa 连接），执行 `src/Emoking.DAL/Sql/UniversalEntity.sql` 建表成功：9 列 + 聚集索引 `(TypeId, Role, IsDeleted)` + 主键 `Id` + `ParentId` 非聚集索引。
2. `EmokingDbContext` 默认连接串改为"环境变量 `GENESIS_SQL_CONN` 优先，回退本地 GenesisIso"，不再指向 LocalDB。
3. `UniversalEntityRepository` 新增 `SoftDeleteAsync`（此前软删只有过滤、没有删除入口）。
4. 新增 `DatabaseRoundTripTests`（3 个真实库集成测试）与 `BenchmarkTests`（万级基准，实体数由环境变量 `GENESIS_BENCH_ROWS` 控制，默认 10000）。
5. DDL 文件 `UniversalEntity.sql` 由 UTF-8 无 BOM 转为 UTF-8 with BOM。

### BUG 记录

| BUG | 现象 | 根因 | 修复方式 |
|---|---|---|---|
| B1 | DDL 执行后表只有 8 列（缺 `TypeId`），两条 CREATE INDEX 报错 1911"列名不存在" | DDL 文件是 UTF-8 无 BOM，Windows sqlcmd 按 ANSI（GBK）解码，中文注释字节错位后把 `[TypeId]` 列定义行吞进行注释 | 文件转 UTF-8 with BOM（sqlcmd 据此走 UTF-8 解码），DROP 表重建，9 列 + 3 索引全部到位 |
| B2 | 软删测试首跑失败：删实体行后 `GetByParentIdAsync` 仍返回 4 条属性行 | 测试设计遗漏：只软删了实体行，属性行自身 IsDeleted=0，被正确读回属引擎正确行为 | 测试改为实体行 + 属性行 5 行全部软删后断言三条查询路径为空；同时暴露"引擎无级联软删入口"，列为旁支 |
| B3 | Benchmark 首版尾实体断言误写成嵌套三元坏表达式 | 书写失误，且违反自身规范"禁止嵌套三元" | 改为直接断言 `(entityCount - 1) % 100 == lastUser.Age` |

### 基准数据（10K 实体 = 5 万行，本机 SQL Server 2019，两次运行）

| 阶段 | 第 1 次 | 第 2 次 | 说明 |
|---|---|---|---|
| [1] 拆解（内存） | 21 ms | 22 ms | 10K 对象 → 50K 行，纯 CPU |
| [2] 写入（分批事务） | 14448 ms | 24749 ms | 3460→2020 行/s，逐行 ExecuteNonQuery 往返是主开销 |
| [3] 实体行读取 | 232 ms | 350 ms | 单条 SQL 拉 10K 行 |
| [4] 指针追逐 | 3623 ms | 6626 ms | 10K 次单行查询（2760→1509 q/s），N+1 模式 |
| [5] 内存组装 | 54 ms | 55 ms | 10K 对象反射赋值，"计算"成本极小 |
| 总计 | 18378 ms | 31802 ms | 544→314 entities/s |

**对"零计算"哲学的初步验证结论**：内存组装确实近乎零成本（10K 对象 55ms）；瓶颈不在计算而在 I/O 往返——写入的逐行往返与读取的 N+1 指针追逐。这与 Advisor"反向挂载本质是计算"的批评部分相符（计算便宜，往返昂贵）。下一步优化方向：SqlBulkCopy/TVP 批量写入、按 ParentId 集合批量读属性行。

### 未改旁支

级联软删入口、MetaCache 真实库加载、跨实体 Edge、雪花 ID、SqlBulkCopy 批量写入优化、加密协议。

### 未验证项

未在 SQL Server 2025 实例（MSSQL17）上对比测试；写入优化（TVP/SqlBulkCopy）收益未实测。

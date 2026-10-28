# Phase 2 最小语义规格（v0.1 冻结版）

> 版本：1.0（第 26 轮）——回答"结构化动作解释器动工前必须回答的五个问题"。
> 范围声明：本规格只定义**第一版**的能力边界，超出边界的诉求在 Phase 3 评审后扩展。

## 1. 组合能力与原语的关系

**CreateType 不是原语，是组合能力。** 它由四个原语组合而成：

| 原语 | 对应内置步骤/动作 | 语义 |
|---|---|---|
| 声明类型 | `InsertTypeMeta`（Role=1 类型行 + 属性行） | 定义"我是谁"与字段集 |
| 绑定行为 | `SetStorageMode` / `BindCSharpType`（Role=4 指令） | 声明存储形态与类绑定 |
| 声明关系 | `ImportSystem.EdgeTypes`（Role=4 EdgeType 指令 + 正反向类型行） | 定义实体间的边 |
| 声明界面 | `ImportSystem.Uis`（Role=8 行） | 定义渲染器表单 |

结构化动作解释器生成的任何"高级意图"，最终都必须编译为这四类原语的池中数据——解释器自身不新增 Role。

## 2. 第一版支持的控制流

| 结构 | 支持 | 语义 |
|---|---|---|
| 顺序 | ✅ | 步骤按 Steps 列表线性执行（现状 Role=7 的 Steps 即顺序结构） |
| 绑定 | ✅ | 参数绑定：动作参数 → 步骤上下文（`K=V` → `StepContext.Args`） |
| 条件 | ✅（仅步骤级守卫） | `When=K=V` 形式的步骤前置条件——上下文参数匹配才执行该步骤；不支持嵌套条件 |
| 动作调用 | ✅ | 步骤可调用其他已注册命令（组合复用）——禁止自调用 |
| 循环 | ❌ | 第一版不支持——批量写入用 BulkInsert 原语，不靠循环 |
| 递归 | ❌ | 永不支持（防无限展开——动作图必须是有向无环的） |

**理由**：循环/递归会让"一段 JSON 的语义"依赖执行时的状态——违背"零计算、数据即程序"的可静态审计原则。批量诉求由原语（BulkInsert）承载，控制流诉求由调用方（LLM/前端）展开为有限步骤。

## 3. 合法与非法示例

**合法（一对）**：

```json
// 例 1：建类型 + 灌数据（顺序 + 动作调用）
{ "actions": [
  { "action": "CreateEntity", "args": { "TypeName": "Book", "Props": "Title:String,Price:Decimal", "Mode": "Snapshot" } },
  { "action": "InsertData",   "args": { "TypeName": "Book", "Data": "{\"Title\":\"三体\",\"Price\":45.0}" } }
] }
```

```json
// 例 2：条件守卫——仅当 Mode=Snapshot 时绑定类名
{ "steps": [
  { "name": "InsertTypeMeta", "when": "Mode=Graph" },
  { "name": "BindCSharpType", "when": "Mode=Snapshot" }
] }
```

**非法（一对）**：

```json
// 非法 1：循环——展开为有限步骤后再提交
{ "actions": [ { "loop": { "over": "Books", "do": "InsertData" } } ] }
// 拒绝原因：循环是 Phase 3 诉求（Error: LOOP_NOT_SUPPORTED）
```

```json
// 非法 2：自调用（递归）
{ "steps": [ { "name": "InsertTypeMeta" }, { "call": "MySelf" } ] }
// 拒绝原因：动作图必须无环（Error: RECURSION_NOT_SUPPORTED）
```

## 4. 验收标准（Phase 2 解释器动工的验收线）

1. 输入"建一个 Book 类型并写入一本书"的**结构化意图 JSON**（非自然语言——自然语言属 Phase 3 的 LLM 层），解释器产出合法的动作序列并执行成功；
2. 执行后 `GetSystemSchema` 含 Book 类型、`GetData` 可读回该书（信封含 Version=1）；
3. 两个非法示例被**前置拒绝**（不产生任何池中写入——Dry-run 语义）；
4. 全量回归 80+ 测试零破坏。

## 5. 边界与未决

- 条件守卫的表达力刻意贫乏（仅等值匹配）——复杂条件是 Phase 3 评审项；
- 错误恢复：解释器不做补偿事务——失败后池中已写入的步骤产物由调用方清理（与 ImportSystem 现状一致）；
- 与 LLM 层的接口：LLM 只负责"自然语言 → 本规格的意图 JSON"，不直接触达引擎。

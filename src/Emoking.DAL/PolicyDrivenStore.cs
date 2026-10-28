// ANCHOR: dal.store.policy-driven - 策略驱动存储：安全规则全部来自池中 Role=5 数据
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Emoking.DAL;

/// <summary>
/// 策略驱动存储——安全行为的"执行器"，**不含任何硬编码策略**。
/// </summary>
/// <remarks>
/// 与第 9 轮 SecuredUniversalStore 的本质区别：
///   - 加密算法、开关、密钥引用、租户隔离、限流阈值全部来自池中 Role=5 安全策略行；
///   - 改一行策略数据 + LoadAsync 热重载 = 换安全策略，零代码零编译；
///   - 密文前缀（AGC1:/PLN1:）是版本标识——切算法后历史数据仍可按前缀路由解密。
/// 本类只做三件事：读策略 → 调执行器 → 委托内层存储。
/// </remarks>
public sealed class PolicyDrivenStore : IUniversalStore
{
    // 内层存储——真正落库的引擎。
    private readonly IUniversalStore _inner;

    // 元数据缓存——Role=5 策略的实时来源（热重载后即刻生效）。
    private readonly MetaCache _metaCache;

    // 租户 Id——参与加密认证绑定。
    private readonly string _tenantId;

    // 密钥环：KeyRef（如 K1）→ 256-bit 密钥。
    private readonly IReadOnlyDictionary<string, byte[]> _keyRing;

    /// <summary>
    /// 环境变量名——部署环境经此注入 Base64 编码的 256-bit 主密钥（映射到 KeyRef=K1）。
    /// </summary>
    public const string KeyEnvVar = "GENESIS_ENC_KEY";

    /// <summary>
    /// 用显式密钥环初始化（测试与密钥管理体系接入用）。
    /// </summary>
    /// <param name="inner">被包装的内层存储。</param>
    /// <param name="metaCache">元数据缓存（须已 LoadAsync）。</param>
    /// <param name="tenantId">本实例服务的租户 Id。</param>
    /// <param name="keyRing">密钥环——KeyRef 到密钥的映射。</param>
    public PolicyDrivenStore(
        IUniversalStore inner,
        MetaCache metaCache,
        string tenantId,
        IReadOnlyDictionary<string, byte[]> keyRing)
    {
        // 内层必填。
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));

        // 元数据缓存必填。
        _metaCache = metaCache ?? throw new ArgumentNullException(nameof(metaCache));

        // 租户必填。
        _tenantId = tenantId ?? throw new ArgumentNullException(nameof(tenantId));

        // 密钥环保存。
        _keyRing = keyRing ?? throw new ArgumentNullException(nameof(keyRing));
    }

    /// <summary>
    /// 用环境变量密钥初始化——生产部署入口（环境变量密钥映射为 KeyRef=K1）。
    /// </summary>
    /// <param name="inner">被包装的内层存储。</param>
    /// <param name="metaCache">元数据缓存。</param>
    /// <param name="tenantId">租户 Id。</param>
    public PolicyDrivenStore(IUniversalStore inner, MetaCache metaCache, string tenantId)
        : this(inner, metaCache, tenantId, new Dictionary<string, byte[]> { ["K1"] = ResolveKeyFromEnvironment() })
    {
        // 委托给显式密钥环构造。
    }

    /// <summary>
    /// 从环境变量解析 Base64 主密钥。
    /// </summary>
    /// <returns>32 字节主密钥。</returns>
    /// <exception cref="InvalidOperationException">环境变量缺失或长度不符时抛出——绝不静默降级。</exception>
    private static byte[] ResolveKeyFromEnvironment()
    {
        // 读环境变量。
        var base64 = Environment.GetEnvironmentVariable(KeyEnvVar);

        // 缺失即失败。
        if (string.IsNullOrWhiteSpace(base64))
        {
            // 抛出配置缺失异常。
            throw new InvalidOperationException(
                $"环境变量 {KeyEnvVar} 未设置——请提供 Base64 编码的 32 字节主密钥。");
        }

        // 解码。
        var key = Convert.FromBase64String(base64);

        // 长度校验。
        if (key.Length != 32)
        {
            // 长度不符。
            throw new InvalidOperationException(
                $"环境变量 {KeyEnvVar} 解码后必须为 32 字节，实际 {key.Length} 字节。");
        }

        // 返回密钥。
        return key;
    }

    /// <summary>
    /// 整包写入——按加密策略逐行处理 Value 后委托内层；行数受 RateLimit 策略约束。
    /// </summary>
    /// <param name="entities">待写入的行集合。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>写入的行数。</returns>
    public async Task<int> BulkInsertAsync(
        IReadOnlyList<UniversalEntity> entities,
        CancellationToken cancellationToken = default)
    {
        // 第 1 步：限流校验。
        EnforceRateLimit(entities.Count);

        // 第 2 步：按加密策略处理全部行。
        var processed = ProcessValues(entities);

        // 委托内层落库。
        return await _inner.BulkInsertAsync(processed, cancellationToken);
    }

    /// <summary>
    /// 条件覆盖写入（CAS upsert）——限流 → **不可见行拒绝覆盖** → 加密 → 委托内层 → 解密返回。
    /// </summary>
    /// <remarks>
    /// 第 20 轮租户不变量补齐（评审洞 2 第 3 点）：与 DeleteAsync 的"不可见不可删"同一条不变量，
    /// 现在也守住了写入入口——对本租户不可见的行（他人数据）拒绝覆盖，
    /// 判断依据：内层存在原始行（GetByIdAsync 非 null）而本层不可见（解密失败/租户不符）。
    /// 历史行由内层从存储中的密文旧行复制生成——天然保持密文形态，无需二次处理。
    /// </remarks>
    /// <param name="entities">待写入的行集合；更新行必须携带与存储一致的 Version。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>落库后的最终行集合（明文形态）。</returns>
    /// <exception cref="InvalidOperationException">限流超限、覆盖不可见行时抛出。</exception>
    public async Task<IReadOnlyList<UniversalEntity>> UpsertAsync(
        IReadOnlyList<UniversalEntity> entities,
        CancellationToken cancellationToken = default)
    {
        // 第 1 步：限流校验。
        EnforceRateLimit(entities.Count);

        // 第 2 步：逐行校验"不可见行拒绝覆盖"。
        foreach (var entity in entities)
        {
            // 内层原始视角——非 null 说明存储中物理存在该行。
            var raw = await _inner.GetByIdAsync(entity.Id, cancellationToken);

            // 物理存在时进一步判断本租户是否可见。
            if (raw is not null)
            {
                // 本层可见性判断——解密失败或租户不符时为 null。
                var visible = await GetByIdAsync(entity.Id, cancellationToken);

                // 不可见即拒绝——知道 Id 也不能覆盖别人的数据。
                if (visible is null)
                {
                    // 抛出拒绝异常。
                    throw new InvalidOperationException(
                        $"行 {entity.Id} 对本租户不可见——拒绝覆盖他人的数据（不可见不可写）。");
                }
            }
        }

        // 第 3 步：按加密策略处理全部行。
        var processed = ProcessValues(entities);

        // 第 4 步：委托内层 CAS upsert。
        var stored = await _inner.UpsertAsync(processed, cancellationToken);

        // 第 5 步：返回行逐行还原为明文。
        var plain = new List<UniversalEntity>(stored.Count);

        // 遍历还原。
        foreach (var row in stored)
        {
            // 还原成功取明文副本；失败保持原样（返回形态一致性由内层保证）。
            plain.Add(TryRestoreValue(row.Value, out var plainValue)
                ? row with { Value = plainValue }
                : row);
        }

        // 返回明文结果集。
        return plain;
    }

    /// <summary>
    /// 批量软删——**全批可见性校验**后委托内层单事务删除。
    /// </summary>
    /// <remarks>
    /// 语义选择：存在任一不可见行则整批拒绝（对齐"不可见不可删"的严格语义），
    /// 不做"删可见部分"的尽力而为——避免部分删除造成的半删状态。
    /// </remarks>
    /// <param name="ids">待软删的行 Id 集合。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>实际软删的行数。</returns>
    /// <exception cref="InvalidOperationException">任一行不存在或对本租户不可见时抛出。</exception>
    public async Task<int> DeleteManyAsync(
        IReadOnlyList<Guid> ids,
        CancellationToken cancellationToken = default)
    {
        // 逐行校验可见性——任一不可见即整批拒绝。
        foreach (var id in ids)
        {
            // 本层点查。
            if (await GetByIdAsync(id, cancellationToken) is null)
            {
                // 抛出拒绝异常。
                throw new InvalidOperationException(
                    $"行 {id} 不存在或对本租户不可见——批量删除被拒绝（不可见不可删）。");
            }
        }

        // 全部可见才委托内层单事务删除。
        return await _inner.DeleteManyAsync(ids, cancellationToken);
    }

    /// <summary>
    /// 实体审计历史链——委托内层取历史行后逐行解密（历史行在加密策略下是密文）。
    /// </summary>
    /// <param name="entityId">被审计的实体行 Id。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>本租户可见的明文历史行集合。</returns>
    public async Task<IReadOnlyList<UniversalEntity>> GetHistoryAsync(
        Guid entityId,
        CancellationToken cancellationToken = default)
    {
        // 从内层取原始历史行（密文形态）。
        var rows = await _inner.GetHistoryAsync(entityId, cancellationToken);

        // 逐行还原。
        var visible = new List<UniversalEntity>(rows.Count);

        // 遍历。
        foreach (var row in rows)
        {
            // 尝试还原——成功才可见（与子图预取同语义）。
            if (TryRestoreValue(row.Value, out var plain))
            {
                // 加入可见集。
                visible.Add(row with { Value = plain });
            }
        }

        // 返回明文历史链。
        return visible;
    }

    /// <summary>
    /// 限流校验——行数超过 Role=5 RateLimit 策略上限时拒绝。
    /// </summary>
    /// <param name="rowCount">本次写入的行数。</param>
    /// <exception cref="InvalidOperationException">超过策略上限时抛出。</exception>
    private void EnforceRateLimit(int rowCount)
    {
        // 读限流策略——策略缺省时无上限（策略是数据，不是代码常量）。
        var rateLimit = _metaCache.GetSecurityPolicy("RateLimit");

        // 策略存在时校验 Max。
        if (rateLimit is not null
            && rateLimit.TryGetValue("Max", out var maxText)
            && int.TryParse(maxText, out var max)
            && rowCount > max)
        {
            // 超限拒绝。
            throw new InvalidOperationException(
                $"单次批量写入 {rowCount} 行超过 RateLimit 策略上限 {max} 行，已拒绝——请分批写入。");
        }
    }

    /// <summary>
    /// 按加密策略处理行集合——启用时逐行加密，关闭时明文带 PLN1 标记。
    /// </summary>
    /// <param name="entities">输入行集合。</param>
    /// <returns>处理后的行集合（Value 已按策略处理）。</returns>
    private List<UniversalEntity> ProcessValues(IReadOnlyList<UniversalEntity> entities)
    {
        // 读加密策略。
        var encryptPolicy = _metaCache.GetSecurityPolicy("EncryptValue");

        // 处理后的行集合。
        var processed = new List<UniversalEntity>(entities.Count);

        // 遍历处理。
        foreach (var row in entities)
        {
            // 加密策略启用时走对应执行器。
            if (encryptPolicy is not null
                && encryptPolicy.TryGetValue("Enabled", out var enabledText)
                && bool.TryParse(enabledText, out var enabled)
                && enabled)
            {
                // 解析算法执行器与密钥。
                var cipher = ResolveCipher(encryptPolicy);

                // 加密 Value。
                processed.Add(row with { Value = cipher.Encrypt(row.Value, ResolveKey(encryptPolicy), _tenantId) });
            }
            else
            {
                // 加密策略关闭——明文直存但带 PLN1 标记（与外来裸数据区分）。
                processed.Add(row with { Value = "PLN1:" + (row.Value ?? string.Empty) });
            }
        }

        // 返回处理结果。
        return processed;
    }

    /// <summary>
    /// 子图预取——按密文前缀路由历史算法解密；解密失败或越权的行剔除（不可见）。
    /// </summary>
    /// <param name="entityTypeId">实体类型的 Meta TypeId。</param>
    /// <param name="role">实体行角色常量。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>本租户可见的明文子图。</returns>
    public async Task<IReadOnlyList<UniversalEntity>> GetSubGraphAsync(
        Guid entityTypeId,
        byte role,
        CancellationToken cancellationToken = default)
    {
        // 从内层取原始子图。
        var rows = await _inner.GetSubGraphAsync(entityTypeId, role, cancellationToken);

        // 逐行还原。
        var visible = new List<UniversalEntity>(rows.Count);

        // 遍历。
        foreach (var row in rows)
        {
            // 尝试还原——成功才可见。
            if (TryRestoreValue(row.Value, out var plain))
            {
                // 加入可见集。
                visible.Add(row with { Value = plain });
            }
        }

        // 返回可见子图。
        return visible;
    }

    /// <summary>
    /// 按主键点查——还原失败或越权时返回 null（不可见）。
    /// </summary>
    /// <param name="id">行 Id。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>本租户可见的明文行；否则 null。</returns>
    public async Task<UniversalEntity?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        // 从内层取原始行。
        var row = await _inner.GetByIdAsync(id, cancellationToken);

        // 行不存在透传。
        if (row is null)
        {
            // 不可见。
            return null;
        }

        // 还原失败即不可见。
        if (!TryRestoreValue(row.Value, out var plain))
        {
            // 不可见。
            return null;
        }

        // 返回明文副本。
        return row with { Value = plain };
    }

    /// <summary>
    /// 指针追逐——从内层取回后逐行解密，不可见行剔除。
    /// </summary>
    /// <param name="parentId">父节点 Id。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>本租户可见的明文子行集合。</returns>
    public async Task<IReadOnlyList<UniversalEntity>> GetByParentIdAsync(
        Guid parentId,
        CancellationToken cancellationToken = default)
    {
        // 从内层取原始子行。
        var rows = await _inner.GetByParentIdAsync(parentId, cancellationToken);

        // 逐行还原。
        var visible = new List<UniversalEntity>(rows.Count);

        // 遍历。
        foreach (var row in rows)
        {
            // 尝试还原——成功才可见。
            if (TryRestoreValue(row.Value, out var plain))
            {
                // 加入可见集。
                visible.Add(row with { Value = plain });
            }
        }

        // 返回可见子行。
        return visible;
    }

    /// <summary>
    /// 按 Id 软删——**先做租户可见性校验**：本租户不可见的行不可删（洞 3 修复：知道 Id 也不再能删别人的行）。
    /// </summary>
    /// <param name="id">行 Id。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>本租户可见且删除成功返回 true；否则 false。</returns>
    public async Task<bool> DeleteAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        // 先点查——解密失败或租户不符时返回 null，此时拒绝删除。
        var row = await GetByIdAsync(id, cancellationToken);

        // 不可见即不可删。
        if (row is null)
        {
            // 拒绝。
            return false;
        }

        // 委托内层软删。
        return await _inner.DeleteAsync(id, cancellationToken);
    }

    /// <summary>
    /// 还原 Value——按前缀路由历史执行器；无前缀的外来裸数据一律不可见。
    /// </summary>
    /// <param name="stored">库中的 Value。</param>
    /// <param name="plain">还原出的明文。</param>
    /// <returns>还原成功返回 true。</returns>
    private bool TryRestoreValue(string? stored, out string? plain)
    {
        // 预置输出。
        plain = null;

        // null 直通——防御性处理（正常写入路径不产生 null）。
        if (stored is null)
        {
            // 直通。
            return true;
        }

        // 按前缀路由执行器。
        var cipher = ValueCiphers.ResolveByPrefix(stored);

        // 无前缀 = 外来裸数据——任何策略下都不可见。
        if (cipher is null)
        {
            // 不可见。
            return false;
        }

        // 解析该行密文使用的算法名（前缀 → Algo 名）。
        var algoName = ValueCiphersResolveAlgoName(cipher);

        // 按算法名取策略里的 KeyRef——历史算法若已从策略移除，回退用当前 KeyRef。
        var encryptPolicy = _metaCache.GetSecurityPolicy("EncryptValue");
        var keyRef = encryptPolicy is not null && encryptPolicy.TryGetValue("KeyRef", out var currentRef)
            ? currentRef
            : "K1";

        // 去前缀后委托执行器解密。
        var payload = stored[cipher.Prefix.Length..];

        // 委托解密。
        return cipher.TryDecrypt(payload, ResolveKey(new Dictionary<string, string> { ["KeyRef"] = keyRef }), _tenantId, out plain);
    }

    /// <summary>
    /// 从执行器前缀反推 Algo 名——前缀与 Algo 名在注册表中一一对应。
    /// </summary>
    /// <param name="cipher">执行器实例。</param>
    /// <returns>算法名。</returns>
    private static string ValueCiphersResolveAlgoName(IValueCipher cipher)
    {
        // 类型匹配——原型期只有两种执行器。
        return cipher switch
        {
            // AES-GCM。
            AesGcmValueCipher => "AesGcm",

            // 明文直存。
            PlainValueCipher => "Plain",

            // 未知执行器。
            _ => throw new InvalidOperationException($"未知的加密执行器类型：{cipher.GetType().Name}。")
        };
    }

    /// <summary>
    /// 按加密策略解析执行器实例。
    /// </summary>
    /// <param name="encryptPolicy">加密策略键值对。</param>
    /// <returns>执行器实例。</returns>
    private IValueCipher ResolveCipher(IReadOnlyDictionary<string, string> encryptPolicy)
    {
        // 取 Algo 键。
        var algoName = encryptPolicy.TryGetValue("Algo", out var algo) ? algo : "AesGcm";

        // 查注册表。
        return ValueCiphers.ResolveByAlgo(algoName);
    }

    /// <summary>
    /// 按策略的 KeyRef 从密钥环取密钥。
    /// </summary>
    /// <param name="policy">含 KeyRef 的策略键值对。</param>
    /// <returns>密钥字节。</returns>
    /// <exception cref="InvalidOperationException">KeyRef 在密钥环中不存在时抛出。</exception>
    private byte[] ResolveKey(IReadOnlyDictionary<string, string> policy)
    {
        // 取 KeyRef。
        var keyRef = policy.TryGetValue("KeyRef", out var keyRefText) ? keyRefText : "K1";

        // 查密钥环。
        if (_keyRing.TryGetValue(keyRef, out var key))
        {
            // 命中。
            return key;
        }

        // 密钥环无此引用。
        throw new InvalidOperationException(
            $"加密策略的 KeyRef='{keyRef}' 在密钥环中不存在。可用引用：{string.Join("/", _keyRing.Keys)}。");
    }
}

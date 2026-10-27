// ANCHOR: test.z-ciphertext-demo - 密文留痕演示：落库后不清理，供 sqlcmd 人工验密
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Emoking.DAL.Tests;

/// <summary>
/// 密文留痕演示——类名以 Z 开头保证在 TestRunner 排序中最后执行，
/// 落库的 AGC1 密文行**刻意不清理**，供测试结束后用 sqlcmd 亲眼验证。
/// </summary>
/// <remarks>
/// 验证命令：
///   SELECT LEFT(Value, 44) FROM dbo.UniversalEntity WHERE Value LIKE 'AGC1:%'
/// 预期：25 行密文，明文标记物（姓名/邮箱）在库中 0 泄露。
/// </remarks>
public sealed class ZCiphertextDemoTests
{
    // User 实体类型的 Meta TypeId——与创世行一致。
    private static readonly Guid UserTypeId = new("11111111-1111-1111-1111-111111111111");

    /// <summary>
    /// 留痕演示：策略驱动存储落库 25 行密文后不清理——sqlcmd 视角全是 AGC1 乱码。
    /// </summary>
    [Fact]
    public async Task Leave_Ciphertext_In_Database_ForManualInspection()
    {
        // 第 1 步：清池 + 自举播种。
        using (var connection = new EmokingDbContext().CreateConnection())
        {
            // 清空命令。
            await using var command = connection.CreateCommand();
            command.CommandText = "TRUNCATE TABLE [dbo].[UniversalEntity]";
            await command.ExecuteNonQueryAsync();
        }

        // 自举。
        var cache = new MetaCache(new UniversalEntityRepository(new EmokingDbContext()));
        await cache.LoadAsync();

        // 第 2 步：策略驱动存储包装磁盘表（加密策略来自池中 Role=5：AesGcm）。
        var inner = new SqlServerUniversalStore(new UniversalEntityRepository(new EmokingDbContext()));
        var secured = new PolicyDrivenStore(inner, cache, "tenant-demo", new Dictionary<string, byte[]>
        {
            // K1——与创世加密策略行 KeyRef 对应。
            ["K1"] = Convert.FromBase64String("qX7wL0pZs2vB8nR4tE6yU1iO9aS3dF5gH7jK2lM4nQ8=")
        });

        // 第 3 步：拆解 5 个 User 并加密落库。
        var decomposer = new NodeEdgeDecomposer(cache);
        var allRows = new List<UniversalEntity>();
        for (var index = 0; index < 5; index++)
        {
            // 拆解单个 User。
            allRows.AddRange(decomposer.Decompose(new User
            {
                // Id 由引擎生成。
                Id = Guid.Empty,
                // 中文姓名——密文中不可见。
                Name = $"密文张三_{index}",
                // 邮箱——密文中不可见。
                Email = $"demo{index}@genesis.local",
                // 年龄。
                Age = 20 + index,
                // 时间。
                CreatedAt = new DateTime(2026, 10, 27, 13, 0, 0, DateTimeKind.Utc).AddMinutes(index)
            }, UserTypeId));
        }

        // 经铁布衫整包落库。
        var inserted = await secured.BulkInsertAsync(allRows);

        // 25 行写入。
        Assert.Equal(25, inserted);

        // 第 4 步：确认密文形态（原始仓储视角）。
        var rawRepository = new UniversalEntityRepository(new EmokingDbContext());

        // 逐行校验密文前缀。
        foreach (var row in allRows.Where(row => row.Value is not null))
        {
            // 原始读取。
            var raw = await rawRepository.GetByIdAsync(row.Id);

            // 命中。
            Assert.NotNull(raw);

            // 必须是 AGC1 密文。
            Assert.StartsWith("AGC1:", raw!.Value);

            // 密文中不允许出现明文标记物。
            Assert.DoesNotContain("密文张三", raw.Value!);
        }

        // 第 5 步：留痕提示——不清理，交给 sqlcmd 人工验证。
        Console.WriteLine();
        Console.WriteLine(">>> 密文已留痕：25 行 AGC1 密文留在 GenesisIso.dbo.UniversalEntity，可用 sqlcmd 亲自查看。");
    }
}

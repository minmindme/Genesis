// ANCHOR: test.runner - 手动测试 runner，绕过 TestHost 权限限制
using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Xunit;

namespace Emoking.DAL.Tests;

/// <summary>
/// 手动测试执行器——反射调用所有带 [Fact] attribute 的 public 实例方法。
/// 用于 Trae 沙箱环境（TestHost 的 SetParentProcessExitCallback 会因 Win32Exception(5) 崩溃）。
/// </summary>
public static class TestRunner
{
    /// <summary>
    /// 入口点：扫描当前程序集所有 [Fact] 方法，逐个执行。
    /// </summary>
    /// <param name="args">可选：类名过滤子串（如 "SecurityStoreTests" 只跑匹配的测试类）。</param>
    public static async Task<int> Main(string[] args)
    {
        // 读取可选的类名过滤子串。
        var classNameFilter = args.Length > 0 ? args[0] : null;

        // 找到所有测试类——至少包含一个 [Fact] 方法；带过滤参数时只保留类名匹配的。
        // 按类名排序确定执行顺序——各测试类共享数据库状态，顺序必须可预测。
        var testTypes = Assembly.GetExecutingAssembly()
            .GetTypes()
            .Where(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Any(m => m.GetCustomAttribute<FactAttribute>() != null))
            .Where(t => classNameFilter is null
                || t.Name.Contains(classNameFilter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToList();

        // 统计。
        var total = 0;
        var passed = 0;
        var failed = 0;
        var skipped = 0;

        foreach (var type in testTypes)
        {
            Console.WriteLine($"\n=== {type.Name} ===");

            // 拿到所有 [Fact] 方法。
            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.GetCustomAttribute<FactAttribute>() != null)
                .ToList();

            // 实例化测试类。
            var instance = Activator.CreateInstance(type);

            foreach (var method in methods)
            {
                // 读取 Fact 特性——Skip 文案约定 "BENCH:" 前缀的测试为环境门禁基准。
                var fact = method.GetCustomAttribute<FactAttribute>();

                // 基准门禁：Skip 文案以 BENCH: 开头且未设 GENESIS_BENCH=1 时跳过——
                // 大数据量压测环境敏感（机器负载会拖红回归），默认移出回归（第 19 轮）。
                if (fact is not null
                    && fact.Skip is not null
                    && fact.Skip.StartsWith("BENCH:", StringComparison.Ordinal)
                    && !string.Equals(Environment.GetEnvironmentVariable("GENESIS_BENCH"), "1", StringComparison.Ordinal))
                {
                    // 计入跳过并说明原因。
                    skipped++;
                    Console.WriteLine($"  {method.Name} ... ↷ SKIP - {fact.Skip["BENCH:".Length..]}");
                    continue;
                }

                // 普通静态 Skip（非 BENCH 门禁）同样跳过。
                if (fact?.Skip is not null)
                {
                    // 计入跳过并说明原因。
                    skipped++;
                    Console.WriteLine($"  {method.Name} ... ↷ SKIP - {fact.Skip}");
                    continue;
                }

                total++;
                Console.Write($"  {method.Name} ... ");

                try
                {
                    // 反射执行方法——async 方法返回 Task，需要 await。
                    var result = method.Invoke(instance, null);

                    // 异步方法 await；同步方法直接忽略返回值。
                    if (result is Task task)
                    {
                        await task;
                    }

                    // 没抛异常 = 通过。
                    Console.WriteLine("✓ PASS");
                    passed++;
                }
                catch (Exception ex)
                {
                    // 反射会包一层 TargetInvocationException——拿 InnerException 看真实错误。
                    var actual = ex is TargetInvocationException tie ? tie.InnerException ?? ex : ex;

                    // 输出完整异常（含堆栈）——便于定位失败根因。
                    Console.WriteLine($"✗ FAIL - {actual}");
                    failed++;
                }
            }
        }

        // 汇总。
        Console.WriteLine($"\n=== SUMMARY ===");
        Console.WriteLine($"Total: {total}, Passed: {passed}, Failed: {failed}, Skipped: {skipped}");

        // 返回 exit code：全部通过 = 0，有失败 = 1。
        return failed == 0 ? 0 : 1;
    }
}

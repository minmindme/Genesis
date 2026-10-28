// ANCHOR: dal.metadata.metadata-json - 元数据 DSL 双格式编解码：旧 "K=V|K2=V2" 与新 "JSON:{...}" 前缀版本化
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Emoking.DAL;

/// <summary>
/// 元数据 DSL 编解码器——键值对与属性规格的双格式路由（第 22 轮，B25/B17 类分隔符裂缝的根治）。
/// </summary>
/// <remarks>
/// 复用项目自己的"前缀版本化"哲学（与 AGC1:/PLN1:/SNAP1: 同构）：
///   - 旧格式（无前缀）：`Key=V1|Key2=V2` / `Name:Type`——存量数据零破坏继续解析；
///   - 新格式（JSON: 前缀）：值/键内含 <c>|</c> <c>=</c> <c>:</c> <c>;</c> <c>,</c> 等分隔符时，
///     整体以 JSON 承载——解析器按前缀路由，永不半解析。
/// JSON 解析约定：数组值 join 为 ";" 分隔串（Role=7 Steps 兼容既有执行器 Split(';') 消费方，零改动）。
/// </remarks>
public static class MetadataJson
{
    /// <summary>
    /// JSON 格式前缀——与密文前缀（AGC1:/PLN1:）、快照前缀（SNAP1:）同一套版本化机制。
    /// </summary>
    public const string Prefix = "JSON:";

    /// <summary>
    /// 键值对解码——按前缀路由：JSON: 走 JSON 解析，否则走旧竖线格式解析。
    /// </summary>
    /// <param name="text">指令文本；可为 null。</param>
    /// <returns>键值对字典；空文本返回空字典。</returns>
    public static Dictionary<string, string> DecodePairs(string? text)
    {
        // 结果字典。
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        // 空文本直接返回空字典。
        if (string.IsNullOrWhiteSpace(text))
        {
            // 返回空字典。
            return result;
        }

        // JSON 格式分支。
        if (text.StartsWith(Prefix, StringComparison.Ordinal))
        {
            // 去前缀。
            var json = text[Prefix.Length..];

            // 反序列化为 JsonElement 字典——值可能是标量或数组。
            var parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);

            // 非法 JSON 显式失败（fail fast，不静默半解析）。
            if (parsed is null)
            {
                // 抛出格式异常。
                throw new InvalidOperationException($"元数据 JSON 格式非法：{json}");
            }

            // 逐键归一——数组值 join 为 ";" 分隔串（Steps 等消费方按既有 Split(';') 消费），标量直接取文本。
            foreach (var pair in parsed)
            {
                // 数组值——逐元素取文本后用分号连接。
                if (pair.Value.ValueKind == JsonValueKind.Array)
                {
                    // 收集数组元素文本。
                    var items = pair.Value.EnumerateArray()
                        .Select(element => element.ToString())
                        .ToList();

                    // 分号连接入字典——与旧 Steps 格式消费方兼容。
                    result[pair.Key] = string.Join(";", items);

                    // 下一键。
                    continue;
                }

                // 标量值——直接取文本。
                result[pair.Key] = pair.Value.ToString();
            }

            // 返回解析结果。
            return result;
        }

        // ===== 旧格式分支：Key1=V1|Key2=V2 =====

        // 按竖线切分键值段。
        var segments = text.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // 逐段解析。
        foreach (var segment in segments)
        {
            // 每段按第一个等号切成键和值——限制 2 段防止值里含等号被误切。
            var parts = segment.Split('=', 2);

            // 缺等号的段格式非法，跳过。
            if (parts.Length != 2)
            {
                // 跳过非法段。
                continue;
            }

            // 键去空白后入字典。
            result[parts[0].Trim()] = parts[1].Trim();
        }

        // 返回解析结果。
        return result;
    }

    /// <summary>
    /// 键值对编码——任一键/值含分隔符（竖线或等号）时整体 JSON 化，否则保持旧格式（保池中可读性）。
    /// </summary>
    /// <param name="pairs">键值对集合。</param>
    /// <returns>可直接落库的 Value 文本。</returns>
    public static string EncodePairs(IReadOnlyDictionary<string, string> pairs)
    {
        // 任一键或值含竖线/等号——旧格式无法无损承载，走 JSON。
        var needsJson = pairs.Any(pair =>
            pair.Key.Contains('|', StringComparison.Ordinal)
            || pair.Key.Contains('=', StringComparison.Ordinal)
            || pair.Value.Contains('|', StringComparison.Ordinal)
            || pair.Value.Contains('=', StringComparison.Ordinal));

        // JSON 分支——序列化整个字典。
        if (needsJson)
        {
            // JSON 化（值原样保留分隔符）。
            return Prefix + JsonSerializer.Serialize(pairs);
        }

        // 旧格式——竖线连接 K=V 段。
        return string.Join("|", pairs.Select(pair => $"{pair.Key}={pair.Value}"));
    }

    /// <summary>
    /// 属性规格解码——按前缀路由：JSON: 走 JSON 对象，否则按第一个冒号切 "Name:Type"。
    /// </summary>
    /// <param name="text">属性规格文本（Role=1 属性行 Value）。</param>
    /// <returns>属性名与数据类型（未声明类型时 Type 为 null）。</returns>
    public static (string Name, string? Type) DecodeProperty(string text)
    {
        // JSON 格式分支——{"Name":"..","Type":".."} 或 {"name":..,"type":..}。
        if (text.StartsWith(Prefix, StringComparison.Ordinal))
        {
            // 去前缀。
            var json = text[Prefix.Length..];

            // 反序列化。
            var parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);

            // 非法 JSON 显式失败。
            if (parsed is null)
            {
                // 抛出格式异常。
                throw new InvalidOperationException($"属性规格 JSON 格式非法：{json}");
            }

            // 取属性名——兼容 Name/name 两种键。
            var name = parsed.TryGetValue("Name", out var nameElement) ? nameElement.ToString()
                : parsed.TryGetValue("name", out var nameLower) ? nameLower.ToString()
                : string.Empty;

            // 取类型——兼容 Type/type；未声明为 null（推断回退）。
            var type = parsed.TryGetValue("Type", out var typeElement) ? typeElement.ToString()
                : parsed.TryGetValue("type", out var typeLower) ? typeLower.ToString()
                : null;

            // 返回解析结果。
            return (name, type);
        }

        // 旧格式分支——按第一个冒号切（第 17 轮格式），无冒号 = 未声明类型。
        var colonIndex = text.IndexOf(':', StringComparison.Ordinal);

        // 无冒号——只有属性名。
        if (colonIndex < 0)
        {
            // 返回属性名，类型未声明。
            return (text, null);
        }

        // 有冒号——属性名在前、类型在后。
        return (text[..colonIndex], text[(colonIndex + 1)..]);
    }

    /// <summary>
    /// 属性规格编码——属性名或类型含冒号时 JSON 化，否则保持 "Name:Type" 旧格式。
    /// </summary>
    /// <param name="name">属性名。</param>
    /// <param name="type">数据类型；null 表示未声明（落库为纯属性名）。</param>
    /// <returns>可直接落库的属性行 Value。</returns>
    public static string EncodeProperty(string name, string? type)
    {
        // 未声明类型——纯属性名（旧格式语义）。
        if (type is null)
        {
            // 含冒号的属性名旧格式无法承载——JSON 化。
            if (name.Contains(':', StringComparison.Ordinal))
            {
                // JSON 化。
                return Prefix + JsonSerializer.Serialize(new Dictionary<string, string>
                {
                    // 属性名。
                    ["Name"] = name
                });
            }

            // 纯属性名。
            return name;
        }

        // 名或类型含冒号——旧格式会半解析，JSON 化。
        if (name.Contains(':', StringComparison.Ordinal) || type.Contains(':', StringComparison.Ordinal))
        {
            // JSON 化。
            return Prefix + JsonSerializer.Serialize(new Dictionary<string, string>
            {
                // 属性名。
                ["Name"] = name,
                // 数据类型。
                ["Type"] = type
            });
        }

        // 旧格式。
        return $"{name}:{type}";
    }
}

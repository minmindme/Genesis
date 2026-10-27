// ANCHOR: dal.store.value-ciphers - 加密执行器注册表：算法即插件，前缀即版本
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace Emoking.DAL;

/// <summary>
/// 值加密执行器契约——策略数据（Role=5 的 Algo 键）按名调用，算法实现是可插拔插件。
/// </summary>
/// <remarks>
/// 每个执行器拥有唯一的密文前缀（如 AGC1:），前缀同时承担"版本标识"：
/// 读取时按前缀路由回创建它的执行器——换算法后历史数据依然可解。
/// 新增算法（如 SM4）= 实现本接口 + 注册进 <see cref="ValueCiphers"/>，业务代码零改动。
/// </remarks>
public interface IValueCipher
{
    /// <summary>
    /// 该执行器的密文前缀（含冒号），如 "AGC1:"。
    /// </summary>
    string Prefix { get; }

    /// <summary>
    /// 加密明文。
    /// </summary>
    /// <param name="plain">明文（可为 null——加密为空载荷以绑定租户认证）。</param>
    /// <param name="key">密钥。</param>
    /// <param name="associatedData">关联数据（租户 Id）。</param>
    /// <returns>带前缀的密文字符串。</returns>
    string Encrypt(string? plain, byte[] key, string associatedData);

    /// <summary>
    /// 解密密文。
    /// </summary>
    /// <param name="stored">去掉前缀后的载荷。</param>
    /// <param name="key">密钥。</param>
    /// <param name="associatedData">关联数据（租户 Id）。</param>
    /// <param name="plain">解密出的明文。</param>
    /// <returns>解密成功返回 true。</returns>
    bool TryDecrypt(string stored, byte[] key, string associatedData, out string? plain);
}

/// <summary>
/// AES-GCM 执行器——认证加密，前缀 AGC1:。
/// </summary>
/// <remarks>
/// 载荷布局（Base64 前）：nonce(12) + tag(16) + cipher(n)。
/// null 明文加密为空载荷（nonce+tag）——保证无值行也携带租户认证绑定。
/// </remarks>
public sealed class AesGcmValueCipher : IValueCipher
{
    // 前缀。
    public string Prefix => "AGC1:";

    // 认证标签长度。
    private const int TagSizeBytes = 16;

    // nonce 长度。
    private const int NonceSizeBytes = 12;

    /// <summary>
    /// AES-GCM 加密。
    /// </summary>
    public string Encrypt(string? plain, byte[] key, string associatedData)
    {
        // null 视为空明文——依旧加密以绑定租户认证。
        var plainBytes = plain is null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(plain);

        // 随机 nonce。
        var nonce = RandomNumberGenerator.GetBytes(NonceSizeBytes);

        // 密文与标签缓冲。
        var cipherBytes = new byte[plainBytes.Length];
        var tagBytes = new byte[TagSizeBytes];

        // 认证加密——租户 Id 作为 Associated Data。
        using var aes = new AesGcm(key, TagSizeBytes);
        aes.Encrypt(nonce, plainBytes, cipherBytes, tagBytes, Encoding.UTF8.GetBytes(associatedData));

        // 组装载荷：nonce + tag + cipher。
        var package = new byte[NonceSizeBytes + TagSizeBytes + cipherBytes.Length];
        nonce.CopyTo(package, 0);
        tagBytes.CopyTo(package, NonceSizeBytes);
        cipherBytes.CopyTo(package, NonceSizeBytes + TagSizeBytes);

        // 返回带前缀密文。
        return Prefix + Convert.ToBase64String(package);
    }

    /// <summary>
    /// AES-GCM 解密——AAD 或密文被篡改时认证失败。
    /// </summary>
    public bool TryDecrypt(string stored, byte[] key, string associatedData, out string? plain)
    {
        // 预置输出。
        plain = null;

        // 解码载荷。
        byte[] package;

        // Base64 非法按失败处理。
        try
        {
            // 解码。
            package = Convert.FromBase64String(stored);
        }
        catch (FormatException)
        {
            // 非法载荷。
            return false;
        }

        // 包长校验。
        if (package.Length < NonceSizeBytes + TagSizeBytes)
        {
            // 载荷过短。
            return false;
        }

        // 拆包。
        var nonce = package.AsSpan(0, NonceSizeBytes);
        var tagBytes = package.AsSpan(NonceSizeBytes, TagSizeBytes);
        var cipherBytes = package[(NonceSizeBytes + TagSizeBytes)..];

        // 明文缓冲。
        var plainBytes = new byte[cipherBytes.Length];

        // 认证解密。
        try
        {
            // 解密。
            using var aes = new AesGcm(key, TagSizeBytes);
            aes.Decrypt(nonce, cipherBytes, tagBytes, plainBytes, Encoding.UTF8.GetBytes(associatedData));
        }
        catch (CryptographicException)
        {
            // 认证失败——密钥/租户/密文任一不符。
            return false;
        }

        // 空载荷还原为 null。
        plain = plainBytes.Length == 0 ? null : Encoding.UTF8.GetString(plainBytes);

        // 成功。
        return true;
    }
}

/// <summary>
/// 明文直存执行器——策略关闭加密时的落位形态，前缀 PLN1:。
/// </summary>
/// <remarks>
/// PLN1: 前缀保证明文行仍是"本系统写入的行"——与外来裸数据（无前缀）严格区分，
/// 外来裸数据在任何策略下都不可见。
/// </remarks>
public sealed class PlainValueCipher : IValueCipher
{
    // 前缀。
    public string Prefix => "PLN1:";

    /// <summary>
    /// 明文直存——null 存为空前缀（保持行可见性标记）。
    /// </summary>
    public string Encrypt(string? plain, byte[] key, string associatedData)
    {
        // 前缀 + 原文。
        return Prefix + (plain ?? string.Empty);
    }

    /// <summary>
    /// 去前缀返回原文。
    /// </summary>
    public bool TryDecrypt(string stored, byte[] key, string associatedData, out string? plain)
    {
        // 去前缀即明文；空前缀还原为 null。
        plain = stored.Length == 0 ? null : stored;

        // 恒成功。
        return true;
    }
}

/// <summary>
/// 执行器注册表——策略数据（Algo 键）到算法插件的映射，以及密文前缀到执行器的反向路由。
/// </summary>
public static class ValueCiphers
{
    // 按 Algo 名索引的执行器表——新算法在此注册。
    private static readonly Dictionary<string, IValueCipher> ByAlgo = new(StringComparer.Ordinal)
    {
        // AES-GCM 认证加密。
        ["AesGcm"] = new AesGcmValueCipher(),

        // 明文直存（策略关闭加密时）。
        ["Plain"] = new PlainValueCipher()
    };

    /// <summary>
    /// 按 Algo 名解析执行器。
    /// </summary>
    /// <param name="algoName">策略里的算法名。</param>
    /// <returns>执行器实例。</returns>
    /// <exception cref="InvalidOperationException">算法未注册时抛出。</exception>
    public static IValueCipher ResolveByAlgo(string algoName)
    {
        // 查表。
        if (ByAlgo.TryGetValue(algoName, out var cipher))
        {
            // 命中。
            return cipher;
        }

        // 未注册的算法名显式失败。
        throw new InvalidOperationException(
            $"安全策略指定的加密算法 '{algoName}' 未注册。已注册：{string.Join("/", ByAlgo.Keys)}。");
    }

    /// <summary>
    /// 按密文前缀反向路由执行器——历史数据解密的版本桥梁。
    /// </summary>
    /// <param name="stored">库中的 Value。</param>
    /// <returns>前缀匹配的执行器；无前缀（外来裸数据）返回 null。</returns>
    public static IValueCipher? ResolveByPrefix(string? stored)
    {
        // 空/null 直接无路由。
        if (string.IsNullOrEmpty(stored))
        {
            // 无路由。
            return null;
        }

        // 遍历注册表按前缀匹配。
        foreach (var cipher in ByAlgo.Values)
        {
            // 前缀命中。
            if (stored.StartsWith(cipher.Prefix, StringComparison.Ordinal))
            {
                // 返回执行器。
                return cipher;
            }
        }

        // 无前缀——外来裸数据。
        return null;
    }
}

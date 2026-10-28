// ANCHOR: dal.exception.contracts - 动作失败语义分类：网关按类型路由（reload / 409 / 400）
using System;

namespace Emoking.DAL;

/// <summary>
/// 元数据未注册异常——部署/配置问题：池中刚插入的命令/类型/边类型尚未进入缓存。
/// </summary>
/// <remarks>
/// 第 25 轮（评审 P0）：网关自愈收窄——**只有本异常触发 LoadAsync 热重载重试**；
/// 业务冲突等其他 InvalidOperationException 不再被误当作部署问题（曾导致 CAS 冲突被
/// reload+重试，非幂等动作重复写入）。
/// </remarks>
public sealed class MetadataNotRegisteredException : InvalidOperationException
{
    /// <summary>
    /// 用错误消息初始化。
    /// </summary>
    /// <param name="message">错误描述。</param>
    public MetadataNotRegisteredException(string message)
        : base(message)
    {
        // 消息直传基类。
    }
}

/// <summary>
/// 并发冲突异常——乐观锁校验失败：客户端携带的 ExpectedVersion 与存储不符。
/// </summary>
/// <remarks>
/// 第 25 轮（评审 P0）：正常业务语义（两读者一胜一败）——网关映射为 409 Conflict，
/// 不触发自愈重试（重试必然再次冲突，且非幂等动作会重复写）。
/// </remarks>
public sealed class ConcurrencyConflictException : InvalidOperationException
{
    /// <summary>
    /// 用错误消息初始化。
    /// </summary>
    /// <param name="message">错误描述。</param>
    public ConcurrencyConflictException(string message)
        : base(message)
    {
        // 消息直传基类。
    }
}

// ANCHOR: dal.engine.storage-mode-mismatch - 存储模式不匹配异常
using System;

namespace Emoking.DAL;

/// <summary>
/// 存储模式不匹配异常——读取时以 StorageMode 指令为准，行数据形态与指令不符时显式抛出。
/// </summary>
/// <remarks>
/// 语义定义（第 12 轮确定）：
///   - 数据不迁移：StorageMode 指令变更后，旧行保持原形态；
///   - 读取 fail fast：Graph 指令遇到 SNAP1 快照行、或 Snapshot 指令遇到图模式行，
///     组装阶段抛出本异常——静默返回空对象会掩盖数据形态错误，比崩溃更危险。
/// 运维含义：切换 StorageMode 前，必须确认该类型的存量数据已经迁移或可弃。
/// </remarks>
public sealed class StorageModeMismatchException : InvalidOperationException
{
    /// <summary>
    /// 初始化异常。
    /// </summary>
    /// <param name="message">异常消息。</param>
    public StorageModeMismatchException(string message)
        : base(message)
    {
        // 消息透传基类。
    }
}

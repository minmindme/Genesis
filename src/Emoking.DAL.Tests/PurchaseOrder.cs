// ANCHOR: test.purchase-order - 快照模式演示用强类型订单（编译期已知类型）
using System;

namespace Emoking.DAL.Tests;

/// <summary>
/// 快照模式演示用订单——编译期已知类型，配合 StorageMode=Snapshot 指令演示"直接煮熟"。
/// </summary>
public sealed class PurchaseOrder
{
    /// <summary>订单号。</summary>
    public string OrderNo { get; set; } = string.Empty;

    /// <summary>订单状态：Created → Paid → Shipped。</summary>
    public string State { get; set; } = "Created";

    /// <summary>金额。</summary>
    public decimal Amount { get; set; }

    /// <summary>所属用户。</summary>
    public string Owner { get; set; } = string.Empty;

    /// <summary>创建时间。</summary>
    public DateTime CreatedAt { get; set; }
}

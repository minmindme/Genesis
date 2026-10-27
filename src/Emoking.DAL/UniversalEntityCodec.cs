// ANCHOR: dal.store.codec - 同构行手工二进制编解码器（FASTER Value 载荷）
using System;
using System.Text;

namespace Emoking.DAL;

/// <summary>
/// 同构行的手工二进制编解码器——无 JSON、无反射、无序列化框架的紧凑格式。
/// </summary>
/// <remarks>
/// 字节布局（小端）：
///   Id(16) | TypeId(16) | Role(1) | HasParent(1) + ParentId(16)? | ValueLen(4) + ValueUtf8? | Version(4) | IsDeleted(1)
/// 仅 FASTER KV 使用（byte[] Value 载荷）；进程内字典直接存对象引用，无需编解码。
/// </remarks>
public static class UniversalEntityCodec
{
    /// <summary>
    /// 把一行同构数据压成字节数组。
    /// </summary>
    /// <param name="entity">同构行。</param>
    /// <returns>打包后的字节。</returns>
    public static byte[] Pack(UniversalEntity entity)
    {
        // 值文本的 UTF-8 字节（可为 null）。
        var valueBytes = entity.Value is null ? null : Encoding.UTF8.GetBytes(entity.Value);

        // 总长度：Id16 + TypeId16 + Role1 + HasParent1 + (ParentId16)? + ValueLen4 + ValueBytes? + Version4 + IsDeleted1。
        var length = 16 + 16 + 1 + 1
            + (entity.ParentId is null ? 0 : 16)
            + 4 + (valueBytes?.Length ?? 0)
            + 4 + 1;

        // 分配缓冲。
        var buffer = new byte[length];

        // 写入游标。
        var offset = 0;

        // Id 写入 16 字节。
        entity.Id.ToByteArray().CopyTo(buffer, offset);
        offset += 16;

        // TypeId 写入 16 字节。
        entity.TypeId.ToByteArray().CopyTo(buffer, offset);
        offset += 16;

        // Role 写入 1 字节。
        buffer[offset++] = entity.Role;

        // 父指针标志 + 可选 ParentId。
        if (entity.ParentId is null)
        {
            // 无父指针。
            buffer[offset++] = 0;
        }
        else
        {
            // 有父指针标志 + 16 字节。
            buffer[offset++] = 1;
            entity.ParentId.Value.ToByteArray().CopyTo(buffer, offset);
            offset += 16;
        }

        // 值长度（-1 表示 null）+ 值字节。
        if (valueBytes is null)
        {
            // null 值标志。
            WriteInt32(buffer, offset, -1);
            offset += 4;
        }
        else
        {
            // 值长度 + 值字节。
            WriteInt32(buffer, offset, valueBytes.Length);
            offset += 4;
            valueBytes.CopyTo(buffer, offset);
            offset += valueBytes.Length;
        }

        // Version 写入 4 字节。
        WriteInt32(buffer, offset, entity.Version);
        offset += 4;

        // IsDeleted 写入尾部 1 字节。
        buffer[offset] = entity.IsDeleted ? (byte)1 : (byte)0;

        // 返回打包结果。
        return buffer;
    }

    /// <summary>
    /// 从字节数组解包一行同构数据。
    /// </summary>
    /// <param name="buffer">打包字节。</param>
    /// <returns>解包完成的行。</returns>
    public static UniversalEntity Unpack(byte[] buffer)
    {
        // 读取游标。
        var offset = 0;

        // 读 Id。
        var id = new Guid(buffer.AsSpan(0, 16));
        offset += 16;

        // 读 TypeId。
        var typeId = new Guid(buffer.AsSpan(16, 16));
        offset += 16;

        // 读 Role。
        var role = buffer[offset++];

        // 读父指针。
        Guid? parentId = null;

        // 有父指针标志时读 16 字节。
        if (buffer[offset++] == 1)
        {
            // 读 ParentId。
            parentId = new Guid(buffer.AsSpan(offset, 16));
            offset += 16;
        }

        // 读值长度。
        var valueLength = ReadInt32(buffer, offset);
        offset += 4;

        // 读值文本。
        string? value = null;

        // 长度非 -1 时解码 UTF-8，并推进游标。
        if (valueLength >= 0)
        {
            // 解码。
            value = Encoding.UTF8.GetString(buffer, offset, valueLength);
            offset += valueLength;
        }

        // 读 Version。
        var version = ReadInt32(buffer, offset);

        // 读 IsDeleted（尾部 1 字节）。
        var isDeleted = buffer[^1] == 1;

        // 构造行。
        return new UniversalEntity(
            Id: id,
            TypeId: typeId,
            Role: role,
            ParentId: parentId,
            Value: value,
            Version: version,
            IsDeleted: isDeleted);
    }

    /// <summary>
    /// 小端写入 Int32。
    /// </summary>
    private static void WriteInt32(byte[] buffer, int offset, int value)
    {
        // 逐字节小端写入。
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
        buffer[offset + 2] = (byte)(value >> 16);
        buffer[offset + 3] = (byte)(value >> 24);
    }

    /// <summary>
    /// 小端读取 Int32。
    /// </summary>
    private static int ReadInt32(byte[] buffer, int offset)
    {
        // 逐字节小端拼装。
        return buffer[offset]
            | (buffer[offset + 1] << 8)
            | (buffer[offset + 2] << 16)
            | (buffer[offset + 3] << 24);
    }
}

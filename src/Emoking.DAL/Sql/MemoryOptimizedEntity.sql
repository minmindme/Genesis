﻿-- ANCHOR: dal.schema.memory-optimized-entity - 内存优化同构表（In-Memory OLTP 物理换装）
-- 与磁盘表 dbo.UniversalEntity 结构同构，但存储引擎为内存优化（无锁、Latch-free、乐观并发）。
-- 关键事实（本机 SQL Server 2019 实测 + 文档明确）：
--   1. 内存优化表不支持 SqlBulkCopy/bcp，批量写入走 TVP + 存储过程单次往返。
--   2. 内存优化表不支持 TRUNCATE，清表用 DELETE。
--   3. 索引必须内联定义在 CREATE TABLE 中，且不支持 INCLUDE 列。
--   4. 删除顺序：先删引用 TYPE 的存储过程，再删 TYPE。
--   5. 哈希索引只支持等值查找，BUCKET_COUNT 建议为预期行数的 1~2 倍向上取 2 的幂。

-- ============================================================
-- 1. 清理旧对象（顺序：PROC → TABLE → TYPE，避免引用阻塞）
-- ============================================================
IF OBJECT_ID(N'[dbo].[usp_BulkInsertMemoryEntities]') IS NOT NULL
    DROP PROCEDURE [dbo].[usp_BulkInsertMemoryEntities];
GO

IF OBJECT_ID(N'[dbo].[MemoryOptimizedEntity]') IS NOT NULL
    DROP TABLE [dbo].[MemoryOptimizedEntity];
GO

IF TYPE_ID(N'[dbo].[UniversalEntityTableType]') IS NOT NULL
    DROP TYPE [dbo].[UniversalEntityTableType];
GO

-- ============================================================
-- 2. TVP 表类型：批量装载的内存侧同构数据包结构
-- ============================================================
CREATE TYPE [dbo].[UniversalEntityTableType] AS TABLE (
    [Id]        UNIQUEIDENTIFIER  NOT NULL,
    [TypeId]    UNIQUEIDENTIFIER  NOT NULL,
    [Role]      TINYINT           NOT NULL,
    [ParentId]  UNIQUEIDENTIFIER  NULL,
    [Value]     NVARCHAR(MAX)     NULL,
    [Version]   INT               NOT NULL,
    [IsDeleted] BIT               NOT NULL,
    [CreatedAt] DATETIME2(3)      NOT NULL,
    [UpdatedAt] DATETIME2(3)      NOT NULL
);
GO

-- ============================================================
-- 3. 内存优化同构表：结构与磁盘表完全一致，索引全部内联（无 INCLUDE）
-- ============================================================
CREATE TABLE [dbo].[MemoryOptimizedEntity] (
    [Id]        UNIQUEIDENTIFIER  NOT NULL,
    [TypeId]    UNIQUEIDENTIFIER  NOT NULL,
    [Role]      TINYINT           NOT NULL,
    [ParentId]  UNIQUEIDENTIFIER  NULL,
    [Value]     NVARCHAR(MAX)     NULL,
    [Version]   INT               NOT NULL DEFAULT 1,
    [IsDeleted] BIT               NOT NULL DEFAULT 0,
    [CreatedAt] DATETIME2(3)      NOT NULL DEFAULT SYSUTCDATETIME(),
    [UpdatedAt] DATETIME2(3)      NOT NULL DEFAULT SYSUTCDATETIME(),

    -- 主键哈希索引：等值点查 Id 的极速路径，桶数 2^18 支持 10 万级行。
    CONSTRAINT [PK_MemoryOptimizedEntity] PRIMARY KEY NONCLUSTERED HASH ([Id])
        WITH (BUCKET_COUNT = 262144),

    -- ParentId 指针索引：子图追逐路径。
    INDEX [IX_MemoryOptimizedEntity_ParentId] NONCLUSTERED ([ParentId]),

    -- 类型 + 角色组合索引：元数据加载与实体行全量拉取路径。
    INDEX [IX_MemoryOptimizedEntity_TypeId_Role] NONCLUSTERED ([TypeId], [Role], [IsDeleted])
)
WITH (MEMORY_OPTIMIZED = ON, DURABILITY = SCHEMA_AND_DATA);
GO

-- ============================================================
-- 4. TVP 批量装载存储过程：单次往返整包写入
-- ============================================================
CREATE PROCEDURE [dbo].[usp_BulkInsertMemoryEntities]
    @Rows [dbo].[UniversalEntityTableType] READONLY
AS
BEGIN
    -- 关闭受影响行数消息，减少往返负载。
    SET NOCOUNT ON;

    -- 整包一次性插入内存优化表——原子性由 TVP 单语句特性保证。
    INSERT INTO [dbo].[MemoryOptimizedEntity]
        ([Id], [TypeId], [Role], [ParentId], [Value], [Version], [IsDeleted], [CreatedAt], [UpdatedAt])
    SELECT
        [Id], [TypeId], [Role], [ParentId], [Value], [Version], [IsDeleted], [CreatedAt], [UpdatedAt]
    FROM @Rows;
END
GO

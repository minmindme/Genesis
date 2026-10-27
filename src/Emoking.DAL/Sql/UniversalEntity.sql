﻿-- ANCHOR: dal.schema.universal-entity - 同构单表物理定义
-- 角色标识 Role 枚举：
--   1 = Meta   元数据定义（实体类型、字段定义、路由规则）
--   2 = Data   业务数据实例（实体实例 + 属性值）
--   3 = Edge   关系边（图指针，连接 Node 到 Node）
--   4 = Engine 引擎指令（路由协议、安全指令）
CREATE TABLE [dbo].[UniversalEntity] (
    [Id]            UNIQUEIDENTIFIER    NOT NULL,       -- 雪花/雪花占位：Guid，原型期防遍历
    [TypeId]        UNIQUEIDENTIFIER    NOT NULL,       -- 类型指针：指向 Meta 行定义它是谁
    [Role]          TINYINT             NOT NULL,       -- 角色同化标识，见上方枚举
    [ParentId]      UNIQUEIDENTIFIER    NULL,           -- 路由/树形/图指针，用于结构跳跃
    [Value]         NVARCHAR(MAX)       NULL,           -- 纯粹的值，无 JSON 结构
    [Version]       INT                 NOT NULL DEFAULT 1,
    [IsDeleted]     BIT                 NOT NULL DEFAULT 0,
    [CreatedAt]     DATETIME2(3)        NOT NULL DEFAULT SYSUTCDATETIME(),
    [UpdatedAt]     DATETIME2(3)        NOT NULL DEFAULT SYSUTCDATETIME()
);
GO

-- 主键 + Id 唯一约束
ALTER TABLE [dbo].[UniversalEntity] ADD CONSTRAINT [PK_UniversalEntity] PRIMARY KEY NONCLUSTERED ([Id]);
GO

-- 按 TypeId 聚集索引：原型期单表，真实部署时改为 TypeId 分区方案
CREATE CLUSTERED INDEX [IX_UniversalEntity_TypeId_Role] ON [dbo].[UniversalEntity] ([TypeId], [Role], [IsDeleted]);
GO

-- ParentId 索引：Edge 反向跳转高频路径
CREATE NONCLUSTERED INDEX [IX_UniversalEntity_ParentId] ON [dbo].[UniversalEntity] ([ParentId]) INCLUDE ([Id], [Role], [Typeid]);
GO

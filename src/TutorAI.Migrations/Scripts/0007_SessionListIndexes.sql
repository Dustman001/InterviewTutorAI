-- The session list pages through sessions newest first: per user, and across all users for admins.
-- Without these the list scanned and sorted the whole table.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Sessions_UserId_StartedAt' AND object_id = OBJECT_ID(N'dbo.Sessions'))
BEGIN
    CREATE INDEX IX_Sessions_UserId_StartedAt ON dbo.Sessions (UserId, StartedAt DESC, Id DESC) INCLUDE (Status);
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Sessions_StartedAt' AND object_id = OBJECT_ID(N'dbo.Sessions'))
BEGIN
    CREATE INDEX IX_Sessions_StartedAt ON dbo.Sessions (StartedAt DESC, Id DESC) INCLUDE (Status, UserId);
END;

IF OBJECT_ID(N'dbo.Users', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Users
    (
        Id           INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Users PRIMARY KEY,
        Username     NVARCHAR(100)     NOT NULL CONSTRAINT UQ_Users_Username UNIQUE,
        PasswordHash NVARCHAR(400)     NOT NULL,
        Role         NVARCHAR(20)      NOT NULL,
        -- NULL means unlimited sessions.
        SessionLimit INT               NULL,
        CreatedAt    DATETIME2(3)      NOT NULL CONSTRAINT DF_Users_CreatedAt DEFAULT SYSUTCDATETIME()
    );
END;

-- Sessions created before sign-in existed have no owner; only admins see them.
IF COL_LENGTH(N'dbo.Sessions', N'UserId') IS NULL
BEGIN
    ALTER TABLE dbo.Sessions ADD UserId INT NULL CONSTRAINT FK_Sessions_Users REFERENCES dbo.Users (Id);
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Sessions_UserId' AND object_id = OBJECT_ID(N'dbo.Sessions'))
BEGIN
    CREATE INDEX IX_Sessions_UserId ON dbo.Sessions (UserId, StartedAt DESC);
END;

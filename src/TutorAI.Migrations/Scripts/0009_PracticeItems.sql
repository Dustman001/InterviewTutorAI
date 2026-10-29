-- Practice drills: corrections and weak words from graded sessions, repeated on a schedule (boxes 0-4).
IF OBJECT_ID(N'dbo.PracticeItems', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PracticeItems
    (
        Id          BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_PracticeItems PRIMARY KEY,
        UserId      INT              NOT NULL CONSTRAINT FK_PracticeItems_Users REFERENCES dbo.Users (Id) ON DELETE CASCADE,
        -- The session the item last came from; kept when the session is deleted.
        SessionId   UNIQUEIDENTIFIER NULL CONSTRAINT FK_PracticeItems_Sessions REFERENCES dbo.Sessions (Id) ON DELETE SET NULL,
        -- correction or word
        Kind        NVARCHAR(20)     NOT NULL,
        -- What the learner reads aloud: the corrected sentence, or the word.
        Target      NVARCHAR(400)    NOT NULL,
        -- Target normalised (lower case, single spaces, no end punctuation), for merging duplicates.
        TargetKey   NVARCHAR(400)    NOT NULL,
        Original    NVARCHAR(400)    NULL,
        Explanation NVARCHAR(1000)   NULL,
        -- The source session's accent; NULL is the default accent.
        Locale      NVARCHAR(20)     NULL,
        Box         TINYINT          NOT NULL CONSTRAINT DF_PracticeItems_Box DEFAULT 0,
        -- NULL once mastered.
        DueAt       DATETIME2(3)     NULL,
        Attempts    INT              NOT NULL CONSTRAINT DF_PracticeItems_Attempts DEFAULT 0,
        LastScore   FLOAT            NULL,
        BestScore   FLOAT            NULL,
        CreatedAt   DATETIME2(3)     NOT NULL CONSTRAINT DF_PracticeItems_CreatedAt DEFAULT SYSUTCDATETIME(),
        UpdatedAt   DATETIME2(3)     NOT NULL CONSTRAINT DF_PracticeItems_UpdatedAt DEFAULT SYSUTCDATETIME(),
        CONSTRAINT UQ_PracticeItems_User_Kind_Target UNIQUE (UserId, Kind, TargetKey),
        CONSTRAINT CK_PracticeItems_Kind CHECK (Kind IN (N'correction', N'word')),
        CONSTRAINT CK_PracticeItems_Box CHECK (Box BETWEEN 0 AND 4)
    );
END;
GO

-- The practice page and the home page's due count read the user's due items in due order.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_PracticeItems_UserId_DueAt' AND object_id = OBJECT_ID(N'dbo.PracticeItems'))
BEGIN
    CREATE INDEX IX_PracticeItems_UserId_DueAt ON dbo.PracticeItems (UserId, DueAt);
END;

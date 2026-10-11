IF OBJECT_ID(N'dbo.Sessions', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Sessions
    (
        Id           UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_Sessions PRIMARY KEY,
        Mode         NVARCHAR(20)     NOT NULL,
        ScenarioJson NVARCHAR(MAX)    NOT NULL,
        StartedAt    DATETIME2(3)     NOT NULL CONSTRAINT DF_Sessions_StartedAt DEFAULT SYSUTCDATETIME(),
        EndedAt      DATETIME2(3)     NULL,
        Status       NVARCHAR(20)     NOT NULL CONSTRAINT DF_Sessions_Status DEFAULT N'Active'
    );
END;

IF OBJECT_ID(N'dbo.Turns', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Turns
    (
        Id                BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Turns PRIMARY KEY,
        SessionId         UNIQUEIDENTIFIER     NOT NULL CONSTRAINT FK_Turns_Sessions REFERENCES dbo.Sessions (Id) ON DELETE CASCADE,
        Seq               INT                  NOT NULL,
        Role              NVARCHAR(10)         NOT NULL,
        Text              NVARCHAR(MAX)        NOT NULL,
        StartedAt         DATETIME2(3)         NOT NULL CONSTRAINT DF_Turns_StartedAt DEFAULT SYSUTCDATETIME(),
        DurationMs        INT                  NULL,
        WordCount         INT                  NOT NULL CONSTRAINT DF_Turns_WordCount DEFAULT 0,
        AccuracyScore     FLOAT                NULL,
        FluencyScore      FLOAT                NULL,
        ProsodyScore      FLOAT                NULL,
        PronunciationJson NVARCHAR(MAX)        NULL,
        CONSTRAINT UQ_Turns_Session_Seq UNIQUE (SessionId, Seq)
    );
END;

IF OBJECT_ID(N'dbo.Assessments', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Assessments
    (
        SessionId     UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_Assessments PRIMARY KEY
                                                CONSTRAINT FK_Assessments_Sessions REFERENCES dbo.Sessions (Id) ON DELETE CASCADE,
        Fluency       NVARCHAR(4)      NULL,
        Grammar       NVARCHAR(4)      NULL,
        Vocabulary    NVARCHAR(4)      NULL,
        Interaction   NVARCHAR(4)      NULL,
        Pronunciation NVARCHAR(4)      NULL,
        Overall       NVARCHAR(4)      NULL,
        MetricsJson   NVARCHAR(MAX)    NOT NULL,
        DetailJson    NVARCHAR(MAX)    NOT NULL,
        Model         NVARCHAR(100)    NULL,
        CreatedAt     DATETIME2(3)     NOT NULL CONSTRAINT DF_Assessments_CreatedAt DEFAULT SYSUTCDATETIME()
    );
END;

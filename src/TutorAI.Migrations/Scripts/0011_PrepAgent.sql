-- Interview-prep agent (P17): its runs, the writes each run made or proposed, and the drills it scheduled.

-- One agent run for one job description.
IF OBJECT_ID(N'dbo.PrepRuns', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PrepRuns
    (
        Id             UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_PrepRuns PRIMARY KEY,
        UserId         INT              NOT NULL CONSTRAINT FK_PrepRuns_Users REFERENCES dbo.Users (Id) ON DELETE CASCADE,
        -- The client's Idempotency-Key header: a retried request returns this run instead of starting another.
        IdempotencyKey NVARCHAR(100)    NULL,
        JobDescription NVARCHAR(MAX)    NOT NULL,
        Role           NVARCHAR(200)    NULL,
        Seniority      NVARCHAR(200)    NULL,
        -- Running, Completed, Stopped (hit a step or token limit) or Failed
        Status         NVARCHAR(20)     NOT NULL,
        StopReason     NVARCHAR(400)    NULL,
        Summary        NVARCHAR(4000)   NULL,
        -- The questions the agent planned (plan_questions), as a JSON array.
        QuestionsJson  NVARCHAR(MAX)    NULL,
        Model          NVARCHAR(100)    NULL,
        Steps          INT              NOT NULL CONSTRAINT DF_PrepRuns_Steps DEFAULT 0,
        InputTokens    INT              NOT NULL CONSTRAINT DF_PrepRuns_InputTokens DEFAULT 0,
        OutputTokens   INT              NOT NULL CONSTRAINT DF_PrepRuns_OutputTokens DEFAULT 0,
        CreatedAt      DATETIME2(3)     NOT NULL CONSTRAINT DF_PrepRuns_CreatedAt DEFAULT SYSUTCDATETIME(),
        CompletedAt    DATETIME2(3)     NULL,
        CONSTRAINT CK_PrepRuns_Status CHECK (Status IN (N'Running', N'Completed', N'Stopped', N'Failed'))
    );
END;
GO

-- The user's runs newest first, and the guests' daily run count.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_PrepRuns_UserId_CreatedAt' AND object_id = OBJECT_ID(N'dbo.PrepRuns'))
BEGIN
    CREATE INDEX IX_PrepRuns_UserId_CreatedAt ON dbo.PrepRuns (UserId, CreatedAt DESC);
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_PrepRuns_UserId_IdempotencyKey' AND object_id = OBJECT_ID(N'dbo.PrepRuns'))
BEGIN
    CREATE UNIQUE INDEX UX_PrepRuns_UserId_IdempotencyKey ON dbo.PrepRuns (UserId, IdempotencyKey)
        WHERE IdempotencyKey IS NOT NULL;
END;
GO

-- Every write a run's tools made (Applied) or proposed for the learner to confirm (PendingConfirmation).
IF OBJECT_ID(N'dbo.PrepActions', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PrepActions
    (
        Id             BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_PrepActions PRIMARY KEY,
        RunId          UNIQUEIDENTIFIER NOT NULL CONSTRAINT FK_PrepActions_PrepRuns REFERENCES dbo.PrepRuns (Id) ON DELETE CASCADE,
        -- SHA-256 (hex) of the tool name and its normalised arguments: the same call twice in a run writes once.
        IdempotencyKey CHAR(64)         NOT NULL,
        Tool           NVARCHAR(50)     NOT NULL,
        ArgsJson       NVARCHAR(MAX)    NOT NULL,
        Status         NVARCHAR(30)     NOT NULL,
        ResultJson     NVARCHAR(MAX)    NULL,
        CreatedAt      DATETIME2(3)     NOT NULL CONSTRAINT DF_PrepActions_CreatedAt DEFAULT SYSUTCDATETIME(),
        DecidedAt      DATETIME2(3)     NULL,
        CONSTRAINT UQ_PrepActions_Run_Key UNIQUE (RunId, IdempotencyKey),
        CONSTRAINT CK_PrepActions_Status CHECK (Status IN (N'Applied', N'PendingConfirmation', N'Confirmed', N'Rejected'))
    );
END;
GO

-- Interview drills the agent scheduled. Starting one creates an interview session from ScenarioJson.
IF OBJECT_ID(N'dbo.Drills', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Drills
    (
        Id           UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_Drills PRIMARY KEY,
        UserId       INT              NOT NULL CONSTRAINT FK_Drills_Users REFERENCES dbo.Users (Id) ON DELETE CASCADE,
        -- The run that scheduled it. No foreign key: a second cascade path from Users is not allowed.
        RunId        UNIQUEIDENTIFIER NULL,
        ScheduledFor DATETIME2(3)     NOT NULL,
        ScenarioJson NVARCHAR(MAX)    NOT NULL,
        -- Scheduled, Started or Cancelled
        Status       NVARCHAR(20)     NOT NULL,
        SessionId    UNIQUEIDENTIFIER NULL CONSTRAINT FK_Drills_Sessions REFERENCES dbo.Sessions (Id) ON DELETE SET NULL,
        CreatedAt    DATETIME2(3)     NOT NULL CONSTRAINT DF_Drills_CreatedAt DEFAULT SYSUTCDATETIME(),
        UpdatedAt    DATETIME2(3)     NOT NULL CONSTRAINT DF_Drills_UpdatedAt DEFAULT SYSUTCDATETIME(),
        CONSTRAINT CK_Drills_Status CHECK (Status IN (N'Scheduled', N'Started', N'Cancelled'))
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Drills_UserId_ScheduledFor' AND object_id = OBJECT_ID(N'dbo.Drills'))
BEGIN
    CREATE INDEX IX_Drills_UserId_ScheduledFor ON dbo.Drills (UserId, ScheduledFor);
END;
GO

-- Practice items gain a third kind: a phrase to rehearse for an interview, added by the agent.
IF NOT EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE name = N'CK_PracticeItems_Kind' AND parent_object_id = OBJECT_ID(N'dbo.PracticeItems')
      AND definition LIKE N'%phrase%')
BEGIN
    IF OBJECT_ID(N'dbo.CK_PracticeItems_Kind', N'C') IS NOT NULL
        ALTER TABLE dbo.PracticeItems DROP CONSTRAINT CK_PracticeItems_Kind;
    ALTER TABLE dbo.PracticeItems ADD CONSTRAINT CK_PracticeItems_Kind CHECK (Kind IN (N'correction', N'word', N'phrase'));
END;

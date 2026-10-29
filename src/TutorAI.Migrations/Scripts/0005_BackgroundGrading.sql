-- Grading runs in a background worker. NULL: never queued. Pending, Running, Done or Failed.
IF COL_LENGTH(N'dbo.Sessions', N'GradingState') IS NULL
BEGIN
    ALTER TABLE dbo.Sessions ADD GradingState NVARCHAR(20) NULL;
END;

-- Attempts since the grading was last queued; the worker gives up after a configured number.
IF COL_LENGTH(N'dbo.Sessions', N'GradingAttempts') IS NULL
BEGIN
    ALTER TABLE dbo.Sessions ADD GradingAttempts INT NOT NULL CONSTRAINT DF_Sessions_GradingAttempts DEFAULT 0;
END;

-- Shown to the learner when the state is Failed.
IF COL_LENGTH(N'dbo.Sessions', N'GradingError') IS NULL
BEGIN
    ALTER TABLE dbo.Sessions ADD GradingError NVARCHAR(400) NULL;
END;

-- When the current attempt was claimed. A Running row older than the stale limit is claimed again.
IF COL_LENGTH(N'dbo.Sessions', N'GradingStartedAt') IS NULL
BEGIN
    ALTER TABLE dbo.Sessions ADD GradingStartedAt DATETIME2(3) NULL;
END;

-- A Pending row waits until this time before its next attempt (retry backoff). NULL: due now.
IF COL_LENGTH(N'dbo.Sessions', N'GradingNextAttemptAt') IS NULL
BEGIN
    ALTER TABLE dbo.Sessions ADD GradingNextAttemptAt DATETIME2(3) NULL;
END;
GO

-- The worker only ever looks for queued or running work.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Sessions_GradingState' AND object_id = OBJECT_ID(N'dbo.Sessions'))
BEGIN
    CREATE INDEX IX_Sessions_GradingState ON dbo.Sessions (GradingState)
        INCLUDE (GradingAttempts, GradingStartedAt, GradingNextAttemptAt)
        WHERE GradingState IN (N'Pending', N'Running');
END;

-- Sessions that ended before this change without an assessment (grading failed) are queued once.
UPDATE s
SET GradingState = N'Pending'
FROM dbo.Sessions s
WHERE s.Status = N'Ended'
  AND s.GradingState IS NULL
  AND NOT EXISTS (SELECT 1 FROM dbo.Assessments a WHERE a.SessionId = s.Id);

-- Sessions already graded are Done.
UPDATE s
SET GradingState = N'Done'
FROM dbo.Sessions s
WHERE s.GradingState IS NULL
  AND EXISTS (SELECT 1 FROM dbo.Assessments a WHERE a.SessionId = s.Id);

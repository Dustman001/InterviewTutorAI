-- Why a learner turn's speech scores were dropped (they failed the plausibility checks). NULL: kept, or none sent.
IF COL_LENGTH(N'dbo.Turns', N'ScoresRejected') IS NULL
BEGIN
    ALTER TABLE dbo.Turns ADD ScoresRejected NVARCHAR(200) NULL;
END;

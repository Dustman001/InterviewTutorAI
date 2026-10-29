-- Token usage of the Claude call behind each tutor turn and each assessment, for cost per user.
-- NULL on rows written before this change and on learner turns.
IF COL_LENGTH(N'dbo.Turns', N'Model') IS NULL
BEGIN
    ALTER TABLE dbo.Turns ADD Model NVARCHAR(100) NULL;
END;

IF COL_LENGTH(N'dbo.Turns', N'InputTokens') IS NULL
BEGIN
    ALTER TABLE dbo.Turns ADD InputTokens INT NULL;
END;

IF COL_LENGTH(N'dbo.Turns', N'OutputTokens') IS NULL
BEGIN
    ALTER TABLE dbo.Turns ADD OutputTokens INT NULL;
END;

IF COL_LENGTH(N'dbo.Turns', N'CacheReadTokens') IS NULL
BEGIN
    ALTER TABLE dbo.Turns ADD CacheReadTokens INT NULL;
END;

IF COL_LENGTH(N'dbo.Turns', N'CacheWriteTokens') IS NULL
BEGIN
    ALTER TABLE dbo.Turns ADD CacheWriteTokens INT NULL;
END;

IF COL_LENGTH(N'dbo.Assessments', N'InputTokens') IS NULL
BEGIN
    ALTER TABLE dbo.Assessments ADD InputTokens INT NULL;
END;

IF COL_LENGTH(N'dbo.Assessments', N'OutputTokens') IS NULL
BEGIN
    ALTER TABLE dbo.Assessments ADD OutputTokens INT NULL;
END;

IF COL_LENGTH(N'dbo.Assessments', N'CacheReadTokens') IS NULL
BEGIN
    ALTER TABLE dbo.Assessments ADD CacheReadTokens INT NULL;
END;

IF COL_LENGTH(N'dbo.Assessments', N'CacheWriteTokens') IS NULL
BEGIN
    ALTER TABLE dbo.Assessments ADD CacheWriteTokens INT NULL;
END;

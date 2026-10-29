-- The grading prompt version that produced an assessment, so stored grades can be traced. NULL: before this change.
IF COL_LENGTH(N'dbo.Assessments', N'PromptVersion') IS NULL
BEGIN
    ALTER TABLE dbo.Assessments ADD PromptVersion NVARCHAR(40) NULL;
END;

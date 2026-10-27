-- Disabled users cannot sign in, and their existing tokens stop working.
IF COL_LENGTH(N'dbo.Users', N'IsDisabled') IS NULL
BEGIN
    ALTER TABLE dbo.Users ADD IsDisabled BIT NOT NULL CONSTRAINT DF_Users_IsDisabled DEFAULT 0;
END;

-- Carried in each JWT; bumping it signs the user out everywhere (password reset, disable, role change).
IF COL_LENGTH(N'dbo.Users', N'TokenVersion') IS NULL
BEGIN
    ALTER TABLE dbo.Users ADD TokenVersion INT NOT NULL CONSTRAINT DF_Users_TokenVersion DEFAULT 0;
END;

-- Sessions started before this time do not count towards SessionLimit. NULL counts all of them.
IF COL_LENGTH(N'dbo.Users', N'SessionsResetAt') IS NULL
BEGIN
    ALTER TABLE dbo.Users ADD SessionsResetAt DATETIME2(3) NULL;
END;

-- Hash of the password last applied from configuration (built-in accounts only), so a password
-- changed on the Users page survives restarts until the configured password itself changes.
IF COL_LENGTH(N'dbo.Users', N'ConfigPasswordHash') IS NULL
BEGIN
    ALTER TABLE dbo.Users ADD ConfigPasswordHash NVARCHAR(400) NULL;
END;

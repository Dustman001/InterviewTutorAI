-- Failed sign-ins in a row; a successful sign-in or a password reset sets it back to 0.
IF COL_LENGTH(N'dbo.Users', N'FailedLoginCount') IS NULL
BEGIN
    ALTER TABLE dbo.Users ADD FailedLoginCount INT NOT NULL CONSTRAINT DF_Users_FailedLoginCount DEFAULT 0;
END;

-- Sign-in is refused until this time (UTC), even with the right password. NULL means not locked.
IF COL_LENGTH(N'dbo.Users', N'LockedUntil') IS NULL
BEGIN
    ALTER TABLE dbo.Users ADD LockedUntil DATETIME2(3) NULL;
END;

-- ============================================================================
--  StockDesk / ChewyPetsFeed  —  schema upgrade v3
--  Security hardening (password hashing + lockout), installation licensing,
--  and login auditing. Safe to run more than once.
-- ============================================================================
USE StockDeskDB;
GO

-- ---- Users: real password hashing + lockout + first-login reset ----------
IF COL_LENGTH('Users', 'FailedAttempts')     IS NULL ALTER TABLE Users ADD FailedAttempts     INT NOT NULL DEFAULT 0;
IF COL_LENGTH('Users', 'LockedUntil')        IS NULL ALTER TABLE Users ADD LockedUntil        DATETIME2 NULL;
IF COL_LENGTH('Users', 'MustChangePassword') IS NULL ALTER TABLE Users ADD MustChangePassword BIT NOT NULL DEFAULT 0;
IF COL_LENGTH('Users', 'LastLoginAt')        IS NULL ALTER TABLE Users ADD LastLoginAt        DATETIME2 NULL;
GO

-- Any account still holding the insecure placeholder hash must set a real
-- password on next sign-in (the login screen forces it).
UPDATE Users SET MustChangePassword = 1
WHERE PasswordHash IS NULL OR PasswordHash NOT LIKE 'pbkdf2$%';
GO

-- ---- Login audit -------------------------------------------------------
IF OBJECT_ID('LoginAudit', 'U') IS NULL
CREATE TABLE LoginAudit (
    LoginAuditID INT IDENTITY(1,1) PRIMARY KEY,
    Username     NVARCHAR(50) NOT NULL,
    Succeeded    BIT NOT NULL,
    Reason       NVARCHAR(100) NULL,
    MachineName  NVARCHAR(100) NULL,
    AtUtc        DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);
GO

-- ---- Licensing (installation key) -------------------------------------
--  AppSettings keys used:
--    license.status   = 'unactivated' | 'active'
--    license.key      = the accepted key
--    license.machine  = machine fingerprint the key was bound to
--    license.activatedAt
IF NOT EXISTS (SELECT 1 FROM AppSettings WHERE SettingKey = 'license.status')
    INSERT INTO AppSettings (SettingKey, SettingValue) VALUES ('license.status', 'unactivated');
GO

PRINT 'Migration v3 complete.';
GO

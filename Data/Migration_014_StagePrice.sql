/* Migration 014 – Bring the StagePrice columns into the migration history
   (AUD-W2-12 / AUD-B-008).

   DatabaseMigrator.EnsureStagePriceColumnsAsync added these two columns on every
   application start, outside ApplyMigrationAsync, so __DatabaseMigrations never knew
   about them: a schema rebuilt from the .sql files alone was missing them, and the
   history could not answer "what has this database seen". The probe also issued DDL
   on every launch.

   Idempotent, so databases that already carry the columns are unaffected.

   Reversal: ALTER TABLE dbo.Buses DROP COLUMN StagePrice;
             ALTER TABLE dbo.Trips DROP COLUMN StagePrice;
*/

IF COL_LENGTH(N'dbo.Buses', N'StagePrice') IS NULL
    ALTER TABLE dbo.Buses ADD StagePrice DECIMAL(10,0) NULL;
GO

IF COL_LENGTH(N'dbo.Trips', N'StagePrice') IS NULL
    ALTER TABLE dbo.Trips ADD StagePrice DECIMAL(10,0) NULL;
GO

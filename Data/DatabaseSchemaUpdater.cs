using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Splitbill.Data;

/// <summary>
/// Applies small, idempotent additions to the EnsureCreated SQLite schema.
/// Existing installations must not be dropped or recreated.
/// </summary>
public static class DatabaseSchemaUpdater
{
    private static readonly string[] MicrosoftCredentialColumns =
    [
        "TenantId", "ClientId", "ProtectedClientSecret", "CredentialRevision", "UpdatedAt", "UpdatedByUserId",
        "LegacyMigrationAttempted", "LegacyMigrationCompleted", "LegacyMigrationError", "ResetMarker",
        "PendingTenantId", "PendingClientId", "ProtectedPendingClientSecret", "PendingCredentialRevision", "PendingUpdatedAt", "PendingUpdatedByUserId"
    ];

    public static async Task EnsureAsync(ApplicationDbContext db, CancellationToken cancellationToken = default)
    {
        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await ExecuteAsync(db, "PRAGMA foreign_keys = ON;", cancellationToken);

            // ApplicationUser inherits Email and NormalizedEmail from ASP.NET Identity.
            // EnsureCreated includes both columns for new databases; these guards keep
            // older/custom installations upgradeable without recreating AspNetUsers.
            if (!await HasColumnAsync(db, "AspNetUsers", "Email", cancellationToken))
                await ExecuteAsync(db, "ALTER TABLE \"AspNetUsers\" ADD COLUMN \"Email\" TEXT NULL;", cancellationToken);
            if (!await HasColumnAsync(db, "AspNetUsers", "NormalizedEmail", cancellationToken))
                await ExecuteAsync(db, "ALTER TABLE \"AspNetUsers\" ADD COLUMN \"NormalizedEmail\" TEXT NULL;", cancellationToken);
            await ExecuteAsync(db, "CREATE INDEX IF NOT EXISTS \"EmailIndex\" ON \"AspNetUsers\" (\"NormalizedEmail\");", cancellationToken);

            await ExecuteAsync(db, """
                CREATE TABLE IF NOT EXISTS "InstallationStates" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_InstallationStates" PRIMARY KEY,
                    "InstallationId" TEXT NOT NULL,
                    "BootstrapCodeHash" TEXT NULL,
                    "ProtectedBootstrapCode" TEXT NULL,
                    "BootstrapCodeExpiresAt" TEXT NULL,
                    "SetupCompletedAt" TEXT NULL
                );
                """, cancellationToken);

            await ExecuteAsync(db, """
                CREATE TABLE IF NOT EXISTS "CurrencyConfigurations" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_CurrencyConfigurations" PRIMARY KEY,
                    "DefaultCurrencyCode" TEXT NOT NULL DEFAULT 'IDR',
                    "ProviderKind" INTEGER NOT NULL DEFAULT 0,
                    "BaseUrl" TEXT NOT NULL DEFAULT 'https://api.frankfurter.dev',
                    "AuthenticationMode" INTEGER NOT NULL DEFAULT 0,
                    "ProtectedApiKey" TEXT NULL,
                    "AllowPrivateNetworkEndpoint" INTEGER NOT NULL DEFAULT 0,
                    "AutoRefreshEnabled" INTEGER NOT NULL DEFAULT 1,
                    "DashboardCurrencyCodes" TEXT NOT NULL DEFAULT 'USD,SGD,EUR,JPY',
                    "DashboardPrimaryCurrencyCode" TEXT NOT NULL DEFAULT 'USD',
                    "LastTestAt" TEXT NULL,
                    "LastTestSucceeded" INTEGER NULL,
                    "LastError" TEXT NULL,
                    "LastRefreshAt" TEXT NULL,
                    "LastRefreshSucceeded" INTEGER NULL,
                    "LastRefreshError" TEXT NULL,
                    "UpdatedAt" TEXT NOT NULL,
                    "UpdatedByUserId" TEXT NULL
                );
                CREATE TABLE IF NOT EXISTS "CurrencyExchangeRates" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_CurrencyExchangeRates" PRIMARY KEY AUTOINCREMENT,
                    "BaseCurrencyCode" TEXT NOT NULL,
                    "QuoteCurrencyCode" TEXT NOT NULL,
                    "Rate" TEXT NOT NULL,
                    "EffectiveDate" TEXT NOT NULL,
                    "RetrievedAt" TEXT NOT NULL,
                    "ProviderKind" INTEGER NOT NULL,
                    "ProviderBaseUrlFingerprint" TEXT NOT NULL
                );
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_CurrencyExchangeRates_PairDateProvider"
                    ON "CurrencyExchangeRates" ("BaseCurrencyCode", "QuoteCurrencyCode", "EffectiveDate", "ProviderBaseUrlFingerprint");
                CREATE TABLE IF NOT EXISTS "GuestAccessLinks" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_GuestAccessLinks" PRIMARY KEY AUTOINCREMENT,
                    "ParticipantId" INTEGER NOT NULL,
                    "TokenHash" TEXT NOT NULL,
                    "ProtectedToken" TEXT NOT NULL,
                    "Status" INTEGER NOT NULL DEFAULT 0,
                    "CreatedAt" TEXT NOT NULL,
                    "RevokedAt" TEXT NULL,
                    "LastAccessedAt" TEXT NULL,
                    "CreatedByUserId" TEXT NULL,
                    "AccessCount" INTEGER NOT NULL DEFAULT 0,
                    CONSTRAINT "FK_GuestAccessLinks_TransactionParticipants_ParticipantId"
                        FOREIGN KEY ("ParticipantId") REFERENCES "TransactionParticipants" ("Id") ON DELETE CASCADE
                );
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_GuestAccessLinks_TokenHash" ON "GuestAccessLinks" ("TokenHash");
                CREATE INDEX IF NOT EXISTS "IX_GuestAccessLinks_ParticipantId_Status" ON "GuestAccessLinks" ("ParticipantId", "Status");
                CREATE TABLE IF NOT EXISTS "GuestTransactionAccessLinks" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_GuestTransactionAccessLinks" PRIMARY KEY AUTOINCREMENT,
                    "TransactionId" INTEGER NOT NULL,
                    "TokenHash" TEXT NOT NULL,
                    "ProtectedToken" TEXT NOT NULL,
                    "Status" INTEGER NOT NULL DEFAULT 0,
                    "CreatedAt" TEXT NOT NULL,
                    "RevokedAt" TEXT NULL,
                    "LastAccessedAt" TEXT NULL,
                    "CreatedByUserId" TEXT NULL,
                    "AccessCount" INTEGER NOT NULL DEFAULT 0,
                    CONSTRAINT "FK_GuestTransactionAccessLinks_Transactions_TransactionId"
                        FOREIGN KEY ("TransactionId") REFERENCES "Transactions" ("Id") ON DELETE CASCADE
                );
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_GuestTransactionAccessLinks_TokenHash"
                    ON "GuestTransactionAccessLinks" ("TokenHash");
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_GuestTransactionAccessLinks_ActiveTransaction"
                    ON "GuestTransactionAccessLinks" ("TransactionId") WHERE "Status" = 0;
                """, cancellationToken);

            if (!await HasColumnAsync(db, "CurrencyConfigurations", "DashboardCurrencyCodes", cancellationToken))
                await ExecuteAsync(db, "ALTER TABLE \"CurrencyConfigurations\" ADD COLUMN \"DashboardCurrencyCodes\" TEXT NOT NULL DEFAULT 'USD,SGD,EUR,JPY';", cancellationToken);
            if (!await HasColumnAsync(db, "CurrencyConfigurations", "DashboardPrimaryCurrencyCode", cancellationToken))
                await ExecuteAsync(db, "ALTER TABLE \"CurrencyConfigurations\" ADD COLUMN \"DashboardPrimaryCurrencyCode\" TEXT NOT NULL DEFAULT 'USD';", cancellationToken);

            await ExecuteAsync(db, """
                CREATE TABLE IF NOT EXISTS "MicrosoftIntegrationConfigurations" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_MicrosoftIntegrationConfigurations" PRIMARY KEY,
                    "TenantId" TEXT NOT NULL DEFAULT '',
                    "ClientId" TEXT NOT NULL DEFAULT '',
                    "ProtectedClientSecret" TEXT NOT NULL DEFAULT '',
                    "CredentialRevision" INTEGER NOT NULL DEFAULT 0,
                    "UpdatedAt" TEXT NOT NULL DEFAULT '0001-01-01T00:00:00.0000000+00:00',
                    "UpdatedByUserId" TEXT NULL,
                    "LegacyMigrationAttempted" INTEGER NOT NULL DEFAULT 0,
                    "LegacyMigrationCompleted" INTEGER NOT NULL DEFAULT 0,
                    "LegacyMigrationError" TEXT NULL,
                    "ResetMarker" TEXT NULL,
                    "PendingTenantId" TEXT NULL,
                    "PendingClientId" TEXT NULL,
                    "ProtectedPendingClientSecret" TEXT NULL,
                    "PendingCredentialRevision" INTEGER NULL,
                    "PendingUpdatedAt" TEXT NULL,
                    "PendingUpdatedByUserId" TEXT NULL
                );
                CREATE TABLE IF NOT EXISTS "MicrosoftLoginConfigurations" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_MicrosoftLoginConfigurations" PRIMARY KEY,
                    "Enabled" INTEGER NOT NULL DEFAULT 0,
                    "CanonicalOrigin" TEXT NULL,
                    "ConfigurationRevision" INTEGER NOT NULL DEFAULT 0,
                    "LastCheckAt" TEXT NULL,
                    "LastCheckSucceeded" INTEGER NULL,
                    "LastError" TEXT NULL,
                    "UpdatedAt" TEXT NOT NULL DEFAULT '0001-01-01T00:00:00.0000000+00:00',
                    "UpdatedByUserId" TEXT NULL
                );
                CREATE TABLE IF NOT EXISTS "MicrosoftAccountLinkIntents" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_MicrosoftAccountLinkIntents" PRIMARY KEY,
                    "UserId" TEXT NOT NULL,
                    "ProtectedState" TEXT NOT NULL,
                    "SecurityStampHash" TEXT NOT NULL,
                    "BrowserBindingHash" TEXT NOT NULL,
                    "CredentialRevision" INTEGER NOT NULL,
                    "CreatedAt" TEXT NOT NULL,
                    "ExpiresAt" TEXT NOT NULL,
                    "UsedAt" TEXT NULL,
                    CONSTRAINT "FK_MicrosoftAccountLinkIntents_AspNetUsers_UserId"
                        FOREIGN KEY ("UserId") REFERENCES "AspNetUsers" ("Id") ON DELETE CASCADE
                );
                CREATE INDEX IF NOT EXISTS "IX_MicrosoftAccountLinkIntents_UserId_ExpiresAt"
                    ON "MicrosoftAccountLinkIntents" ("UserId", "ExpiresAt");
                """, cancellationToken);

            foreach (var column in new[]
            {
                ("MicrosoftSubject", "TEXT NULL"), ("MicrosoftTenantId", "TEXT NULL"),
                ("MicrosoftLinkedAt", "TEXT NULL"), ("MicrosoftLastVerifiedAt", "TEXT NULL"),
                ("MicrosoftLinkRevoked", "INTEGER NOT NULL DEFAULT 0"),
                ("MicrosoftCredentialRevision", "INTEGER NULL"),
                ("MicrosoftAccountDisplayName", "TEXT NULL"), ("MicrosoftAccountEmail", "TEXT NULL"),
                ("MicrosoftLinkVersion", "INTEGER NOT NULL DEFAULT 0")
            })
            {
                if (!await HasColumnAsync(db, "AspNetUsers", column.Item1, cancellationToken))
                    await ExecuteAsync(db, $"ALTER TABLE \"AspNetUsers\" ADD COLUMN \"{column.Item1}\" {column.Item2};", cancellationToken);
            }
            await ExecuteAsync(db, "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_AspNetUsers_MicrosoftTenantId_MicrosoftSubject\" ON \"AspNetUsers\" (\"MicrosoftTenantId\", \"MicrosoftSubject\") WHERE \"MicrosoftTenantId\" IS NOT NULL AND \"MicrosoftSubject\" IS NOT NULL;", cancellationToken);

            foreach (var column in MicrosoftCredentialColumns)
            {
                var definition = column switch
                {
                    "CredentialRevision" or "PendingCredentialRevision" => "INTEGER NULL",
                    "UpdatedAt" => "TEXT NOT NULL DEFAULT '0001-01-01T00:00:00.0000000+00:00'",
                    "LegacyMigrationAttempted" or "LegacyMigrationCompleted" => "INTEGER NOT NULL DEFAULT 0",
                    _ => "TEXT NULL"
                };
                if (!await HasColumnAsync(db, "MicrosoftIntegrationConfigurations", column, cancellationToken))
                    await ExecuteAsync(db, $"ALTER TABLE \"MicrosoftIntegrationConfigurations\" ADD COLUMN \"{column}\" {definition};", cancellationToken);
            }
            if (!await HasColumnAsync(db, "SharePointConfigurations", "UseSharedMicrosoftCredentials", cancellationToken))
                await ExecuteAsync(db, "ALTER TABLE \"SharePointConfigurations\" ADD COLUMN \"UseSharedMicrosoftCredentials\" INTEGER NOT NULL DEFAULT 0;", cancellationToken);

            await ExecuteAsync(db, """
                CREATE TABLE IF NOT EXISTS "AdminUserAuditLogs" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_AdminUserAuditLogs" PRIMARY KEY AUTOINCREMENT,
                    "ActorUserId" TEXT NOT NULL,
                    "TargetUserId" TEXT NOT NULL,
                    "Action" INTEGER NOT NULL,
                    "SummaryJson" TEXT NULL,
                    "CreatedAt" TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS "IX_AdminUserAuditLogs_TargetUserId_CreatedAt"
                    ON "AdminUserAuditLogs" ("TargetUserId", "CreatedAt");
                CREATE INDEX IF NOT EXISTS "IX_AdminUserAuditLogs_ActorUserId_CreatedAt"
                    ON "AdminUserAuditLogs" ("ActorUserId", "CreatedAt");
                CREATE TABLE IF NOT EXISTS "SharePointNotificationOutbox" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_SharePointNotificationOutbox" PRIMARY KEY AUTOINCREMENT,
                    "EventId" TEXT NOT NULL,
                    "EventType" INTEGER NOT NULL,
                    "RecipientEmail" TEXT NOT NULL,
                    "RecipientName" TEXT NOT NULL,
                    "ActorName" TEXT NOT NULL,
                    "Title" TEXT NOT NULL,
                    "Description" TEXT NOT NULL,
                    "TransactionId" INTEGER NOT NULL,
                    "TransactionNumber" TEXT NOT NULL,
                    "MerchantName" TEXT NOT NULL,
                    "ParticipantId" INTEGER NOT NULL,
                    "ApprovalId" INTEGER NULL,
                    "Amount" TEXT NOT NULL,
                    "Status" INTEGER NOT NULL,
                    "AttemptCount" INTEGER NOT NULL,
                    "NextAttemptAt" TEXT NOT NULL,
                    "LockedUntil" TEXT NULL,
                    "CreatedAt" TEXT NOT NULL,
                    "SentAt" TEXT NULL,
                    "LastErrorCode" TEXT NULL
                );
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_SharePointNotificationOutbox_EventId"
                    ON "SharePointNotificationOutbox" ("EventId");
                CREATE INDEX IF NOT EXISTS "IX_SharePointNotificationOutbox_Status_NextAttemptAt"
                    ON "SharePointNotificationOutbox" ("Status", "NextAttemptAt");
                CREATE TABLE IF NOT EXISTS "FoodPickupConfigurations" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_FoodPickupConfigurations" PRIMARY KEY,
                    "Enabled" INTEGER NOT NULL,
                    "Strategy" INTEGER NOT NULL DEFAULT 0,
                    "UpdatedAt" TEXT NOT NULL,
                    "UpdatedByUserId" TEXT NULL
                );
                CREATE TABLE IF NOT EXISTS "FoodPickupEligibleUsers" (
                    "UserId" TEXT NOT NULL CONSTRAINT "PK_FoodPickupEligibleUsers" PRIMARY KEY,
                    "EnabledAt" TEXT NOT NULL,
                    "EnabledByUserId" TEXT NOT NULL,
                    CONSTRAINT "FK_FoodPickupEligibleUsers_AspNetUsers_UserId"
                        FOREIGN KEY ("UserId") REFERENCES "AspNetUsers" ("Id") ON DELETE CASCADE
                );
                CREATE TABLE IF NOT EXISTS "FoodPickupAssignments" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_FoodPickupAssignments" PRIMARY KEY AUTOINCREMENT,
                    "TransactionId" INTEGER NOT NULL,
                    "SelectedUserId" TEXT NOT NULL,
                    "SelectedParticipantId" INTEGER NOT NULL,
                    "RecordedProbability" TEXT NOT NULL,
                    "Strategy" INTEGER NOT NULL DEFAULT 0,
                    "DrawKind" INTEGER NOT NULL,
                    "SelectedAt" TEXT NOT NULL,
                    "SelectedByUserId" TEXT NULL,
                    CONSTRAINT "FK_FoodPickupAssignments_Transactions_TransactionId"
                        FOREIGN KEY ("TransactionId") REFERENCES "Transactions" ("Id") ON DELETE CASCADE,
                    CONSTRAINT "FK_FoodPickupAssignments_AspNetUsers_SelectedUserId"
                        FOREIGN KEY ("SelectedUserId") REFERENCES "AspNetUsers" ("Id") ON DELETE RESTRICT,
                    CONSTRAINT "FK_FoodPickupAssignments_TransactionParticipants_SelectedParticipantId"
                        FOREIGN KEY ("SelectedParticipantId") REFERENCES "TransactionParticipants" ("Id") ON DELETE CASCADE
                );
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_FoodPickupAssignments_TransactionId"
                    ON "FoodPickupAssignments" ("TransactionId");
                CREATE INDEX IF NOT EXISTS "IX_FoodPickupAssignments_SelectedUserId"
                    ON "FoodPickupAssignments" ("SelectedUserId");
                CREATE TABLE IF NOT EXISTS "FoodPickupDrawHistories" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_FoodPickupDrawHistories" PRIMARY KEY AUTOINCREMENT,
                    "TransactionId" INTEGER NOT NULL,
                    "SequenceNumber" INTEGER NOT NULL,
                    "PreviousSelectedUserId" TEXT NULL,
                    "SelectedUserId" TEXT NOT NULL,
                    "SelectedParticipantId" INTEGER NOT NULL,
                    "Strategy" INTEGER NOT NULL DEFAULT 0,
                    "DrawKind" INTEGER NOT NULL,
                    "Reason" TEXT NULL,
                    "CandidateSnapshotJson" TEXT NOT NULL,
                    "CreatedAt" TEXT NOT NULL,
                    "CreatedByUserId" TEXT NULL,
                    CONSTRAINT "FK_FoodPickupDrawHistories_Transactions_TransactionId"
                        FOREIGN KEY ("TransactionId") REFERENCES "Transactions" ("Id") ON DELETE CASCADE
                );
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_FoodPickupDrawHistories_TransactionId_SequenceNumber"
                    ON "FoodPickupDrawHistories" ("TransactionId", "SequenceNumber");
                CREATE INDEX IF NOT EXISTS "IX_FoodPickupDrawHistories_SelectedUserId"
                    ON "FoodPickupDrawHistories" ("SelectedUserId");
                """, cancellationToken);

            await ExecuteAsync(db, """
                CREATE TABLE IF NOT EXISTS "ParticipantAccountLinks" (
                    "ParticipantId" INTEGER NOT NULL CONSTRAINT "PK_ParticipantAccountLinks" PRIMARY KEY,
                    "UserId" TEXT NOT NULL,
                    "LinkedAt" TEXT NOT NULL,
                    CONSTRAINT "FK_ParticipantAccountLinks_TransactionParticipants_ParticipantId"
                        FOREIGN KEY ("ParticipantId") REFERENCES "TransactionParticipants" ("Id") ON DELETE CASCADE,
                    CONSTRAINT "FK_ParticipantAccountLinks_AspNetUsers_UserId"
                        FOREIGN KEY ("UserId") REFERENCES "AspNetUsers" ("Id") ON DELETE RESTRICT
                );
                CREATE INDEX IF NOT EXISTS "IX_ParticipantAccountLinks_UserId"
                    ON "ParticipantAccountLinks" ("UserId");
                CREATE TABLE IF NOT EXISTS "PaymentApprovals" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_PaymentApprovals" PRIMARY KEY AUTOINCREMENT,
                    "ParticipantId" INTEGER NOT NULL,
                    "RequestedByUserId" TEXT NOT NULL,
                    "RequestedAt" TEXT NOT NULL,
                    "Status" INTEGER NOT NULL,
                    "ResolvedByUserId" TEXT NULL,
                    "ResolvedAt" TEXT NULL,
                    "Note" TEXT NULL,
                    CONSTRAINT "FK_PaymentApprovals_TransactionParticipants_ParticipantId"
                        FOREIGN KEY ("ParticipantId") REFERENCES "TransactionParticipants" ("Id") ON DELETE CASCADE
                );
                CREATE INDEX IF NOT EXISTS "IX_PaymentApprovals_ParticipantId_Status"
                    ON "PaymentApprovals" ("ParticipantId", "Status");
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_PaymentApprovals_ParticipantId_Pending"
                    ON "PaymentApprovals" ("ParticipantId") WHERE "Status" = 0;
                CREATE TABLE IF NOT EXISTS "UserNotifications" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_UserNotifications" PRIMARY KEY AUTOINCREMENT,
                    "UserId" TEXT NOT NULL,
                    "ParticipantId" INTEGER NULL,
                    "Type" INTEGER NOT NULL,
                    "IsRead" INTEGER NOT NULL,
                    "CreatedAt" TEXT NOT NULL,
                    CONSTRAINT "FK_UserNotifications_AspNetUsers_UserId"
                        FOREIGN KEY ("UserId") REFERENCES "AspNetUsers" ("Id") ON DELETE RESTRICT,
                    CONSTRAINT "FK_UserNotifications_TransactionParticipants_ParticipantId"
                        FOREIGN KEY ("ParticipantId") REFERENCES "TransactionParticipants" ("Id") ON DELETE CASCADE
                );
                CREATE INDEX IF NOT EXISTS "IX_UserNotifications_UserId_IsRead_CreatedAt"
                    ON "UserNotifications" ("UserId", "IsRead", "CreatedAt");
                """, cancellationToken);

            if (!await HasColumnAsync(db, "PaymentHistories", "ActionType", cancellationToken))
                await ExecuteAsync(db, "ALTER TABLE \"PaymentHistories\" ADD COLUMN \"ActionType\" INTEGER NOT NULL DEFAULT 0;", cancellationToken);
            if (!await HasColumnAsync(db, "FoodPickupConfigurations", "Strategy", cancellationToken))
                await ExecuteAsync(db, "ALTER TABLE \"FoodPickupConfigurations\" ADD COLUMN \"Strategy\" INTEGER NOT NULL DEFAULT 0;", cancellationToken);
            if (!await HasColumnAsync(db, "FoodPickupAssignments", "Strategy", cancellationToken))
                await ExecuteAsync(db, "ALTER TABLE \"FoodPickupAssignments\" ADD COLUMN \"Strategy\" INTEGER NOT NULL DEFAULT 0;", cancellationToken);
            if (!await HasColumnAsync(db, "FoodPickupDrawHistories", "Strategy", cancellationToken))
                await ExecuteAsync(db, "ALTER TABLE \"FoodPickupDrawHistories\" ADD COLUMN \"Strategy\" INTEGER NOT NULL DEFAULT 0;", cancellationToken);
            if (!await HasColumnAsync(db, "PaymentHistories", "Note", cancellationToken))
                await ExecuteAsync(db, "ALTER TABLE \"PaymentHistories\" ADD COLUMN \"Note\" TEXT NULL;", cancellationToken);
            if (!await HasColumnAsync(db, "Transactions", "AllocationPlanJson", cancellationToken))
                await ExecuteAsync(db, "ALTER TABLE \"Transactions\" ADD COLUMN \"AllocationPlanJson\" TEXT NULL;", cancellationToken);
            if (!await HasColumnAsync(db, "Transactions", "NotificationBaseUrl", cancellationToken))
                await ExecuteAsync(db, "ALTER TABLE \"Transactions\" ADD COLUMN \"NotificationBaseUrl\" TEXT NULL;", cancellationToken);
            if (!await HasColumnAsync(db, "Transactions", "RequiresFoodPickup", cancellationToken))
                await ExecuteAsync(db, "ALTER TABLE \"Transactions\" ADD COLUMN \"RequiresFoodPickup\" INTEGER NOT NULL DEFAULT 0;", cancellationToken);
            if (!await HasColumnAsync(db, "Transactions", "CurrencyCode", cancellationToken))
                await ExecuteAsync(db, "ALTER TABLE \"Transactions\" ADD COLUMN \"CurrencyCode\" TEXT NOT NULL DEFAULT 'IDR';", cancellationToken);
            if (!await HasColumnAsync(db, "Transactions", "ReportingCurrencyCode", cancellationToken))
                await ExecuteAsync(db, "ALTER TABLE \"Transactions\" ADD COLUMN \"ReportingCurrencyCode\" TEXT NOT NULL DEFAULT 'IDR';", cancellationToken);
            if (!await HasColumnAsync(db, "Transactions", "ExchangeRateToReporting", cancellationToken))
                await ExecuteAsync(db, "ALTER TABLE \"Transactions\" ADD COLUMN \"ExchangeRateToReporting\" TEXT NOT NULL DEFAULT '1';", cancellationToken);
            if (!await HasColumnAsync(db, "Transactions", "ExchangeRateEffectiveDate", cancellationToken))
                await ExecuteAsync(db, "ALTER TABLE \"Transactions\" ADD COLUMN \"ExchangeRateEffectiveDate\" TEXT NULL;", cancellationToken);
            if (!await HasColumnAsync(db, "Transactions", "ExchangeRateCapturedAt", cancellationToken))
                await ExecuteAsync(db, "ALTER TABLE \"Transactions\" ADD COLUMN \"ExchangeRateCapturedAt\" TEXT NULL;", cancellationToken);
            if (!await HasColumnAsync(db, "Transactions", "ExchangeRateSource", cancellationToken))
                await ExecuteAsync(db, "ALTER TABLE \"Transactions\" ADD COLUMN \"ExchangeRateSource\" TEXT NOT NULL DEFAULT 'LegacyIdentity';", cancellationToken);
            if (!await HasColumnAsync(db, "Transactions", "ExchangeRateCaptureMode", cancellationToken))
                await ExecuteAsync(db, "ALTER TABLE \"Transactions\" ADD COLUMN \"ExchangeRateCaptureMode\" INTEGER NOT NULL DEFAULT 3;", cancellationToken);
            if (!await HasColumnAsync(db, "Transactions", "ExchangeRateManualNote", cancellationToken))
                await ExecuteAsync(db, "ALTER TABLE \"Transactions\" ADD COLUMN \"ExchangeRateManualNote\" TEXT NULL;", cancellationToken);
            await ExecuteAsync(db, "UPDATE \"Transactions\" SET \"CurrencyCode\"='IDR' WHERE \"CurrencyCode\" IS NULL OR \"CurrencyCode\"='';", cancellationToken);
            await ExecuteAsync(db, "UPDATE \"Transactions\" SET \"ReportingCurrencyCode\"='IDR' WHERE \"ReportingCurrencyCode\" IS NULL OR \"ReportingCurrencyCode\"='';", cancellationToken);
            await ExecuteAsync(db, "UPDATE \"Transactions\" SET \"ExchangeRateToReporting\"='1' WHERE \"ExchangeRateToReporting\" IS NULL OR \"ExchangeRateToReporting\"='';", cancellationToken);
            // Existing transactions that already have a winner are historical
            // pickup transactions. Preserve that behavior when adding the flag.
            await ExecuteAsync(db, """
                UPDATE "Transactions"
                SET "RequiresFoodPickup" = 1
                WHERE "RequiresFoodPickup" = 0
                  AND EXISTS (
                      SELECT 1 FROM "FoodPickupAssignments" a
                      WHERE a."TransactionId" = "Transactions"."Id"
                  );
                """, cancellationToken);
            if (!await HasColumnAsync(db, "PaymentApprovals", "ProofFileName", cancellationToken))
                await ExecuteAsync(db, "ALTER TABLE \"PaymentApprovals\" ADD COLUMN \"ProofFileName\" TEXT NULL;", cancellationToken);
            if (!await HasColumnAsync(db, "PaymentApprovals", "ProofOriginalFileName", cancellationToken))
                await ExecuteAsync(db, "ALTER TABLE \"PaymentApprovals\" ADD COLUMN \"ProofOriginalFileName\" TEXT NULL;", cancellationToken);
            if (!await HasColumnAsync(db, "PaymentApprovals", "ProofContentType", cancellationToken))
                await ExecuteAsync(db, "ALTER TABLE \"PaymentApprovals\" ADD COLUMN \"ProofContentType\" TEXT NULL;", cancellationToken);
            if (!await HasColumnAsync(db, "UserNotifications", "PaymentApprovalId", cancellationToken))
                await ExecuteAsync(db, "ALTER TABLE \"UserNotifications\" ADD COLUMN \"PaymentApprovalId\" INTEGER NULL;", cancellationToken);
            await ExecuteAsync(db, "CREATE INDEX IF NOT EXISTS \"IX_UserNotifications_PaymentApprovalId\" ON \"UserNotifications\" (\"PaymentApprovalId\");", cancellationToken);

            await ExecuteAsync(db, """
                CREATE TABLE IF NOT EXISTS "WebPushConfigurations" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_WebPushConfigurations" PRIMARY KEY,
                    "Enabled" INTEGER NOT NULL,
                    "Subject" TEXT NOT NULL,
                    "PublicKey" TEXT NOT NULL,
                    "ProtectedPrivateKey" TEXT NOT NULL,
                    "CreatedAt" TEXT NOT NULL,
                    "UpdatedAt" TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS "WebPushSubscriptions" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_WebPushSubscriptions" PRIMARY KEY AUTOINCREMENT,
                    "UserId" TEXT NOT NULL,
                    "EndpointHash" TEXT NOT NULL,
                    "ProtectedEndpoint" TEXT NOT NULL,
                    "ProtectedP256dh" TEXT NOT NULL,
                    "ProtectedAuth" TEXT NOT NULL,
                    "InstallationIdHash" TEXT NOT NULL,
                    "Culture" TEXT NOT NULL,
                    "BrowserLabel" TEXT NULL,
                    "CreatedAt" TEXT NOT NULL,
                    "LastSeenAt" TEXT NOT NULL,
                    "ExpiresAt" TEXT NOT NULL,
                    "DisabledAt" TEXT NULL,
                    "LastFailureAt" TEXT NULL,
                    CONSTRAINT "FK_WebPushSubscriptions_AspNetUsers_UserId"
                        FOREIGN KEY ("UserId") REFERENCES "AspNetUsers" ("Id") ON DELETE CASCADE
                );
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_WebPushSubscriptions_EndpointHash"
                    ON "WebPushSubscriptions" ("EndpointHash");
                CREATE INDEX IF NOT EXISTS "IX_WebPushSubscriptions_UserId_DisabledAt_ExpiresAt"
                    ON "WebPushSubscriptions" ("UserId", "DisabledAt", "ExpiresAt");
                CREATE INDEX IF NOT EXISTS "IX_WebPushSubscriptions_UserId_InstallationIdHash"
                    ON "WebPushSubscriptions" ("UserId", "InstallationIdHash");
                CREATE TABLE IF NOT EXISTS "WebPushDeliveries" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_WebPushDeliveries" PRIMARY KEY AUTOINCREMENT,
                    "UserNotificationId" INTEGER NOT NULL,
                    "WebPushSubscriptionId" INTEGER NOT NULL,
                    "Status" INTEGER NOT NULL,
                    "AttemptCount" INTEGER NOT NULL,
                    "NextAttemptAt" TEXT NOT NULL,
                    "LockedUntil" TEXT NULL,
                    "CreatedAt" TEXT NOT NULL,
                    "SentAt" TEXT NULL,
                    "LastErrorCode" TEXT NULL,
                    CONSTRAINT "FK_WebPushDeliveries_UserNotifications_UserNotificationId"
                        FOREIGN KEY ("UserNotificationId") REFERENCES "UserNotifications" ("Id") ON DELETE CASCADE,
                    CONSTRAINT "FK_WebPushDeliveries_WebPushSubscriptions_WebPushSubscriptionId"
                        FOREIGN KEY ("WebPushSubscriptionId") REFERENCES "WebPushSubscriptions" ("Id") ON DELETE CASCADE
                );
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_WebPushDeliveries_UserNotificationId_WebPushSubscriptionId"
                    ON "WebPushDeliveries" ("UserNotificationId", "WebPushSubscriptionId");
                CREATE INDEX IF NOT EXISTS "IX_WebPushDeliveries_Status_NextAttemptAt"
                    ON "WebPushDeliveries" ("Status", "NextAttemptAt");
                """, cancellationToken);

            await ExecuteAsync(db, "PRAGMA foreign_key_check;", cancellationToken);
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private static async Task ExecuteAsync(ApplicationDbContext db, string sql, CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlRawAsync(sql, cancellationToken);
    }

    private static async Task<bool> HasColumnAsync(ApplicationDbContext db, string table, string column, CancellationToken cancellationToken)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{table.Replace("\"", "\"\"")}\");";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}

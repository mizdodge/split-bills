using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Splitbill.Models;

namespace Splitbill.Data;

public sealed class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
    : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<AiConfiguration> AiConfigurations => Set<AiConfiguration>();
    public DbSet<MicrosoftIntegrationConfiguration> MicrosoftIntegrationConfigurations => Set<MicrosoftIntegrationConfiguration>();
    public DbSet<MicrosoftLoginConfiguration> MicrosoftLoginConfigurations => Set<MicrosoftLoginConfiguration>();
    public DbSet<MicrosoftAccountLinkIntent> MicrosoftAccountLinkIntents => Set<MicrosoftAccountLinkIntent>();
    public DbSet<SharePointConfiguration> SharePointConfigurations => Set<SharePointConfiguration>();
    public DbSet<SharePointNotificationOutbox> SharePointNotificationOutbox => Set<SharePointNotificationOutbox>();
    public DbSet<FoodPickupConfiguration> FoodPickupConfigurations => Set<FoodPickupConfiguration>();
    public DbSet<FoodPickupEligibleUser> FoodPickupEligibleUsers => Set<FoodPickupEligibleUser>();
    public DbSet<FoodPickupAssignment> FoodPickupAssignments => Set<FoodPickupAssignment>();
    public DbSet<FoodPickupDrawHistory> FoodPickupDrawHistories => Set<FoodPickupDrawHistory>();
    public DbSet<AdminUserAuditLog> AdminUserAuditLogs => Set<AdminUserAuditLog>();
    public DbSet<BillTransaction> Transactions => Set<BillTransaction>();
    public DbSet<TransactionItem> TransactionItems => Set<TransactionItem>();
    public DbSet<TransactionReceiptImage> TransactionReceiptImages => Set<TransactionReceiptImage>();
    public DbSet<TransactionCharge> TransactionCharges => Set<TransactionCharge>();
    public DbSet<TransactionParticipant> TransactionParticipants => Set<TransactionParticipant>();
    public DbSet<ParticipantItemAllocation> ParticipantItemAllocations => Set<ParticipantItemAllocation>();
    public DbSet<PaymentHistory> PaymentHistories => Set<PaymentHistory>();
    public DbSet<ParticipantAccountLink> ParticipantAccountLinks => Set<ParticipantAccountLink>();
    public DbSet<PaymentApproval> PaymentApprovals => Set<PaymentApproval>();
    public DbSet<UserNotification> UserNotifications => Set<UserNotification>();
    public DbSet<ReceiptProcessingLog> ReceiptProcessingLogs => Set<ReceiptProcessingLog>();
    public DbSet<WebPushConfiguration> WebPushConfigurations => Set<WebPushConfiguration>();
    public DbSet<WebPushSubscription> WebPushSubscriptions => Set<WebPushSubscription>();
    public DbSet<WebPushDelivery> WebPushDeliveries => Set<WebPushDelivery>();
    public DbSet<InstallationState> InstallationStates => Set<InstallationState>();
    public DbSet<CurrencyConfiguration> CurrencyConfigurations => Set<CurrencyConfiguration>();
    public DbSet<CurrencyExchangeRate> CurrencyExchangeRates => Set<CurrencyExchangeRate>();
    public DbSet<GuestAccessLink> GuestAccessLinks => Set<GuestAccessLink>();
    public DbSet<GuestTransactionAccessLink> GuestTransactionAccessLinks => Set<GuestTransactionAccessLink>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<BillTransaction>()
            .HasIndex(x => x.TransactionNumber)
            .IsUnique();

        builder.Entity<BillTransaction>()
            .HasOne(x => x.UploadedByUser)
            .WithMany()
            .HasForeignKey(x => x.UploadedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Entity<MicrosoftIntegrationConfiguration>().HasKey(x => x.Id);
        builder.Entity<MicrosoftIntegrationConfiguration>().Property(x => x.Id).ValueGeneratedNever();
        builder.Entity<MicrosoftLoginConfiguration>().HasKey(x => x.Id);
        builder.Entity<MicrosoftLoginConfiguration>().Property(x => x.Id).ValueGeneratedNever();
        builder.Entity<MicrosoftAccountLinkIntent>().HasKey(x => x.Id);
        builder.Entity<MicrosoftAccountLinkIntent>()
            .HasOne(x => x.User)
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Entity<MicrosoftAccountLinkIntent>()
            .HasIndex(x => new { x.UserId, x.ExpiresAt });

        builder.Entity<SharePointConfiguration>()
            .HasKey(x => x.Id);
        builder.Entity<SharePointConfiguration>()
            .Property(x => x.UseSharedMicrosoftCredentials).HasDefaultValue(false);

        builder.Entity<InstallationState>()
            .HasKey(x => x.Id);

        builder.Entity<CurrencyConfiguration>().HasKey(x => x.Id);
        builder.Entity<CurrencyExchangeRate>()
            .HasIndex(x => new { x.BaseCurrencyCode, x.QuoteCurrencyCode, x.EffectiveDate, x.ProviderBaseUrlFingerprint })
            .IsUnique();
        builder.Entity<GuestAccessLink>()
            .HasIndex(x => x.TokenHash).IsUnique();
        builder.Entity<GuestAccessLink>()
            .HasOne(x => x.Participant)
            .WithMany(x => x.GuestAccessLinks)
            .HasForeignKey(x => x.ParticipantId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Entity<GuestTransactionAccessLink>()
            .HasIndex(x => x.TokenHash).IsUnique();
        builder.Entity<GuestTransactionAccessLink>()
            .HasOne(x => x.Transaction)
            .WithMany(x => x.GuestTransactionAccessLinks)
            .HasForeignKey(x => x.TransactionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<SharePointNotificationOutbox>()
            .HasIndex(x => x.EventId)
            .IsUnique();

        builder.Entity<SharePointNotificationOutbox>()
            .HasIndex(x => new { x.Status, x.NextAttemptAt });

        builder.Entity<FoodPickupConfiguration>()
            .HasKey(x => x.Id);

        builder.Entity<FoodPickupEligibleUser>()
            .HasKey(x => x.UserId);

        builder.Entity<FoodPickupEligibleUser>()
            .HasOne(x => x.User)
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<FoodPickupAssignment>()
            .HasIndex(x => x.TransactionId)
            .IsUnique();

        builder.Entity<FoodPickupAssignment>()
            .HasOne(x => x.Transaction)
            .WithOne(x => x.PickupAssignment)
            .HasForeignKey<FoodPickupAssignment>(x => x.TransactionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<FoodPickupAssignment>()
            .HasOne(x => x.SelectedUser)
            .WithMany()
            .HasForeignKey(x => x.SelectedUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Entity<FoodPickupAssignment>()
            .HasOne(x => x.SelectedParticipant)
            .WithMany()
            .HasForeignKey(x => x.SelectedParticipantId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<FoodPickupDrawHistory>()
            .HasIndex(x => new { x.TransactionId, x.SequenceNumber })
            .IsUnique();

        builder.Entity<FoodPickupDrawHistory>()
            .HasOne(x => x.Transaction)
            .WithMany(x => x.PickupDrawHistories)
            .HasForeignKey(x => x.TransactionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<AdminUserAuditLog>()
            .HasIndex(x => new { x.TargetUserId, x.CreatedAt });

        builder.Entity<AdminUserAuditLog>()
            .HasIndex(x => new { x.ActorUserId, x.CreatedAt });

        builder.Entity<TransactionCharge>()
            .HasOne(x => x.Transaction)
            .WithMany(x => x.Charges)
            .HasForeignKey(x => x.TransactionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<TransactionReceiptImage>()
            .HasOne(x => x.Transaction)
            .WithMany(x => x.ReceiptImages)
            .HasForeignKey(x => x.TransactionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<ParticipantItemAllocation>()
            .HasOne(x => x.Participant)
            .WithMany(x => x.ItemAllocations)
            .HasForeignKey(x => x.ParticipantId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<ParticipantItemAllocation>()
            .HasOne(x => x.Item)
            .WithMany(x => x.ParticipantAllocations)
            .HasForeignKey(x => x.TransactionItemId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<PaymentHistory>()
            .HasOne(x => x.Participant)
            .WithMany(x => x.PaymentHistories)
            .HasForeignKey(x => x.ParticipantId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<ParticipantAccountLink>()
            .HasKey(x => x.ParticipantId);

        builder.Entity<ParticipantAccountLink>()
            .HasOne(x => x.Participant)
            .WithOne(x => x.AccountLink)
            .HasForeignKey<ParticipantAccountLink>(x => x.ParticipantId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<ParticipantAccountLink>()
            .HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Entity<PaymentApproval>()
            .HasOne(x => x.Participant)
            .WithMany(x => x.PaymentApprovals)
            .HasForeignKey(x => x.ParticipantId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<PaymentApproval>()
            .HasIndex(x => new { x.ParticipantId, x.Status });

        builder.Entity<UserNotification>()
            .HasOne(x => x.PaymentApproval)
            .WithMany()
            .HasForeignKey(x => x.PaymentApprovalId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.Entity<UserNotification>()
            .HasIndex(x => x.PaymentApprovalId);

        builder.Entity<UserNotification>()
            .HasOne(x => x.Participant)
            .WithMany()
            .HasForeignKey(x => x.ParticipantId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<UserNotification>()
            .HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Entity<UserNotification>()
            .HasIndex(x => new { x.UserId, x.IsRead, x.CreatedAt });

        builder.Entity<WebPushConfiguration>()
            .HasKey(x => x.Id);

        builder.Entity<WebPushSubscription>()
            .HasOne(x => x.User)
            .WithMany(x => x.WebPushSubscriptions)
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<WebPushSubscription>()
            .HasIndex(x => x.EndpointHash)
            .IsUnique();

        builder.Entity<WebPushSubscription>()
            .HasIndex(x => new { x.UserId, x.DisabledAt, x.ExpiresAt });

        builder.Entity<WebPushSubscription>()
            .HasIndex(x => new { x.UserId, x.InstallationIdHash });

        builder.Entity<WebPushDelivery>()
            .HasOne(x => x.UserNotification)
            .WithMany(x => x.WebPushDeliveries)
            .HasForeignKey(x => x.UserNotificationId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<WebPushDelivery>()
            .HasOne(x => x.WebPushSubscription)
            .WithMany(x => x.Deliveries)
            .HasForeignKey(x => x.WebPushSubscriptionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<WebPushDelivery>()
            .HasIndex(x => new { x.UserNotificationId, x.WebPushSubscriptionId })
            .IsUnique();

        builder.Entity<WebPushDelivery>()
            .HasIndex(x => new { x.Status, x.NextAttemptAt });

        foreach (var property in builder.Model.GetEntityTypes()
                     .SelectMany(x => x.GetProperties())
                     .Where(x => x.ClrType == typeof(decimal) || x.ClrType == typeof(decimal?)))
        {
            property.SetPrecision(18);
            property.SetScale(2);
        }
        builder.Entity<CurrencyExchangeRate>().Property(x => x.Rate).HasPrecision(28, 12);
        builder.Entity<BillTransaction>().Property(x => x.ExchangeRateToReporting).HasPrecision(28, 12);
    }
}

using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Splitbill.Controllers;
using Splitbill.Data;
using Splitbill.Models;
using Splitbill.Services;
using Splitbill.ViewModels;

namespace Splitbill.Tests;

/// <summary>
/// Focused tests for DashboardController.Index perspective-switching:
/// - Member cannot access operational view regardless of ?view= param
/// - Moderator default → operations (own uploaded only)
/// - Admin default → operations (all transactions)
/// - Moderator with ?view=personal → personal bills linked to their account
/// - Admin with ?view=personal → personal bills linked to their account
/// - Promoted user (Moderator) sees personal bills uploaded by another
/// - Zero-bill personal state shows empty model without crashing
/// - Member with ?view=operations is silently ignored (returns personal)
/// </summary>
public sealed class DashboardPerspectiveTests : IDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:;Foreign Keys=True");
    private readonly ApplicationDbContext db;

    public DashboardPerspectiveTests()
    {
        connection.Open();
        db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection).Options);
        db.Database.EnsureCreated();
    }

    // ── Member always sees personal ───────────────────────────────────────────

    [Fact]
    public async Task Member_DefaultView_IsPersonalDashboard()
    {
        await SeedUser("member1");
        var ctrl = MakeController("member1", DatabaseSeeder.MemberRole);
        var vm = await GetViewModel(ctrl, view: null);
        Assert.True(vm.IsMemberDashboard);
        Assert.Empty(vm.PerspectiveOptions);       // no switcher for plain Members
    }

    [Fact]
    public async Task Member_RequestsOperationsView_IsStillPersonalDashboard()
    {
        await SeedUser("member1");
        var ctrl = MakeController("member1", DatabaseSeeder.MemberRole);
        var vm = await GetViewModel(ctrl, view: "operations");
        Assert.True(vm.IsMemberDashboard);
        Assert.Empty(vm.PerspectiveOptions);
    }

    // ── Moderator defaults ───────────────────────────────────────────────────

    [Fact]
    public async Task Moderator_DefaultView_IsOperationalWithOwnTransactionsOnly()
    {
        await SeedUser("mod1");
        await SeedUser("other");
        await SeedTransaction("mod1", TransactionStatus.Unpaid, 50_000);
        await SeedTransaction("other", TransactionStatus.Unpaid, 99_000); // should NOT appear
        var ctrl = MakeController("mod1", DatabaseSeeder.ModeratorRole);
        var vm = await GetViewModel(ctrl, view: null);
        Assert.False(vm.IsMemberDashboard);
        Assert.Equal(DashboardPerspective.Operations, vm.ActivePerspective);
        Assert.Equal(2, vm.PerspectiveOptions.Count);
        // Only own transaction counted (other's 99_000 transaction is excluded)
        Assert.Equal(1, vm.TotalTransactions);
        Assert.Equal(1, vm.UnpaidTransactions);
        // Options label key identifies Moderator label
        Assert.Equal("PerspectiveMyTransactions", vm.PerspectiveOptions[0].LabelKey);
    }


    // ── Admin defaults ───────────────────────────────────────────────────────

    [Fact]
    public async Task Admin_DefaultView_IsOperationalWithAllTransactions()
    {
        await SeedUser("admin1");
        await SeedUser("mod1");
        await SeedTransaction("admin1", TransactionStatus.Unpaid, 30_000);
        await SeedTransaction("mod1", TransactionStatus.Unpaid, 40_000);
        var ctrl = MakeController("admin1", DatabaseSeeder.AdminRole);
        var vm = await GetViewModel(ctrl, view: null);
        Assert.False(vm.IsMemberDashboard);
        Assert.Equal(DashboardPerspective.Operations, vm.ActivePerspective);
        Assert.Equal(2, vm.TotalTransactions);
        // Operations label key identifies Admin label
        Assert.Equal("PerspectiveAllTransactions", vm.PerspectiveOptions[0].LabelKey);
    }

    // ── Personal perspective for elevated roles ───────────────────────────────

    [Fact]
    public async Task Moderator_PersonalView_ShowsLinkedParticipantBillsOnly()
    {
        await SeedUser("mod1");
        await SeedUser("other");
        // mod1 uploaded both, but only participates in tx1
        var tx1 = await SeedTransaction("mod1", TransactionStatus.Unpaid, 60_000);
        await SeedTransaction("mod1", TransactionStatus.Unpaid, 70_000);
        await SeedParticipantLink(tx1, "mod1", 60_000);
        var ctrl = MakeController("mod1", DatabaseSeeder.ModeratorRole);
        var vm = await GetViewModel(ctrl, view: "personal");
        Assert.True(vm.IsMemberDashboard);
        Assert.Equal(DashboardPerspective.Personal, vm.ActivePerspective);
        Assert.Equal(2, vm.PerspectiveOptions.Count);
        Assert.Equal(60_000m, vm.OutstandingAmount);  // only linked bill
        Assert.Single(vm.RecentMemberBills);
    }

    [Fact]
    public async Task Admin_PersonalView_ShowsLinkedBillsIncludingBillsUploadedByOthers()
    {
        await SeedUser("admin1");
        await SeedUser("mod1");
        // mod1 uploaded a bill but admin1 is a participant
        var tx1 = await SeedTransaction("mod1", TransactionStatus.Paid, 80_000);
        await SeedParticipantLink(tx1, "admin1", 20_000);
        var ctrl = MakeController("admin1", DatabaseSeeder.AdminRole);
        var vm = await GetViewModel(ctrl, view: "personal");
        Assert.True(vm.IsMemberDashboard);
        Assert.Single(vm.RecentMemberBills);
        Assert.Equal(20_000m, vm.MemberPaidAmount);
    }

    // ── Promoted user sees historical bills ───────────────────────────────────

    [Fact]
    public async Task PromotedModerator_PersonalView_SeesPrePromotionBill()
    {
        await SeedUser("promoted1");
        await SeedUser("uploader");
        // Bill created before promotion, uploaded by someone else
        var oldTx = await SeedTransaction("uploader", TransactionStatus.Paid, 45_000);
        await SeedParticipantLink(oldTx, "promoted1", 45_000);
        // Bill where promoted1 is both uploader and participant
        var ownTx = await SeedTransaction("promoted1", TransactionStatus.Unpaid, 30_000);
        await SeedParticipantLink(ownTx, "promoted1", 30_000);
        var ctrl = MakeController("promoted1", DatabaseSeeder.ModeratorRole);
        var vm = await GetViewModel(ctrl, view: "personal");
        Assert.True(vm.IsMemberDashboard);
        Assert.Equal(2, vm.RecentMemberBills.Count);
        Assert.Equal(45_000m, vm.MemberPaidAmount);
        Assert.Equal(30_000m, vm.OutstandingAmount);
    }

    // ── Draft bills excluded from personal view ────────────────────────────────

    [Fact]
    public async Task PersonalView_ExcludesDraftBills()
    {
        await SeedUser("mod1");
        var draftTx = await SeedTransaction("mod1", TransactionStatus.Draft, 99_000);
        await SeedParticipantLink(draftTx, "mod1", 99_000);
        var ctrl = MakeController("mod1", DatabaseSeeder.ModeratorRole);
        var vm = await GetViewModel(ctrl, view: "personal");
        Assert.True(vm.IsMemberDashboard);
        Assert.Empty(vm.RecentMemberBills);
        Assert.Equal(0m, vm.OutstandingAmount);
    }

    // ── Zero bills personal state ────────────────────────────────────────────

    [Fact]
    public async Task Admin_PersonalView_NoBills_ShowsEmptyStateSafely()
    {
        await SeedUser("admin1");
        var ctrl = MakeController("admin1", DatabaseSeeder.AdminRole);
        var vm = await GetViewModel(ctrl, view: "personal");
        Assert.True(vm.IsMemberDashboard);
        Assert.Equal(DashboardPerspective.Personal, vm.ActivePerspective);
        Assert.Empty(vm.RecentMemberBills);
        Assert.Equal(0m, vm.OutstandingAmount);
        Assert.Equal(0m, vm.MemberCurrentMonthAmount);
    }

    // ── Unknown view value falls back to operations ───────────────────────────

    [Fact]
    public async Task Admin_UnknownViewValue_FallsBackToOperations()
    {
        await SeedUser("admin1");
        await SeedTransaction("admin1", TransactionStatus.Unpaid, 10_000);
        var ctrl = MakeController("admin1", DatabaseSeeder.AdminRole);
        var vm = await GetViewModel(ctrl, view: "garbage");
        Assert.False(vm.IsMemberDashboard);
        Assert.Equal(DashboardPerspective.Operations, vm.ActivePerspective);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static async Task<DashboardViewModel> GetViewModel(DashboardController ctrl, string? view)
    {
        var result = Assert.IsType<ViewResult>(await ctrl.Index(view));
        return Assert.IsType<DashboardViewModel>(result.Model);
    }

    private async Task SeedUser(string userId)
    {
        if (!await db.Users.AnyAsync(u => u.Id == userId))
        {
            db.Users.Add(new ApplicationUser { Id = userId, UserName = userId, DisplayName = userId });
            await db.SaveChangesAsync();
        }
        // Ensure CurrencyConfiguration exists (needed by BuildCurrentCurrencyRatesAsync).
        if (!await db.CurrencyConfigurations.AnyAsync(x => x.Id == 1))
        {
            db.CurrencyConfigurations.Add(new CurrencyConfiguration
            {
                Id = 1, DefaultCurrencyCode = "IDR", DashboardCurrencyCodes = "USD",
                DashboardPrimaryCurrencyCode = "USD", UpdatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }
    }

    private async Task<long> SeedTransaction(string uploadedBy, TransactionStatus status, decimal amount)
    {
        var tx = new BillTransaction
        {
            TransactionNumber = $"TRX-{Guid.NewGuid():N}",
            MerchantName = "Test Cafe",
            UploadedByUserId = uploadedBy,
            Status = status,
            GrandTotal = amount,
            UploadDate = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Transactions.Add(tx);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return tx.Id;
    }

    /// <summary>
    /// Creates a <see cref="TransactionParticipant"/> linked to <paramref name="userId"/>
    /// as a participant on the given transaction. Uses the navigation-property pattern so
    /// EF populates ParticipantAccountLink.ParticipantId correctly.
    /// </summary>
    private async Task SeedParticipantLink(long transactionId, string userId, decimal amount)
    {
        var tx = await db.Transactions.FindAsync(transactionId);
        var participant = new TransactionParticipant
        {
            Name = userId,
            Amount = amount,
            PaymentStatus = tx!.Status == TransactionStatus.Paid
                ? ParticipantPaymentStatus.Paid
                : ParticipantPaymentStatus.Unpaid,
            Transaction = tx,
            // Setting AccountLink as a navigation property lets EF set the PK/FK correctly.
            AccountLink = new ParticipantAccountLink { UserId = userId, LinkedAt = DateTimeOffset.UtcNow }
        };
        db.Set<TransactionParticipant>().Add(participant);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }


    private DashboardController MakeController(string userId, string role)
    {
        var users = new UserManager<ApplicationUser>(
            new UserStore<ApplicationUser>(db),
            Options.Create(new IdentityOptions()),
            new PasswordHasher<ApplicationUser>(), [], [],
            new UpperInvariantLookupNormalizer(),
            new IdentityErrorDescriber(), null!,
            NullLogger<UserManager<ApplicationUser>>.Instance);

        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, userId),
                new Claim(ClaimTypes.Name, userId),
                new Claim(ClaimTypes.Role, role)
            }, "Test"))
        };

        var analytics = new DashboardAnalyticsService();
        var localizer = new NoOpStringLocalizer();
        var currencyTrends = new StubCurrencyTrendService();
        // Use the real catalog so DashboardCurrencySelectionService.Normalize never gets an empty list.
        var realCatalog = new CurrencyCatalog();
        var currencySelection = new DashboardCurrencySelectionService(realCatalog);
        var currencyRates = new StubCurrencyRateService();
        var currencyFormatter = new CurrencyFormatter(realCatalog);

        return new DashboardController(db, users, analytics, currencyTrends, currencySelection, currencyRates, realCatalog, currencyFormatter, localizer)
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
    }

    // ── Minimal stubs ─────────────────────────────────────────────────────────

    private sealed class NoOpStringLocalizer : IStringLocalizer<SharedResource>
    {
        public LocalizedString this[string name] => new(name, name);
        public LocalizedString this[string name, params object[] arguments] => new(name, string.Format(name, arguments));
        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => [];
    }

    private sealed class StubCurrencyTrendService : ICurrencyTrendService
    {
        public IReadOnlyList<CurrencyTrendResult> Build(IReadOnlyList<CurrencyExchangeRate> rates,
            IReadOnlyDictionary<string, int> currencyUsage, DateTimeOffset now, int maxCurrencies = 6) => [];
    }

    private sealed class StubCurrencyRateService : ICurrencyRateService
    {
        public Task<CurrencyRateResult?> GetRateAsync(string baseCurrency, string quoteCurrency,
            DateOnly date, CancellationToken cancellationToken = default) => Task.FromResult<CurrencyRateResult?>(null);
        public Task<IReadOnlyList<CurrencyExchangeRate>> RefreshAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CurrencyExchangeRate>>([]);
    }

}

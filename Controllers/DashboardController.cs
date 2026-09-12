using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Splitbill.Data;
using Splitbill.Models;
using Splitbill.Services;
using Splitbill.ViewModels;

namespace Splitbill.Controllers;

[Authorize]
public sealed class DashboardController(ApplicationDbContext db, UserManager<ApplicationUser> userManager,
    IDashboardAnalyticsService analytics, ICurrencyTrendService currencyTrends,
    IDashboardCurrencySelectionService dashboardCurrencySelection, ICurrencyRateService currencyRates,
    ICurrencyCatalog currencyCatalog, ICurrencyFormatter currencyFormatter,
    IStringLocalizer<SharedResource> localizer) : Controller
{
    public async Task<IActionResult> Index()
    {
        var memberOnly = User.IsInRole(DatabaseSeeder.MemberRole) && !User.IsInRole(DatabaseSeeder.ModeratorRole) && !User.IsInRole(DatabaseSeeder.AdminRole);
        if (memberOnly)
        {
            var memberUserId = userManager.GetUserId(User)!;
            var participants = await db.Set<TransactionParticipant>().AsNoTracking()
                .Include(x => x.Transaction)
                .Include(x => x.AccountLink)
                .Where(x => x.AccountLink != null && x.AccountLink.UserId == memberUserId)
                .ToListAsync();
            var memberInsight = analytics.CalculateMember(participants, DateTimeOffset.Now);
            return View(new DashboardViewModel
            {
                IsMemberDashboard = true,
                MemberCurrentMonthAmount = memberInsight.CurrentMonthAmount,
                MemberPreviousMonthAmount = memberInsight.PreviousMonthAmount,
                MemberPaidAmount = memberInsight.PaidAmount,
                OutstandingAmount = memberInsight.OutstandingAmount,
                MemberMonthlySpending = memberInsight.MonthlySpending.ToList(),
                RecentMemberBills = memberInsight.RecentBills.ToList()
            });
        }
        var query = db.Transactions.AsNoTracking()
            .Include(x => x.Participants).ThenInclude(x => x.AccountLink)
            .AsQueryable();
        if (!User.IsInRole(DatabaseSeeder.AdminRole))
        {
            var userId = userManager.GetUserId(User);
            query = query.Where(x => x.UploadedByUserId == userId);
        }
        var transactions = (await query.ToListAsync()).OrderByDescending(x => x.UploadDate).ToList();
        var insight = analytics.Calculate(transactions, DateTimeOffset.Now);
        var reportingCurrency = await db.CurrencyConfigurations.AsNoTracking().Where(x => x.Id == 1).Select(x => x.DefaultCurrencyCode).SingleOrDefaultAsync() ?? "IDR";
        var currencyUsage = transactions.Where(x => !string.IsNullOrWhiteSpace(x.CurrencyCode) && !string.Equals(x.CurrencyCode, reportingCurrency, StringComparison.OrdinalIgnoreCase))
            .GroupBy(x => x.CurrencyCode, StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => x.Count(), StringComparer.OrdinalIgnoreCase);
        var usedCurrencies = currencyUsage.Keys.ToList();
        var rateRows = await db.CurrencyExchangeRates.AsNoTracking().Where(x => x.QuoteCurrencyCode == reportingCurrency && usedCurrencies.Contains(x.BaseCurrencyCode)).ToListAsync();
        var rates = rateRows
            .GroupBy(x => x.BaseCurrencyCode, StringComparer.OrdinalIgnoreCase).Select(g => g.OrderByDescending(x => x.EffectiveDate).First()).OrderBy(x => x.BaseCurrencyCode).Take(12)
            .Select(x => new DashboardRateHistoryViewModel(x.BaseCurrencyCode, x.QuoteCurrencyCode, x.Rate, x.EffectiveDate, x.RetrievedAt < DateTimeOffset.UtcNow.AddDays(-2))).ToList();
        var trends = currencyTrends.Build(rateRows, currencyUsage, DateTimeOffset.UtcNow)
            .Select(x => new DashboardCurrencyTrendViewModel(x.BaseCurrency, x.ReportingCurrency, x.LatestRate, x.StartDate, x.LatestDate,
                x.ChangeFromPreviousPercent, x.IsStale, x.PointCount, x.SvgPoints)).ToList();
        var currentCurrencyRates = await BuildCurrentCurrencyRatesAsync(reportingCurrency, cancellationToken: default);
        return View(new DashboardViewModel
        {
            TotalTransactions = transactions.Count,
            UnpaidTransactions = transactions.Count(x => x.Status == TransactionStatus.Unpaid),
            PartialTransactions = transactions.Count(x => x.Status == TransactionStatus.Partial),
            PaidTransactions = transactions.Count(x => x.Status == TransactionStatus.Paid),
            OutstandingAmount = transactions.SelectMany(x => x.Participants).Where(x => x.PaymentStatus != ParticipantPaymentStatus.Paid).Sum(x => x.Amount),
            RecentTransactions = transactions.Take(6).ToList(),
            TopOutstandingPeople = insight.TopOutstandingPeople.ToList(),
            OldestOutstanding = insight.OldestOutstanding,
            MonthlySpending = insight.MonthlySpending.ToList(),
            TopMerchants = insight.TopMerchants.ToList(),
            AverageSettlementHours = insight.AverageSettlementHours,
            SettledTransactionCount = insight.SettledTransactionCount, RateHistory = rates, CurrencyTrends = trends,
            CurrentCurrencyRates = currentCurrencyRates
        });
    }

    [Authorize(Roles = DatabaseSeeder.AdminRole)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveCurrencyDashboard(DashboardCurrencyPreferencesInput input, CancellationToken cancellationToken)
    {
        var configuration = await db.CurrencyConfigurations.SingleOrDefaultAsync(x => x.Id == 1, cancellationToken);
        if (configuration is null) return NotFound();

        var reportingCurrency = configuration.DefaultCurrencyCode.Trim().ToUpperInvariant();
        var selected = (input.CurrencyCodes ?? [])
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code.Trim().ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var primary = input.PrimaryCurrencyCode?.Trim().ToUpperInvariant() ?? string.Empty;
        var valid = selected.Count is > 0 and <= DashboardCurrencySelectionService.MaximumCurrencies
                    && selected.All(code => code != reportingCurrency && currencyCatalog.TryGet(code, out _))
                    && selected.Contains(primary, StringComparer.OrdinalIgnoreCase);
        if (!valid)
        {
            TempData["Error"] = localizer["DashboardCurrencySelectionInvalid"].Value;
            return RedirectToAction(nameof(Index));
        }

        configuration.DashboardCurrencyCodes = string.Join(',', selected);
        configuration.DashboardPrimaryCurrencyCode = primary;
        configuration.UpdatedAt = DateTimeOffset.UtcNow;
        configuration.UpdatedByUserId = userManager.GetUserId(User);
        await db.SaveChangesAsync(cancellationToken);
        TempData["Success"] = localizer["DashboardCurrencySelectionSaved"].Value;
        return RedirectToAction(nameof(Index));
    }

    private async Task<DashboardCurrentCurrencySectionViewModel> BuildCurrentCurrencyRatesAsync(
        string reportingCurrency, CancellationToken cancellationToken)
    {
        var configuration = await db.CurrencyConfigurations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == 1, cancellationToken)
            ?? new CurrencyConfiguration { Id = 1, DefaultCurrencyCode = reportingCurrency, UpdatedAt = DateTimeOffset.UtcNow };
        var selection = dashboardCurrencySelection.Normalize(
            configuration.DashboardCurrencyCodes, configuration.DashboardPrimaryCurrencyCode, reportingCurrency);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var rows = await db.CurrencyExchangeRates.AsNoTracking()
            .Where(x => x.QuoteCurrencyCode == reportingCurrency && selection.CurrencyCodes.Contains(x.BaseCurrencyCode))
            .ToListAsync(cancellationToken);
        var availableCodes = rows.Select(x => x.BaseCurrencyCode).Distinct(StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var code in selection.CurrencyCodes.Where(code => !availableCodes.Contains(code)))
            await currencyRates.GetRateAsync(code, reportingCurrency, today, cancellationToken);

        if (availableCodes.Count < selection.CurrencyCodes.Count)
        {
            rows = await db.CurrencyExchangeRates.AsNoTracking()
                .Where(x => x.QuoteCurrencyCode == reportingCurrency && selection.CurrencyCodes.Contains(x.BaseCurrencyCode))
                .ToListAsync(cancellationToken);
        }

        var priority = selection.CurrencyCodes
            .Select((code, index) => new { code, score = selection.CurrencyCodes.Count - index })
            .ToDictionary(x => x.code, x => x.score, StringComparer.OrdinalIgnoreCase);
        var trends = currencyTrends.Build(rows, priority, DateTimeOffset.UtcNow, DashboardCurrencySelectionService.MaximumCurrencies)
            .ToDictionary(x => x.BaseCurrency, StringComparer.OrdinalIgnoreCase);
        var displayPrimary = trends.ContainsKey(selection.PrimaryCurrencyCode)
            ? selection.PrimaryCurrencyCode
            : selection.CurrencyCodes.FirstOrDefault(trends.ContainsKey);
        var cards = selection.CurrencyCodes
            .Where(trends.ContainsKey)
            .Select(code =>
            {
                var trend = trends[code];
                currencyCatalog.TryGet(code, out var currency);
                return new DashboardCurrentCurrencyRateViewModel(
                    trend.BaseCurrency, trend.ReportingCurrency, currency?.Name ?? code,
                    currencyFormatter.Format(trend.LatestRate, trend.ReportingCurrency),
                    trend.StartDate, trend.LatestDate, trend.ChangeFromPreviousPercent,
                    trend.IsStale, trend.PointCount, trend.SvgPoints,
                    string.Equals(code, displayPrimary, StringComparison.OrdinalIgnoreCase));
            }).ToList();

        return new DashboardCurrentCurrencySectionViewModel
        {
            ReportingCurrency = reportingCurrency,
            PrimaryCurrencyCode = selection.PrimaryCurrencyCode,
            SelectedCurrencyCodes = selection.CurrencyCodes.ToList(),
            AvailableCurrencies = currencyCatalog.GetAll().Where(x => x.Code != reportingCurrency).ToList(),
            Rates = cards,
            LastUpdatedAt = rows.Count == 0 ? null : rows.Max(x => x.RetrievedAt)
        };
    }

    [Authorize(Roles = DatabaseSeeder.AdminRole + "," + DatabaseSeeder.ModeratorRole)]
    [HttpGet]
    public async Task<IActionResult> RateHistory(int page = 1, CancellationToken cancellationToken = default)
    {
        page = Math.Clamp(page, 1, 10_000);
        const int pageSize = 25;
        var query = db.Transactions.AsNoTracking().AsQueryable();
        if (!User.IsInRole(DatabaseSeeder.AdminRole))
        {
            var userId = userManager.GetUserId(User);
            query = query.Where(x => x.UploadedByUserId == userId);
        }
        var reportingCurrency = await db.CurrencyConfigurations.AsNoTracking()
            .Where(x => x.Id == 1).Select(x => x.DefaultCurrencyCode).SingleOrDefaultAsync(cancellationToken) ?? "IDR";
        var usedCurrencies = await query
            .Where(x => x.Status != TransactionStatus.Draft && x.CurrencyCode != reportingCurrency)
            .Select(x => x.CurrencyCode).Distinct().ToListAsync(cancellationToken);
        var rates = db.CurrencyExchangeRates.AsNoTracking()
            .Where(x => x.QuoteCurrencyCode == reportingCurrency && usedCurrencies.Contains(x.BaseCurrencyCode))
            .OrderByDescending(x => x.EffectiveDate).ThenBy(x => x.BaseCurrencyCode);
        var total = await rates.CountAsync(cancellationToken);
        var rows = await rates.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        return View(new DashboardRateHistoryPageViewModel
        {
            ReportingCurrency = reportingCurrency,
            Page = page,
            PageSize = pageSize,
            TotalCount = total,
            Rows = rows.Select(x => new DashboardRateHistoryViewModel(
                x.BaseCurrencyCode, x.QuoteCurrencyCode, x.Rate, x.EffectiveDate,
                x.RetrievedAt < DateTimeOffset.UtcNow.AddDays(-2))).ToList()
        });
    }
}

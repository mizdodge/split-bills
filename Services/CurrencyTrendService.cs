using System.Globalization;
using Splitbill.Models;

namespace Splitbill.Services;

public sealed record CurrencyTrendResult(
    string BaseCurrency,
    string ReportingCurrency,
    decimal LatestRate,
    DateOnly StartDate,
    DateOnly LatestDate,
    decimal? ChangeFromPreviousPercent,
    bool IsStale,
    int PointCount,
    string SvgPoints);

public interface ICurrencyTrendService
{
    IReadOnlyList<CurrencyTrendResult> Build(
        IReadOnlyList<CurrencyExchangeRate> rates,
        IReadOnlyDictionary<string, int> currencyUsage,
        DateTimeOffset now,
        int maxCurrencies = 6);
}

public sealed class CurrencyTrendService : ICurrencyTrendService
{
    public IReadOnlyList<CurrencyTrendResult> Build(
        IReadOnlyList<CurrencyExchangeRate> rates,
        IReadOnlyDictionary<string, int> currencyUsage,
        DateTimeOffset now,
        int maxCurrencies = 6)
    {
        if (maxCurrencies <= 0) return [];

        return rates
            .Where(rate => rate.Rate > 0m)
            .GroupBy(rate => rate.BaseCurrencyCode, StringComparer.OrdinalIgnoreCase)
            .Select(group => BuildOne(group, now))
            .Where(result => result is not null)
            .Select(result => result!)
            .OrderByDescending(result => currencyUsage.GetValueOrDefault(result.BaseCurrency))
            .ThenBy(result => result.BaseCurrency, StringComparer.OrdinalIgnoreCase)
            .Take(maxCurrencies)
            .ToList();
    }

    private static CurrencyTrendResult? BuildOne(IEnumerable<CurrencyExchangeRate> group, DateTimeOffset now)
    {
        var points = group
            .GroupBy(rate => rate.EffectiveDate)
            .Select(dateGroup => dateGroup.OrderByDescending(rate => rate.RetrievedAt).First())
            .OrderBy(rate => rate.EffectiveDate)
            .TakeLast(7)
            .ToList();
        if (points.Count == 0) return null;

        var latest = points[^1];
        decimal? change = null;
        if (points.Count > 1 && points[^2].Rate > 0m)
            change = Math.Round((latest.Rate - points[^2].Rate) / points[^2].Rate * 100m, 2, MidpointRounding.AwayFromZero);

        return new CurrencyTrendResult(
            latest.BaseCurrencyCode,
            latest.QuoteCurrencyCode,
            latest.Rate,
            points[0].EffectiveDate,
            latest.EffectiveDate,
            change,
            latest.RetrievedAt < now.AddDays(-2),
            points.Count,
            BuildSvgPoints(points));
    }

    private static string BuildSvgPoints(IReadOnlyList<CurrencyExchangeRate> points)
    {
        if (points.Count == 1) return "0,20 100,20";
        var minimum = points.Min(point => point.Rate);
        var maximum = points.Max(point => point.Rate);
        var range = maximum - minimum;
        return string.Join(' ', points.Select((point, index) =>
        {
            var x = index * 100m / (points.Count - 1);
            var y = range == 0m ? 20m : 36m - ((point.Rate - minimum) / range * 32m);
            return $"{x.ToString("0.##", CultureInfo.InvariantCulture)},{y.ToString("0.##", CultureInfo.InvariantCulture)}";
        }));
    }
}

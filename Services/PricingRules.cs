using System;

namespace PremierAPI.Services;

public sealed record PricingQuote(
    string Period,
    int Computers,
    int Slots,
    int Days,
    decimal Gross,
    decimal PeriodDiscount,
    decimal HardwareDiscount,
    decimal Total);

public static class PricingRules
{
    public const int MinComputers = 1;
    public const int MaxComputers = 20;
    public const int MinSlots = 1;
    public const int MaxSlots = 8;
    public const int MinDailyComputers = 3;
    public const int MinDailyDays = 3;
    public const int MaxDailyDays = 6;
    public const int WeeklyDays = 7;
    public const int MonthlyDays = 30;
    public const decimal WeeklyBasePrice = 35M;
    public const decimal DailyWeeklyBasePrice = 40M;
    public const decimal AdditionalSlotPrice = 10M;
    public const decimal AdditionalComputerDiscount = 5M;
    public const int MonthlyWeeks = 4;
    public const decimal MonthlyDiscountRate = 0.25M;
    public const decimal ReferralDiscountRate = 0.05M;
    public const decimal CommercialRoundingThreshold = 50M;

    public static PricingQuote Calculate(string? period, int computers, int slots, int days)
    {
        string normalizedPeriod = (period ?? "").Trim().ToLowerInvariant();
        if (normalizedPeriod is not ("diaria" or "semanal" or "mensal"))
            throw new ArgumentException("Período inválido.");
        if (computers < MinComputers || computers > MaxComputers || slots < MinSlots || slots > MaxSlots)
            throw new ArgumentException("Quantidade de computadores ou slots inválida.");

        if (normalizedPeriod == "diaria")
        {
            if (computers < MinDailyComputers || days < MinDailyDays || days > MaxDailyDays)
                throw new ArgumentException($"O plano diário exige de {MinDailyDays} a {MaxDailyDays} dias e no mínimo {MinDailyComputers} computadores.");
        }
        else
        {
            days = normalizedPeriod == "semanal" ? WeeklyDays : MonthlyDays;
        }

        decimal slotIncrement = (Math.Max(slots, MinSlots) - 1) * AdditionalSlotPrice;
        decimal gross;
        decimal periodDiscount = 0;
        decimal hardwareDiscount = 0;

        if (normalizedPeriod == "diaria")
        {
            gross = ((DailyWeeklyBasePrice + slotIncrement) / WeeklyDays) * days * computers;
        }
        else
        {
            decimal weeklyBase = WeeklyBasePrice + slotIncrement;
            gross = normalizedPeriod == "semanal" ? weeklyBase * computers : weeklyBase * MonthlyWeeks * computers;
            if (normalizedPeriod == "mensal") periodDiscount = gross * MonthlyDiscountRate;
            hardwareDiscount = Math.Max(0, computers - 1) * AdditionalComputerDiscount;
        }

        decimal total = ApplyCommercialRounding(gross - periodDiscount - hardwareDiscount);
        return new PricingQuote(normalizedPeriod, computers, slots, days, gross, periodDiscount, hardwareDiscount, total);
    }

    public static decimal ApplyCommercialRounding(decimal total)
    {
        if (total <= 0) return 0.01M;
        return Math.Round(total > CommercialRoundingThreshold ? Math.Floor(total) : total, 2);
    }
}

using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using Splitbill.Models;
using Splitbill.Services;

namespace Splitbill.ViewModels;

public sealed class UploadReceiptViewModel
{
    public List<IFormFile> ReceiptImages { get; set; } = [];
}

public sealed class ReviewTransactionViewModel
{
    public long Id { get; set; }
    [BindNever, ValidateNever] public string TransactionNumber { get; set; } = string.Empty;
    [BindNever, ValidateNever] public string ReceiptImagePath { get; set; } = string.Empty;
    [BindNever, ValidateNever] public int ReceiptImageCount { get; set; } = 1;
    public string? MerchantName { get; set; }
    [DataType(DataType.Date)] public DateTime? TransactionDate { get; set; }
    public decimal? Subtotal { get; set; }
    [BindNever, ValidateNever] public decimal? Discount { get; set; }
    [BindNever, ValidateNever] public decimal? Tax { get; set; }
    [BindNever, ValidateNever] public decimal? ServiceCharge { get; set; }
    public decimal? GrandTotal { get; set; }
    [BindNever, ValidateNever] public decimal AiConfidence { get; set; }
    [BindNever, ValidateNever] public bool AiNeedsReview { get; set; }
    [BindNever, ValidateNever] public List<string> Warnings { get; set; } = [];
    public List<ReviewItemViewModel> Items { get; set; } = [];
    public List<ReviewChargeViewModel> Charges { get; set; } = [];
}

public sealed class ReviewItemViewModel
{
    public long Id { get; set; }
    public int LineNumber { get; set; }
    public string? Name { get; set; }
    public int? Quantity { get; set; }
    public decimal? UnitPrice { get; set; }
    public decimal? TotalPrice { get; set; }
}

public sealed class ReviewChargeViewModel
{
    public long Id { get; set; }
    public string? Label { get; set; }
    public decimal? Amount { get; set; }
    public ChargeOperation Operation { get; set; } = ChargeOperation.Add;
}

public sealed class SplitTransactionViewModel
{
    public long TransactionId { get; set; }
    [BindNever, ValidateNever] public string MerchantName { get; set; } = string.Empty;
    [BindNever, ValidateNever] public decimal GrandTotal { get; set; }
    public SplitMethod SplitMethod { get; set; }
    public bool RequiresFoodPickup { get; set; }
    [BindNever, ValidateNever] public bool PickupRotationEnabled { get; set; }
    [Required] public string ParticipantNames { get; set; } = string.Empty;
    public string ParticipantsJson { get; set; } = "[]";
    public string AssignmentsJson { get; set; } = "{}";
    [BindNever, ValidateNever] public List<TransactionItem> Items { get; set; } = [];
    [BindNever, ValidateNever] public List<ApplicationUser> AvailableUsers { get; set; } = [];
}

public sealed class ParticipantSelectionViewModel
{
    public string ClientKey { get; set; } = string.Empty;
    public string? UserId { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class TransactionListViewModel
{
    public List<BillTransaction> Transactions { get; set; } = [];
}

public sealed class TransactionDetailsViewModel
{
    public BillTransaction Transaction { get; set; } = null!;
    public int ReceiptImageCount { get; set; } = 1;
    public FoodPickupAssignment? PickupAssignment { get; set; }
    public IReadOnlyList<FoodPickupDrawHistory> PickupHistory { get; set; } = [];
    public List<ReviewChargeViewModel> Charges { get; set; } = [];
    public IReadOnlyDictionary<long, ParticipantBillBreakdown> ParticipantBreakdowns { get; set; } =
        new Dictionary<long, ParticipantBillBreakdown>();
}

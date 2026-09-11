using System.ComponentModel.DataAnnotations;
using Splitbill.Models;

namespace Splitbill.ViewModels;

public sealed class FoodPickupSettingsViewModel
{
    public bool Enabled { get; set; }
    public FoodPickupSelectionStrategy Strategy { get; set; }
    public IReadOnlyList<FoodPickupUserRowViewModel> Users { get; set; } = [];
    public IReadOnlyList<FoodPickupHistoryRowViewModel> History { get; set; } = [];
}

public sealed class FoodPickupSettingsPostViewModel
{
    public bool Enabled { get; set; }
    public FoodPickupSelectionStrategy Strategy { get; set; }
    public List<string> EligibleUserIds { get; set; } = [];
}

public sealed record FoodPickupUserRowViewModel(
    string Id,
    string Username,
    string DisplayName,
    string Role,
    bool IsEligible,
    bool IsDisabled,
    int ParticipationCount,
    int PickupCount,
    decimal PickupRatio,
    DateTimeOffset? LastPickupAt,
    string? LastPickupMerchant);

public sealed record FoodPickupHistoryRowViewModel(
    long TransactionId,
    string TransactionNumber,
    string MerchantName,
    string? PreviousSelectedUserName,
    string SelectedUserName,
    FoodPickupSelectionStrategy Strategy,
    FoodPickupDrawKind DrawKind,
    string? Reason,
    DateTimeOffset CreatedAt);

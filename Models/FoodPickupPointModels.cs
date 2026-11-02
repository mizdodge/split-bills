namespace Splitbill.Models;

// One effective contribution per transaction. Replaying these rounds restores
// balances after corrections/deletion without changing other saved winners.
public sealed class FoodPickupPointRound
{
    public long Id { get; set; }
    public long TransactionId { get; set; }
    public BillTransaction? Transaction { get; set; }
    public string SelectedUserId { get; set; } = string.Empty;
    public string MembersJson { get; set; } = "[]";
    public string RosterJson { get; set; } = "[]";
    public DateTimeOffset EffectiveAt { get; set; }
}

public sealed class FoodPickupPointAudit
{
    public long Id { get; set; }
    public long? TransactionId { get; set; }
    public BillTransaction? Transaction { get; set; }
    public string TransactionNumber { get; set; } = string.Empty;
    public string MerchantName { get; set; } = string.Empty;
    public string Kind { get; set; } = "Initial";
    public string? ActorUserId { get; set; }
    public string? Reason { get; set; }
    public string SnapshotJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
}

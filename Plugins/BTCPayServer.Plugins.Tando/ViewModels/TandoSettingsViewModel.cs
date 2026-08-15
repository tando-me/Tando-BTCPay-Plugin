using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace BTCPayServer.Plugins.Tando.ViewModels;

public class TandoSettingsViewModel
{
    public string? SubscriptionOfferingId { get; set; }
    public string? SubscriptionPlanId { get; set; }
    public string? FallbackSubscriptionPlanId { get; set; }
    public string? TreasuryLightningAddress { get; set; }
    public List<SelectListItem> Offerings { get; set; } = new();
    public List<SelectListItem> Plans { get; set; } = new();
    public string? CreateOfferingUrl { get; set; }
}

public class TandoSettings
{
    public string? SubscriptionOfferingId { get; set; }
    public string? SubscriptionPlanId { get; set; }
    public string? FallbackSubscriptionPlanId { get; set; }
    public string? TreasuryLightningAddress { get; set; }
}

public class TandoConnectLightningRequest
{
    [Required]
    public string ConnectionString { get; set; }
}

public enum TandoMpesaDestinationType { MobileNumber, TillNumber, PayBill }

public class TandoMpesaSettingsRequest
{
    public TandoMpesaDestinationType DestinationType { get; set; }
    public string? Destination { get; set; }
    public string? AccountNumber { get; set; }
}

public class TandoMpesaSettingsResponse
{
    public TandoMpesaDestinationType DestinationType { get; set; }
    public string? Destination { get; set; }
    public string? AccountNumber { get; set; }
}

public class TandoSplitConfigRequest
{
    [Range(0, 100)]
    public decimal MpesaPercentage { get; set; }
}

public class TandoSplitConfigResponse
{
    public decimal MpesaPercentage { get; set; }
}

public class TandoSplitBreakdownResponse
{
    public string InvoiceId { get; set; }
    public decimal TotalAmount { get; set; }
    public string Currency { get; set; }
    public decimal BtcPortionAmount { get; set; }
    public decimal MpesaPortionAmount { get; set; }
    public decimal MpesaPercentage { get; set; }
    public TandoMpesaDestinationType? MpesaDestinationType { get; set; }
    public string? MpesaDestination { get; set; }
    public bool MpesaSettled { get; set; }
    public DateTimeOffset? MpesaSettledAt { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
}

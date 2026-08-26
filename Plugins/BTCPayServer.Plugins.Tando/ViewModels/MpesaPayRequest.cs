namespace BTCPayServer.Plugins.Tando.ViewModels;

public class MpesaPayRequest
{
    public string CustomerPhone { get; set; }
    public decimal AmountKes { get; set; }
    public string OrderId { get; set; }
}

using System.Threading.Tasks;
using BTCPayServer.Data;
using BTCPayServer.Plugins.Tando.Helper;
using BTCPayServer.Plugins.Tando.ViewModels;
using BTCPayServer.Services.Stores;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Tando.Services;

public record TandoMpesaValidationError(string Field, string Message);

public class TandoMerchantSettingsService(StoreRepository storeRepository)
{
    private const string MpesaSettingsKey = "tandoMpesaSettings";
    private const string SplitConfigKey = "tandoSplitConfig";

    public async Task<TandoMpesaSettingsResponse?> GetMpesaSettings(string storeId)
    {
        var store = await storeRepository.FindStore(storeId);
        if (store is null) return null;

        var blob = store.GetStoreBlob();
        return blob.AdditionalData.TryGetValue(MpesaSettingsKey, out var token) ? token.ToObject<TandoMpesaSettingsResponse>() : null;
    }

    public TandoMpesaValidationError? ValidateMpesaSettings(TandoMpesaSettingsRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Destination))
            return new TandoMpesaValidationError(nameof(request.Destination), "Destination is required.");

        switch (request.DestinationType)
        {
            case TandoMpesaDestinationType.MobileNumber:
                if (KenyanPhoneNumber.Normalize(request.Destination) is null)
                    return new TandoMpesaValidationError(nameof(request.Destination), $"Expected a Kenyan MSISDN, e.g. {KenyanPhoneNumber.ExampleFormat}.");
                break;
            case TandoMpesaDestinationType.TillNumber:
            case TandoMpesaDestinationType.PayBill:
                if (!IsDigitsOnly(request.Destination, minLength: 5, maxLength: 10))
                    return new TandoMpesaValidationError(nameof(request.Destination), "Expected a numeric till/paybill number (5-10 digits).");
                if (request.DestinationType == TandoMpesaDestinationType.PayBill
                    && !string.IsNullOrWhiteSpace(request.AccountNumber)
                    && request.AccountNumber.Length > 20)
                    return new TandoMpesaValidationError(nameof(request.AccountNumber), "Account number is too long.");
                break;
        }
        return null;
    }

    public async Task<bool> SaveMpesaSettings(string storeId, TandoMpesaSettingsRequest request)
    {
        var store = await storeRepository.FindStore(storeId);
        if (store is null) return false;

        var normalizedDestination = request.DestinationType == TandoMpesaDestinationType.MobileNumber
            ? KenyanPhoneNumber.Normalize(request.Destination)
            : request.Destination!.Trim();

        var blob = store.GetStoreBlob();
        blob.AdditionalData[MpesaSettingsKey] = JToken.FromObject(new TandoMpesaSettingsResponse
        {
            DestinationType = request.DestinationType,
            Destination = normalizedDestination,
            AccountNumber = request.AccountNumber?.Trim()
        });
        store.SetStoreBlob(blob);
        await storeRepository.UpdateStore(store);
        return true;
    }

    public async Task<TandoSplitConfigResponse> GetSplitConfig(string storeId)
    {
        var store = await storeRepository.FindStore(storeId);
        if (store is null) return new TandoSplitConfigResponse { MpesaPercentage = 0 };

        var blob = store.GetStoreBlob();
        if (blob.AdditionalData.TryGetValue(SplitConfigKey, out var token))
            return token.ToObject<TandoSplitConfigResponse>() ?? new TandoSplitConfigResponse();

        // No config yet = no split, matches Phase 3 behaviour (every sale a straight BTC payment).
        return new TandoSplitConfigResponse { MpesaPercentage = 0 };
    }

    public async Task<bool> SaveSplitConfig(string storeId, TandoSplitConfigRequest request)
    {
        var store = await storeRepository.FindStore(storeId);
        if (store is null) return false;

        var blob = store.GetStoreBlob();
        blob.AdditionalData[SplitConfigKey] = JToken.FromObject(new TandoSplitConfigResponse { MpesaPercentage = request.MpesaPercentage });
        store.SetStoreBlob(blob);
        await storeRepository.UpdateStore(store);
        return true;
    }

    private static bool IsDigitsOnly(string value, int minLength, int maxLength)
    {
        var trimmed = value.Trim();
        if (trimmed.Length < minLength || trimmed.Length > maxLength) return false;
        foreach (var c in trimmed)
            if (!char.IsDigit(c)) return false;
        return true;
    }
}

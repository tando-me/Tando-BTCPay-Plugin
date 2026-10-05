using System.Net;
using System.Net.Http.Headers;
using System.Text;
using BTCPayServer.Client;
using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Tests;
using Newtonsoft.Json;

namespace BTCPayServer.Plugins.Tando.Tests;

public class TandoLightningConnectTests : UnitTestBase
{
    public TandoLightningConnectTests(ITestOutputHelper helper) : base(helper)
    {
    }

    [Fact]
    public async Task TandoLightningConnect_ValidatesAndEnforcesStoreScope()
    {
        using var s = CreateServerTester(newDb: true);
        await s.StartAsync();
        var user = s.NewAccount();
        await user.GrantAccessAsync();

        var ownerClient = await user.CreateClient();
        var otherStore = await ownerClient.CreateStore(new CreateStoreRequest { Name = "Other store" });
        var scopedKey = (await ownerClient.CreateAPIKey(new CreateApiKeyRequest
        {
            Label = "Tando Lightning test",
            Permissions = [Permission.Create(Policies.CanModifyStoreSettings, user.StoreId)]
        })).ApiKey;
        var unscopedKey = (await ownerClient.CreateAPIKey(new CreateApiKeyRequest
        {
            Label = "Tando Lightning unscoped test",
            Permissions = [Permission.Create(Policies.CanModifyStoreSettings)]
        })).ApiKey;
        var http = s.PayTester.HttpClient;

        async Task<HttpResponseMessage> Connect(string apiKey, string storeId, string connectionString)
        {
            var request = new HttpRequestMessage(HttpMethod.Put,
                new Uri(http.BaseAddress!, $"/plugins/api/tando/stores/{storeId}/lightning/connect"));
            request.Headers.Authorization = new AuthenticationHeaderValue("token", apiKey);
            request.Content = new StringContent(JsonConvert.SerializeObject(new { connectionString }), Encoding.UTF8, "application/json");
            return await http.SendAsync(request);
        }

        using (var emptyResponse = await Connect(unscopedKey, user.StoreId, " "))
            Assert.Equal(HttpStatusCode.BadRequest, emptyResponse.StatusCode);

        const string connectionString = "type=clightning;server=tcp://127.0.0.1:9735";
        using (var missingStoreResponse = await Connect(unscopedKey, "missing-store", connectionString))
            Assert.Equal(HttpStatusCode.NotFound, missingStoreResponse.StatusCode);

        using (var validResponse = await Connect(unscopedKey, user.StoreId, connectionString))
            Assert.Equal(HttpStatusCode.OK, validResponse.StatusCode);

        var paymentMethodId = PaymentTypes.LN.GetPaymentMethodId("BTC");
        var handlers = s.PayTester.GetService<PaymentMethodHandlerDictionary>();
        var updatedStore = await s.PayTester.StoreRepository.FindStore(user.StoreId);
        var lightningConfig = updatedStore!.GetPaymentMethodConfig<LightningPaymentMethodConfig>(paymentMethodId, handlers);
        Assert.Equal(connectionString, lightningConfig?.ConnectionString);
        Assert.False(updatedStore.GetStoreBlob().IsExcluded(paymentMethodId));

        using (var crossStoreResponse = await Connect(scopedKey, otherStore.Id, connectionString))
            Assert.Equal(HttpStatusCode.Forbidden, crossStoreResponse.StatusCode);

        var untouchedStore = await s.PayTester.StoreRepository.FindStore(otherStore.Id);
        Assert.Null(untouchedStore!.GetPaymentMethodConfig(paymentMethodId));
    }
}
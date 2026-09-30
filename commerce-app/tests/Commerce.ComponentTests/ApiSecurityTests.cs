// Externally visible security behaviour of the API: authentication, role and resource
// based authorization, response masking, idempotency, input validation and security
// headers. Each test describes one rule a client can observe over HTTP.
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Commerce.ComponentTests.Infrastructure;
using Npgsql;

namespace Commerce.ComponentTests;

public sealed class ApiSecurityTests(CommerceApiFactory api)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private async Task<(string Sku, Guid Id)> ActiveProductAsync(decimal price = 25m)
    {
        var catalog = api.ClientFor("cathy", "catalog-manager");
        var sku = $"TST-{Random.Shared.Next(100000, 999999)}";
        var created = await catalog.PostAsJsonAsync("/api/catalog/products", new { sku, name = $"Item {sku}", description = "", category = "tests", price, currency = "EUR" });
        created.EnsureSuccessStatusCode();
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("id").GetGuid();
        (await catalog.PostAsync($"/api/catalog/products/{id}/activate", null)).EnsureSuccessStatusCode();
        return (sku, id);
    }

    private static async Task RegisterAsync(HttpClient customer) =>
        await customer.PostAsJsonAsync("/api/customers/me/", new { displayName = "Test Customer", phone = "+49 30 1234" });

    private static async Task<HttpResponseMessage> PlaceOrderAsync(HttpClient customer, object body, Guid? key = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/orders") { Content = JsonContent.Create(body) };
        request.Headers.Add("Idempotency-Key", (key ?? Guid.NewGuid()).ToString());
        return await customer.SendAsync(request);
    }

    [Fact]
    public async Task The_public_catalog_is_readable_anonymously_but_orders_are_not()
    {
        var anonymous = api.ClientFor(null);

        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/api/catalog/products")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/orders/mine")).StatusCode);
    }

    [Fact]
    public async Task A_token_for_another_audience_is_rejected()
    {
        var client = api.ClientFor(null);
        client.DefaultRequestHeaders.Authorization = new("Bearer", TestTokens.Create("carol", ["customer"], "some-other-api", TestTokens.SigningKey));

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/orders/mine")).StatusCode);
    }

    [Fact]
    public async Task Only_catalog_managers_may_create_products()
    {
        var product = new { sku = "HAX-100001", name = "x", description = "", category = "c", price = 1m, currency = "EUR" };

        var byCustomer = await api.ClientFor("carol", "customer").PostAsJsonAsync("/api/catalog/products", product);
        var byManager = await api.ClientFor("cathy", "catalog-manager").PostAsJsonAsync("/api/catalog/products", product);

        Assert.Equal(HttpStatusCode.Forbidden, byCustomer.StatusCode);
        Assert.Equal(HttpStatusCode.Created, byManager.StatusCode);
    }

    [Fact]
    public async Task Invalid_input_is_rejected_before_reaching_the_domain()
    {
        var response = await api.ClientFor("cathy", "catalog-manager").PostAsJsonAsync("/api/catalog/products",
            new { sku = "", name = "", description = "", category = "", price = 1m, currency = "XXX" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Currency", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Customers_see_only_their_own_orders_while_order_managers_see_all()
    {
        var (sku, _) = await ActiveProductAsync();
        var owner = api.ClientFor("erin", "customer");
        await RegisterAsync(owner);
        var order = await (await PlaceOrderAsync(owner, new { lines = new[] { new { sku, quantity = 1 } }, paymentMethodToken = "card_approved_4242" }))
            .Content.ReadFromJsonAsync<JsonElement>(Json);
        var orderId = order.GetProperty("id").GetGuid();

        var asOwner = await owner.GetAsync($"/api/orders/{orderId}");
        var asOtherCustomer = await api.ClientFor("frank", "customer").GetAsync($"/api/orders/{orderId}");
        var asOrderManager = await api.ClientFor("olga", "order-manager").GetAsync($"/api/orders/{orderId}");

        Assert.Equal(HttpStatusCode.OK, asOwner.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, asOtherCustomer.StatusCode);
        Assert.Equal(HttpStatusCode.OK, asOrderManager.StatusCode);
    }

    [Fact]
    public async Task Prices_sent_by_the_client_are_ignored()
    {
        var (sku, _) = await ActiveProductAsync(price: 40m);
        var customer = api.ClientFor("gina", "customer");
        await RegisterAsync(customer);

        var response = await PlaceOrderAsync(customer, new { lines = new[] { new { sku, quantity = 2, unitPrice = 0.01m } }, paymentMethodToken = "card_approved_1111", total = 0.02m });

        var order = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal(80m, order.GetProperty("total").GetDecimal());
    }

    [Fact]
    public async Task Retrying_with_the_same_idempotency_key_returns_the_original_order()
    {
        var (sku, _) = await ActiveProductAsync();
        var customer = api.ClientFor("hank", "customer");
        await RegisterAsync(customer);
        var key = Guid.NewGuid();
        var body = new { lines = new[] { new { sku, quantity = 1 } }, paymentMethodToken = "card_approved_4242" };

        var first = await PlaceOrderAsync(customer, body, key);
        var retry = await PlaceOrderAsync(customer, body, key);

        var firstId = (await first.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("id").GetGuid();
        var retryId = (await retry.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("id").GetGuid();
        Assert.Equal(firstId, retryId);
        Assert.Equal("true", retry.Headers.GetValues("Idempotent-Replay").Single());
    }

    [Fact]
    public async Task Placing_an_order_without_an_idempotency_key_is_rejected()
    {
        var response = await api.ClientFor("ivy", "customer").PostAsJsonAsync("/api/orders", new { lines = new[] { new { sku = "TST-1", quantity = 1 } }, paymentMethodToken = "card_approved_4242" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Personal_data_is_masked_for_support_staff_and_visible_to_owner_and_admin()
    {
        var owner = api.ClientFor("jane", "customer");
        await RegisterAsync(owner);
        var profile = await owner.GetFromJsonAsync<JsonElement>("/api/customers/me/", Json);
        var id = profile.GetProperty("id").GetGuid();

        var support = await api.ClientFor("sam", "support-agent").GetFromJsonAsync<JsonElement>($"/api/customers/{id}", Json);
        var admin = await api.ClientFor("ada", "admin").GetFromJsonAsync<JsonElement>($"/api/customers/{id}", Json);

        Assert.Equal("jane@commerce.test", profile.GetProperty("email").GetString());
        Assert.Equal("j***", support.GetProperty("email").GetString());
        Assert.Equal("T***", support.GetProperty("displayName").GetString());
        Assert.Equal("jane@commerce.test", admin.GetProperty("email").GetString());
    }

    [Fact]
    public async Task Refunds_are_reserved_for_finance_not_administrators()
    {
        var path = $"/api/payments/{Guid.NewGuid()}/refunds";
        var body = new { amount = 1m, reason = "test" };

        var byAdmin = await api.ClientFor("ada", "admin").PostAsJsonAsync(path, body);
        var byFinance = await api.ClientFor("fiona", "finance").PostAsJsonAsync(path, body);

        Assert.Equal(HttpStatusCode.Forbidden, byAdmin.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, byFinance.StatusCode);
    }

    [Fact]
    public async Task An_administrator_cannot_change_their_own_roles_and_keycloak_is_not_called()
    {
        var subject = TestTokens.Subject("ada");
        api.Keycloak.Calls.Clear();

        var response = await api.ClientFor("ada", "admin").PutAsJsonAsync($"/api/identity/users/{subject}/roles", new { roles = new[] { "admin", "finance" } });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(api.Keycloak.Calls);
    }

    // Found by the platform's authenticated ZAP scan: a malformed or unknown user id used to
    // surface Keycloak's error as a 500.
    [Theory]
    [InlineData("not-a-user-id")]
    [InlineData(RecordingKeycloakAdmin.UnknownUser)]
    public async Task A_role_change_for_a_user_that_cannot_exist_is_not_found_not_an_error(string subject)
    {
        var response = await api.ClientFor("ada", "admin").PutAsJsonAsync($"/api/identity/users/{subject}/roles", new { roles = new[] { "support-agent" } });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_role_change_is_applied_in_keycloak_and_recorded_for_the_audit_trail()
    {
        var target = Guid.NewGuid().ToString();

        var response = await api.ClientFor("ada", "admin").PutAsJsonAsync($"/api/identity/users/{target}/roles", new { roles = new[] { "support-agent" } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains($"add:{target}:support-agent", api.Keycloak.Calls);
        Assert.Contains($"remove:{target}:customer", api.Keycloak.Calls);
        await using var connection = new NpgsqlConnection(api.DatabaseConnection);
        await connection.OpenAsync();
        await using var query = new NpgsqlCommand("SELECT count(*) FROM identity.outbox_messages WHERE type = 'audit.recorded' AND payload::text LIKE @target", connection);
        query.Parameters.AddWithValue("target", $"%{target}%");
        Assert.Equal(1L, (long)(await query.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Responses_carry_security_headers_and_hide_the_server()
    {
        var response = await api.ClientFor(null).GetAsync("/api/catalog/products");

        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.False(response.Headers.Contains("Server"));
        Assert.True(response.Headers.Contains("X-Correlation-Id"));
    }
}

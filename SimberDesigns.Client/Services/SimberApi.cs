using System.Net.Http.Json;

namespace SimberDesigns.Client.Services;

public sealed record AuthResponse(
    string Token,
    string Email,
    string FullName,
    string Role,
    decimal CreditsBalance,
    string? SubscriptionTier,
    int DailyLimit,
    int DownloadsToday);

public sealed record DesignDto(
    Guid Id,
    string Title,
    string Slug,
    string? Description,
    string Category,
    decimal PriceUsd,
    decimal CreditsCost,
    string PreviewUrl,
    DateTime CreatedAt,
    bool IsFreeDaily,
    float? Similarity);

public sealed record DownloadResponse(string DownloadUrl, DateTime ExpiresAt, int RemainingToday);

public sealed record CheckoutResponse(string CheckoutUrl, Guid TransactionId, bool FakeCheckout);

public sealed record StorefrontDto(
    List<CreditPackageDto> Packages,
    decimal PluginMonthPricePen,
    string PluginPlanName,
    bool HasCorelDownload = false,
    bool HasIllustratorDownload = false);

public sealed record PluginLicenseDto(
    string Plan,
    string Status,
    string ActivationCode,
    DateTime ExpiresAt,
    bool IsActive,
    string? HardwareId,
    string Edition = "");

public sealed record PluginInstallerStatusDto(
    string Edition,
    bool Ready,
    string? FileName,
    long SizeBytes,
    DateTime? UpdatedAt);

public sealed record CreditPackageDto(Guid Id, string Name, decimal CreditsAmount, decimal BonusAmount, decimal PriceUsd);

public sealed record AccountDashboardDto(
    string Email,
    string FullName,
    string Role,
    decimal CreditsBalance,
    string? SubscriptionTier,
    string? SubscriptionStatus,
    DateTime? SubscriptionExpiresAt,
    int DailyLimit,
    int DownloadsToday,
    List<UnlockedDesignDto> UnlockedDesigns,
    List<TransactionDto> Transactions,
    List<CreditLogDto> CreditLogs);

public sealed record UnlockedDesignDto(Guid Id, string Title, string Slug, string PreviewUrl, bool UnlockedViaSubscription);

public sealed record TransactionDto(
    Guid Id,
    string DetailName,
    decimal Amount,
    string Currency,
    string Gateway,
    string Status,
    string? ExternalReferenceId,
    string? PaymentReceiptUrl,
    string? Notes,
    DateTime CreatedAt);

public sealed record CreditLogDto(Guid Id, string Description, decimal CreditsChanged, DateTime CreatedAt);

public sealed record AdminMetricsDto(
    int TotalUsers,
    int ActiveLicenses,
    int ExpiredLicenses,
    int SalesThisMonth,
    decimal RevenueThisMonth,
    decimal TotalRevenue,
    int PaidPayments);

public sealed record AdminLicenseDto(
    string CustomerEmail,
    string CustomerName,
    string Plan,
    string Status,
    bool IsActive,
    DateTime ExpiresAt,
    string? HardwareId,
    string ActivationCode,
    DateTime CreatedAt,
    string Edition = "");

public sealed record AdminCustomerDto(
    Guid Id,
    string Email,
    string FullName,
    string Role,
    decimal CreditsBalance,
    int ActiveLicenses,
    DateTime CreatedAt);

public sealed class SimberApi(HttpClient http)
{
    public Task<AdminMetricsDto?> GetAdminMetricsAsync()
        => http.GetFromJsonAsync<AdminMetricsDto>("api/admin/metrics");

    public Task<List<AdminLicenseDto>?> GetAdminLicensesAsync(string? q = null)
        => http.GetFromJsonAsync<List<AdminLicenseDto>>(
            string.IsNullOrWhiteSpace(q) ? "api/admin/licenses" : $"api/admin/licenses?q={Uri.EscapeDataString(q)}");

    public Task<List<AdminCustomerDto>?> GetAdminCustomersAsync(string? q = null)
        => http.GetFromJsonAsync<List<AdminCustomerDto>>(
            string.IsNullOrWhiteSpace(q) ? "api/admin/customers" : $"api/admin/customers?q={Uri.EscapeDataString(q)}");

    public async Task AdjustCustomerCreditsAsync(Guid userId, decimal credits)
    {
        var response = await http.PostAsJsonAsync($"api/admin/customers/{userId}/credits", new { credits, note = (string?)null });
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(body) ? "No se pudo ajustar créditos." : body);
        }
    }

    // ---- CMS: contenido de la web ----
    public async Task<Dictionary<string, string>> GetContentAsync()
        => await http.GetFromJsonAsync<Dictionary<string, string>>("api/content") ?? new();

    public Task SaveContentAsync(Dictionary<string, string> items)
        => http.PutAsJsonAsync("api/content", items);

    public Task DeleteContentAsync(string key)
        => http.DeleteAsync($"api/content/{Uri.EscapeDataString(key)}");

    public async Task<string?> UploadAssetAsync(string key, Stream data, string fileName, string contentType)
    {
        using var content = new MultipartFormDataContent();
        var sc = new StreamContent(data);
        sc.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        content.Add(sc, "file", fileName);
        var resp = await http.PostAsync($"api/content/asset/{Uri.EscapeDataString(key)}", content);
        return resp.IsSuccessStatusCode ? $"/api/content/asset/{key}" : null;
    }

    public Task<List<DesignDto>?> GetDesignsAsync(string? category = null, string? q = null)
    {
        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(category) && category != "Todos")
        {
            query.Add($"category={Uri.EscapeDataString(category)}");
        }

        if (!string.IsNullOrWhiteSpace(q))
        {
            query.Add($"q={Uri.EscapeDataString(q)}");
        }

        var suffix = query.Count == 0 ? "" : "?" + string.Join("&", query);
        return http.GetFromJsonAsync<List<DesignDto>>($"api/designs{suffix}");
    }

    public async Task<List<DesignDto>?> SearchByImageAsync(Stream image, string fileName, string contentType)
    {
        using var content = new MultipartFormDataContent();
        var fileContent = new StreamContent(image);
        if (!string.IsNullOrWhiteSpace(contentType))
        {
            fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        }

        content.Add(fileContent, "imageFile", fileName);
        var response = await http.PostAsync("api/designs/visual-search", content);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<DesignDto>>();
    }

    public async Task<DownloadResponse?> DownloadAsync(Guid id)
    {
        var response = await http.GetAsync($"api/designs/{id}/download");
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(body) ? "No se pudo descargar el diseño." : body);
        }

        return await response.Content.ReadFromJsonAsync<DownloadResponse>();
    }

    public async Task UnlockAsync(Guid id)
    {
        var response = await http.PostAsync($"api/designs/{id}/unlock", null);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(body) ? "No se pudo canjear el diseño." : body);
        }
    }

    public async Task<AuthResponse?> LoginAsync(string email, string password)
    {
        var response = await http.PostAsJsonAsync("api/auth/login", new { email, password });
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return await response.Content.ReadFromJsonAsync<AuthResponse>();
    }

    public async Task<(AuthResponse? Auth, string? Error)> LoginWithMessageAsync(string email, string password, bool admin)
    {
        var url = admin ? "api/auth/admin-login" : "api/auth/login";
        var response = await http.PostAsJsonAsync(url, new { email, password });
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            var msg = body?.Trim() ?? "";
            if (msg.Length >= 2 && msg[0] == '"' && msg[^1] == '"')
                msg = msg[1..^1];
            return (null, string.IsNullOrWhiteSpace(msg) ? "Credenciales inválidas." : msg);
        }

        return (await response.Content.ReadFromJsonAsync<AuthResponse>(), null);
    }

    public async Task<AuthResponse?> RegisterAsync(string email, string password, string fullName)
    {
        var response = await http.PostAsJsonAsync("api/auth/register", new { email, password, fullName });
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return await response.Content.ReadFromJsonAsync<AuthResponse>();
    }

    public Task<AccountDashboardDto?> GetDashboardAsync()
        => http.GetFromJsonAsync<AccountDashboardDto>("api/account/dashboard");

    public async Task ChangePasswordAsync(string currentPassword, string newPassword)
    {
        var response = await http.PostAsJsonAsync("api/account/password", new { currentPassword, newPassword });
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            var msg = body?.Trim() ?? "";
            if (msg.Length >= 2 && msg[0] == '"' && msg[^1] == '"')
                msg = msg[1..^1];
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(msg) ? "No se pudo cambiar la contraseña." : msg);
        }
    }

    public Task<StorefrontDto?> GetStorefrontAsync()
        => http.GetFromJsonAsync<StorefrontDto>("api/payments/storefront");

    public async Task<CheckoutResponse?> CheckoutAsync(string kind, Guid? packageId, string? planKey)
    {
        var response = await http.PostAsJsonAsync("api/payments/checkout", new { kind, packageId, planKey });
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            var msg = body?.Trim() ?? "";
            if (msg.Length >= 2 && msg[0] == '"' && msg[^1] == '"')
                msg = msg[1..^1];
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(msg) ? "No se pudo iniciar el pago." : msg);
        }

        return await response.Content.ReadFromJsonAsync<CheckoutResponse>();
    }

    public async Task ConfirmPaymentAsync(Guid? transactionId, string? paymentId, string? externalReference)
    {
        var response = await http.PostAsJsonAsync("api/payments/confirm", new { transactionId, paymentId, externalReference });
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(body) ? "No se pudo confirmar el pago." : body);
        }
    }

    public async Task<List<PluginLicenseDto>?> GetPluginLicensesAsync()
    {
        var response = await http.GetAsync("api/plugin/me");
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return [];
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<PluginLicenseDto>>() ?? [];
    }

    public Task<List<PluginInstallerStatusDto>?> GetInstallersAsync()
        => http.GetFromJsonAsync<List<PluginInstallerStatusDto>>("api/plugin/installers");

    public async Task UploadInstallerAsync(string edition, Stream file, string fileName)
    {
        using var content = new MultipartFormDataContent();
        var part = new StreamContent(file);
        part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        content.Add(part, "file", fileName);
        var response = await http.PostAsync($"api/plugin/installers/{Uri.EscapeDataString(edition)}", content);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(body) ? "No se pudo subir el ejecutable." : body);
        }
    }

    public async Task<DesignDto?> CreateDesignAsync(
        string title,
        string category,
        decimal price,
        string cdrVersion,
        Stream preview,
        string previewName,
        string previewType,
        Stream file,
        string fileName,
        string fileType,
        bool isFreeDaily = false)
    {
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(title), "title");
        content.Add(new StringContent(category), "category");
        content.Add(new StringContent(price.ToString(System.Globalization.CultureInfo.InvariantCulture)), "price");
        content.Add(new StringContent(cdrVersion), "cdrVersion");
        content.Add(new StringContent(isFreeDaily ? "true" : "false"), "isFreeDaily");

        var previewContent = new StreamContent(preview);
        previewContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
            string.IsNullOrWhiteSpace(previewType) ? "image/jpeg" : previewType);
        content.Add(previewContent, "preview", previewName);

        var fileContent = new StreamContent(file);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
            string.IsNullOrWhiteSpace(fileType) ? "application/octet-stream" : fileType);
        content.Add(fileContent, "file", fileName);

        var response = await http.PostAsync("api/designs", content);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(body) ? "No se pudo publicar el diseño." : body);
        }

        return await response.Content.ReadFromJsonAsync<DesignDto>();
    }

    public async Task DeleteDesignAsync(Guid id)
    {
        var response = await http.DeleteAsync($"api/designs/{id}");
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(body) ? "No se pudo borrar el diseño." : body);
        }
    }
}

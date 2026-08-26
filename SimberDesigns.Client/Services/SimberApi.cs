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
    float? Similarity);

public sealed record DownloadResponse(string DownloadUrl, DateTime ExpiresAt, int RemainingToday);

public sealed record CheckoutResponse(string CheckoutUrl);

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

public sealed record AdminTransactionDto(
    Guid Id,
    Guid UserId,
    string UserName,
    string UserEmail,
    decimal Amount,
    decimal CreditsToReceive,
    string PackageName,
    string? PaymentReceiptUrl,
    string Status,
    string? Notes,
    DateTime CreatedAt);

public sealed class SimberApi(HttpClient http)
{
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

    public async Task<CheckoutResponse?> CheckoutAsync(string? planKey, string? packKey)
    {
        var response = await http.PostAsJsonAsync("api/payments/checkout", new { planKey, packKey });
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<CheckoutResponse>();
    }

    public async Task SubmitManualPaymentAsync(Stream proof, string fileName, string itemKey)
    {
        using var content = new MultipartFormDataContent();
        content.Add(new StreamContent(proof), "receipt", fileName);
        content.Add(new StringContent(itemKey), "itemKey");
        var response = await http.PostAsync("api/payments/manual-recharge", content);
        response.EnsureSuccessStatusCode();
    }

    public Task<List<AdminTransactionDto>?> GetAdminPaymentsAsync(string? status = null)
    {
        var suffix = string.IsNullOrWhiteSpace(status) ? "" : $"?status={Uri.EscapeDataString(status)}";
        return http.GetFromJsonAsync<List<AdminTransactionDto>>($"api/payments/admin{suffix}");
    }

    public async Task ApproveAsync(Guid id)
    {
        var response = await http.PostAsync($"api/payments/{id}/approve", null);
        response.EnsureSuccessStatusCode();
    }

    public async Task RejectAsync(Guid id, string notes)
    {
        var response = await http.PostAsJsonAsync($"api/payments/{id}/reject", new { notes });
        response.EnsureSuccessStatusCode();
    }
}

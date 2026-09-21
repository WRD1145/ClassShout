using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace ClassShout.Core.Remote;

/// <summary>
/// 教师端账号客户端：注册、登录、登出、会话恢复。
///
/// 姓名以服务器上的账号为准，而不是让客户端随便填一个字符串 ——
/// 这样教室端弹窗上显示的"哪位老师"才是可信的。
/// </summary>
public sealed class AccountClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly TeacherRelaySettings _settings;

    public AccountClient(HttpClient http, TeacherRelaySettings settings)
    {
        _http = http;
        _settings = settings;
    }

    /// <summary>登录状态变化。参数为当前用户，未登录时为 null。</summary>
    public event Action<UserProfileDto?>? SignedInChanged;

    public bool IsSignedIn => _settings.IsSignedIn;

    /// <summary>老师姓名。未登录时回退到设备名，保证界面上总有东西可显示。</summary>
    public string DisplayName => _settings.DisplayName ?? string.Empty;

    public string AccountLabel
    {
        get
        {
            if (!IsSignedIn)
            {
                return "未登录";
            }

            return string.IsNullOrWhiteSpace(_settings.Username)
                ? _settings.Email ?? DisplayName
                : _settings.Username;
        }
    }

    public string? Token => _settings.AuthToken;

    /// <summary>注册。用户名与邮箱至少要填一个。</summary>
    public Task<(bool Ok, string? Error)> RegisterAsync(
        string? username,
        string? email,
        string displayName,
        string password,
        CancellationToken cancellationToken = default)
        => PostAuthAsync(
            RelayPaths.AuthRegister,
            new RegisterRequest(
                string.IsNullOrWhiteSpace(username) ? null : username.Trim(),
                string.IsNullOrWhiteSpace(email) ? null : email.Trim(),
                displayName.Trim(),
                password),
            cancellationToken);

    /// <summary>登录。<paramref name="account"/> 可以填用户名，也可以填邮箱。</summary>
    public Task<(bool Ok, string? Error)> LoginAsync(
        string account,
        string password,
        CancellationToken cancellationToken = default)
        => PostAuthAsync(RelayPaths.AuthLogin, new LoginRequest(account.Trim(), password), cancellationToken);

    private async Task<(bool Ok, string? Error)> PostAuthAsync(
        string path,
        object payload,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_settings.ServerUrl))
        {
            return (false, "请先填写服务器地址。");
        }

        try
        {
            using var response = await _http
                .PostAsJsonAsync(Url(path), payload, JsonOptions, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return (false, $"服务器返回 HTTP {(int)response.StatusCode}");
            }

            var result = await response.Content
                .ReadFromJsonAsync<AuthResponse>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);

            if (result is null || !result.Ok || result.User is null || string.IsNullOrEmpty(result.Token))
            {
                return (false, result?.Error ?? "操作失败。");
            }

            Apply(result.Token, result.User);
            return (true, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return (false, $"连接服务器失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 用本地保存的令牌尝试恢复登录状态。
    /// 令牌可能已在服务器端失效（重启、过期、被停用），所以必须真的问一次。
    /// </summary>
    public async Task<bool> TryRestoreSessionAsync(CancellationToken cancellationToken = default)
    {
        if (!IsSignedIn || string.IsNullOrWhiteSpace(_settings.ServerUrl))
        {
            return false;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Url(RelayPaths.AuthMe));
            request.Headers.TryAddWithoutValidation(RelayPaths.AuthTokenHeader, _settings.AuthToken);

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                // 令牌已失效，清掉本地状态，避免界面显示"已登录"却处处被拒
                Clear();
                return false;
            }

            response.EnsureSuccessStatusCode();

            var profile = await response.Content
                .ReadFromJsonAsync<UserProfileDto>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);

            if (profile is null)
            {
                Clear();
                return false;
            }

            Apply(_settings.AuthToken!, profile);
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // 网络不通不等于登录失效，保留本地状态，下次再试
            return false;
        }
    }

    /// <summary>登出。即使服务器不响应也会清掉本地状态。</summary>
    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        var token = _settings.AuthToken;

        Clear();

        if (string.IsNullOrEmpty(token) || string.IsNullOrWhiteSpace(_settings.ServerUrl))
        {
            return;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, Url(RelayPaths.AuthLogout));
            request.Headers.TryAddWithoutValidation(RelayPaths.AuthTokenHeader, token);
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // 服务器不可达也无妨：本地令牌已清，等于本机已登出
        }
    }

    private void Apply(string token, UserProfileDto user)
    {
        _settings.AuthToken = token;
        _settings.UserId = user.Id;
        _settings.DisplayName = user.DisplayName;
        _settings.Username = user.Username;
        _settings.Email = user.Email;
        LocalSettings.SaveTeacher(_settings);

        SignedInChanged?.Invoke(user);
    }

    private void Clear()
    {
        _settings.AuthToken = null;
        _settings.UserId = null;
        _settings.DisplayName = null;
        _settings.Username = null;
        _settings.Email = null;
        LocalSettings.SaveTeacher(_settings);

        SignedInChanged?.Invoke(null);
    }

    /// <summary>服务器地址用户随时可改，所以每次请求现拼绝对地址。</summary>
    private string Url(string path) => $"{(_settings.ServerUrl ?? string.Empty).TrimEnd('/')}{path}";
}

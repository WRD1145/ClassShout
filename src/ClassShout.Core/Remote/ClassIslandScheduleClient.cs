using System.Net.Http.Json;
using System.Text.Json;

namespace ClassShout.Core.Remote;

/// <summary>按账号授权读取绑定教室的实时课表，并等待教室本机保存确认。</summary>
public sealed class ClassIslandScheduleClient(HttpClient http, TeacherRelaySettings settings)
{
    private string Url(string uuid) => $"{settings.ServerUrl?.TrimEnd('/')}/api/auth/classrooms/{Uri.EscapeDataString(uuid)}/classisland";

    public async Task<ClassIslandSchedule> ReadAsync(string uuid)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Url(uuid));
            request.Headers.TryAddWithoutValidation(RelayPaths.AuthTokenHeader, settings.AuthToken);
            using var response = await http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
                return new ClassIslandSchedule { Error = $"服务器拒绝读取课表：HTTP {(int)response.StatusCode}，请检查登录和教室授权。" };
            return await response.Content.ReadFromJsonAsync<ClassIslandSchedule>()
                ?? new ClassIslandSchedule { Error = "服务器未返回课表。" };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new ClassIslandSchedule { Error = $"无法读取绑定教室的实时课表：{ex.Message}" };
        }
    }

    public async Task<string?> SendAsync(string uuid, ClassIslandTask task)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, Url(uuid)) { Content = JsonContent.Create(task) };
            request.Headers.TryAddWithoutValidation(RelayPaths.AuthTokenHeader, settings.AuthToken);
            using var response = await http.SendAsync(request);
            if (response.IsSuccessStatusCode) return null;
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return body.RootElement.TryGetProperty("error", out var error) ? error.GetString() : $"服务器拒绝下发：HTTP {(int)response.StatusCode}。";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return $"下发未确认：{ex.Message}。请刷新课表核对任务。";
        }
    }
}

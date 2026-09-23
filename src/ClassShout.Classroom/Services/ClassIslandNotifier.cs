using System.Net.Http.Json;

namespace ClassShout.Classroom.Services;

/// <summary>
/// 把喊话投递给本机的 ClassIsland 联动插件。
///
/// 为什么是"教室端主动 POST 到本机端口"：ClassIsland 的跨进程通信只能从外部调用它，
/// 而它公开的远程服务里没有"显示一条提醒"（只有课程、档案、Uri 导航），
/// 所以必须由插件自己开一个入口。详见插件仓库。
///
/// 这个类**绝不抛异常、也绝不阻塞喊话主流程**：
/// 它只是"顺便再通知一处"，插件没装、端口没开、ClassIsland 没运行都很正常，
/// 那些情况下喊话本身照常（教室端自己的弹窗还在）。
/// </summary>
public sealed class ClassIslandNotifier
{
    /// <summary>插件的监听地址。只绑回环，所以这里也只连回环。</summary>
    private const string Endpoint = "http://127.0.0.1:45902/shout";

    private readonly HttpClient _http;

    /// <summary>连续失败后不再反复写日志。教室里没人看日志，但刷屏会淹没真正的问题。</summary>
    private int _consecutiveFailures;

    public ClassIslandNotifier(HttpClient http)
    {
        _http = http;
    }

    /// <summary>
    /// 投递一条喊话。<paramref name="from"/> 是老师的姓名，会显示在提醒的遮罩上。
    /// </summary>
    /// <returns>是否投递成功。调用方可以用它决定要不要记日志。</returns>
    public async Task<bool> TryNotifyAsync(string from, string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            // 超时压得很短：这是一个本机回环请求，正常情况是毫秒级。
            // 若不设上限，插件卡住就会把喊话这条链路一起拖住 —— 而那正是不能接受的。
            timeout.CancelAfter(TimeSpan.FromSeconds(2));

            using var response = await _http
                .PostAsJsonAsync(Endpoint, new { from, text }, timeout.Token)
                .ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                _consecutiveFailures = 0;
                return true;
            }

            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or InvalidOperationException)
        {
            _consecutiveFailures++;
            return false;
        }
    }

    /// <summary>
    /// 是否该把这次失败写进日志。
    ///
    /// 只在第一次和每 20 次时写：插件没装是最常见的情况，那时每次喊话都记一条
    /// 会把教室端的日志刷满，真正的异常反而被埋掉。
    /// </summary>
    public bool ShouldLogFailure() => _consecutiveFailures == 1 || _consecutiveFailures % 20 == 0;
}

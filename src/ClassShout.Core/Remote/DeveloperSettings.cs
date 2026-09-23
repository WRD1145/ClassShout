namespace ClassShout.Core.Remote;

/// <summary>
/// 开发者模式开关。
///
/// 单独一个文件、单独一份配置，不和外观或联网配置混在一起 ——
/// 它是一个"排障时才打开"的状态，不该跟着别的设置被一起导出、重置或覆盖。
///
/// 为什么需要持久化：解锁动作是"在版本号上连点 10 次"，属于刻意加的门槛。
/// 若不记住，每次重启都得重新点 10 次 —— 那会逼着人干脆把它写死进代码，
/// 门槛也就白设了。
/// </summary>
public sealed class DeveloperSettings
{
    /// <summary>是否已解锁开发者模式。</summary>
    public bool Enabled { get; set; }
}

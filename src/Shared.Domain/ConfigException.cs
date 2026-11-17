namespace Mmo.Shared.Domain;

/// <summary>
/// 配置有问题时抛这个，启动直接失败，不带着半截配置跑。
/// C6/D12 起由共享域统一持有：战斗服、角色服、世界服的表加载都抛同一个类型，
/// 各进程的启动壳只需 catch 一次。
/// </summary>
public sealed class ConfigException(string message) : Exception(message);

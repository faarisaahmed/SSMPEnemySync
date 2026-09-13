using SSMP.Logging;

namespace SSMPEnemySync;

/// <summary>
/// Thin static wrapper over the logger SSMP hands to addons, so the rest of the addon does not have
/// to carry a logger reference into every static helper.
/// </summary>
internal static class Log {
    private static ILogger _logger;

    private const string Prefix = "[EnemySync] ";

    public static void Initialize(ILogger logger) => _logger = logger;

    public static void Debug(string message) => _logger?.Debug(Prefix + message);

    public static void Info(string message) => _logger?.Info(Prefix + message);

    public static void Warn(string message) => _logger?.Warn(Prefix + message);

    public static void Error(string message) => _logger?.Error(Prefix + message);
}

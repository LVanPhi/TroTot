namespace TroViec.Migrator;

/// <summary>Expected failure with a message safe to print (no secrets, no personal data).</summary>
public sealed class MigratorException(string message) : Exception(message);

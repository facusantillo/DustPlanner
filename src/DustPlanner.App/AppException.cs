namespace DustPlanner.App;

/// <summary>A user-facing failure (bad input, missing file, nothing exported yet): the message is the whole explanation.</summary>
public sealed class AppException(string message) : Exception(message);

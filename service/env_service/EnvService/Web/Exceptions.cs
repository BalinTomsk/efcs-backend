namespace EnvService.Web;

/// <summary>A request the caller got wrong (unknown parameter value): 400.</summary>
public sealed class InvalidRequestException(string message) : Exception(message);

/// <summary>An addressed thing that does not exist (e.g. an unknown job): 404.</summary>
public sealed class NotFoundException(string message) : Exception(message);

/// <summary>Something the answer needs was never loaded (the station cache before its first load): 503 + Retry-After.</summary>
public sealed class ServiceUnavailableException(string message) : Exception(message);

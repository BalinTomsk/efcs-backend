namespace EnvService.Web;

/// <summary>A request the caller got wrong (unknown parameter value): 400.</summary>
public sealed class InvalidRequestException(string message) : Exception(message);

/// <summary>An addressed thing that does not exist (e.g. an unknown job): 404.</summary>
public sealed class NotFoundException(string message) : Exception(message);

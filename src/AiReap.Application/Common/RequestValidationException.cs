namespace AiReap.Application.Common;

// A caller sent something the business rules reject (empty title, etc.). Mapped to 400 by the API
// layer, so services can validate without knowing about HTTP.
public class RequestValidationException(string message) : Exception(message);

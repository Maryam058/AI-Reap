using AiReap.Application.Files;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;

namespace AiReap.Api.Authorization;

// The request body limit of an upload action, from OriginalDocuments:MaxUploadBytes (plus room for the
// multipart framing) instead of a constant, so the limit is configured in one place. Runs before model
// binding reads the form. The action still checks the file's own length against the same setting,
// since a host without this feature (the test server) enforces no body limit.
[AttributeUsage(AttributeTargets.Method)]
public sealed class UploadSizeLimitAttribute : Attribute, IResourceFilter, IOrderedFilter
{
    private const long MultipartOverheadBytes = 64 * 1024;

    // Ahead of the framework's own request-size filters.
    public int Order => -1000;

    public void OnResourceExecuting(ResourceExecutingContext context)
    {
        var options = context.HttpContext.RequestServices.GetRequiredService<IOptions<OriginalDocumentOptions>>().Value;
        var feature = context.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false })
        {
            feature.MaxRequestBodySize = options.MaxUploadBytes + MultipartOverheadBytes;
        }
    }

    public void OnResourceExecuted(ResourceExecutedContext context)
    {
    }
}

using System.Reflection;
using LocalAudiobookStudio.Web.Endpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace LocalAudiobookStudio.Web.Tests;

public sealed class AdvertisedBodyRejectionTests
{
    [Fact]
    public void Header_only_rejection_does_not_lower_transport_limit_on_the_unconsumed_body()
    {
        var context = new DefaultHttpContext();
        context.Request.ContentLength = 524289;
        var transport = new BodyLimit(); context.Features.Set<IHttpMaxRequestBodySizeFeature>(transport);
        // Exercise the real internal boundary without adding a production test hook.
        var limit = typeof(ProjectEndpoints).Assembly.GetType("LocalAudiobookStudio.Web.Endpoints.ApiRequest")!
            .GetMethod("Limit", BindingFlags.Static | BindingFlags.NonPublic)!.CreateDelegate<Action<HttpContext, long>>();
        var error = Assert.ThrowsAny<Exception>(() => limit(context, 524288));
        Assert.Equal(413, error.GetType().GetProperty("Status")!.GetValue(error));
        Assert.Equal("request.too_large", error.GetType().GetProperty("Code")!.GetValue(error));
        // Kestrel still owns the unread rejected body. Installing a smaller read
        // cap here can make its later drain abort an otherwise valid413 response.
        Assert.Equal(1048576, transport.MaxRequestBodySize);
    }
    private sealed class BodyLimit : IHttpMaxRequestBodySizeFeature
    {
        public bool IsReadOnly => false;
        public long? MaxRequestBodySize { get; set; } = 1048576;
    }
}

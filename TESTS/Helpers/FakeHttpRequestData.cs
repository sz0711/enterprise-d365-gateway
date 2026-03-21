using System.Net;
using System.Security.Claims;
using System.Text;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace enterprise_d365_gateway.Tests.Helpers;

public class FakeHttpRequestData : HttpRequestData
{
    public FakeHttpRequestData(FunctionContext context, string body = "")
        : base(context)
    {
        Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        Headers = new HttpHeadersCollection();
    }

    public override Stream Body { get; }
    public override HttpHeadersCollection Headers { get; }
    public override IReadOnlyCollection<IHttpCookie> Cookies { get; } = Array.Empty<IHttpCookie>();
    public override Uri Url { get; } = new("https://localhost/api/upsert");
    public override IEnumerable<ClaimsIdentity> Identities { get; } = Enumerable.Empty<ClaimsIdentity>();
    public override string Method { get; } = "POST";

    public override HttpResponseData CreateResponse()
    {
        return new FakeHttpResponseData(FunctionContext);
    }
}

public class FakeHttpResponseData : HttpResponseData
{
    public FakeHttpResponseData(FunctionContext context) : base(context)
    {
        Headers = new HttpHeadersCollection();
        Body = new MemoryStream();
    }

    public override HttpStatusCode StatusCode { get; set; }
    public override HttpHeadersCollection Headers { get; set; }
    public override Stream Body { get; set; }
    public override HttpCookies Cookies { get; } = new FakeHttpCookies();

    public string ReadBody()
    {
        Body.Position = 0;
        using var reader = new StreamReader(Body, leaveOpen: true);
        return reader.ReadToEnd();
    }
}

public class FakeHttpCookies : HttpCookies
{
    public override void Append(string name, string value) { }
    public override void Append(IHttpCookie cookie) { }
    public override IHttpCookie CreateNew() => throw new NotImplementedException();
}
